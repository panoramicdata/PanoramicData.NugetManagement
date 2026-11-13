using System.Text.RegularExpressions;
using PanoramicData.NugetManagement.Models;

namespace PanoramicData.NugetManagement.Rules;

/// <summary>
/// Checks that README.md states the .NET versions the package supports in one fixed phrase, and that no
/// current documentation (README and instruction files) or package description names a .NET version the package does not support.
/// </summary>
public partial class SupportedDotNetVersionsDocumentedRule : RuleBase
{
	private const string _phrasePrefix = "Supports ";

	/// <inheritdoc />
	public override string RuleId => "DOC-02";

	/// <inheritdoc />
	public override string RuleName => "Documentation states the supported .NET versions";

	/// <inheritdoc />
	public override AssessmentCategory Category => AssessmentCategory.Documentation;

	/// <inheritdoc />
	public override AssessmentSeverity Severity => AssessmentSeverity.Warning;

	/// <inheritdoc />
	public override Task<RuleResult> EvaluateAsync(RepositoryContext context, CancellationToken cancellationToken)
	{
		var supported = GetSupportedVersions(context);
		if (supported.Count == 0)
		{
			return Task.FromResult(NotApplicable("No published project declares a target framework."));
		}

		var phrase = BuildPhrase(supported);
		var supportedKeys = supported.Select(v => v.Key).ToHashSet(StringComparer.Ordinal);
		var problems = new List<string>();

		var readme = context.GetFileContent("README.md");
		if (readme is null)
		{
			return Task.FromResult(NotApplicable("README.md is missing; README-01 reports that."));
		}

		if (!readme.Contains(phrase, StringComparison.Ordinal))
		{
			problems.Add($"README.md does not contain the phrase \"{phrase}\"");
		}

		var wrong = new List<(string Source, string Text)>();
		foreach (var (path, content) in context.FileContents)
		{
			if (IsCurrentDocumentation(path))
			{
				wrong.AddRange(FindUnsupportedMentions(path, content, supportedKeys));
			}
		}

		foreach (var project in context.FindPackableProjectFiles())
		{
			var content = context.GetFileContent(project);
			foreach (var property in new[] { "Description", "PackageDescription" })
			{
				foreach (var value in MsBuildProperties.TryGetValues(content, property) ?? [])
				{
					wrong.AddRange(FindUnsupportedMentions($"{project} <{property}>", value, supportedKeys));
				}
			}
		}

		problems.AddRange(wrong
			.GroupBy(w => w.Source, StringComparer.OrdinalIgnoreCase)
			.Select(g => $"{g.Key} mentions {string.Join(", ", g.Select(w => $"\"{w.Text}\"").Distinct())}"));

		if (problems.Count == 0)
		{
			return Task.FromResult(Pass($"README.md contains \"{phrase}\" and no documentation names an unsupported .NET version."));
		}

