using PanoramicData.NugetManagement.Models;

namespace PanoramicData.NugetManagement.Rules;

/// <summary>
/// Checks that .editorconfig enforces tab indentation for C# and XML files.
/// </summary>
public class TabIndentationRule : RuleBase
{
	/// <inheritdoc />
	public override string RuleId => "CQ-04";

	/// <inheritdoc />
	public override string RuleName => "Tab indentation enforced for C# and XML";

	/// <inheritdoc />
	public override AssessmentCategory Category => AssessmentCategory.CodeQuality;

	/// <inheritdoc />
	public override AssessmentSeverity Severity => AssessmentSeverity.Warning;

	/// <inheritdoc />
	public override Task<RuleResult> EvaluateAsync(RepositoryContext context, CancellationToken cancellationToken)
	{
		var content = context.GetFileContent(".editorconfig");
		if (content is null)
		{
			return Task.FromResult(Fail(
				".editorconfig not found.",
				new RuleAdvisory
				{
					Summary = "Create an .editorconfig with indent_style = tab for C# and XML files.",
					Detail = "Create a `.editorconfig` file at the repository root with `indent_style = tab` in the `[*]` section to enforce tab indentation for C# and XML files.",
					Data = new() { ["expected_path"] = ".editorconfig", ["template_content"] = Standards.EditorConfigContent }
				}));
		}

		// Check that the global [*] section uses tabs
		var hasGlobalTab = Contains(content, "indent_style = tab");
		if (!hasGlobalTab)
		{
			return Task.FromResult(Fail(
				".editorconfig does not set indent_style = tab.",
				new RuleAdvisory
				{
					Summary = "Set indent_style = tab in the [*] section of .editorconfig.",
					Detail = "The `.editorconfig` file exists but does not set `indent_style = tab`. Add this setting in the `[*]` section.",
					Data = new()
					{
						["file"] = ".editorconfig",
						["remediation_type"] = "append_line",
						["line_content"] = "indent_style = tab"
					}
				}));
		}

		// Check there is no override to spaces for C# or XML sections
		// We look for indent_style = space after a [*.cs] or [*.{xml,...}] section header
		var lines = content.Split('\n');
		var currentSection = "";
		foreach (var rawLine in lines)
		{
			var line = rawLine.Trim();
			if (line.StartsWith('[') && line.EndsWith(']'))
			{
				currentSection = line;
				continue;
			}

			if (line.Equals("indent_style = space", StringComparison.OrdinalIgnoreCase) &&
				IsCSharpOrXmlSection(currentSection))
			{
				return Task.FromResult(Fail(
					$".editorconfig overrides indent_style to space in section {currentSection}.",
					new RuleAdvisory
					{
						Summary = $"Change indent_style = space to indent_style = tab in the {currentSection} section.",
						Detail = $"The `.editorconfig` section `{currentSection}` overrides `indent_style` to `space`. Change it to `indent_style = tab`.",
						Data = new()
						{
							["file"] = ".editorconfig",
							["remediation_type"] = "replace_in_file",
							["section"] = currentSection,
							["old_text"] = "indent_style = space",
							["new_text"] = "indent_style = tab"
						}
					}));
			}
		}

		return Task.FromResult(Pass("Tab indentation is enforced for C# and XML files."));
	}

	private static bool IsCSharpOrXmlSection(string sectionHeader)
	{
		if (string.IsNullOrEmpty(sectionHeader))
		{
			return false;
		}

		var inner = sectionHeader.TrimStart('[').TrimEnd(']');
		return inner.Contains("*.cs", StringComparison.OrdinalIgnoreCase) ||
			inner.Contains("*.xml", StringComparison.OrdinalIgnoreCase) ||
			inner.Contains("*.csproj", StringComparison.OrdinalIgnoreCase) ||
			inner.Contains("*.props", StringComparison.OrdinalIgnoreCase) ||
			inner.Contains("*.targets", StringComparison.OrdinalIgnoreCase);
	}
}
