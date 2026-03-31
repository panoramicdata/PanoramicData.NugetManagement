using Codacy.Api;
using Codacy.Api.Models;
using PanoramicData.NugetManagement.Models;

namespace PanoramicData.NugetManagement.Rules;

/// <summary>
/// Checks that .editorconfig exists at the repository root.
/// </summary>
public class EditorConfigExistsRule : RuleBase
{
	/// <inheritdoc />
	public override string RuleId => "CQ-01";

	/// <inheritdoc />
	public override string RuleName => ".editorconfig exists";

	/// <inheritdoc />
	public override AssessmentCategory Category => AssessmentCategory.CodeQuality;

	/// <inheritdoc />
	public override AssessmentSeverity Severity => AssessmentSeverity.Error;

	/// <inheritdoc />
	public override Task<RuleResult> EvaluateAsync(RepositoryContext context, CancellationToken cancellationToken)
	{
		var content = context.GetFileContent(".editorconfig");
		if (content is null)
		{
			return Task.FromResult(Fail(
				".editorconfig not found at repository root.",
				"Create an .editorconfig file with root = true and standard C# formatting rules."));
		}

		return Task.FromResult(Contains(content, "root = true")
			? Pass(".editorconfig found with root = true.")
			: Fail(
				".editorconfig does not contain 'root = true'.",
				"Add 'root = true' at the top of .editorconfig."));
	}
}

/// <summary>
/// Checks that file-scoped namespaces are enforced in .editorconfig.
/// </summary>
public class FileScopedNamespacesRule : RuleBase
{
	/// <inheritdoc />
	public override string RuleId => "CQ-02";

	/// <inheritdoc />
	public override string RuleName => "File-scoped namespaces enforced";

	/// <inheritdoc />
	public override AssessmentCategory Category => AssessmentCategory.CodeQuality;

	/// <inheritdoc />
	public override AssessmentSeverity Severity => AssessmentSeverity.Info;

	/// <inheritdoc />
	public override Task<RuleResult> EvaluateAsync(RepositoryContext context, CancellationToken cancellationToken)
	{
		var content = context.GetFileContent(".editorconfig");
		if (content is null)
		{
			return Task.FromResult(Fail(
				".editorconfig not found.",
				"Create an .editorconfig file and set csharp_style_namespace_declarations = file_scoped:error."));
		}

		return Task.FromResult(Contains(content, "csharp_style_namespace_declarations = file_scoped")
			? Pass("File-scoped namespaces are enforced in .editorconfig.")
			: Fail(
				".editorconfig does not enforce file-scoped namespaces.",
				"Add 'csharp_style_namespace_declarations = file_scoped:error' to .editorconfig."));
	}
}

/// <summary>
/// Checks that Codacy is configured (.codacy directory or integration).
/// </summary>
public class CodacyConfiguredRule : RuleBase
{
	/// <inheritdoc />
	public override string RuleId => "CQ-03";

	/// <inheritdoc />
	public override string RuleName => "Codacy configured";

	/// <inheritdoc />
	public override AssessmentCategory Category => AssessmentCategory.CodeQuality;

	/// <inheritdoc />
	public override AssessmentSeverity Severity => AssessmentSeverity.Warning;

	/// <inheritdoc />
	public override async Task<RuleResult> EvaluateAsync(RepositoryContext context, CancellationToken cancellationToken)
	{
		if (context.Options.Codacy is not null)
		{
			return await EvaluateUsingCodacyApiAsync(context, context.Options.Codacy, cancellationToken).ConfigureAwait(false);
		}

		var hasCodacyDir = context.FilePaths.Any(p =>
			p.StartsWith(".codacy/", StringComparison.OrdinalIgnoreCase));
		var hasCodacyYml = context.FileExists(".codacy.yml") || context.FileExists(".codacy.yaml");

		return hasCodacyDir || hasCodacyYml
			? Pass("Codacy is configured.")
			: Fail(
				"No Codacy configuration found (.codacy/ directory or .codacy.yml).",
				"Set up Codacy integration and add .codacy/cli.sh or .codacy.yml.");
	}

	private async Task<RuleResult> EvaluateUsingCodacyApiAsync(
		RepositoryContext context,
		CodacyOptions codacy,
		CancellationToken cancellationToken)
	{
		if (string.IsNullOrWhiteSpace(codacy.ApiToken))
		{
			return Fail(
				"Codacy options are present but ApiToken is missing.",
				"Set RepoOptions.Codacy.ApiToken to a valid Codacy API token.");
		}

		var parts = context.FullName.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
		if (parts.Length != 2)
		{
			return Fail(
				$"Repository full name '{context.FullName}' is invalid for Codacy provider lookup.",
				"Set RepositoryContext.FullName to 'organization/repository'.");
		}

		var organizationName = parts[0];
		var repositoryName = parts[1];

		try
		{
			var client = new CodacyClient(new CodacyClientOptions
			{
				ApiToken = codacy.ApiToken
			});

			var overview = await client.Issues.GetIssuesOverviewAsync(
				Provider.Github,
				organizationName,
				repositoryName,
				new SearchRepositoryIssuesBody(),
				cancellationToken).ConfigureAwait(false);

			var issueCount = overview?.Data?.Counts?.Levels?.Sum(level => level.Total) ?? 0;

			var files = await client.Repositories.ListFilesAsync(
				Provider.Github,
				organizationName,
				repositoryName,
				context.DefaultBranch,
				null,
				null,
				null,
				null,
				500,
				cancellationToken).ConfigureAwait(false);

			var levels = files.Data
				.Select(file => TryParseCodacyLevel(file.GradeLetter, out var level) ? level : CodacyLevel.F)
				.ToList();

			if (levels.Count == 0)
			{
				return Fail(
					"Codacy API returned no file quality data for the repository.",
					"Ensure Codacy analysis has run for the default branch before enforcing Codacy thresholds.");
			}

			var actualMinimum = levels.MinBy(GetLevelRank);
			var meetsLevel = GetLevelRank(actualMinimum) >= GetLevelRank(codacy.MinimumLevel);
			var meetsIssueCount = issueCount <= codacy.MaxIssueCount;

			if (meetsLevel && meetsIssueCount)
			{
				return Pass($"Codacy checks passed (minimum file grade {actualMinimum}, total issues {issueCount}).");
			}

			return Fail(
				$"Codacy checks failed (minimum file grade {actualMinimum}, total issues {issueCount}).",
				$"Improve repository quality until minimum file grade is {codacy.MinimumLevel} or better and total issues are <= {codacy.MaxIssueCount}.");
		}
		catch (Exception ex)
		{
			return Fail(
				$"Failed to evaluate Codacy quality gate via Codacy API: {ex.Message}",
				"Verify Codacy token validity and repository/provider mapping (GitHub provider)." );
		}
	}

	private static bool TryParseCodacyLevel(string? gradeLetter, out CodacyLevel level)
	{
		level = CodacyLevel.F;
		if (string.IsNullOrWhiteSpace(gradeLetter))
		{
			return false;
		}

		return Enum.TryParse(gradeLetter.Trim(), ignoreCase: true, out level);
	}

	private static int GetLevelRank(CodacyLevel level)
		=> level switch
		{
			CodacyLevel.A => 6,
			CodacyLevel.B => 5,
			CodacyLevel.C => 4,
			CodacyLevel.D => 3,
			CodacyLevel.E => 2,
			_ => 1
		};
}