		return Task.FromResult(Fail(
			$"Supported .NET versions are not documented correctly (supported: {string.Join(", ", supported.Select(v => v.Display))}): {string.Join("; ", problems)}.",
			new RuleAdvisory
			{
				Summary = $"Put the sentence \"{phrase}.\" in README.md and remove every other .NET version mention from the documentation.",
				Detail = $"The projects this repository publishes support {string.Join(", ", supported.Select(v => v.Display))}, derived from their `TargetFramework`/`TargetFrameworks`. `README.md` must contain exactly `{phrase}` (for example as the sentence \"{phrase}.\"). No README, `AGENTS.md`, `CLAUDE.md`, `CONTRIBUTING.md`, `SECURITY.md`, `copilot-instructions.md` or published project `Description` may name a .NET version, target framework moniker, .NET Framework, .NET Core or .NET Standard version outside that set: reword the text, or update it to the supported versions. Problems: {string.Join("; ", problems)}.",
				Data = new()
				{
					["file"] = "README.md",
					["required_phrase"] = phrase,
					["supported_versions"] = supported.Select(v => v.Display).ToArray(),
					["unsupported_mentions"] = wrong.Select(w => $"{w.Source}: {w.Text}").Distinct().ToArray()
				}
			}));
	}

	/// <summary>
	/// Builds the set phrase, for example <c>Supports .NET 8.0, .NET 10.0 and .NET 11.0</c>.
	/// </summary>
	/// <param name="supported">The supported versions in display order.</param>
	internal static string BuildPhrase(IReadOnlyList<DotNetVersion> supported)
	{
		var names = supported.Select(v => v.Display).ToList();
		return names.Count == 1
			? _phrasePrefix + names[0]
			: _phrasePrefix + string.Join(", ", names.Take(names.Count - 1)) + " and " + names[^1];
	}

	/// <summary>
	/// The versions the repository's published projects target, oldest family first, then ascending.
	/// </summary>
	/// <param name="context">The repository being assessed.</param>
	internal static List<DotNetVersion> GetSupportedVersions(RepositoryContext context)
	{
		var inherited = TargetFrameworksOf(context.GetFileContent("Directory.Build.props"));
		var versions = new Dictionary<string, DotNetVersion>(StringComparer.Ordinal);

		foreach (var project in context.FindPackableProjectFiles())
		{
			if (context.IsCompilerExtensionProject(project))
			{
				continue;
			}

			var monikers = TargetFrameworksOf(context.GetFileContent(project));
			foreach (var moniker in monikers.Count > 0 ? monikers : inherited)
			{
				if (DotNetVersion.FromMoniker(moniker) is { } version)
				{
					versions[version.Key] = version;
				}
			}
		}

		return [.. versions.Values.OrderBy(v => v.Family).ThenBy(v => v.SortValue)];
	}

	private static readonly string[] _currentDocumentation =
	[
		"README.md",
		"AGENTS.md",
		"CLAUDE.md",
		"CONTRIBUTING.md",
		"SECURITY.md",
		"copilot-instructions.md",
		".github/copilot-instructions.md"
	];

	/// <summary>
	/// Only files that describe the package as it is now. Changelogs, plans and publishing notes are
	/// records: rewriting their version numbers would falsify them.
	/// </summary>
	private static bool IsCurrentDocumentation(string path)
	{
		var normalised = path.Replace('\\', '/');
		return _currentDocumentation.Contains(normalised, StringComparer.OrdinalIgnoreCase)
			|| normalised.EndsWith("/README.md", StringComparison.OrdinalIgnoreCase);
	}

	private static List<string> TargetFrameworksOf(string? xml)
	{
		var lists = MsBuildProperties.TryGetValues(xml, "TargetFrameworks");
		var singles = lists is { Count: > 0 } ? lists : MsBuildProperties.TryGetValues(xml, "TargetFramework");
		return [.. (singles ?? [])
			.SelectMany(list => list.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))];
	}

	private static IEnumerable<(string Source, string Text)> FindUnsupportedMentions(
		string source,
		string text,
		HashSet<string> supportedKeys)
	{
		var mentions = ProseVersionRegex().Matches(text)
			.Select(match => (match.Value, Version: DotNetVersion.FromProse(match)))
			.Concat(MonikerRegex().Matches(text)
				.Select(match => (match.Value, Version: DotNetVersion.FromMoniker(match.Value))));

		foreach (var (value, version) in mentions)
		{
			if (version is not null && !supportedKeys.Contains(version.Key))
			{
				yield return (source, value);
			}
		}
	}

	[GeneratedRegex(@"\.NET\s+(?:(?<kind>Framework|Standard|Core)\s+)?(?<version>\d+(?:\.\d+)?)(?!\.\d)")]
	private static partial Regex ProseVersionRegex();

	[GeneratedRegex(@"\b(?:netstandard\d\.\d|netcoreapp\d\.\d|net\d{1,2}\.\d|net4\d{1,2})\b")]
	private static partial Regex MonikerRegex();

	/// <summary>
	/// A .NET version in one of the four families, comparable however the text spells it.
	/// </summary>
	/// <param name="Family">0 .NET Standard, 1 .NET Framework, 2 .NET Core, 3 .NET.</param>
	/// <param name="Number">The normalised version, for example <c>11.0</c> or <c>4.8</c>.</param>
	internal sealed record DotNetVersion(int Family, string Number)
	{
		/// <summary>The comparison key.</summary>
		public string Key => $"{Family}:{Number}";

		/// <summary>The name used in documentation, for example <c>.NET 11.0</c>.</summary>
		public string Display => Family switch
		{
			0 => $".NET Standard {Number}",
			1 => $".NET Framework {Number}",
			2 => $".NET Core {Number}",
			_ => $".NET {Number}"
		};

		/// <summary>Numeric ordering within a family.</summary>
		public Version SortValue => Version.TryParse(Number, out var v) ? v : new Version(0, 0);

		/// <summary>Parses a target framework moniker, or returns null for one that is not a .NET version.</summary>
		/// <param name="moniker">For example <c>net11.0</c>, <c>net48</c> or <c>netstandard2.0</c>.</param>
		public static DotNetVersion? FromMoniker(string moniker)
		{
			var name = moniker.Split('-')[0].Trim().ToLowerInvariant();

			if (name.StartsWith("netstandard", StringComparison.Ordinal))
			{
				return new(0, name["netstandard".Length..]);
			}

			if (name.StartsWith("netcoreapp", StringComparison.Ordinal))
			{
				return new(2, name["netcoreapp".Length..]);
			}

			if (!name.StartsWith("net", StringComparison.Ordinal))
			{
				return null;
			}

			var rest = name[3..];
			if (rest.Contains('.'))
			{
				return Version.TryParse(rest, out _) ? new(3, rest) : null;
			}

			return rest.Length is 2 or 3 && rest.All(char.IsDigit)
				? new(1, string.Join('.', rest.Select(c => c.ToString())))
				: null;
		}

		/// <summary>Parses a prose mention such as <c>.NET 10</c> or <c>.NET Framework 4.8</c>.</summary>
		/// <param name="match">A match of the prose pattern.</param>
		public static DotNetVersion? FromProse(Match match)
		{
			var number = match.Groups["version"].Value;
			var kind = match.Groups["kind"].Value;
			var normalised = number.Contains('.') ? number : number + ".0";
			return kind switch
			{
				"Standard" => new(0, normalised),
				"Framework" => new(1, number),
				"Core" => new(2, normalised),
				_ => int.Parse(normalised.Split('.')[0], System.Globalization.CultureInfo.InvariantCulture) >= 5
					? new(3, normalised)
					: null
			};
		}
	}
}
