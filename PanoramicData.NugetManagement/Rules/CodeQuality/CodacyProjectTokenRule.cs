using PanoramicData.NugetManagement.Models;

namespace PanoramicData.NugetManagement.Rules;

/// <summary>
/// A repository that uploads coverage to Codacy has the project token that upload needs.
/// </summary>
/// <remarks>
/// The third cause of a RED TST-10, and the only one no agent can fix by editing files: a Codacy
/// project token has to be minted against the Codacy API and written to the repository as an Actions
/// secret. The upload step is deliberately continue-on-error, so a missing or expired token costs
/// nothing visible in CI — coverage simply stops arriving, and the repository drifts to RED with
/// every check still green.
/// <para>
/// Tokens expire a year after they are minted, which is the same failure on a delay: everything
/// works, then one day it silently does not.
/// </para>
/// </remarks>
public class CodacyProjectTokenRule : RuleBase
{
	/// <summary>The secret the Codacy coverage reporter reads.</summary>
	private const string SecretName = "CODACY_PROJECT_TOKEN";

	/// <inheritdoc />
	public override string RuleId => "CQ-07";

	/// <inheritdoc />
	public override string RuleName => "Codacy project token is configured";

	/// <inheritdoc />
	public override AssessmentCategory Category => AssessmentCategory.CodeQuality;

	/// <inheritdoc />
	public override AssessmentSeverity Severity => AssessmentSeverity.Warning;

	/// <inheritdoc />
	public override Task<RuleResult> EvaluateAsync(RepositoryContext context, CancellationToken cancellationToken)
	{
		if (!context.FindTestProjectFiles().Any())
		{
			return Task.FromResult(NotApplicable("No test projects found; there is no coverage to upload."));
		}

		// Not established is not the same as absent. Reading an unanswered question as "no token"
		// would fail every repository the moment the assessing token loses the rights this needs.
		if (context.ActionsSecretNames is not { } secrets)
		{
			return Task.FromResult(NotApplicable(
				"This repository's Actions secrets could not be read, so whether a Codacy project token exists is unknown."));
		}

		return Task.FromResult(secrets.Contains(SecretName, StringComparer.OrdinalIgnoreCase)
			? Pass($"{SecretName} is configured.")
			: Fail(
				$"{SecretName} is not configured, so no coverage can reach Codacy.",
				new RuleAdvisory
				{
					Summary = $"Add a Codacy project token as the {SecretName} repository secret.",
					Detail = $$"""
						Without this secret the coverage upload has nothing to authenticate with. The
						step is continue-on-error, so it fails quietly and CI stays green while Codacy
						receives nothing and TST-10 grades this repository RED.

						This cannot be fixed by editing files in the repository. The token is minted
						against the Codacy API and then written to the repository:

						  POST /api/v3/organizations/gh/{org}/repositories/{repo}/tokens
						  gh secret set {{SecretName}} --repo {org}/{repo}

						Note that Codacy repository names are case-sensitive, and that the token it
						returns expires one year later.
						""",
					Data = new()
					{
						["secret_name"] = SecretName,
						["secret_count"] = secrets.Count
					}
				}));
	}
}
