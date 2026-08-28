using System.Xml;
using System.Xml.Linq;

namespace PanoramicData.NugetManagement.Models;

/// <summary>
/// Reads MSBuild properties out of a .csproj or .props file. Parsing rather than searching, so a
/// commented-out declaration does not count as a declaration, and the text of a property is not
/// confused with the same text appearing elsewhere in the file.
/// </summary>
public static class MsBuildProperties
{
	/// <summary>
	/// Returns the trimmed values of every declaration of the named property, or null when the
	/// content cannot be parsed as XML.
	/// </summary>
	/// <param name="xml">The project or props file content.</param>
	/// <param name="propertyName">The property element name, without angle brackets.</param>
	public static List<string>? TryGetValues(string? xml, string propertyName)
	{
		if (string.IsNullOrWhiteSpace(xml))
		{
			return [];
		}

		try
		{
			return [.. XDocument.Parse(xml)
				.Descendants()
				.Where(element => string.Equals(element.Name.LocalName, propertyName, StringComparison.OrdinalIgnoreCase))
				.Select(element => element.Value.Trim())];
		}
		catch (XmlException)
		{
			return null;
		}
	}

	/// <summary>
	/// Whether the property is declared at all, whatever its value. Unparseable content falls back to
	/// a substring check, which is the best that can be done with a malformed project file.
	/// </summary>
	/// <param name="xml">The project or props file content.</param>
	/// <param name="propertyName">The property element name, without angle brackets.</param>
	public static bool Has(string? xml, string propertyName)
	{
		var values = TryGetValues(xml, propertyName);
		return values is null
			? xml?.Contains($"<{propertyName}>", StringComparison.OrdinalIgnoreCase) == true
			: values.Count > 0;
	}

	/// <summary>
	/// Whether the property is declared with the expected value, ignoring case and surrounding
	/// whitespace.
	/// </summary>
	/// <param name="xml">The project or props file content.</param>
	/// <param name="propertyName">The property element name, without angle brackets.</param>
	/// <param name="expectedValue">The value the property is expected to have.</param>
	public static bool HasValue(string? xml, string propertyName, string expectedValue)
	{
		var values = TryGetValues(xml, propertyName);
		return values is null
			? xml?.Contains($"<{propertyName}>{expectedValue}</{propertyName}>", StringComparison.OrdinalIgnoreCase) == true
			: values.Any(value => string.Equals(value, expectedValue, StringComparison.OrdinalIgnoreCase));
	}
}
