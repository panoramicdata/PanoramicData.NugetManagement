using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace PanoramicData.NugetManagement.Web.Services;

/// <summary>
/// One source file's line coverage.
/// </summary>
/// <param name="Path">The file, relative to the repository root where that could be worked out.</param>
/// <param name="Lines">Every line the instrumentation tracks, with whether it was hit.</param>
public sealed record FileCoverage(string Path, IReadOnlyDictionary<int, bool> Lines)
{
	/// <summary>How many tracked lines were never run.</summary>
	public int Uncovered => Lines.Count(line => !line.Value);

	/// <summary>The never-run lines as ranges, for example <c>12-18, 40, 52-55</c>.</summary>
	public string UncoveredRanges()
	{
		var missed = Lines.Where(line => !line.Value).Select(line => line.Key).Order().ToList();
		var parts = new List<string>();

		for (var i = 0; i < missed.Count;)
		{
			var start = missed[i];
			var end = start;

			while (i + 1 < missed.Count && missed[i + 1] == end + 1)
			{
				end = missed[++i];
			}

			parts.Add(start == end
				? start.ToString(CultureInfo.InvariantCulture)
				: $"{start.ToString(CultureInfo.InvariantCulture)}-{end.ToString(CultureInfo.InvariantCulture)}");
			i++;
		}

		return string.Join(", ", parts);
	}
}

/// <summary>
/// A Cobertura coverage report, reduced to what deciding where to write tests next needs.
/// </summary>
/// <remarks>
/// Codacy grades the same file, so reading it here means a session can be judged on the figure it will
/// be judged on later — without waiting for a push, a CI run and an analysis to find out that a hundred
/// tests moved nothing. It is a local estimate and not the verdict: Codacy has its own exclusions.
/// </remarks>
public sealed class CoberturaReport
{
	private CoberturaReport(IReadOnlyList<FileCoverage> files) => Files = files;

	/// <summary>A report built from files already read, as when several reports are merged.</summary>
	/// <param name="files">The files.</param>
	public static CoberturaReport FromFiles(IReadOnlyList<FileCoverage> files) => new(files);

	/// <summary>Every file the report tracks.</summary>
	public IReadOnlyList<FileCoverage> Files { get; }

	/// <summary>How many tracked lines there are.</summary>
	public int TotalLines => Files.Sum(file => file.Lines.Count);

	/// <summary>How many tracked lines ran.</summary>
	public int CoveredLines => Files.Sum(file => file.Lines.Count(line => line.Value));

	/// <summary>Line coverage as a percentage, or zero when nothing is tracked.</summary>
	public double LinePercent => TotalLines == 0 ? 0 : 100.0 * CoveredLines / TotalLines;

