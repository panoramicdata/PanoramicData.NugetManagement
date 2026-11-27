using PanoramicData.NugetManagement.Models;

namespace PanoramicData.NugetManagement.Rules;

/// <summary>
/// Checks that a public repository shows the Sponsor button, which the organisation-wide FUNDING.yml
/// in panoramicdata/.github fills in only when the repository's Sponsorships setting is on.
/// </summary>
public class SponsorshipsEnabledRule : RuleBase
{
	/// <inheritdoc />
	public override string RuleId => "COM-06";

	/// <inheritdoc />
	public override string RuleName => "Sponsor button enabled";

	/// <inheritdoc />
	public override AssessmentCategory Category => AssessmentCategory.CommunityHealth;

	/// <inheritdoc />
	public override AssessmentSeverity Severity => AssessmentSeverity.Warning;

	/// <inheritdoc />
	public override Task<RuleResult> EvaluateAsync(RepositoryContext context, CancellationToken cancellationToken)
	{
		if (context.IsPrivateRepository is not false)
		{
			return Task.FromResult(NotApplicable("A Sponsor button is shown only on public repositories."));
		}

		if (context.SponsorshipsEnabled is not { } enabled)
		{
			return Task.FromResult(NotApplicable("The setting could not be read, so it is neither reported nor assumed off."));
		}

		return Task.FromResult(enabled
			? Pass("The Sponsorships setting is on, so the Sponsor button is shown.")
			: Fail(
				"The Sponsorships setting is off, so the organisation FUNDING.yml shows no Sponsor button.",
				new RuleAdvisory
				{
					Summary = "Switch on the repository's Sponsorships setting.",
					Detail = "Switch on Sponsorships, which needs admin rights on the repository. Only GraphQL exposes it:\n\n"
						+ $"```\nid=$(gh api repos/{context.FullName} --jq .node_id)\n"
						+ "gh api graphql -f query='mutation($id:ID!){updateRepository(input:{repositoryId:$id,hasSponsorshipsEnabled:true}){repository{hasSponsorshipsEnabled}}}' -f id=$id\n```\n\n"
						+ "Or: Settings → General → Features → Sponsorships. "
						+ "The funding links come from FUNDING.yml in panoramicdata/.github, so there is nothing to commit.",
					Data = new()
					{
						["repository"] = context.FullName
					}
				}));
	}
}
