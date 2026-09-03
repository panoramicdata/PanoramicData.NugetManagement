using PanoramicData.NugetManagement.Models;
using PanoramicData.NugetManagement.Services;
using NuGet.Versioning;

namespace PanoramicData.NugetManagement.Rules;

/// <summary>
/// Base class for rules that enforce NuGet package freshness by semantic update level.
/// </summary>
/// <remarks>
/// <para>
/// Asks two questions. Are you behind the estate — a version some repository of ours already runs?
/// That fails immediately, because it is a fact about us and somebody has already proven it works.
/// Are you behind nuget.org? That fails only after a grace period measured from the release's own
/// publication date, so drift still has consequences without handing the verdict to whoever
/// published this morning.
/// </para>
/// <para>
/// Neither question touches the network. Resolving "latest" live made an assessment depend on what
/// strangers published that day, and turned repositories red without a line of code changing.
/// </para>
/// </remarks>
public abstract class NuGetPackageUpdateRuleBase : RuleBase, IGovernsDependency
{
	/// <summary>
	/// The advisory key naming the packages a failure of one of these rules will move.
	/// </summary>
	/// <remarks>
	/// Separate from <c>behind_estate</c> and <c>behind_upstream</c>, which carry a rendered sentence
	/// per finding for a human to read. Triage needs the bare identifiers, and parsing them back out of
	/// prose is the kind of coupling that breaks the moment the wording improves.
	/// </remarks>
	public const string GovernedPackagesKey = "governed_packages";

	/// <inheritdoc />
	/// <remarks>
	/// Every NuGet package, because these rules act on whatever the repository declares rather than on
	/// a named list. The ecosystem still has to match: no amount of package updating moves a GitHub
	/// Action's version.
	/// </remarks>
	public bool Governs(DependencyRef dependency)
		=> dependency.Ecosystem == DependencyEcosystem.NuGet;

	/// <inheritdoc />
	/// <remarks>
	/// <see cref="Governs"/> claims the whole ecosystem, but a failure moves only the packages it
	/// named, so the claim has to be narrowed to those. Without this, a failure about one package
	/// reports every other NuGet pull request as covered by a fix that never touches it — and those
	/// pull requests then wait indefinitely for it, with no gap issue raised because they look handled.
	/// <para>
	/// A package the scanner never reads can never be named here, which is the honest answer for one
	/// declared somewhere these rules do not look — <c>nbgv</c> in <c>.config/dotnet-tools.json</c>
	/// being the case that prompted this.
	/// </para>
	/// </remarks>
	public bool WillMove(RuleResult failure, DependencyRef dependency)
		=> Governs(dependency)
			&& AdvisoryNames.Contains(failure, GovernedPackagesKey, dependency.Name);

	private readonly NuGetVersionCache _cache;
	private readonly NuGetFloorCatalog _floors;
	private readonly TimeProvider _timeProvider;
	private readonly NuGetOwnedPackageCatalog _owned;

	/// <summary>
	/// Initializes a new instance using the shared stores. This is the constructor
	/// <see cref="RuleRegistry"/> uses, via <c>Activator.CreateInstance</c>.
	/// </summary>
	protected NuGetPackageUpdateRuleBase()
		: this(
			NuGetVersionCache.Default,
			NuGetFloorCatalog.Default,
			TimeProvider.System,
			NuGetOwnedPackageCatalog.Default)
	{
	}

	/// <summary>
	/// Initializes a new instance with explicit stores and clock, for tests.
	/// </summary>
	/// <param name="cache">The committed upstream snapshot.</param>
	/// <param name="floors">The estate-learned floors.</param>
	/// <param name="timeProvider">The clock the grace period is measured against.</param>
	/// <remarks>
	/// Every package is somebody else's, so the grace period applies to all of them. Tests about our
	/// own packages use the overload that takes a catalogue; every other test keeps the behaviour it
	/// was written against.
	/// </remarks>
	protected NuGetPackageUpdateRuleBase(
		NuGetVersionCache cache,
		NuGetFloorCatalog floors,
		TimeProvider timeProvider)
		: this(cache, floors, timeProvider, new NuGetOwnedPackageCatalog(null))
	{
	}

	/// <summary>
	/// Initializes a new instance with explicit stores, clock and owned-package list, for tests.
	/// </summary>
	/// <param name="cache">The committed upstream snapshot.</param>
	/// <param name="floors">The estate-learned floors.</param>
	/// <param name="timeProvider">The clock the grace period is measured against.</param>
	/// <param name="owned">The packages the estate publishes itself.</param>
	protected NuGetPackageUpdateRuleBase(
		NuGetVersionCache cache,
		NuGetFloorCatalog floors,
		TimeProvider timeProvider,
		NuGetOwnedPackageCatalog owned)
	{
		_cache = cache;
		_floors = floors;
		_timeProvider = timeProvider;
		_owned = owned;
	}

	/// <inheritdoc />
	public override AssessmentCategory Category => AssessmentCategory.NuGetHygiene;

	/// <summary>
	/// Gets the update level this rule enforces.
	/// </summary>
	protected abstract PackageUpdateLevel TargetUpdateLevel { get; }

	/// <summary>
	/// Gets the user-facing label for the update level.
	/// </summary>
	protected abstract string UpdateLevelDisplayName { get; }

	/// <summary>
	/// Gets how long a published release may go un-adopted before it becomes a failure.
	/// </summary>
	protected abstract int GraceDays { get; }