	/// <summary>
	/// Reads a report, keeping only files the caller does not exclude.
	/// </summary>
	/// <param name="xml">The Cobertura XML.</param>
	/// <param name="cloneRoot">The repository root, used to make file names relative.</param>
	/// <param name="exclude">
	/// True for a file that should not count. Test code is the usual one: it is nearly always covered by
	/// definition, and counting it flatters the figure.
	/// </param>
	public static CoberturaReport Parse(string xml, string cloneRoot, Func<string, bool>? exclude = null)
	{
		var document = XDocument.Parse(xml);
		var sources = document.Descendants("source").Select(source => source.Value.Trim()).ToList();

		var files = new Dictionary<string, Dictionary<int, bool>>(StringComparer.OrdinalIgnoreCase);

		foreach (var type in document.Descendants("class"))
		{
			var name = Relative((string?)type.Attribute("filename") ?? string.Empty, sources, cloneRoot);

			if (name.Length == 0 || exclude?.Invoke(name) == true)
			{
				continue;
			}

			if (!files.TryGetValue(name, out var lines))
			{
				files[name] = lines = [];
			}

			foreach (var line in type.Elements("lines").Elements("line"))
			{
				if (!int.TryParse((string?)line.Attribute("number"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var number)
					|| !int.TryParse((string?)line.Attribute("hits"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var hits))
				{
					continue;
				}

				// A line can appear under several classes — a partial class, a lambda's generated class. It
				// ran if any of them ran it.
				lines[number] = (lines.TryGetValue(number, out var already) && already) || hits > 0;
			}
		}

		return new CoberturaReport([.. files.Select(entry => new FileCoverage(entry.Key, entry.Value))]);
	}

	/// <summary>
	/// What to tell a model: the figure, and where the lines that never ran are.
	/// </summary>
	/// <param name="target">The percentage being aimed at.</param>
	/// <param name="topFiles">How many files to list.</param>
	public string Describe(double target, int topFiles = 10)
	{
		var builder = new StringBuilder();

		builder.AppendLine(string.Create(
			CultureInfo.InvariantCulture,
			$"Line coverage is {LinePercent:0.0}% ({CoveredLines} of {TotalLines} lines). The target is {target:0}%."));

		var needed = (int)Math.Ceiling(target / 100.0 * TotalLines) - CoveredLines;

		if (needed > 0)
		{
			builder.AppendLine(string.Create(
				CultureInfo.InvariantCulture,
				$"About {needed} more lines have to run."));
		}

		var worst = Files.Where(file => file.Uncovered > 0).OrderByDescending(file => file.Uncovered).Take(topFiles).ToList();

		if (worst.Count > 0)
		{
			builder.AppendLine("Files with the most lines that never run (file: uncovered of tracked, then the line numbers):");
		}

		foreach (var file in worst)
		{
			var ranges = file.UncoveredRanges();

			builder.AppendLine(string.Create(
				CultureInfo.InvariantCulture,
				$"- {file.Path}: {file.Uncovered} of {file.Lines.Count}, lines {(ranges.Length <= 200 ? ranges : ranges[..200] + "…")}"));
		}

		return builder.ToString().TrimEnd();
	}

	private static string Relative(string filename, List<string> sources, string cloneRoot)
	{
		var normalised = filename.Replace('\\', '/');
		var root = cloneRoot.Replace('\\', '/').TrimEnd('/') + "/";

		if (normalised.StartsWith(root, StringComparison.OrdinalIgnoreCase))
		{
			return normalised[root.Length..];
		}

		// Cobertura names a file relative to one of its <source> folders, so try each of them.
		foreach (var source in sources.Select(source => source.Replace('\\', '/').TrimEnd('/') + "/"))
		{
			if (source.StartsWith(root, StringComparison.OrdinalIgnoreCase) && !Path.IsPathRooted(normalised))
			{
				return (source[root.Length..] + normalised).TrimStart('/');
			}
		}

		// A deterministic build records "/_/" in place of the source root.
		return normalised.StartsWith("/_/", StringComparison.Ordinal) ? normalised[3..] : normalised.TrimStart('/');
	}
}

/// <summary>
/// The outcome of running a repository's tests under coverage.
/// </summary>
/// <param name="TestsPassed">Whether the test run exited cleanly.</param>
/// <param name="Report">The coverage report, or null when none was written.</param>
/// <param name="Problem">Why there is no usable report, or what went wrong with the run.</param>
/// <param name="OutputTail">The last lines of the test output, for diagnosing a failure.</param>
/// <param name="FailedTests">The names of the tests that failed, so failures can be compared with a baseline.</param>
public sealed record CoverageRun(
	bool TestsPassed,
	CoberturaReport? Report,
	string? Problem,
	string OutputTail,
	IReadOnlySet<string>? FailedTests = null);

/// <summary>
/// Runs a repository's tests with coverage and reads the report.
/// </summary>
public static partial class CoverageMeasurement
{
	private static readonly TimeSpan _testTimeout = TimeSpan.FromMinutes(10);

	private static readonly string[] _testFrameworkMarkers =
	[
		"Microsoft.NET.Test.Sdk", "xunit", "MSTest", "NUnit", "TUnit"
	];

	/// <summary>
	/// The repository's test projects, as paths relative to its root.
	/// </summary>
	/// <param name="cloneRoot">The repository root.</param>
	public static IReadOnlyList<string> FindTestProjects(string cloneRoot)
		=> [.. Directory
			.EnumerateFiles(cloneRoot, "*.csproj", SearchOption.AllDirectories)
			.Select(path => Path.GetRelativePath(cloneRoot, path).Replace('\\', '/'))
			.Where(path => !path.Contains("/bin/", StringComparison.OrdinalIgnoreCase)
				&& !path.Contains("/obj/", StringComparison.OrdinalIgnoreCase)
				&& !path.Contains("/.claude/", StringComparison.OrdinalIgnoreCase))
			.Where(path => IsTestProject(Path.Combine(cloneRoot, path)))
			.Order(StringComparer.OrdinalIgnoreCase)];

	/// <summary>
	/// Whether the project runs under Microsoft.Testing.Platform rather than VSTest, which decides the
	/// flags that make it write coverage.
	/// </summary>
	/// <param name="projectText">The project file's text.</param>
	/// <param name="packagesText">The central package file's text, where there is one.</param>
	public static bool UsesTestingPlatform(string projectText, string packagesText)
		=> projectText.Contains("xunit.v3", StringComparison.OrdinalIgnoreCase)
			|| packagesText.Contains("xunit.v3", StringComparison.OrdinalIgnoreCase)
			|| projectText.Contains("UseMicrosoftTestingPlatformRunner", StringComparison.OrdinalIgnoreCase)
			|| projectText.Contains("TestingPlatformDotnetTestSupport", StringComparison.OrdinalIgnoreCase);

	/// <summary>
	/// Runs every test project under coverage and merges what they report.
	/// </summary>
	/// <param name="cloneRoot">The repository root.</param>
	/// <param name="onOutput">Where the test output is echoed, or null.</param>
	/// <param name="cancellationToken">A cancellation token.</param>
	public static async Task<CoverageRun> MeasureAsync(
		string cloneRoot,
		Action<string>? onOutput,
		CancellationToken cancellationToken)
	{
		var projects = FindTestProjects(cloneRoot);

		if (projects.Count == 0)
		{
			return new CoverageRun(false, null, "This repository has no test project.", string.Empty);
		}

		var packages = Path.Combine(cloneRoot, "Directory.Packages.props");
		var packagesText = File.Exists(packages) ? await File.ReadAllTextAsync(packages, cancellationToken).ConfigureAwait(false) : string.Empty;

		var results = Path.Combine(Path.GetTempPath(), "nugetmanagement-coverage", Guid.NewGuid().ToString("n"));
		Directory.CreateDirectory(results);

		var reports = new List<CoberturaReport>();
		var failedTests = new HashSet<string>(StringComparer.Ordinal);
		var passed = true;
		var tail = new List<string>();
		string? problem = null;

		try
		{
			var testFolders = projects.Select(project => (Path.GetDirectoryName(project) ?? string.Empty).Replace('\\', '/')).ToList();

			foreach (var project in projects)
			{
				var projectText = await File.ReadAllTextAsync(Path.Combine(cloneRoot, project), cancellationToken).ConfigureAwait(false);
				var output = Path.Combine(results, Path.GetFileNameWithoutExtension(project));
				Directory.CreateDirectory(output);

				// Built first, in the normal environment: restoring packages may need this machine's NuGet
				// configuration, which the test run below deliberately does not get.
				var (built, buildText) = await LocalRepoService
					.RunCommandWithStreamingAsync(cloneRoot, "dotnet", ["build", project, "--nologo", "--verbosity", "quiet"], onOutput, cancellationToken)
					.ConfigureAwait(false);

				if (!built)
				{
					passed = false;
					problem = $"{project} does not build. The errors are below; fix them before anything else.";
					tail.AddRange(buildText.Split('\n').Where(line => line.Contains(": error ", StringComparison.OrdinalIgnoreCase)).Distinct().Take(15));
					continue;
				}

				var arguments = UsesTestingPlatform(projectText, packagesText)
					? new List<string>
					{
						"test", "--project", project, "--no-build", "--coverage", "--coverage-output-format", "cobertura",
						"--coverage-output", "coverage.cobertura.xml", "--results-directory", output
					}
					: ["test", project, "--no-build", "--collect:XPlat Code Coverage", "--results-directory", output];

				// Run as CI runs: no user secrets and no credentials in the environment, and not for ever.
				// A test that calls a live service with a real key took five minutes and printed the key.
				using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
				timeout.CancelAfter(_testTimeout);

				bool ok;
				string text;

				try
				{
					(ok, text) = await LocalRepoService
						.RunCommandWithStreamingAsync(cloneRoot, "dotnet", arguments, onOutput, timeout.Token, TestSandbox.Overrides())
						.ConfigureAwait(false);
				}
				catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
				{
					passed = false;
					problem = $"{project}'s tests were still running after {_testTimeout.TotalMinutes:0} minutes and were stopped. "
						+ "A test that waits on a network service or a prompt is the usual cause.";
					continue;
				}

				passed &= ok;
				tail.AddRange(text.Split('\n').Where(line => !string.IsNullOrWhiteSpace(line)).TakeLast(30));
				failedTests.UnionWith(ParseFailedTests(text));

				var file = Directory.EnumerateFiles(output, "coverage.cobertura.xml", SearchOption.AllDirectories)
					.OrderByDescending(File.GetLastWriteTimeUtc)
					.FirstOrDefault();

				if (file is null)
				{
					// Two different faults look the same from here. A run that failed never got as far as
					// writing a report — a test file that does not compile is the usual cause, and blaming the
					// collector for it sends a model off to edit project files instead of fixing its own test.
					problem = ok
						? $"{project} ran but wrote no coverage report. Its tests need a coverage collector "
							+ "(Microsoft.Testing.Extensions.CodeCoverage for xunit.v3, coverlet.collector otherwise)."
						: $"{project} failed before it wrote a coverage report — most likely a test that does not "
							+ "compile, or one that crashed. The end of its output is below.";
					continue;
				}

				reports.Add(CoberturaReport.Parse(
					await File.ReadAllTextAsync(file, cancellationToken).ConfigureAwait(false),
					cloneRoot,
					name => testFolders.Any(folder => folder.Length > 0 && name.StartsWith(folder + "/", StringComparison.OrdinalIgnoreCase))));
			}
		}
		finally
		{
			try
			{
				Directory.Delete(results, recursive: true);
			}
			catch (IOException)
			{
				// A temp folder that cannot be removed is the operating system's to clean.
			}
		}

		if (reports.Count == 0)
		{
			return new CoverageRun(passed, null, problem ?? "No coverage report was written.", string.Join('\n', tail.TakeLast(30)), failedTests);
		}

		return new CoverageRun(passed, reports.Count == 1 ? reports[0] : Merge(reports), problem, string.Join('\n', tail.TakeLast(30)), failedTests);
	}

	/// <summary>
	/// The names of the tests a run reports as failed.
	/// </summary>
	/// <param name="output">The test runner's output.</param>
	/// <remarks>
	/// Both runners' wording: Microsoft.Testing.Platform prints <c>failed Namespace.Class.Method (12ms)</c>
	/// and VSTest prints <c>Failed Namespace.Class.Method [12 ms]</c>. Needed because a failure is only a
	/// problem if the session caused it — a test that needs a credential fails the same way before the
	/// model has written a line, and asking the model to repair it just gets it editing integration tests.
	/// </remarks>
	public static IReadOnlySet<string> ParseFailedTests(string output)
		=> FailedTestLine().Matches(output)
			.Select(match => match.Groups["name"].Value)
			.ToHashSet(StringComparer.Ordinal);

	[GeneratedRegex(@"^\s*(?:failed|Failed)\s+(?<name>[\w.`+<>,\[\]-]+(?:\([^)]*\))?)", RegexOptions.Multiline)]
	private static partial Regex FailedTestLine();

	private static CoberturaReport Merge(List<CoberturaReport> reports)
	{
		// Reports from separate test projects cover the same production files, and a line counts as run if
		// any of them ran it.
		var merged = reports
			.SelectMany(report => report.Files)
			.GroupBy(file => file.Path, StringComparer.OrdinalIgnoreCase)
			.Select(group => new FileCoverage(
				group.Key,
				group.SelectMany(file => file.Lines)
					.GroupBy(line => line.Key)
					.ToDictionary(line => line.Key, line => line.Any(hit => hit.Value))));

		return CoberturaReport.FromFiles([.. merged]);
	}

	private static bool IsTestProject(string fullPath)
	{
		try
		{
			var text = File.ReadAllText(fullPath);

			return text.Contains("<IsTestProject>true", StringComparison.OrdinalIgnoreCase)
				|| (Path.GetFileNameWithoutExtension(fullPath).Contains("Test", StringComparison.OrdinalIgnoreCase)
					&& _testFrameworkMarkers.Any(marker => text.Contains(marker, StringComparison.OrdinalIgnoreCase)));
		}
		catch (IOException)
		{
			return false;
		}
	}
}
