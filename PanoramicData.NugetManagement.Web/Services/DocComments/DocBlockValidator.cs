using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace PanoramicData.NugetManagement.Web.Services.DocComments;

/// <summary>
/// The quality gate on what the model proposes for one slot.
/// </summary>
/// <remarks>
/// A rejected slot is left undocumented rather than given a template: a cosmetic comment satisfies
/// CS1591 and tells the reader nothing, which is worse than the warning that says work remains.
/// </remarks>
public static partial class DocBlockValidator
{
	private const int _maxSummaryCharacters = 600;

	private static readonly HashSet<string> _allowedElements = ["summary", "param", "typeparam", "returns", "value"];

	private static readonly string[] _codeMarkers = ["`", "{", "}", ";", "=>"];

	[GeneratedRegex(@"[a-z0-9]+", RegexOptions.CultureInvariant)]
	private static partial Regex WordPattern();

	[GeneratedRegex("(?<!^)([A-Z])", RegexOptions.CultureInvariant)]
	private static partial Regex CapitalPattern();

	[GeneratedRegex(@"\n\s*\n",RegexOptions.CultureInvariant)]
	private static partial Regex BlankLinePattern();

	/// <summary>
	/// Why a block cannot be used, or null when it can.
	/// </summary>
	/// <param name="docLines">The block's <c>///</c> lines.</param>
	/// <param name="slot">The slot it is for.</param>
	public static string? Reject(IReadOnlyList<string> docLines, DocSlot slot)
	{
		if (docLines.Count == 0)
		{
			return "empty block";
		}

		if (docLines.Any(l => !l.StartsWith("///", StringComparison.Ordinal)))
		{
			return "a line is not a /// line";
		}

		if (!TryParse(docLines, out var root))
		{
			return "not well-formed XML";
		}

		return RejectStructure(root!)
			?? RejectSummary(root!, slot)
			?? RejectCrefs(root!)
			?? RejectParameters(root!, slot)
			?? RejectCode(root!);
	}

	private static bool TryParse(IReadOnlyList<string> docLines, out XElement? root)
	{
		var inner = string.Join('\n', docLines.Select(l => l[3..]));

		try
		{
			root = XDocument.Parse("<doc>" + inner + "</doc>").Root;

			return root is not null;
		}
		catch (System.Xml.XmlException)
		{
			root = null;

			return false;
		}
	}

	private static string? RejectStructure(XElement root)
	{
		if (root.Nodes().OfType<XText>().Any(t => !string.IsNullOrWhiteSpace(t.Value)))
		{
			return "text outside a tag";
		}

		var unexpected = root.Elements().FirstOrDefault(e => !_allowedElements.Contains(e.Name.LocalName));

		return unexpected is null ? null : $"unexpected element <{unexpected.Name.LocalName}>";
	}

	private static string? RejectSummary(XElement root, DocSlot slot)
	{
		var summaries = root.Elements("summary").ToList();

		if (summaries.Count != 1)
		{
			return "needs exactly one summary";
		}

		var summary = summaries[0];
		var text = summary.Value.Trim();

		if (text.Length == 0)
		{
			return "empty summary";
		}

		if (summary.Descendants().Any(e => e.Name.LocalName is "para" or "list" or "br") || BlankLinePattern().IsMatch(text))
		{
			return "summary is more than one paragraph";
		}

		if (text.Length > _maxSummaryCharacters)
		{
			return "summary is too long";
		}

		return Restates(text, slot) ? "summary only restates the member name" : null;
	}

	private static string? RejectCrefs(XElement root)
	{
		// Well-formed XML is not enough: a cref of DateTime? parses fine and then fails CS1584.
		var bad = root.DescendantsAndSelf()
			.Select(e => e.Attribute("cref")?.Value)
			.FirstOrDefault(v => v is not null && (v.Trim().Length == 0 || v.IndexOfAny(['?', '[', ']', '<', '>']) >= 0));

		return bad is null ? null : $"invalid cref '{bad}'";
	}

	private static string? RejectParameters(XElement root, DocSlot slot)
	{
		var checkNames = slot.Declaration.Contains(')', StringComparison.Ordinal);

		foreach (var param in root.Elements().Where(e => e.Name.LocalName is "param" or "typeparam"))
		{
			var name = param.Attribute("name")?.Value;

			if (string.IsNullOrWhiteSpace(name) || param.Value.Trim().Length == 0)
			{
				return $"empty {param.Name.LocalName} tag";
			}

			if (checkNames && !Regex.IsMatch(slot.Declaration, $@"\b{Regex.Escape(name)}\b", RegexOptions.CultureInvariant))
			{
				return $"{param.Name.LocalName} '{name}' is not in the declaration";
			}
		}

		return root.Elements().Any(e => e.Name.LocalName is "returns" or "value" && e.Value.Trim().Length == 0)
			? "empty returns or value tag"
			: null;
	}

	private static string? RejectCode(XElement root)
	{
		if (root.Descendants("code").Any())
		{
			return "contains a code element";
		}

		var text = string.Concat(root.DescendantNodes().OfType<XText>().Select(t => t.Value));

		return _codeMarkers.Any(m => text.Contains(m, StringComparison.Ordinal)) ? "contains code" : null;
	}

	// 'Gets or sets the Name.' for Name says nothing the signature did not. The boilerplate words are
	// dropped and what is left must introduce at least one word that the member's own name does not.
	private static bool Restates(string summary, DocSlot slot)
	{
		var name = WordsOf(NameOf(slot));
		var remaining = WordPattern().Matches(summary.ToLowerInvariant())
			.Select(m => m.Value)
			.Where(w => !_boilerplate.Contains(w));

		return remaining.All(name.Contains);
	}

	private static readonly HashSet<string> _boilerplate =
	[
		"gets", "sets", "get", "set", "or", "and", "the", "a", "an", "of", "for", "to", "that", "is", "this",
		"represents", "initializes", "initialises", "new", "instance", "class", "struct", "type", "interface",
		"enum", "property", "field", "value", "returns", "whether"
	];

	private static string NameOf(DocSlot slot)
	{
		var key = slot.Members[0];
		var paren = key.IndexOf('(', StringComparison.Ordinal);
		var bare = paren >= 0 ? key[..paren] : key;

		return bare[(bare.LastIndexOf('.') + 1)..];
	}

	private static HashSet<string> WordsOf(string name)
		=> [.. WordPattern().Matches(CapitalPattern().Replace(name, " $1").ToLowerInvariant()).Select(m => m.Value)];
}