	/// <inheritdoc />
	public override Task<RuleResult> EvaluateAsync(RepositoryContext context, CancellationToken cancellationToken)
	{
		var packageReferences = PackageReferenceScanner.Scan(context);
		if (packageReferences.Count == 0)
		{
			return Task.FromResult(Pass("No explicit NuGet package versions were found to evaluate."));
		}

		var behindEstate = new List<string>();
		var behindUpstream = new List<string>();
		var updates = new List<string>();
		var pending = new List<string>();
		var governed = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
		var now = _timeProvider.GetUtcNow();

		foreach (var reference in packageReferences)
		{
			cancellationToken.ThrowIfCancellationRequested();

			// Raises the floor for subsequent runs only; this run's floor was frozen at load.
			_floors.Observe(reference.PackageId, reference.CurrentVersion, context.FullName);

			if (!NuGetVersion.TryParse(reference.CurrentVersion, out var current))
			{
				continue;
			}

			// Consistency: behind a version the estate already runs.
			var floor = _floors.GetFloor(reference.PackageId);
			if (floor is not null
				&& NuGetVersion.TryParse(floor, out var floorVersion)
				&& NuGetVersionChecker.ClassifyUpdateLevel(current, floorVersion) == TargetUpdateLevel)
			{
				behindEstate.Add($"{reference.PackageId} {current.ToNormalizedString()} → {floor} ({reference.FilePath})");
				updates.Add(SerializeUpdate(reference, floor));
				governed.Add(reference.PackageId);
				continue;
			}

			// Freshness: behind nuget.org for longer than this level's grace.
			if (!_cache.TryGet(reference.PackageId, out var snapshot)
				|| !NuGetVersion.TryParse(snapshot.LatestVersion, out var latest)
				|| NuGetVersionChecker.ClassifyUpdateLevel(current, latest) != TargetUpdateLevel)
			{
				continue;
			}

			var age = now - snapshot.Published;
			var entry = $"{reference.PackageId} {current.ToNormalizedString()} → {snapshot.LatestVersion} ({reference.FilePath})";

			// A release of ours gets no grace. The grace period exists so that a verdict is not handed
			// to whoever published this morning; when we published it, there is nobody else to wait on,
			// and waiting is how a Dependabot pull request bumping one of our own packages sits open for
			// a month with no failing rule queued to move it.
			var isOurs = _owned.Contains(reference.PackageId);
			var graceDays = isOurs ? 0 : GraceDays;

			if (age.TotalDays > graceDays)
			{
				behindUpstream.Add(isOurs
					? $"{entry}, published {age.Days} days ago and we publish it, so it has no grace period"
					: $"{entry}, published {age.Days} days ago");
				updates.Add(SerializeUpdate(reference, snapshot.LatestVersion));
				governed.Add(reference.PackageId);
			}
			else
			{
				pending.Add(entry);
			}
		}

		if (behindEstate.Count == 0 && behindUpstream.Count == 0)
		{
			// Drift inside the grace period is always reported, so it is visible before it is a failure.
			return Task.FromResult(pending.Count == 0
				? Pass($"No {UpdateLevelDisplayName} NuGet package updates are overdue.")
				: Pass($"No {UpdateLevelDisplayName} NuGet package updates are overdue. Available within the {GraceDays}-day grace period: {string.Join("; ", pending)}"));
		}

		var messages = new List<string>();
		if (behindEstate.Count > 0)
		{
			messages.Add($"behind the estate: {string.Join("; ", behindEstate)}");
		}

		if (behindUpstream.Count > 0)
		{
			messages.Add($"overdue against nuget.org: {string.Join("; ", behindUpstream)}");
		}

		return Task.FromResult(Fail(
			$"The following NuGet packages have {UpdateLevelDisplayName} updates outstanding — {string.Join(", ", messages)}",
			new RuleAdvisory
			{
				Summary = $"Update the listed packages to at least the version the estate already uses, and adopt {UpdateLevelDisplayName} releases within {GraceDays} days.",
				Detail = $"A package below the estate floor is behind a version another repository of ours already runs. A package past its {GraceDays}-day grace period has been behind a published release for too long, and a package the estate publishes itself has no grace period at all. Update the listed versions in `Directory.Packages.props` or the affected project files.",
				Data = new()
				{
					["remediation_type"] = "update_package_versions",
					["updates"] = updates.ToArray(),
					["behind_estate"] = behindEstate.ToArray(),
					["behind_upstream"] = behindUpstream.ToArray(),
					[GovernedPackagesKey] = governed.ToArray(),
					["grace_days"] = GraceDays
				}
			}));
	}

	/// <summary>
	/// Renders one finding in the pipe-delimited form the <c>update_package_versions</c> remediation
	/// parses: which file declared it, which package, how the version was written, and what to move it
	/// from and to.
	/// </summary>
	/// <remarks>
	/// Separate from <c>behind_estate</c> and <c>behind_upstream</c> for the same reason
	/// <see cref="GovernedPackagesKey"/> is: those carry a sentence for a human, and the version
	/// syntax a rewrite needs — attribute or element — does not survive being written as prose.
	/// </remarks>
	/// <param name="reference">The declaration the finding was raised against.</param>
	/// <param name="targetVersion">The version the remediation should write.</param>
	/// <returns>The serialized update.</returns>
	private static string SerializeUpdate(PackageVersionReference reference, string targetVersion)
		=> string.Join(
			'|',
			reference.FilePath,
			reference.PackageId,
			reference.VersionKind,
			reference.CurrentVersion,
			targetVersion);
}
