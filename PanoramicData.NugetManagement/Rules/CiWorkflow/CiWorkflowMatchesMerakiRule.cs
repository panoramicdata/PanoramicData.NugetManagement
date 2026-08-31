using PanoramicData.NugetManagement.Models;

namespace PanoramicData.NugetManagement.Rules;

/// <summary>
/// Checks that ci.yml matches the Meraki.Api trusted publishing workflow shape.
/// </summary>
public class CiWorkflowMatchesMerakiRule : RuleBase, IGovernsDependency
{
	/// <summary>
	/// The actions this rule carries to a known version: <c>actions/upload-artifact</c> is checked
	/// against the learned floor outright, and <c>actions/download-artifact</c> comes along because
	/// the remediation replaces the whole workflow with a template that pins it.
	/// </summary>
	private static readonly DependencyRef[] _governed =
	[
		new(DependencyEcosystem.GitHubActions, "actions/upload-artifact"),
		new(DependencyEcosystem.GitHubActions, "actions/download-artifact")
	];

	/// <inheritdoc />
	public override string RuleId => "CI-08";

	/// <inheritdoc />
	public bool Governs(DependencyRef dependency) => _governed.Contains(dependency);

	/// <inheritdoc />
	/// <remarks>
	/// The remediation replaces the whole workflow with the template, so any failure of this rule
	/// carries both governed actions to the template's versions. Nothing to narrow.
	/// </remarks>
	public bool WillMove(RuleResult failure, DependencyRef dependency) => Governs(dependency);

	/// <inheritdoc />
	public override string RuleName => "CI workflow matches Meraki.Api standard";

	/// <inheritdoc />
	public override AssessmentCategory Category => AssessmentCategory.CiCd;

	/// <inheritdoc />
	public override AssessmentSeverity Severity => AssessmentSeverity.Error;

	/// <inheritdoc />
	public override Task<RuleResult> EvaluateAsync(RepositoryContext context, CancellationToken cancellationToken)
	{
		var ciWorkflowPath = CiWorkflowPathResolver.Resolve(context);
		var content = context.GetFileContent(ciWorkflowPath);
		var expectedNuGetUser = context.Options.NuGetUser;
		if (content is null)
		{
			return Task.FromResult(Fail(
				"CI workflow not found.",
				new RuleAdvisory
				{
					Summary = "Create CI workflow matching the standard Trusted Publishing pattern",
					Detail = "Copy the standard CI workflow from Meraki.Api and adapt only repository-specific project paths. The workflow must include tag triggers, artifact upload, NuGet login, and nuget push steps.",
					Data = new() { ["expected_path"] = ciWorkflowPath }
				}));
		}

		var requiredSnippets = new[]
		{
			"tags: ['[0-9]*.[0-9]*.[0-9]*']",
			"publish:",
			"if: startsWith(github.ref, 'refs/tags/')",
			"id-token: write",
			"uses: NuGet/login@v1",
		 $"user: {expectedNuGetUser}",
			"dotnet nuget push ./artifacts/*.nupkg --api-key ${{ steps.login.outputs.NUGET_API_KEY }}"
		};

		var missing = requiredSnippets
			.Where(snippet => !Contains(content, snippet))
			.ToList();

		// upload-artifact is checked version-aware: at or above the learned floor (repos ahead are fine).
		var uploadFloor = Services.ActionVersionCatalog.Default.GetFloorSpec("actions/upload-artifact", Standards.LatestActionsUploadArtifactVersion);
		if (!GitHubActionVersion.UsesAtLeast(content, "actions/upload-artifact", uploadFloor, out var uploadUsed))
		{
			missing.Add($"uses: actions/upload-artifact@{uploadFloor} (or later)");
		}

		if (uploadUsed is not null)
		{
			Services.ActionVersionCatalog.Default.Observe("actions/upload-artifact", uploadUsed.Value, Standards.LatestActionsUploadArtifactVersion, context.FullName);
		}

		return Task.FromResult(missing.Count == 0
			? Pass("CI workflow matches the Meraki.Api trusted publishing standard.")
			: Fail(
				"CI workflow does not match the Meraki.Api standard trusted publishing shape.",
				new RuleAdvisory
				{
					Summary = "Update CI workflow to match the standard Trusted Publishing pattern",
					Detail = $"Ensure `{ciWorkflowPath}` includes all standard sections for trusted publishing including tag triggers, artifact upload, NuGet login, and push.",
					Data = new()
					{
						["remediation_type"] = "replace_file_content",
						["file"] = ciWorkflowPath,
						["new_content"] = Standards.GetTrustedPublishingCiWorkflowContent(expectedNuGetUser),
						["missing_snippets"] = missing.ToArray()
					}
				}));
	}
}
