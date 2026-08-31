using System.Text.RegularExpressions;
using PanoramicData.NugetManagement.Models;

namespace PanoramicData.NugetManagement.Rules;

/// <summary>
/// Checks that the CI workflow uses the latest actions/setup-dotnet version
/// and the correct .NET SDK version.
/// </summary>
public partial class CiSetupDotnetVersionRule : RuleBase, IGovernsDependency
{
	/// <inheritdoc />
	public override string RuleId => "CI-06";

	/// <inheritdoc />
	public bool Governs(DependencyRef dependency)
		=> dependency == new DependencyRef(DependencyEcosystem.GitHubActions, "actions/setup-dotnet");

	/// <inheritdoc />
	/// <remarks>
	/// One action, and a failure of this rule is always a failure about that action, so there is
	/// nothing to narrow: whatever it governs, it moves.
	/// </remarks>
	public bool WillMove(RuleResult failure, DependencyRef dependency) => Governs(dependency);

	/// <inheritdoc />
	public override string RuleName => "CI uses latest setup-dotnet and SDK";

	/// <inheritdoc />
	public override AssessmentCategory Category => AssessmentCategory.CiCd;

	/// <inheritdoc />
	public override AssessmentSeverity Severity => AssessmentSeverity.Warning;

	/// <inheritdoc />
	public override Task<RuleResult> EvaluateAsync(RepositoryContext context, CancellationToken cancellationToken)
	{
		var ciWorkflowPath = CiWorkflowPathResolver.Resolve(context);
		var content = context.GetFileContent(ciWorkflowPath);
		if (content is null)
		{
			return Task.FromResult(Fail(
				"CI workflow not found.",
				new RuleAdvisory
				{
					Summary = $"Create `{ciWorkflowPath}` and use `actions/setup-dotnet@{Standards.LatestActionsSetupDotnetVersion}` with dotnet-version `{Standards.LatestDotNetVersionSpecifier}`",
					Detail = $"Create `{ciWorkflowPath}` using `actions/setup-dotnet@{Standards.LatestActionsSetupDotnetVersion}` with `dotnet-version: {Standards.LatestDotNetVersionSpecifier}`.",
					Data = new()
					{
						["expected_path"] = ciWorkflowPath,
						["latest_sdk"] = Standards.LatestDotNetVersionSpecifier
					}
				}));
		}

		var floor = Services.ActionVersionCatalog.Default.GetFloorSpec("actions/setup-dotnet", Standards.LatestActionsSetupDotnetVersion);
		var hasAction = GitHubActionVersion.UsesAtLeast(content, "actions/setup-dotnet", floor, out var used);
		if (used is not null)
		{
			Services.ActionVersionCatalog.Default.Observe("actions/setup-dotnet", used.Value, Standards.LatestActionsSetupDotnetVersion, context.FullName);
		}

		var hasSdk = Contains(content, Standards.LatestDotNetVersionSpecifier);

		if (hasAction && hasSdk)
		{
			return Task.FromResult(Pass(
				$"CI uses actions/setup-dotnet@v{used} (at or above {floor}) with {Standards.LatestDotNetVersionSpecifier}."));
		}

		var data = new Dictionary<string, object>
		{
			["workflow_file"] = ciWorkflowPath,
			["latest_sdk"] = Standards.LatestDotNetVersionSpecifier
		};

		AddRemediation(data, content, ciWorkflowPath, floor, hasAction, used, hasSdk);

		return Task.FromResult(Fail(
			$"CI does not use actions/setup-dotnet@{floor} or later with dotnet-version: '{Standards.LatestDotNetVersionSpecifier}'{(used is null ? "" : $" (found setup-dotnet@v{used})")}.",
			new RuleAdvisory
			{
				Summary = $"Update actions/setup-dotnet to {floor} or later and SDK",
				Detail = $"Update to `uses: actions/setup-dotnet@{floor}` (or later) with `dotnet-version: {Standards.LatestDotNetVersionSpecifier}`.",
				Data = data
			}));
	}

	/// <summary>
	/// Matches a single-line <c>dotnet-version:</c> whose value is a plain version specifier — the
	/// only shape that can be rewritten in place. A block list (<c>dotnet-version: |</c>) or an
	/// expression is left to the AI, which can see what the other entries are for. The multiline
	/// option is inline so the pattern carries it into the remediation payload.
	/// </summary>
	private const string _scalarDotnetVersionPattern =
		@"(?m)^([ \t]*dotnet-version:[ \t]*)(['""]?)\d+\.[\dx*]+(\.[\dx*]+)?\2[ \t]*$";

	[GeneratedRegex(_scalarDotnetVersionPattern, RegexOptions.IgnoreCase)]
	private static partial Regex ScalarDotnetVersionLine();

	/// <summary>
	/// Adds the remediation payload when — and only when — every edit this rule wants is a rewrite of
	/// text already present in the workflow. Anything that would mean inserting a step or guessing
	/// where a key belongs is left without a payload, so the UI offers the AI prompt instead.
	/// </summary>
	private static void AddRemediation(
		Dictionary<string, object> data,
		string content,
		string ciWorkflowPath,
		string floor,
		bool hasAction,
		int? used,
		bool hasSdk)
	{
		var patterns = new List<string>();
		var replacements = new List<string>();

		if (!hasAction)
		{
			// No setup-dotnet step at all: there is no version to bump, and where the step belongs in
			// the job is a judgement this rule cannot make.
			if (used is null)
			{
				return;
			}

			patterns.Add(@"(actions/setup-dotnet@)v\d+(\.\d+)*");
			replacements.Add($"${{1}}{floor}");
		}

		if (!hasSdk)
		{
			if (!ScalarDotnetVersionLine().IsMatch(content))
			{
				return;
			}

			patterns.Add(_scalarDotnetVersionPattern);
			replacements.Add($"${{1}}${{2}}{Standards.LatestDotNetVersionSpecifier}${{2}}");
		}

		data["remediation_type"] = "replace_regex_in_file";
		data["file"] = ciWorkflowPath;
		data["patterns"] = patterns.ToArray();
		data["replacements"] = replacements.ToArray();
	}
}
