using PanoramicData.NugetManagement.Models;

namespace PanoramicData.NugetManagement.Rules;

internal static class CiWorkflowPathResolver
{
	public static string Resolve(RepositoryContext context)
	{
		var fileName = string.IsNullOrWhiteSpace(context.Options.CiFileName)
			? "ci.yml"
			: context.Options.CiFileName.Trim();

		return $".github/workflows/{fileName}";
	}
}

/// <summary>
/// Checks that a CI workflow exists at .github/workflows/ci.yml.
/// </summary>
public class CiWorkflowExistsRule : RuleBase
{
	/// <inheritdoc />
	public override string RuleId => "CI-01";

	/// <inheritdoc />
	public override string RuleName => "CI workflow exists";

	/// <inheritdoc />
	public override AssessmentCategory Category => AssessmentCategory.CiCd;

	/// <inheritdoc />
	public override AssessmentSeverity Severity => AssessmentSeverity.Error;

	/// <inheritdoc />
	public override Task<RuleResult> EvaluateAsync(RepositoryContext context, CancellationToken cancellationToken)
	{
		var ciWorkflowPath = CiWorkflowPathResolver.Resolve(context);

		return Task.FromResult(context.FileExists(ciWorkflowPath)
			? Pass($"CI workflow found at {ciWorkflowPath}")
			: Fail(
				$"No CI workflow found at {ciWorkflowPath}",
				$"Create {ciWorkflowPath} that restores, builds Release, and tests."));
	}
}

/// <summary>
/// Checks that the CI workflow triggers on push and PR to main.
/// </summary>
public class CiWorkflowTriggersRule : RuleBase
{
	/// <inheritdoc />
	public override string RuleId => "CI-02";

	/// <inheritdoc />
	public override string RuleName => "CI triggers on push+PR to main";

	/// <inheritdoc />
	public override AssessmentCategory Category => AssessmentCategory.CiCd;

	/// <inheritdoc />
	public override AssessmentSeverity Severity => AssessmentSeverity.Error;

	/// <inheritdoc />
	public override Task<RuleResult> EvaluateAsync(RepositoryContext context, CancellationToken cancellationToken)
	{
		var ciWorkflowPath = CiWorkflowPathResolver.Resolve(context);
		var content = context.GetFileContent(ciWorkflowPath);
		if (content is null)
		{
			return Task.FromResult(Fail(
				"CI workflow not found — cannot check triggers.",
				$"Create {ciWorkflowPath} with push and pull_request triggers for the main branch."));
		}

		var hasPush = Contains(content, "push:");
		var hasPr = Contains(content, "pull_request:");
		var hasMain = Contains(content, "main");

		return Task.FromResult(hasPush && hasPr && hasMain
			? Pass("CI workflow triggers on push and pull_request to main.")
			: Fail(
				"CI workflow does not trigger on both push and pull_request to main.",
				"Add 'on: push: branches: [main]' and 'on: pull_request: branches: [main]' to ci.yml."));
	}
}

/// <summary>
/// Checks that the CI workflow restores, builds Release, and packs artifacts.
/// </summary>
public class CiWorkflowStepsRule : RuleBase
{
	/// <inheritdoc />
	public override string RuleId => "CI-03";

	/// <inheritdoc />
	public override string RuleName => "CI restores, builds Release, and packs";

	/// <inheritdoc />
	public override AssessmentCategory Category => AssessmentCategory.CiCd;

	/// <inheritdoc />
	public override AssessmentSeverity Severity => AssessmentSeverity.Error;

	/// <inheritdoc />
	public override Task<RuleResult> EvaluateAsync(RepositoryContext context, CancellationToken cancellationToken)
	{
		var ciWorkflowPath = CiWorkflowPathResolver.Resolve(context);
		var content = context.GetFileContent(ciWorkflowPath);
		if (content is null)
		{
			return Task.FromResult(Fail(
				"CI workflow not found — cannot check steps.",
				$"Create {ciWorkflowPath} with dotnet restore, dotnet build --configuration Release, and dotnet test steps."));
		}

		var hasRestore = Contains(content, "dotnet restore");
		var hasBuild = Contains(content, "dotnet build");
		var hasRelease = Contains(content, "--configuration Release");
		var hasPack = Contains(content, "dotnet pack");

		return Task.FromResult(hasRestore && hasBuild && hasRelease && hasPack
			? Pass("CI workflow contains restore, build (Release), and pack steps.")
			: Fail(
				"CI workflow is missing one or more required steps (restore, build --configuration Release, pack).",
				"Ensure ci.yml has 'dotnet restore', 'dotnet build --configuration Release', and 'dotnet pack' steps."));
	}
}

/// <summary>
/// Checks that the CI workflow uses fetch-depth: 0 for Nerdbank.GitVersioning.
/// </summary>
public class CiCheckoutFetchDepthRule : RuleBase
{
	/// <inheritdoc />
	public override string RuleId => "CI-04";

	/// <inheritdoc />
	public override string RuleName => "CI checkout uses fetch-depth: 0";

	/// <inheritdoc />
	public override AssessmentCategory Category => AssessmentCategory.CiCd;

	/// <inheritdoc />
	public override AssessmentSeverity Severity => AssessmentSeverity.Error;

