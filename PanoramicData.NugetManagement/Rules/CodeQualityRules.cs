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
				new RuleAdvisory
				{
					Summary = "Create an .editorconfig file with root = true and standard C# formatting rules.",
					Detail = "Create a `.editorconfig` file at the repository root with `root = true` at the top, followed by standard C# formatting rules.",
					Data = new()
					{
						["expected_path"] = ".editorconfig",
						["template_content"] = Standards.EditorConfigContent
					}
				}));
		}

		return Task.FromResult(Contains(content, "root = true")
			? Pass(".editorconfig found with root = true.")
			: Fail(
				".editorconfig does not contain 'root = true'.",
				new RuleAdvisory
				{
					Summary = "Add 'root = true' at the top of .editorconfig.",
					Detail = "The `.editorconfig` file exists but is missing `root = true`. Add this as the first non-comment line to prevent editors from searching parent directories.",
					Data = new()
					{
						["file"] = ".editorconfig",
						["remediation_type"] = "prepend_line",
						["line_content"] = "root = true"
					}
				}));
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
				new RuleAdvisory
				{
					Summary = "Create an .editorconfig file and set csharp_style_namespace_declarations = file_scoped:error.",
					Detail = "Create a `.editorconfig` file at the repository root and add `csharp_style_namespace_declarations = file_scoped:error` in the `[*.cs]` section.",
					Data = new()
					{
						["file"] = ".editorconfig",
						["remediation_type"] = "append_line",
						["line_content"] = "csharp_style_namespace_declarations = file_scoped:error"
					}
				}));
		}

		return Task.FromResult(Contains(content, "csharp_style_namespace_declarations = file_scoped")
			? Pass("File-scoped namespaces are enforced in .editorconfig.")
			: Fail(
				".editorconfig does not enforce file-scoped namespaces.",
				new RuleAdvisory
				{
					Summary = "Add 'csharp_style_namespace_declarations = file_scoped:error' to .editorconfig.",
					Detail = "The `.editorconfig` file does not enforce file-scoped namespaces. Add `csharp_style_namespace_declarations = file_scoped:error` to the `[*.cs]` section.",
					Data = new()
					{
						["file"] = ".editorconfig",
						["remediation_type"] = "append_line",
						["line_content"] = "csharp_style_namespace_declarations = file_scoped:error"
					}
				}));
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
				new RuleAdvisory
				{
					Summary = "Set up Codacy integration and add .codacy/cli.sh or .codacy.yml.",
					Detail = "No Codacy configuration was found. Set up Codacy integration at app.codacy.com and add a `.codacy.yml` or `.codacy/cli.sh` file to the repository root.",
					Data = new() { ["expected_files"] = new[] { ".codacy.yml", ".codacy.yaml", ".codacy/cli.sh" } }
				});
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
				new RuleAdvisory
				{
					Summary = "Set RepoOptions.Codacy.ApiToken to a valid Codacy API token.",
					Detail = "Codacy options were provided but the `ApiToken` is empty. Set `RepoOptions.Codacy.ApiToken` to a valid Codacy API token to enable API-based quality evaluation.",
					Data = new() { ["missing_config"] = "Codacy.ApiToken" }
				});
		}

		var parts = context.FullName.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
		if (parts.Length != 2)
		{
			return Fail(
				$"Repository full name '{context.FullName}' is invalid for Codacy provider lookup.",
				new RuleAdvisory
				{
					Summary = "Set RepositoryContext.FullName to 'organization/repository'.",
					Detail = $"The repository full name `{context.FullName}` could not be split into organization/repository. Ensure `RepositoryContext.FullName` is in `owner/repo` format.",
					Data = new() { ["full_name"] = context.FullName }
				});
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
					new RuleAdvisory
					{
						Summary = "Ensure Codacy analysis has run for the default branch before enforcing Codacy thresholds.",
						Detail = "The Codacy API returned no file quality data. Run a Codacy analysis on the default branch before enforcing quality thresholds.",
						Data = new() { ["default_branch"] = context.DefaultBranch }
					});
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
				new RuleAdvisory
				{
					Summary = $"Improve repository quality until minimum file grade is {codacy.MinimumLevel} or better and total issues are <= {codacy.MaxIssueCount}.",
					Detail = $"Codacy quality gate failed. Current minimum file grade is `{actualMinimum}` (required: `{codacy.MinimumLevel}` or better). Total issues: `{issueCount}` (maximum allowed: `{codacy.MaxIssueCount}`).",
					Data = new()
					{
						["actual_min_grade"] = actualMinimum.ToString(),
						["required_min_grade"] = codacy.MinimumLevel.ToString(),
						["actual_issues"] = issueCount,
						["max_issues"] = codacy.MaxIssueCount
					}
				});
		}
		catch (Exception ex)
		{
			return Fail(
				$"Failed to evaluate Codacy quality gate via Codacy API: {ex.Message}",
				new RuleAdvisory
				{
					Summary = "Verify Codacy token validity and repository/provider mapping (GitHub provider).",
					Detail = $"An exception occurred when calling the Codacy API: `{ex.Message}`. Verify that the API token is valid and that the repository is correctly mapped to the GitHub provider.",
					Data = new() { ["exception"] = ex.Message }
				});
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

/// <summary>
/// Checks that .editorconfig enforces tab indentation for C# and XML files.
/// </summary>
public class TabIndentationRule : RuleBase
{
	/// <inheritdoc />
	public override string RuleId => "CQ-04";

	/// <inheritdoc />
	public override string RuleName => "Tab indentation enforced for C# and XML";

	/// <inheritdoc />
	public override AssessmentCategory Category => AssessmentCategory.CodeQuality;

	/// <inheritdoc />
	public override AssessmentSeverity Severity => AssessmentSeverity.Warning;

	/// <inheritdoc />
	public override Task<RuleResult> EvaluateAsync(RepositoryContext context, CancellationToken cancellationToken)
	{
		var content = context.GetFileContent(".editorconfig");
		if (content is null)
		{
			return Task.FromResult(Fail(
				".editorconfig not found.",
				new RuleAdvisory
				{
					Summary = "Create an .editorconfig with indent_style = tab for C# and XML files.",
					Detail = "Create a `.editorconfig` file at the repository root with `indent_style = tab` in the `[*]` section to enforce tab indentation for C# and XML files.",
					Data = new() { ["file"] = ".editorconfig" }
				}));
		}

		// Check that the global [*] section uses tabs
		var hasGlobalTab = Contains(content, "indent_style = tab");
		if (!hasGlobalTab)
		{
			return Task.FromResult(Fail(
				".editorconfig does not set indent_style = tab.",
				new RuleAdvisory
				{
					Summary = "Set indent_style = tab in the [*] section of .editorconfig.",
					Detail = "The `.editorconfig` file exists but does not set `indent_style = tab`. Add this setting in the `[*]` section.",
					Data = new() { ["file"] = ".editorconfig" }
				}));
		}

		// Check there is no override to spaces for C# or XML sections
		// We look for indent_style = space after a [*.cs] or [*.{xml,...}] section header
		var lines = content.Split('\n');
		var currentSection = "";
		foreach (var rawLine in lines)
		{
			var line = rawLine.Trim();
			if (line.StartsWith('[') && line.EndsWith(']'))
			{
				currentSection = line;
				continue;
			}

			if (line.Equals("indent_style = space", StringComparison.OrdinalIgnoreCase) &&
				IsCSharpOrXmlSection(currentSection))
			{
				return Task.FromResult(Fail(
					$".editorconfig overrides indent_style to space in section {currentSection}.",
					new RuleAdvisory
					{
						Summary = $"Change indent_style = space to indent_style = tab in the {currentSection} section.",
						Detail = $"The `.editorconfig` section `{currentSection}` overrides `indent_style` to `space`. Change it to `indent_style = tab`.",
						Data = new() { ["file"] = ".editorconfig", ["section"] = currentSection }
					}));
			}
		}

		return Task.FromResult(Pass("Tab indentation is enforced for C# and XML files."));
	}

	private static bool IsCSharpOrXmlSection(string sectionHeader)
	{
		if (string.IsNullOrEmpty(sectionHeader))
		{
			return false;
		}

		var inner = sectionHeader.TrimStart('[').TrimEnd(']');
		return inner.Contains("*.cs", StringComparison.OrdinalIgnoreCase) ||
			inner.Contains("*.xml", StringComparison.OrdinalIgnoreCase) ||
			inner.Contains("*.csproj", StringComparison.OrdinalIgnoreCase) ||
			inner.Contains("*.props", StringComparison.OrdinalIgnoreCase) ||
			inner.Contains("*.targets", StringComparison.OrdinalIgnoreCase);
	}
}
