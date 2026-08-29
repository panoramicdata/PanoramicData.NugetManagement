using Codacy.Api;
using Codacy.Api.Models;
using PanoramicData.NugetManagement.Models;

namespace PanoramicData.NugetManagement.Rules;

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

	/// <summary>
	/// The largest page Codacy's file listing accepts. Anything above this is a 400.
	/// </summary>
	private const int _codacyMaximumPageSize = 100;

	/// <inheritdoc />
	public override async Task<RuleResult> EvaluateAsync(RepositoryContext context, CancellationToken cancellationToken)
	{
		if (context.Options.Codacy is not null)
		{
			return await EvaluateUsingCodacyApiAsync(context, context.Options.Codacy, cancellationToken).ConfigureAwait(false);
		}

		return HasLocalCodacyEvidence(context)
			? Pass("Codacy is configured.")
			: Fail(
				"No Codacy configuration found (.codacy/ directory, .codacy.yml, or Codacy badge in README).",
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

			var levels = new List<CodacyLevel>();
			string? cursor = null;

			// Codacy rejects a page size above 100, so the worst file in a large repository is only
			// reachable by paging. Asking for 500 in one go returned 400, and the whole gate fell into
			// the catch below and reported itself unevaluated.
			do
			{
				var page = await client.Repositories.ListFilesAsync(
					Provider.Github,
					organizationName,
					repositoryName,
					context.DefaultBranch,
					null,
					null,
					null,
					cursor,
					_codacyMaximumPageSize,
					cancellationToken).ConfigureAwait(false);

				levels.AddRange(CollectGradedLevels(page.Data.Select(file => file.GradeLetter)));

				cursor = page.Pagination?.Cursor;
			}
			while (!string.IsNullOrEmpty(cursor));

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
			// An unreachable API leaves the quality gate unknown, not met. Local evidence — a
			// .codacy.yml, or a badge in the README — says somebody once set Codacy up. It says nothing
			// about the code's quality today, and reporting it as a pass hid a token that could not see
			// a single repository: 69 repositories showed green, every one on the strength of a badge,
			// so the split between pass and fail was by README content rather than by Codacy.
			if (HasLocalCodacyEvidence(context))
			{
				return NotApplicable(
					$"Codacy is configured locally, but the quality gate could not be evaluated: {ex.Message}");
			}

			return Fail(
				$"Failed to evaluate Codacy quality gate via Codacy API: {ex.Message}",
				new RuleAdvisory
				{
					Summary = "Verify Codacy token validity and repository/provider mapping (GitHub provider).",
					Detail = $"An exception occurred when calling the Codacy API: `{ex.Message}`. Verify that the API token is valid and that the repository is correctly mapped to the GitHub provider. Alternatively, add a `.codacy.yml` file or Codacy badge to the README.",
					Data = new() { ["exception"] = ex.Message }
				});
		}
	}

	/// <summary>
	/// Checks for local evidence of Codacy integration: config files
	/// (.codacy.yml, .codacy.yaml, .codacy/ directory) or a Codacy badge in README.md.
	/// </summary>
	private static bool HasLocalCodacyEvidence(RepositoryContext context)
	{
		if (context.FilePaths.Any(p =>
			p.StartsWith(".codacy/", StringComparison.OrdinalIgnoreCase)))
		{
			return true;
		}

		if (context.FileExists(".codacy.yml") || context.FileExists(".codacy.yaml"))
		{
			return true;
		}

		var readme = context.GetFileContent("README.md");
		return readme is not null && Contains(readme, "codacy");
	}

	/// <summary>
	/// Turns a page of Codacy grade letters into the levels the gate should judge, dropping the files
	/// Codacy never graded.
	/// </summary>
	/// <remarks>
	/// Codacy's file listing returns every file on the branch, not just the
	/// analysed ones, and Codacy only grades what it analyses: markdown, JSON, images and the solution
	/// file come back with no grade letter at all. Folding that absence into <see cref="CodacyLevel.F"/>
	/// made the worst file in every repository an F, so CQ-03 failed repositories that had zero issues
	/// and told them to fix a grade they never had.
	/// </remarks>
	internal static List<CodacyLevel> CollectGradedLevels(IEnumerable<string?> gradeLetters)
	{
		var levels = new List<CodacyLevel>();

		foreach (var gradeLetter in gradeLetters)
		{
			// No letter means "not analysed", which is not a grade and must not become one.
			if (string.IsNullOrWhiteSpace(gradeLetter))
			{
				continue;
			}

			// A letter we cannot parse is a grade we do not understand rather than a good one, so it
			// stays conservative and the gate errs towards failing rather than towards silence.
			levels.Add(Enum.TryParse<CodacyLevel>(gradeLetter.Trim(), ignoreCase: true, out var level)
				? level
				: CodacyLevel.F);
		}

		return levels;
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
