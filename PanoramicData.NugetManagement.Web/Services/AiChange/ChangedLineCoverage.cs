using System.Xml.Linq;

namespace PanoramicData.NugetManagement.Web.Services.AiChange;

/// <summary>
/// A file a change touched and the (new-side) line numbers it added or modified.
/// </summary>
/// <param name="Path">The repository-relative path, with forward slashes.</param>
/// <param name="Lines">The changed line numbers.</param>
public sealed record ChangedFileLines(string Path, IReadOnlySet<int> Lines);

/// <summary>
/// How well the changed lines were exercised.
/// </summary>
/// <param name="Passed">Whether the threshold was met.</param>
/// <param name="Executable">How many changed lines are executable.</param>
/// <param name="Covered">How many of those ran.</param>
/// <param name="Message">What to tell the model or the user.</param>
public sealed record ChangedLineCoverageResult(bool Passed, int Executable, int Covered, string Message);

/// <summary>
/// Whether the lines a change touched are covered by the tests that ran.
/// </summary>
/// <remarks>
/// The bar is the one the TST rules already use (60% line coverage is green), applied to the lines this
/// change touched rather than to the repository. A changed file the report never mentions counts as
/// fully uncovered: otherwise a new file that nothing references would pass by being invisible.
/// </remarks>
public static class ChangedLineCoverage
{
	/// <summary>The fraction of changed executable lines that must have run.</summary>
	public const double Threshold = 0.60;

	/// <summary>
	/// Checks the changed lines against a Cobertura report.
	/// </summary>
	/// <param name="changed">The changed files and lines.</param>
	/// <param name="coberturaXml">The report from the test run that included the change.</param>
	public static ChangedLineCoverageResult Evaluate(IReadOnlyList<ChangedFileLines> changed, string coberturaXml)
	{
		var hitsByFile = ReadHits(coberturaXml);
		var executable = 0;
		var covered = 0;
		var worst = new List<string>();

		foreach (var file in changed)
		{
			var hits = FindHits(hitsByFile, file.Path);

			if (hits is null)
			{
				executable += file.Lines.Count;
				worst.Add($"{file.Path} is not in the coverage report");
				continue;
			}

			var fileExecutable = file.Lines.Where(hits.ContainsKey).ToList();
			var fileCovered = fileExecutable.Count(line => hits[line] > 0);

			executable += fileExecutable.Count;
			covered += fileCovered;

			if (fileExecutable.Count > 0 && (double)fileCovered / fileExecutable.Count < Threshold)
			{
				worst.Add($"{file.Path}: {fileCovered} of {fileExecutable.Count} changed lines ran");
			}
		}

		var passed = executable == 0 || (double)covered / executable >= Threshold;

		var message = passed
			? $"{covered} of {executable} changed executable lines are covered."
			: $"Only {covered} of {executable} changed executable lines are covered, below {Threshold:P0}. "
				+ string.Join("; ", worst) + ". Add tests that execute the changed code.";

		return new ChangedLineCoverageResult(passed, executable, covered, message);
	}

	private static Dictionary<string, Dictionary<int, int>> ReadHits(string coberturaXml)
	{
		var result = new Dictionary<string, Dictionary<int, int>>(StringComparer.OrdinalIgnoreCase);

		foreach (var cls in XDocument.Parse(coberturaXml).Descendants("class"))
		{
			var filename = ((string?)cls.Attribute("filename") ?? string.Empty).Replace('\\', '/');

			if (!result.TryGetValue(filename, out var lines))
			{
				lines = [];
				result[filename] = lines;
			}

			// Only the class's own <lines>: each method repeats its lines inside, and counting both
			// would double every executable line.
			foreach (var line in cls.Elements("lines").Elements("line"))
			{
				var number = (int?)line.Attribute("number");
				var hits = (int?)line.Attribute("hits") ?? 0;

				if (number is not null)
				{
					// A partial class appears once per part, so the most hits seen wins.
					lines[number.Value] = Math.Max(lines.GetValueOrDefault(number.Value), hits);
				}
			}
		}

		return result;
	}

	private static Dictionary<int, int>? FindHits(Dictionary<string, Dictionary<int, int>> byFile, string path)
	{
		var wanted = path.Replace('\\', '/');

		foreach (var (filename, hits) in byFile)
		{
			if (filename.Equals(wanted, StringComparison.OrdinalIgnoreCase)
				|| filename.EndsWith("/" + wanted, StringComparison.OrdinalIgnoreCase))
			{
				return hits;
			}
		}

		return null;
	}
}