	/// <inheritdoc />
	public override Task<RuleResult> EvaluateAsync(RepositoryContext context, CancellationToken cancellationToken)
	{
		var ciWorkflowPath = CiWorkflowPathResolver.Resolve(context);
		var content = context.GetFileContent(ciWorkflowPath);
		if (content is null)
		{
			return Task.FromResult(Fail(
				"CI workflow not found — cannot check fetch-depth.",
				$"Create {ciWorkflowPath} and configure actions/checkout with 'fetch-depth: 0'."));
		}

		return Task.FromResult(Contains(content, "fetch-depth: 0")
			? Pass("CI checkout uses fetch-depth: 0.")
			: Fail(
				"CI checkout does not use fetch-depth: 0, which is required for Nerdbank.GitVersioning.",
				"Add 'with: fetch-depth: 0' to the actions/checkout step."));
	}
}

/// <summary>
/// Checks that the CI workflow uses the latest actions/checkout version.
/// </summary>
public class CiActionsCheckoutVersionRule : RuleBase
{
	/// <inheritdoc />
	public override string RuleId => "CI-05";

	/// <inheritdoc />
	public override string RuleName => "CI uses latest actions/checkout";

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
				$"Create {ciWorkflowPath} and use 'actions/checkout@{Standards.LatestActionsCheckoutVersion}'."));
		}

		var expected = $"actions/checkout@{Standards.LatestActionsCheckoutVersion}";
		return Task.FromResult(Contains(content, expected)
			? Pass($"CI uses {expected}.")
			: Fail(
				$"CI does not use {expected}.",
				$"Update the checkout step to 'uses: {expected}'."));
	}
}

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
				$"Create {ciWorkflowPath} and use 'actions/setup-dotnet@{Standards.LatestActionsSetupDotnetVersion}' with dotnet-version '{Standards.LatestDotNetVersionSpecifier}'."));
		}

		var expectedAction = $"actions/setup-dotnet@{Standards.LatestActionsSetupDotnetVersion}";
		var hasAction = Contains(content, expectedAction);
		var hasSdk = Contains(content, Standards.LatestDotNetVersionSpecifier);

		return Task.FromResult(hasAction && hasSdk
			? Pass($"CI uses {expectedAction} with {Standards.LatestDotNetVersionSpecifier}.")
			: Fail(
				$"CI does not use {expectedAction} with dotnet-version: '{Standards.LatestDotNetVersionSpecifier}'.",
				$"Update to 'uses: {expectedAction}' with 'dotnet-version: {Standards.LatestDotNetVersionSpecifier}'."));
	}
}

/// <summary>
/// Checks that Publish.ps1 exists at the repository root.
/// </summary>
public class PublishScriptExistsRule : RuleBase
{
	/// <inheritdoc />
	public override string RuleId => "CI-07";

	/// <inheritdoc />
	public override string RuleName => "Publish.ps1 exists";

	/// <inheritdoc />
	public override AssessmentCategory Category => AssessmentCategory.CiCd;

	/// <inheritdoc />
	public override AssessmentSeverity Severity => AssessmentSeverity.Warning;

	/// <inheritdoc />
	public override Task<RuleResult> EvaluateAsync(RepositoryContext context, CancellationToken cancellationToken)
	{
		if (!context.Options.IsPackable)
		{
			return Task.FromResult(Pass("Repository is not packable — Publish.ps1 not required."));
		}

		return Task.FromResult(context.FileExists("Publish.ps1")
			? Pass("Publish.ps1 found.")
			: Fail(
				"Publish.ps1 not found at repository root.",
				"Add a Publish.ps1 script that tags from nbgv and pushes the tag to trigger trusted publishing in CI."));
	}
}

/// <summary>
/// Checks that ci.yml matches the Meraki.Api trusted publishing workflow shape.
/// </summary>
public class CiWorkflowMatchesMerakiRule : RuleBase
{
	/// <inheritdoc />
	public override string RuleId => "CI-08";

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
		if (content is null)
		{
			return Task.FromResult(Fail(
				"CI workflow not found.",
				"Copy the standard CI workflow from Meraki.Api and adapt only repository-specific project paths."));
		}

		var requiredSnippets = new[]
		{
			"tags: ['[0-9]*.[0-9]*.[0-9]*']",
			"uses: actions/upload-artifact@v4",
			"publish:",
			"if: startsWith(github.ref, 'refs/tags/')",
			"id-token: write",
			"uses: NuGet/login@v1",
			"dotnet nuget push ./artifacts/*.nupkg --api-key ${{ steps.login.outputs.NUGET_API_KEY }}"
		};

		var missing = requiredSnippets
			.Where(snippet => !Contains(content, snippet))
			.ToList();

		return Task.FromResult(missing.Count == 0
			? Pass("CI workflow matches the Meraki.Api trusted publishing standard.")
			: Fail(
				"CI workflow does not match the Meraki.Api standard trusted publishing shape.",
				$"Ensure ci.yml includes all standard sections; missing snippets include: {string.Join(" | ", missing)}"));
	}
}

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
				"Copy the standard Publish.ps1 from Meraki.Api (clean tree, nbgv version, tag push)."));
		}

		var requiredSnippets = new[]
		{
			"git status --porcelain",
			"git rev-parse --abbrev-ref HEAD",
			"git fetch origin main --quiet",
			"nbgv get-version -f json",
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
				$"Ensure Publish.ps1 contains all standard checks and tag operations; missing snippets include: {string.Join(" | ", missing)}"));
	}
}
