using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Components;

namespace PanoramicData.NugetManagement.Web.Components;

/// <summary>
/// Text in which http and https addresses are links that open in a new tab. Everything else, links included, is
/// encoded by Blazor as usual, so a message can never inject markup.
/// </summary>
public partial class LinkifiedText
{
	/// <summary>The text to show.</summary>
	[Parameter]
	public string? Text { get; set; }

	/// <summary>A run of text, and whether it is an address to link.</summary>
	/// <param name="Text">The text.</param>
	/// <param name="IsLink">Whether it is an http or https address.</param>
	internal readonly record struct Part(string Text, bool IsLink);

	[GeneratedRegex(@"https?://[^\s<>""']+", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
	private static partial Regex Address();

	/// <summary>
	/// Splits text into plain runs and addresses. Punctuation that ends a sentence is not part of the address.
	/// </summary>
	/// <param name="text">The text to split.</param>
	internal static IReadOnlyList<Part> Split(string? text)
	{
		var parts = new List<Part>();
		if (string.IsNullOrEmpty(text))
		{
			return parts;
		}

		var position = 0;
		foreach (var match in Address().Matches(text).Cast<Match>())
		{
			var address = match.Value.TrimEnd('.', ',', ';', ':', ')', ']', '!', '?');
			if (address.Length <= "https://".Length)
			{
				continue;
			}

			if (match.Index > position)
			{
				parts.Add(new Part(text[position..match.Index], false));
			}

			parts.Add(new Part(address, true));
			position = match.Index + address.Length;
		}

		if (position < text.Length)
		{
			parts.Add(new Part(text[position..], false));
		}

		return parts;
	}
}
