using System.Text.RegularExpressions;

namespace PanoramicData.NugetManagement.Web.Services.DocComments;

/// <summary>
/// Reads the model's answer: <c>///</c> lines under an <c>@@@ n</c> marker, and nothing else.
/// </summary>
public static partial class DocResponseParser
{
	[GeneratedRegex(@"^@@@\s*(\d+)\s*$", RegexOptions.CultureInvariant)]
	private static partial Regex MarkerPattern();

	/// <summary>
	/// The doc lines by slot id. Prose, fences and code are discarded outright, and the first block for a number wins.
	/// </summary>
	/// <param name="response">The model's text.</param>
	public static IReadOnlyDictionary<int, IReadOnlyList<string>> Parse(string response)
	{
		var blocks = new Dictionary<int, List<string>>();
		List<string>? current = null;

		foreach (var raw in response.Split('\n'))
		{
			var line = raw.Trim();
			var marker = MarkerPattern().Match(line);

			if (marker.Success)
			{
				var id = int.Parse(marker.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);

				current = blocks.ContainsKey(id) ? null : (blocks[id] = []);
			}
			else if (current is not null && IsDocLine(line))
			{
				current.Add(Normalise(line));
			}
		}

		return blocks.Where(kv => kv.Value.Count > 0).ToDictionary(kv => kv.Key, kv => (IReadOnlyList<string>)kv.Value);
	}

	private static bool IsDocLine(string line)
		=> line.StartsWith("///", StringComparison.Ordinal) && !line.StartsWith("////", StringComparison.Ordinal);

	private static string Normalise(string line)
	{
		var rest = line[3..];

		return rest.Length == 0 || rest[0] == ' ' ? line : "/// " + rest;
	}
}
