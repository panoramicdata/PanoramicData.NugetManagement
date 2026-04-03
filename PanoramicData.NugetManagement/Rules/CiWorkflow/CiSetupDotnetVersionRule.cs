using PanoramicData.NugetManagement.Models;

namespace PanoramicData.NugetManagement.Rules;

/// <summary>
/// Checks that the CI workflow uses the latest actions/setup-dotnet version
/// and the correct .NET SDK version.
/// </summary>
public class CiSetupDotnetVersionRule : RuleBase
{
	/// <inheritdoc />
	public override string RuleId => "CI-06";

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

		var expectedAction = $"actions/setup-dotnet@{Standards.LatestActionsSetupDotnetVersion}";
		var hasAction = Contains(content, expectedAction);
		var hasSdk = Contains(content, Standards.LatestDotNetVersionSpecifier);

		return Task.FromResult(hasAction && hasSdk
			? Pass($"CI uses {expectedAction} with {Standards.LatestDotNetVersionSpecifier}.")
			: Fail(
				$"CI does not use {expectedAction} with dotnet-version: '{Standards.LatestDotNetVersionSpecifier}'.",
				new RuleAdvisory
				{
					Summary = "Update actions/setup-dotnet to latest version and SDK",
					Detail = $"Update to `uses: {expectedAction}` with `dotnet-version: {Standards.LatestDotNetVersionSpecifier}`.",
					Data = new()
					{
						["workflow_file"] = ciWorkflowPath,
						["latest_sdk"] = Standards.LatestDotNetVersionSpecifier
					}
				}));
	}
}
