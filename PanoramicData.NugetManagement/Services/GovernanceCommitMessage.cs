using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace PanoramicData.NugetManagement.Services;

/// <summary>
/// The one place that builds, and reads back, the commit message of a governance remediation.
/// </summary>
/// <remarks>
/// <see cref="RegressionAttribution"/> recognises our commits by what this class writes, so the subject
/// prefix and the trailer key cannot drift apart from the matcher.
/// </remarks>
public static partial class GovernanceCommitMessage
{
	/// <summary>The trailer naming the rules a commit applied; its presence marks the commit as ours.</summary>
	public const string TrailerKey = "Governance-Remediation";

	/// <summary>The trailer value written when no rule id was recorded.</summary>
	public const string UnspecifiedRules = "unspecified";

	private const string SubjectPrefix = "NuGet governance remediation for ";
	private const int MaxListedFiles = 50;

	/// <summary>The subject of a remediation commit for a repository.</summary>
	/// <param name="repositoryFullName">The repository, as owner/name.</param>
	public static string Subject(string repositoryFullName) => $"{SubjectPrefix}{repositoryFullName}";

	/// <summary>Whether a commit subject is one <see cref="Subject"/> produces.</summary>
	/// <param name="subject">The commit subject.</param>
	public static bool IsRemediationSubject(string? subject)
		=> subject is not null && subject.StartsWith(SubjectPrefix, StringComparison.Ordinal);

	/// <summary>Builds the full message: subject, a body naming what changed, and the trailer.</summary>
	/// <param name="subject">The commit subject.</param>
	/// <param name="ruleIds">The rules applied.</param>
	/// <param name="files">The files touched.</param>
	/// <param name="packageMoves">The package version moves, as <see cref="PackageMoves"/> describes them.</param>
	public static string Compose(
		string subject,
		IReadOnlyCollection<string> ruleIds,
		IReadOnlyCollection<string> files,
		IReadOnlyCollection<string> packageMoves)
	{
		var rules = RuleList(ruleIds);
		var message = new StringBuilder(subject).Append("\n\nRules: ").Append(rules).Append('\n');

		if (files.Count > 0)
		{
			message.Append(CultureInfo.InvariantCulture, $"\nFiles ({files.Count}):\n");
			foreach (var file in files.Take(MaxListedFiles))
			{
				message.Append("- ").Append(file).Append('\n');
			}

			if (files.Count > MaxListedFiles)
			{
				message.Append(CultureInfo.InvariantCulture, $"- and {files.Count - MaxListedFiles} more\n");
			}
		}

		if (packageMoves.Count > 0)
		{
			message.Append("\nPackage versions:\n");
			foreach (var move in packageMoves)
			{
				message.Append("- ").Append(move).Append('\n');
			}
		}

		return message.Append('\n').Append(TrailerKey).Append(": ").Append(rules).ToString();
	}

	/// <summary>Splits the value of the trailer back into rule ids.</summary>
	/// <param name="trailerValue">The trailer value, as git reports it.</param>
	public static IReadOnlyList<string> ReadRuleIds(string? trailerValue)
		=> string.IsNullOrWhiteSpace(trailerValue)
			? []
			: [.. trailerValue.Split([',', ' ', '\n', '\r'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)];

	/// <summary>
	/// Describes each package whose version a unified diff of project files moved, such as
	/// <c>Serilog 3.0.0 -> 4.0.0</c>.
	/// </summary>
	/// <param name="unifiedDiff">The output of <c>git diff</c> over the project and props files.</param>
	public static IReadOnlyList<string> PackageMoves(string unifiedDiff)
	{
		var removed = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
		var added = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

		foreach (var line in unifiedDiff.Split('\n'))
		{
			var side = line.StartsWith('-') && !line.StartsWith("---", StringComparison.Ordinal) ? removed
				: line.StartsWith('+') && !line.StartsWith("+++", StringComparison.Ordinal) ? added
				: null;
			if (side is null)
			{
				continue;
			}

			var include = IncludeAttribute().Match(line);
			var version = VersionAttribute().Match(line);
			if (include.Success && version.Success)
			{
				side[include.Groups["v"].Value] = version.Groups["v"].Value;
			}
		}

		return
		[
			.. added
				.Where(pair => !removed.TryGetValue(pair.Key, out var before) || before != pair.Value)
				.OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
				.Select(pair => removed.TryGetValue(pair.Key, out var before)
					? $"{pair.Key} {before} -> {pair.Value}"
					: $"{pair.Key} {pair.Value} (added)")
		];
	}

	private static string RuleList(IReadOnlyCollection<string> ruleIds)
	{
		var clean = ruleIds
			.Select(id => id.Trim())
			.Where(id => id.Length > 0 && RuleIdShape().IsMatch(id))
			.Distinct(StringComparer.OrdinalIgnoreCase)
			.Order(StringComparer.OrdinalIgnoreCase)
			.ToList();

		return clean.Count == 0 ? UnspecifiedRules : string.Join(", ", clean);
	}

	[GeneratedRegex("""(?:Include|Update)="(?<v>[^"]+)""")]
	private static partial Regex IncludeAttribute();

	[GeneratedRegex("""Version="(?<v>[^"]+)""")]
	private static partial Regex VersionAttribute();

	[GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9._-]*$")]
	private static partial Regex RuleIdShape();
}
