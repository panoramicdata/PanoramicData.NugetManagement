using System.Xml.Linq;
using PanoramicData.NugetManagement.Models;

namespace PanoramicData.NugetManagement.Rules;

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
			return Task.FromResult(Problem(
				"No .slnx solution file found — cannot check Solution Items.",
				"Create an SDK-style .slnx solution file at the repository root.",
				"No `.slnx` solution file was found at the repository root. Create one before Solution Items can be checked.",
				new() { ["expected_extension"] = ".slnx" }));
		}

		var content = context.GetFileContent(slnxFile);
		if (content is null)
		{
			return Task.FromResult(Problem(
				$"Could not read {slnxFile} content.",
				"Ensure the .slnx file is accessible.",
				$"The `.slnx` file `{slnxFile}` could not be read. Ensure it is accessible and not corrupted.",
				new() { ["file"] = slnxFile }));
		}

		return Task.FromResult(CheckSolutionItems(context, slnxFile, content));
	}

	private RuleResult CheckSolutionItems(RepositoryContext context, string slnxFile, string content)
	{
		XDocument doc;
		try
		{
			doc = XDocument.Parse(content);
		}
		catch
		{
			return Problem(
				$"{slnxFile} is not valid XML.",
				"Fix the .slnx file to be valid XML.",
				$"The `.slnx` file `{slnxFile}` is not valid XML. Fix the XML syntax.",
				new() { ["file"] = slnxFile });
		}

		var solutionItemsFolder = FindSolutionItemsFolder(doc);

		if (solutionItemsFolder is null)
		{
			return Problem(
				$"{slnxFile} does not contain a Solution Items folder.",
				"Add a <Folder Name=\"/Solution Items/\"> element to the .slnx file containing standard repo files.",
				$"The `.slnx` file `{slnxFile}` does not contain a `Solution Items` folder. Add a `<Folder Name=\"/Solution Items/\">` element with `<File Path=\"...\"/>` entries for standard repository root files.",
				new() { ["file"] = slnxFile });
		}

		var missing = FindMissingReferences(context, solutionItemsFolder);

		return missing.Count == 0
			? Pass("Solution Items folder references all standard repository files.")
			: Problem(
				$"Solution Items folder is missing references to: {string.Join(", ", missing)}.",
				"Add <File Path=\"...\"/> entries for the missing files in the Solution Items folder.",
				$"The Solution Items folder in `{slnxFile}` is missing references to: {string.Join(", ", missing)}. Add `<File Path=\"...\"/>` entries for each missing file.",
				new()
				{
					["file"] = slnxFile,
					["missing_files"] = missing.ToArray(),
					["remediation_type"] = "add_slnx_file_entries"
				});
	}

	private static XElement? FindSolutionItemsFolder(XDocument doc)
		=> doc.Root?
			.Elements("Folder")
			.FirstOrDefault(f => f.Attribute("Name")?.Value is { } name
				&& name.Contains("Solution Items", StringComparison.OrdinalIgnoreCase));

	private static List<string> FindMissingReferences(RepositoryContext context, XElement solutionItemsFolder)
	{
		// Get the files referenced in Solution Items
		var referencedFiles = solutionItemsFolder
			.Elements("File")
			.Select(f => f.Attribute("Path")?.Value)
			.Where(p => p is not null)
			.ToHashSet(StringComparer.OrdinalIgnoreCase);

		// Check which expected files exist in the repo but are missing from Solution Items
		return _expectedSolutionItems
			.Where(f => context.FileExists(f) && !referencedFiles.Contains(f))
			.ToList();
	}

	private RuleResult Problem(string message, string summary, string detail, Dictionary<string, object> data)
		=> Fail(message, new RuleAdvisory { Summary = summary, Detail = detail, Data = data });
}
