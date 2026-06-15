using PanoramicData.NugetManagement.Models;

namespace PanoramicData.NugetManagement.Rules;

/// <summary>
/// Checks that Publish.ps1 matches the Meraki.Api tagging-and-trigger standard.
/// </summary>
public class PublishScriptMatchesMerakiRule : RuleBase
{
	/// <inheritdoc />
	public override string RuleId => "CI-09";

	/// <inheritdoc />
	public override string RuleName => "Publish.ps1 matches Meraki.Api standard";

	/// <inheritdoc />
	public override AssessmentCategory Category => AssessmentCategory.CiCd;

	/// <inheritdoc />
	public override AssessmentSeverity Severity => AssessmentSeverity.Error;

	/// <inheritdoc />
	public override Task<RuleResult> EvaluateAsync(RepositoryContext context, CancellationToken cancellationToken)
	{
		if (!context.Options.IsPackable)
		{
			return Task.FromResult(Pass("Repository is not packable — Publish.ps1 standard not required."));
		}

		var content = context.GetFileContent("Publish.ps1");
		if (content is null)
		{
			return Task.FromResult(Fail(
				"Publish.ps1 not found.",
				new RuleAdvisory
				{
					Summary = "Add standard Publish.ps1 matching the Meraki.Api tagging-and-trigger pattern",
					Detail = "Copy the standard Publish.ps1 from Meraki.Api. It should check for a clean tree, verify the main branch, resolve the version via the Nerdbank.GitVersioning MSBuild target (`dotnet build -t:GetBuildVersion`), create a tag, and push the tag.",
					Data = new()
					{
						["expected_path"] = "Publish.ps1",
						["template_content"] = Standards.PublishPs1Content
					}
				}));
		}

		// The version must be resolved via the Nerdbank.GitVersioning MSBuild target,
		// not the global 'nbgv' CLI tool, which fails when it is not on PATH (e.g. in
		// fresh shells or CI runners without the global tool installed).
		if (Contains(content, "nbgv get-version"))
		{
			return Task.FromResult(Fail(
				"Publish.ps1 resolves the version with the 'nbgv' CLI tool; use the Nerdbank.GitVersioning MSBuild target instead.",
				new RuleAdvisory
				{
					Summary = "Replace the 'nbgv' CLI call in Publish.ps1 with the MSBuild GetBuildVersion target",
					Detail = "The global 'nbgv' tool fails when it is not on PATH. Resolve the version with `dotnet build <project> -t:GetBuildVersion --getProperty:NuGetPackageVersion -p:TreatWarningsAsErrors=false` instead, which relies only on the referenced Nerdbank.GitVersioning package.",
					Data = new()
					{
						["remediation_type"] = "replace_file_content",
						["file"] = "Publish.ps1",
						["new_content"] = Standards.PublishPs1Content
					}
				}));
		}

		var requiredSnippets = new[]
		{
			"git status --porcelain",
			"git rev-parse --abbrev-ref HEAD",
			"git fetch origin main --quiet",
			"-t:GetBuildVersion",
			"--getProperty:NuGetPackageVersion",
			"git tag -l $version",
			"git tag $version",
			"git push origin $version"
		};

		var missing = requiredSnippets
			.Where(snippet => !Contains(content, snippet))
			.ToList();

		return Task.FromResult(missing.Count == 0
			? Pass("Publish.ps1 matches the Meraki.Api tagging-and-trigger standard.")
			: Fail(
				"Publish.ps1 does not match the Meraki.Api standard.",
				new RuleAdvisory
				{
					Summary = "Update Publish.ps1 to match the standard Meraki.Api tagging-and-trigger pattern",
					Detail = "Ensure Publish.ps1 contains all standard checks and tag operations: clean tree check, branch check, MSBuild version resolution (`-t:GetBuildVersion --getProperty:NuGetPackageVersion`), tag creation, and tag push.",
					Data = new()
					{
						["remediation_type"] = "replace_file_content",
						["file"] = "Publish.ps1",
						["new_content"] = Standards.PublishPs1Content,
						["missing_snippets"] = missing.ToArray()
					}
				}));
	}
}
