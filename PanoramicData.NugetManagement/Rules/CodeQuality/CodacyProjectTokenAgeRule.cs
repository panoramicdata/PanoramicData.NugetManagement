using System.Globalization;
using PanoramicData.NugetManagement.Models;

namespace PanoramicData.NugetManagement.Rules;

/// <summary>
/// Warns before the repository's Codacy project token expires.
/// </summary>
/// <remarks>
/// Codacy project tokens last a year, and the coverage upload that uses one is continue-on-error, so an
/// expired token is silent: CI stays green while coverage stops arriving. GitHub exposes only when a
/// secret was last updated, never its value, so the age of the secret stands in for the token's age.
/// There is deliberately no automatic remediation: minting a token needs the Codacy account credential.
/// </remarks>
public class CodacyProjectTokenAgeRule : RuleBase
{
	/// <summary>Days after which the token is reported, about eleven months of its one-year life.</summary>
	public static int DefaultWarnAfterDays => 335;

	private const string SecretName = "CODACY_PROJECT_TOKEN";

	private readonly TimeProvider _timeProvider;

	/// <summary>
	/// Initializes a new instance of the <see cref="CodacyProjectTokenAgeRule"/> class.
	/// </summary>
	public CodacyProjectTokenAgeRule()
		: this(TimeProvider.System)
	{
	}

	/// <summary>
	/// Initializes a new instance of the <see cref="CodacyProjectTokenAgeRule"/> class.
	/// </summary>
	/// <param name="timeProvider">Clock that decides the token's age.</param>
	public CodacyProjectTokenAgeRule(TimeProvider timeProvider)
	{
		_timeProvider = timeProvider;
	}

	/// <inheritdoc />
	public override string RuleId => "CQ-09";

	/// <inheritdoc />
	public override string RuleName => "Codacy project token is not near expiry";

	/// <inheritdoc />
	public override AssessmentCategory Category => AssessmentCategory.CodeQuality;

	/// <inheritdoc />
	public override AssessmentSeverity Severity => AssessmentSeverity.Warning;

	/// <inheritdoc />
	public override Task<RuleResult> EvaluateAsync(RepositoryContext context, CancellationToken cancellationToken)
	{
		if (NoCoverageToMeasure(context, "no coverage to upload") is { } notApplicable)
		{
			return Task.FromResult(notApplicable);
		}

		if (context.ActionsSecretUpdatedAt is not { } updatedAt)
		{
			return Task.FromResult(NotApplicable(
				"This repository's Actions secret dates could not be read, so the Codacy project token's age is unknown."));
		}

		var match = updatedAt.FirstOrDefault(entry => string.Equals(entry.Key, SecretName, StringComparison.OrdinalIgnoreCase));

		if (match.Key is null)
		{
			return Task.FromResult(NotApplicable(
				$"{SecretName} is not configured, so there is no token to expire (see CQ-07)."));
		}

		var lastUpdated = match.Value;
		var threshold = context.Options.Codacy?.TokenWarnAfterDays ?? DefaultWarnAfterDays;
		var ageDays = (int)(_timeProvider.GetUtcNow() - lastUpdated).TotalDays;

		if (ageDays <= threshold)
		{
			return Task.FromResult(Pass(
				$"{SecretName} is {ageDays.ToString(CultureInfo.InvariantCulture)} days old, inside the {threshold.ToString(CultureInfo.InvariantCulture)} day limit."));
		}

		var age = ageDays.ToString(CultureInfo.InvariantCulture);

		return Task.FromResult(Fail(
			ageDays >= 365
				? $"{SecretName} is {age} days old and has probably expired, so coverage may no longer reach Codacy."
				: $"{SecretName} is {age} days old and Codacy project tokens expire after a year.",
			Advisory(ageDays, threshold)));
	}

	private static RuleAdvisory Advisory(int ageDays, int threshold)
		=> new()
		{
			Summary = $"Replace the {SecretName} repository secret before the Codacy token expires.",
			Detail = $$"""
				Codacy project tokens last one year. The coverage upload is continue-on-error, so an
				expired token fails quietly: CI stays green while Codacy receives nothing and TST-10
				drifts to RED.

				The {{SecretName}} secret's age is used as the token's age, since GitHub never returns its value.
				Replacing the token is manual and needs the Codacy account credential, so no fix is
				applied automatically. Mint a new token and write it to the secret with the codacy skill:

				  dotnet ../PanoramicData.Skills/.github/skills/codacy/Codacy.cs create-repo-token --repo {org}/{repo} --set-github-secret

				Re-assess afterwards: setting the secret resets this age.
				""",
			Data = new()
			{
				["secret_name"] = SecretName,
				["age_days"] = ageDays,
				["warn_after_days"] = threshold
			}
		};
}