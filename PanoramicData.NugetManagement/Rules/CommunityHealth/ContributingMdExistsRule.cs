using PanoramicData.NugetManagement.Models;

namespace PanoramicData.NugetManagement.Rules;

/// <summary>
/// Checks that CONTRIBUTING.md exists and is the standard one.
/// </summary>
/// <remarks>
/// Held to the standard text, not just to existing, so the whole estate tells contributors the same
/// thing and CLAUDE.md can refer to the file knowing it is there. A repository with a genuine reason
/// to differ waives the rule rather than drifting. The licence section is the exception: it may carry
/// notes after the standard sentence, such as the terms of inherited code, and the fix keeps them.
/// </remarks>
public class ContributingMdExistsRule : RuleBase
{
	/// <inheritdoc />
	public override string RuleId => "COM-02";

	/// <inheritdoc />
	public override string RuleName => "CONTRIBUTING.md matches the standard";

	/// <inheritdoc />
	public override AssessmentCategory Category => AssessmentCategory.CommunityHealth;

	/// <inheritdoc />
	public override AssessmentSeverity Severity => AssessmentSeverity.Warning;

	/// <inheritdoc />
	public override Task<RuleResult> EvaluateAsync(RepositoryContext context, CancellationToken cancellationToken)
	{
		var license = context.Options.ExpectedLicense;
		var standard = Standards.ContributingMdContentFor(license);
		if (!context.FileExists("CONTRIBUTING.md"))
		{
			return Task.FromResult(Fail(
				"CONTRIBUTING.md not found at repository root.",
				new RuleAdvisory
				{
					Summary = "Create CONTRIBUTING.md with the standard contributing guide",
					Detail = "Create a `CONTRIBUTING.md` file at the repository root with the standard contributing guide.",
					Data = new()
					{
						["expected_path"] = "CONTRIBUTING.md",
						["template_content"] = standard,
						["expected_license"] = license
					}
				}));
		}

		var content = context.GetFileContent("CONTRIBUTING.md");
		if (content is null || Standards.IsGeneratedContent(content, standard))
		{
			return Task.FromResult(Pass("CONTRIBUTING.md matches the standard."));
		}

		var (preamble, section) = Split(content);
		var standardSection = Split(standard).Section!;
		var keepSection = section is not null && IsAcceptableLicenseSection(section, standardSection, license);
		if (keepSection && preamble.Trim() == Standards.ContributingMdPreamble().Trim())
		{
			return Task.FromResult(Pass("CONTRIBUTING.md matches the standard, with a licence note."));
		}

		var replacement = Standards.WithContributingLineEndings(
			$"{Standards.ContributingMdPreamble()}{Standards.ContributingLicenseHeading}\n\n{(keepSection ? section : standardSection)}");
		return Task.FromResult(Fail(
			"CONTRIBUTING.md differs from the standard contributing guide.",
			new RuleAdvisory
			{
				Summary = "Replace CONTRIBUTING.md with the standard contributing guide",
				Detail = "Every repository carries the same CONTRIBUTING.md; only notes after the sentence in its License section may differ. Replace this one with the standard text, keeping any such notes, or waive COM-02 for this repository if it genuinely needs its own.",
				Data = new()
				{
					["remediation_type"] = "replace_file_content",
					["file"] = "CONTRIBUTING.md",
					["new_content"] = replacement,
					["expected_license"] = license
				}
			}));
	}

	/// <summary>
	/// The text before the licence heading, and the licence section's body, or null when there is none.
	/// </summary>
	private static (string Preamble, string? Section) Split(string content)
	{
		var normalised = content.Replace("\r\n", "\n", StringComparison.Ordinal);
		var heading = $"\n{Standards.ContributingLicenseHeading}\n";
		var index = normalised.IndexOf(heading, StringComparison.Ordinal);
		return index < 0
			? (normalised, null)
			: (normalised[..(index + 1)], normalised[(index + heading.Length)..].Trim());
	}

	/// <summary>
	/// Whether a licence section may stay: no further headings, and for the default licence the standard
	/// sentence first. Under any other licence the bare MIT sentence is the one thing that cannot stay.
	/// </summary>
	private static bool IsAcceptableLicenseSection(string section, string standardSection, string license)
	{
		if (section.Length == 0 || section.Split('\n').Any(line => line.TrimStart().StartsWith('#')))
		{
			return false;
		}

		var defaultSection = Split(Standards.ContributingMdContent).Section!;
		return LicenseExpressions.IsDefault(license)
			? section.StartsWith(standardSection, StringComparison.Ordinal)
			: section != defaultSection;
	}
}
