using System.Text.RegularExpressions;
using PanoramicData.NugetManagement.Models;

namespace PanoramicData.NugetManagement.Rules;

/// <summary>
/// Checks that the GitHub URLs a packable project declares name this repository exactly, casing
/// included.
/// </summary>
/// <remarks>
/// GitHub resolves a repository URL case-insensitively and follows renames, so a wrong name in a
/// csproj works everywhere anyone would think to click it and is never corrected. It is still wrong,
/// and it is published: the nuspec carries it to nuget.org, this tool reads the estate's identities
/// out of it, and the clone directory, the clone's remote and the Codacy lookup all inherit it.
/// Codacy is case-sensitive, which is how Dell.CloudIq.Api — declaring itself Dell.CloudIQ.Api —
/// came to be reported as never added to Codacy while its dashboard showed it graded A.
/// </remarks>
public partial class DeclaredRepositoryUrlRule : RuleBase
{
	/// <inheritdoc />
	public override string RuleId => "META-06";

	/// <inheritdoc />
	public override string RuleName => "Declared URLs name the repository";

	/// <inheritdoc />
	public override AssessmentCategory Category => AssessmentCategory.ProjectMetadata;

	/// <inheritdoc />
	public override AssessmentSeverity Severity => AssessmentSeverity.Error;

	[GeneratedRegex(
		@"^(?:https?://)?(?:www\.)?github\.com/(?<owner>[^/?#\s]+)/(?<name>[^/?#\s]+)/?$",
		RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
	private static partial Regex GitHubRepositoryUrl { get; }

	/// <inheritdoc />
	public override Task<RuleResult> EvaluateAsync(RepositoryContext context, CancellationToken cancellationToken)
	{
		var notApplicable = PackagingCheckApplies(context, out var projects);
		if (notApplicable is not null)
		{
			return Task.FromResult(notApplicable);
		}

		var canonical = $"https://github.com/{context.FullName}";
		var wrong = new List<DeclaredUrl>();

		foreach (var project in projects)
		{
			var content = context.GetFileContent(project);

			foreach (var propertyName in new[] { "RepositoryUrl", "PackageProjectUrl" })
			{
				foreach (var declared in MsBuildProperties.TryGetValues(content, propertyName) ?? [])
				{
					if (IsWrong(declared, context.FullName, propertyName))
					{
						wrong.Add(new DeclaredUrl(project, propertyName, declared));
					}
				}
			}
		}

		if (wrong.Count == 0)
		{
			return Task.FromResult(Pass(
				$"All {projects.Count} published project(s) declare this repository by its own name."));
		}

		// One pattern per distinct declared value, rather than one per finding: the same wrong URL in
		// two properties of one project is one substitution, and Regex.Replace is case-sensitive, which
		// is the whole point here.
		var patterns = wrong
			.Select(url => url.Value)
			.Distinct(StringComparer.Ordinal)
			.ToList();

		var described = wrong
			.Select(url => $"{url.Project}: <{url.Property}> is {url.Value}")
			.ToArray();

		return Task.FromResult(Fail(
			$"{string.Join("; ", described)} — the repository is {context.FullName}.",
			new RuleAdvisory
			{
				Summary = $"Declare {canonical} in the project file(s).",
				Detail = $"""
					These declarations name something other than this repository, spelled exactly:

					{string.Join("\n", described.Select(line => $"- {line}"))}

					The repository is `{context.FullName}`, so every one of them should read
					`{canonical}`. GitHub resolves the wrong name anyway, which is why this survives
					unnoticed; the published nuspec carries it to nuget.org, and case-sensitive
					consumers of that metadata — Codacy among them — cannot find the repository at all.
					""",
				Data = new()
				{
					["remediation_type"] = "replace_regex_in_files",
					["globs"] = wrong.Select(url => url.Project).Distinct(StringComparer.Ordinal).ToArray(),
					["patterns"] = patterns.Select(Regex.Escape).ToArray(),
					["replacements"] = patterns.Select(_ => canonical).ToArray(),
					["repository"] = context.FullName,
					["canonical_url"] = canonical
				}
			}));
	}

	/// <summary>
	/// Whether a declared URL names this repository wrongly.
	/// </summary>
	/// <remarks>
	/// A <c>RepositoryUrl</c> that is a GitHub URL has to be this repository. A
	/// <c>PackageProjectUrl</c> may legitimately point at a documentation site or another project
	/// entirely, so it is only wrong when it is trying to be this repository and misspelling it.
	/// </remarks>
	private static bool IsWrong(string declared, string fullName, string propertyName)
	{
		var match = GitHubRepositoryUrl.Match(TrimGitSuffix(declared.Trim()));
		if (!match.Success)
		{
			return false;
		}

		var declaredFullName = $"{match.Groups["owner"].Value}/{match.Groups["name"].Value}";
		if (string.Equals(declaredFullName, fullName, StringComparison.Ordinal))
		{
			return false;
		}

		return string.Equals(declaredFullName, fullName, StringComparison.OrdinalIgnoreCase)
			|| propertyName == "RepositoryUrl";
	}

	private static string TrimGitSuffix(string url)
		=> url.EndsWith(".git", StringComparison.OrdinalIgnoreCase) ? url[..^4] : url;

	private sealed record DeclaredUrl(string Project, string Property, string Value);
}
