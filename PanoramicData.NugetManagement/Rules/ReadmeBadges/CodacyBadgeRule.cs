using System.Text.RegularExpressions;
using PanoramicData.NugetManagement.Models;
using PanoramicData.NugetManagement.Services;

namespace PanoramicData.NugetManagement.Rules;

/// <summary>
/// Checks that README.md carries the repository's real Codacy grade badge. Private repositories are skipped.
/// </summary>
/// <remarks>
/// A real badge is <c>app.codacy.com/project/badge/Grade/&lt;32 hex project id&gt;</c>. The id is not derivable
/// from the repository name, so the expected badge is read from Codacy when a token is configured;
/// without one the rule can only require a well-formed badge.
/// </remarks>
public partial class CodacyBadgeRule : RuleBase
{
	private readonly ICodacyBadgeService _badgeService;

	/// <summary>
	/// Initializes a new instance of the <see cref="CodacyBadgeRule"/> class.
	/// </summary>
	public CodacyBadgeRule()
		: this(new CodacyBadgeService())
	{
	}

	/// <summary>
	/// Initializes a new instance of the <see cref="CodacyBadgeRule"/> class with an explicit badge service (for testing).
	/// </summary>
	public CodacyBadgeRule(ICodacyBadgeService badgeService)
	{
		_badgeService = badgeService;
	}

	/// <inheritdoc />
	public override string RuleId => "README-02";

	/// <inheritdoc />
	public override string RuleName => "Codacy badge in README";

	/// <inheritdoc />
	public override AssessmentCategory Category => AssessmentCategory.ReadmeBadges;

	/// <inheritdoc />
	public override AssessmentSeverity Severity => AssessmentSeverity.Warning;

	/// <inheritdoc />
	public override async Task<RuleResult> EvaluateAsync(RepositoryContext context, CancellationToken cancellationToken)
	{
		// A private repository's Codacy dashboard is not public, so the badge is meaningless.
		if (context.IsPrivateRepository == true)
		{
			return NotApplicable("The repository is private, so its Codacy dashboard is not public.");
		}

		var readme = context.GetFileContent("README.md") ?? string.Empty;
		var badgeUrls = BadgeUrl().Matches(readme).Select(m => m.Value).Distinct().ToList();
		var info = await LookUpAsync(context, cancellationToken).ConfigureAwait(false);

		if (info is { IsAdded: false })
		{
			return Fail(
				"The repository is not enrolled in Codacy, so there is no grade badge to show.",
				new RuleAdvisory
				{
					Summary = "Add the repository to Codacy, then add its grade badge to README.md.",
					Detail = "Codacy holds no repository under this name, so any Codacy badge in `README.md` would be dead. Add the repository in Codacy first; the badge can then be added."
				});
		}

		return info?.GradeBadgeUrl is { Length: > 0 } expected
			? EvaluateAgainst(context, badgeUrls, expected)
			: EvaluateFormatOnly(badgeUrls);
	}

	private async Task<CodacyBadgeInfo?> LookUpAsync(RepositoryContext context, CancellationToken cancellationToken)
	{
		var token = context.Options.Codacy?.ApiToken;
		var parts = context.FullName.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
		if (string.IsNullOrWhiteSpace(token) || parts.Length != 2)
		{
			return null;
		}

		try
		{
			return await _badgeService.GetBadgeAsync(token, parts[0], parts[1], cancellationToken).ConfigureAwait(false);
		}
		catch (Exception ex) when (ex is not OperationCanceledException)
		{
			// Codacy being unreachable says nothing about the README, so fall back to checking its shape.
			return null;
		}
	}

	private RuleResult EvaluateAgainst(RepositoryContext context, List<string> badgeUrls, string expected)
	{
		if (badgeUrls.Contains(expected, StringComparer.Ordinal))
		{
			return Pass("README.md carries the Codacy grade badge Codacy reports for this repository.");
		}

		var orgName = context.FullName.Split('/')[0];
		var wrong = badgeUrls.FirstOrDefault();
		if (wrong is not null)
		{
			return Fail(
				$"README.md has a Codacy badge ({wrong}) that is not the one Codacy reports for this repository.",
				new RuleAdvisory
				{
					Summary = "Replace the Codacy badge in README.md with the one Codacy reports for this repository.",
					Detail = $"The Codacy badge in `README.md` does not resolve to this repository's project. Use `{expected}`.",
					Data = new()
					{
						["file"] = "README.md",
						["remediation_type"] = "replace_in_file",
						["old_text"] = wrong,
						["new_text"] = expected
					}
				});
		}

		return Fail(
			"README.md does not contain the Codacy grade badge.",
			new RuleAdvisory
			{
				Summary = "Add the Codacy grade badge at the top of README.md.",
				Detail = $"The `README.md` does not contain a Codacy grade badge. Add `{expected}` at the top of the file.",
				Data = new()
				{
					["file"] = "README.md",
					["remediation_type"] = "prepend_line",
					["line_content"] = $"[![Codacy Badge]({expected})](https://app.codacy.com/gh/{orgName}/{context.Name}/dashboard)"
				}
			});
	}

	private RuleResult EvaluateFormatOnly(List<string> badgeUrls)
	{
		if (badgeUrls.Any(url => RealBadgeUrl().IsMatch(url)))
		{
			return Pass("README.md contains a Codacy grade badge (its project id was not checked against Codacy).");
		}

		return Fail(
			badgeUrls.Count > 0
				? "README.md has a Codacy badge, but not a real one: the project id is not a 32 character hex id."
				: "README.md does not contain a Codacy grade badge.",
			new RuleAdvisory
			{
				Summary = "Add the repository's real Codacy grade badge to README.md.",
				Detail = "A real badge looks like `https://app.codacy.com/project/badge/Grade/<32 hex project id>`. The id cannot be derived from the repository name: copy the badge from the repository's Badges settings in Codacy, or run the assessment with a Codacy token so the rule can supply it."
			});
	}

	[GeneratedRegex(@"https://app\.codacy\.com/project/badge/Grade/[^)\s""'\]]+", RegexOptions.IgnoreCase)]
	private static partial Regex BadgeUrl();

	[GeneratedRegex(@"^https://app\.codacy\.com/project/badge/Grade/[0-9a-f]{32}$")]
	private static partial Regex RealBadgeUrl();
}
