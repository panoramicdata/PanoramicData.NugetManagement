using PanoramicData.NugetManagement.Models;
using PanoramicData.NugetManagement.Services;

namespace PanoramicData.NugetManagement.Rules;

/// <summary>
/// Reports a repository's code coverage, and whether it has fallen below the best it has reached.
/// </summary>
/// <remarks>
/// Informational on purpose, and for some time. The estate is a long way from a figure worth
/// enforcing — this repository sits near 90% in its rules library and near 2% in its web layer — and
/// turning coverage red today would bury every other finding. What matters first is the direction of
/// travel, which needs a floor per repository rather than one number for the estate.
/// </remarks>
public class CodeCoverageTrendRule : RuleBase
{
	private readonly CoverageBaselineCatalog _baselines;

	/// <summary>
	/// Initializes a new instance using the default, repository-root baseline catalogue.
	/// </summary>
	public CodeCoverageTrendRule()
		: this(CoverageBaselines.Default)
	{
	}

	/// <summary>
	/// Initializes a new instance using a supplied catalogue.
	/// </summary>
	/// <param name="baselines">The catalogue recording each repository's best coverage.</param>
	public CodeCoverageTrendRule(CoverageBaselineCatalog baselines)
	{
		_baselines = baselines;
	}

	/// <inheritdoc />
	public override string RuleId => "TST-07";

	/// <inheritdoc />
	public override string RuleName => "Code coverage is not going backwards";

	/// <inheritdoc />
	public override AssessmentCategory Category => AssessmentCategory.Testing;

	/// <inheritdoc />
	public override AssessmentSeverity Severity => AssessmentSeverity.Info;

	/// <inheritdoc />
	public override Task<RuleResult> EvaluateAsync(RepositoryContext context, CancellationToken cancellationToken)
	{
		if (!context.FindTestProjectFiles().Any())
		{
			return Task.FromResult(NotApplicable("No test projects found; there is nothing to measure."));
		}

		if (context.LineCoveragePercent is not { } line || context.BranchCoveragePercent is not { } branch)
		{
			// Never a pass: an unmeasured repository has not demonstrated anything, and saying so is
			// the difference between "we do not know" and "this is fine".
			return Task.FromResult(NotApplicable(
				$"No coverage has been measured. Run Run-Coverage.ps1, or the test application with --coverage, to record a first figure."));
		}

		var measured = new CoverageBaseline(line, branch);
		var baseline = _baselines.GetBaseline(context.FullName);

		if (baseline is not { } best)
		{
			_baselines.Observe(context.FullName, measured);
			return Task.FromResult(Pass(
				$"First coverage recorded: {line:N1}% line, {branch:N1}% branch. That is now the floor."));
		}

		var lineFell = line < best.LinePercent;
		var branchFell = branch < best.BranchPercent;

		if (!lineFell && !branchFell)
		{
			var raised = _baselines.Observe(context.FullName, measured);
			return Task.FromResult(Pass(raised
				? $"Coverage improved to {line:N1}% line, {branch:N1}% branch."
				: $"Coverage held at {line:N1}% line, {branch:N1}% branch."));
		}

		var fallen = lineFell && branchFell
			? $"line coverage {line:N1}% (best {best.LinePercent:N1}%) and branch coverage {branch:N1}% (best {best.BranchPercent:N1}%)"
			: lineFell
				? $"line coverage {line:N1}% (best {best.LinePercent:N1}%)"
				: $"branch coverage {branch:N1}% (best {best.BranchPercent:N1}%)";

		return Task.FromResult(Fail(
			$"Coverage has fallen: {fallen}.",
			new RuleAdvisory
			{
				Summary = "Restore coverage to at least the level this repository has already reached.",
				Detail = $"""
					Coverage is below the best figure recorded for this repository. The floor only ever
					moves up, so this means code was added without tests, or tests were removed.

					Measured: {line:N1}% line, {branch:N1}% branch.
					Best so far: {best.LinePercent:N1}% line, {best.BranchPercent:N1}% branch.

					Reproduce locally with `Run-Coverage.ps1`, which prints coverage per module so the
					drop can be attributed. There is no estate-wide threshold to meet — only this
					repository's own previous best.
					""",
				Data = new()
				{
					["measured_line"] = line,
					["measured_branch"] = branch,
					["best_line"] = best.LinePercent,
					["best_branch"] = best.BranchPercent
				}
			}));
	}
}

/// <summary>
/// The process-wide coverage baseline catalogue, read from and written to the repository root.
/// </summary>
public static class CoverageBaselines
{
	/// <summary>The shared catalogue.</summary>
	public static CoverageBaselineCatalog Default { get; } = new(FindBaselineFile());

	private static string? FindBaselineFile()
	{
		var directory = new DirectoryInfo(AppContext.BaseDirectory);

		while (directory is not null)
		{
			var candidate = Path.Combine(directory.FullName, CoverageBaselineCatalog.FileName);
			if (File.Exists(candidate))
			{
				return candidate;
			}

			directory = directory.Parent;
		}

		return null;
	}
}
