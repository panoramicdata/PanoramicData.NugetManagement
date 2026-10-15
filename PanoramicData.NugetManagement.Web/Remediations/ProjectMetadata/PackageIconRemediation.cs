using System.Reflection;
using System.Text.RegularExpressions;
using PanoramicData.NugetManagement.Models;

namespace PanoramicData.NugetManagement.Web.Remediations.ProjectMetadata;

/// <summary>
/// META-05: gives each published project a PackageIcon, the image file it names, and the item that
/// packs it.
/// </summary>
/// <remarks>
/// A remediation rather than a Fix with AI playbook, because nothing about it needs judgement and one
/// part of it cannot be done by a model at all: the image. Offered the job, a model wrote the base64
/// text of a PNG into a file called <c>Logo.png</c> — a file that looks right to every check except the
/// one that opens it. The three edits have to agree with each other (the property, the item and the
/// file), and doing them in code means they always do.
/// </remarks>
public sealed partial class PackageIconRemediation : IRemediation
{
	private const string IconFileName = "Logo.png";

	/// <inheritdoc />
	public string RuleId => "META-05";

	/// <inheritdoc />
	public bool CanRemediate(RuleResult result)
		=> !result.Passed
			&& result.Advisory?.Data.ContainsKey("projects") == true
			&& OpenLogo() is not null;

	/// <inheritdoc />
	public void Apply(string localPath, RuleResult result, List<string> applied, Action<string>? onOutput)
	{
		if (result.Advisory?.Data.TryGetValue("projects", out var value) != true)
		{
			return;
		}

		var projects = value switch
		{
			string[] many => many,
			IEnumerable<string> many => [.. many],
			string one => [one],
			_ => []
		};

		foreach (var project in projects)
		{
			Apply(localPath, project, applied, onOutput);
		}
	}

	private static void Apply(string localPath, string project, List<string> applied, Action<string>? onOutput)
	{
		var projectPath = RemediationHelpers.ResolvePath(localPath, project);

		if (!File.Exists(projectPath))
		{
			onOutput?.Invoke($"⏭️ [META-05] {project} does not exist — skipping.");
			return;
		}

		var folder = Path.GetDirectoryName(projectPath)!;
		var logoPath = Path.Combine(folder, IconFileName);

		if (!File.Exists(logoPath))
		{
			using var logo = OpenLogo();

			if (logo is null)
			{
				onOutput?.Invoke("❌ [META-05] The logo is not embedded in this build, so none can be added.");
				return;
			}

			using var target = File.Create(logoPath);
			logo.CopyTo(target);

			applied.Add(Path.GetRelativePath(localPath, logoPath).Replace('\\', '/'));
			onOutput?.Invoke($"✅ [META-05] Added {IconFileName} beside {project}");
		}

		// Edited as text rather than parsed and re-saved, so that everything the file already says stays
		// byte for byte as it was. A round trip through an XML document reflows whitespace, and the
		// resulting diff buries the two lines that matter.
		var bytes = File.ReadAllBytes(projectPath);
		var bom = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF;
		var text = new System.Text.UTF8Encoding(bom).GetString(bytes, bom ? 3 : 0, bytes.Length - (bom ? 3 : 0));
		var nl = text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
		var changed = false;

		if (!PackageIconPattern().IsMatch(text))
		{
			var close = FirstPropertyGroupClose().Match(text);

			if (close.Success)
			{
				var indent = close.Groups["indent"].Value;
				var unit = indent.Contains('\t', StringComparison.Ordinal) ? "\t" : "  ";

				text = text.Insert(close.Index, $"{indent}{unit}<PackageIcon>{IconFileName}</PackageIcon>{nl}");
			}
			else
			{
				var open = text.IndexOf('>', StringComparison.Ordinal);
				var end = text.IndexOf("</Project>", StringComparison.Ordinal);

				if (open < 0 || end < 0)
				{
					onOutput?.Invoke($"❌ [META-05] {project} has no <Project> element to add a property to.");
					return;
				}

				text = text.Insert(
					end,
					$"  <PropertyGroup>{nl}    <PackageIcon>{IconFileName}</PackageIcon>{nl}  </PropertyGroup>{nl}");
			}

			changed = true;
		}

		if (!PackedIconPattern().IsMatch(text))
		{
			var end = text.LastIndexOf("</Project>", StringComparison.Ordinal);

			if (end < 0)
			{
				onOutput?.Invoke($"❌ [META-05] {project} has no closing </Project> to add an item before.");
				return;
			}

			var lead = end > 0 && text[end - 1] != '\n' ? nl : string.Empty;

			text = text.Insert(
				end,
				$"{lead}  <ItemGroup>{nl}    <None Include=\"{IconFileName}\" Pack=\"true\" PackagePath=\"\\\" />{nl}  </ItemGroup>{nl}");
			changed = true;
		}

		if (!changed)
		{
			return;
		}

		File.WriteAllText(projectPath, text, new System.Text.UTF8Encoding(bom));

		applied.Add(project);
		onOutput?.Invoke($"✅ [META-05] Set PackageIcon and packed {IconFileName} in {project}");
	}

	[GeneratedRegex(@"<PackageIcon\s*>", RegexOptions.IgnoreCase)]
	private static partial Regex PackageIconPattern();

	[GeneratedRegex(@"<None\s[^>]*Include\s*=\s*""Logo\.png""[^>]*Pack\s*=\s*""true""|<None\s[^>]*Pack\s*=\s*""true""[^>]*Include\s*=\s*""Logo\.png""", RegexOptions.IgnoreCase)]
	private static partial Regex PackedIconPattern();

	[GeneratedRegex(@"(?<indent>[ \t]*)</PropertyGroup>")]
	private static partial Regex FirstPropertyGroupClose();

	/// <summary>The embedded logo, or null if this build does not carry it.</summary>
	private static Stream? OpenLogo()
		=> Assembly.GetExecutingAssembly().GetManifestResourceStream(IconFileName);
}
