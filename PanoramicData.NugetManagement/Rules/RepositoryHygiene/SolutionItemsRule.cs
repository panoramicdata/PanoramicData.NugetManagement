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
			return Task.FromResult(Fail(
				"No .slnx solution file found — cannot check Solution Items.",
				new RuleAdvisory
				{
					Summary = "Create an SDK-style .slnx solution file at the repository root.",
					Detail = "No `.slnx` solution file was found at the repository root. Create one before Solution Items can be checked.",
					Data = new() { ["expected_extension"] = ".slnx" }
				}));
		}

		var content = context.GetFileContent(slnxFile);
		if (content is null)
		{
			return Task.FromResult(Fail(
				$"Could not read {slnxFile} content.",
				new RuleAdvisory
				{
					Summary = "Ensure the .slnx file is accessible.",
					Detail = $"The `.slnx` file `{slnxFile}` could not be read. Ensure it is accessible and not corrupted.",
					Data = new() { ["file"] = slnxFile }
				}));
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
				new RuleAdvisory
				{
					Summary = "Fix the .slnx file to be valid XML.",
					Detail = $"The `.slnx` file `{slnxFile}` is not valid XML. Fix the XML syntax.",
					Data = new() { ["file"] = slnxFile }
				}));
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
				new RuleAdvisory
				{
					Summary = "Add a <Folder Name=\"/Solution Items/\"> element to the .slnx file containing standard repo files.",
					Detail = $"The `.slnx` file `{slnxFile}` does not contain a `Solution Items` folder. Add a `<Folder Name=\"/Solution Items/\">` element with `<File Path=\"...\"/>` entries for standard repository root files.",
					Data = new() { ["file"] = slnxFile }
				}));
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
				new RuleAdvisory
				{
					Summary = "Add <File Path=\"...\"/> entries for the missing files in the Solution Items folder.",
					Detail = $"The Solution Items folder in `{slnxFile}` is missing references to: {string.Join(", ", missing)}. Add `<File Path=\"...\"/>` entries for each missing file.",
					Data = new()
					{
						["file"] = slnxFile,
						["missing_files"] = missing.ToArray(),
						["remediation_type"] = "add_slnx_file_entries"
					}
				}));
		}

		return Task.FromResult(Pass("Solution Items folder references all standard repository files."));
	}
}
