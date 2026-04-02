using System.Xml.Linq;
using PanoramicData.NugetManagement.Models;

namespace PanoramicData.NugetManagement.Rules;

/// <summary>
/// Checks that a .slnx solution file exists in the repository root.
/// </summary>
public class SlnxExistsRule : RuleBase
{
	/// <inheritdoc />
	public override string RuleId => "REPO-04";

	/// <inheritdoc />
	public override string RuleName => ".slnx solution file exists";

	/// <inheritdoc />
	public override AssessmentCategory Category => AssessmentCategory.RepositoryHygiene;

	/// <inheritdoc />
	public override AssessmentSeverity Severity => AssessmentSeverity.Error;

	/// <inheritdoc />
	public override Task<RuleResult> EvaluateAsync(RepositoryContext context, CancellationToken cancellationToken)
	{
		var slnxFiles = context.FindFiles(".slnx")
			.Where(f => !f.Contains('/'))
			.ToList();

		return Task.FromResult(slnxFiles.Count > 0
			? Pass($"Solution file found: {slnxFiles[0]}.")
			: Fail(
				"No .slnx solution file found at repository root.",
				"Create an SDK-style .slnx solution file at the repository root."));
	}
}

/// <summary>
/// Checks that the .slnx solution file contains a Solution Items folder
/// referencing the standard repository root files.
/// </summary>
public class SolutionItemsRule : RuleBase
{
	/// <summary>
	/// Standard files that should appear in the Solution Items folder when they exist in the repo.
	/// </summary>
	private static readonly string[] _expectedSolutionItems =
	[
		".codacy.yml",
		".editorconfig",
		".gitignore",
		"CONTRIBUTING.md",
		"Directory.Build.props",
		"Directory.Packages.props",
		"global.json",
		"LICENSE",
		"Publish.ps1",
		"README.md",
		"SECURITY.md",
		"version.json"
	];

	/// <inheritdoc />
	public override string RuleId => "REPO-05";

	/// <inheritdoc />
	public override string RuleName => "Solution Items folder references standard files";

	/// <inheritdoc />
	public override AssessmentCategory Category => AssessmentCategory.RepositoryHygiene;

	/// <inheritdoc />
	public override AssessmentSeverity Severity => AssessmentSeverity.Warning;

	/// <inheritdoc />
	public override Task<RuleResult> EvaluateAsync(RepositoryContext context, CancellationToken cancellationToken)
	{
		var slnxFile = context.FindFiles(".slnx")
			.Where(f => !f.Contains('/'))
			.FirstOrDefault();

		if (slnxFile is null)
		{
			return Task.FromResult(Fail(
				"No .slnx solution file found — cannot check Solution Items.",
				"Create an SDK-style .slnx solution file at the repository root."));
		}

		var content = context.GetFileContent(slnxFile);
		if (content is null)
		{
			return Task.FromResult(Fail(
				$"Could not read {slnxFile} content.",
				"Ensure the .slnx file is accessible."));
		}

		XDocument doc;
		try
		{
			doc = XDocument.Parse(content);
		}
		catch
		{
			return Task.FromResult(Fail(
				$"{slnxFile} is not valid XML.",
				"Fix the .slnx file to be valid XML."));
		}

		// Find the Solution Items folder
		var solutionItemsFolder = doc.Root?
			.Elements("Folder")
			.FirstOrDefault(f =>
			{
				var name = f.Attribute("Name")?.Value;
				return name is not null &&
					name.Contains("Solution Items", StringComparison.OrdinalIgnoreCase);
			});

		if (solutionItemsFolder is null)
		{
			return Task.FromResult(Fail(
				$"{slnxFile} does not contain a Solution Items folder.",
				"Add a <Folder Name=\"/Solution Items/\"> element to the .slnx file containing standard repo files."));
		}

		// Get the files referenced in Solution Items
		var referencedFiles = solutionItemsFolder
			.Elements("File")
			.Select(f => f.Attribute("Path")?.Value)
			.Where(p => p is not null)
			.ToHashSet(StringComparer.OrdinalIgnoreCase);

		// Check which expected files exist in the repo but are missing from Solution Items
		var missing = _expectedSolutionItems
			.Where(f => context.FileExists(f) && !referencedFiles.Contains(f))
			.ToList();

		if (missing.Count > 0)
		{
			return Task.FromResult(Fail(
				$"Solution Items folder is missing references to: {string.Join(", ", missing)}.",
				$"Add <File Path=\"...\"/> entries for the missing files in the Solution Items folder."));
		}

		return Task.FromResult(Pass("Solution Items folder references all standard repository files."));
	}
}
