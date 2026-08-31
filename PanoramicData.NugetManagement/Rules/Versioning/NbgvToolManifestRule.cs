using System.Text.Json;
using System.Text.RegularExpressions;
using PanoramicData.NugetManagement.Models;

namespace PanoramicData.NugetManagement.Rules;

/// <summary>
/// Checks that the repository does not carry a committed dotnet tool manifest for the
/// <c>nbgv</c> CLI.
/// </summary>
/// <remarks>
/// The standard resolves the version through the referenced Nerdbank.GitVersioning package's
/// MSBuild target, not the CLI: <see cref="PublishScriptMatchesMerakiRule"/> already fails a
/// publish script that shells out to <c>nbgv</c>, and the standard <c>.gitignore</c> ignores
/// <c>.config/dotnet-tools.json</c> outright. Nothing enforced either against a repository that
/// already had one committed, so the manifests survived the migration and Dependabot has been
/// dutifully bumping a tool nobody runs.
/// <para>
/// Deliberately not an <see cref="IGovernsDependency"/> rule. It cannot move a version — it
/// removes the declaration entirely — and claiming otherwise would let Dependabot triage report a
/// tool-manifest bump as covered by a remediation that never touches versions.
/// </para>
/// </remarks>
public partial class NbgvToolManifestRule : RuleBase
{
	private const string _manifestPath = ".config/dotnet-tools.json";
	private const string _nbgv = "nbgv";

	/// <summary>Files that could depend on the manifest, and are cheap enough to read in full.</summary>
	private static readonly string[] _scriptSuffixes = [".ps1", ".yml", ".yaml"];


	/// <inheritdoc />
	public override string RuleId => "VER-04";

	/// <inheritdoc />
	public override string RuleName => "nbgv tool manifest not committed";

	/// <inheritdoc />
	public override AssessmentCategory Category => AssessmentCategory.Versioning;

	/// <inheritdoc />
	public override AssessmentSeverity Severity => AssessmentSeverity.Warning;

	/// <inheritdoc />
	public override Task<RuleResult> EvaluateAsync(RepositoryContext context, CancellationToken cancellationToken)
	{
		var content = context.GetFileContent(_manifestPath);
		if (content is null)
		{
			return Task.FromResult(Pass("No dotnet tool manifest is committed."));
		}

		if (!TryReadToolNames(content, out var tools))
		{
			// An unreadable manifest proves nothing about what it declares, and "we could not parse it"
			// must never be the reason a file gets deleted.
			return Task.FromResult(Pass($"`{_manifestPath}` could not be read as a tool manifest."));
		}

		var others = tools.Where(t => !string.Equals(t, _nbgv, StringComparison.OrdinalIgnoreCase)).ToList();
		if (tools.Count > 0 && others.Count == tools.Count)
		{
			return Task.FromResult(Pass(
				$"`{_manifestPath}` declares no nbgv tool ({string.Join(", ", others)})."));
		}

		var dependants = DependingFiles(context);

		return Task.FromResult((others.Count, dependants.Count) switch
		{
			(0, 0) => Fail(
				tools.Count == 0
					? $"`{_manifestPath}` is committed but declares no tools at all."
					: $"`{_manifestPath}` pins the nbgv CLI tool, which nothing in the repository invokes.",
				new RuleAdvisory
				{
					Summary = $"Delete `{_manifestPath}`.",
					Detail = tools.Count == 0
						? $"`{_manifestPath}` declares no tools, so it does nothing except attract Dependabot "
							+ "pull requests. Delete it."
						: $"`{_manifestPath}` pins the `nbgv` CLI tool, but no script or workflow in this "
							+ "repository invokes it — the version is resolved through the referenced "
							+ "Nerdbank.GitVersioning package's MSBuild target instead. Delete the manifest so "
							+ "Dependabot stops maintaining a tool nobody runs.",
					Data = new()
					{
						["remediation_type"] = "delete_file",
						["file"] = _manifestPath
					}
				}),

			(> 0, _) => Fail(
				$"`{_manifestPath}` declares the nbgv CLI tool alongside {string.Join(", ", others)}.",
				new RuleAdvisory
				{
					Summary = $"Remove the `nbgv` entry from `{_manifestPath}`, keeping the other tools.",
					Detail = $"`{_manifestPath}` declares `nbgv` as well as {string.Join(", ", others)}. Only "
						+ "the `nbgv` entry should go — the version is resolved through the referenced "
						+ "Nerdbank.GitVersioning package's MSBuild target — and the rest of the manifest is "
						+ "not this rule's to discard."
				}),

			_ => Fail(
				$"`{_manifestPath}` pins the nbgv CLI tool, and {string.Join(", ", dependants)} still depends on it.",
				new RuleAdvisory
				{
					Summary = "Resolve the version through the Nerdbank.GitVersioning MSBuild target, then "
						+ $"delete `{_manifestPath}`.",
					Detail = $"{string.Join(", ", dependants)} runs the `nbgv` CLI or restores the manifest, so deleting "
						+ $"`{_manifestPath}` on its own would break it. Change the script to resolve the "
						+ "version with `dotnet build <project> -t:GetBuildVersion "
						+ "--getProperty:NuGetPackageVersion -p:TreatWarningsAsErrors=false`, which relies only "
						+ "on the referenced Nerdbank.GitVersioning package, drop any `dotnet tool restore` step "
						+ "that only existed to fetch it, and then remove the manifest."
				})
		});
	}

	/// <summary>
	/// The tool names a manifest declares, or false when it does not read as one.
	/// </summary>
	private static bool TryReadToolNames(string content, out List<string> tools)
	{
		tools = [];

		try
		{
			using var document = JsonDocument.Parse(content);
			if (document.RootElement.ValueKind != JsonValueKind.Object)
			{
				return false;
			}

			if (!document.RootElement.TryGetProperty("tools", out var toolsElement))
			{
				return false;
			}

			if (toolsElement.ValueKind != JsonValueKind.Object)
			{
				return false;
			}

			tools = [.. toolsElement.EnumerateObject().Select(property => property.Name)];
			return true;
		}
		catch (JsonException)
		{
			return false;
		}
	}

	/// <summary>
	/// The scripts and workflows that run the CLI, or restore the manifest to get it.
	/// </summary>
	/// <remarks>
	/// Comments do not count. A migrated <c>Publish.ps1</c> carries a comment explaining that it
	/// deliberately does <em>not</em> depend on the CLI, and reading that as a usage would withhold
	/// the fix from exactly the repositories that have already done the work.
	/// </remarks>
	private static List<string> DependingFiles(RepositoryContext context)
		=> [.. _scriptSuffixes
			.SelectMany(context.FindFiles)
			.Distinct(StringComparer.OrdinalIgnoreCase)
			.Where(path => DependsOnManifest(context.GetFileContent(path)))
			.OrderBy(path => path, StringComparer.OrdinalIgnoreCase)];

	[GeneratedRegex(@"\bnbgv\b", RegexOptions.IgnoreCase)]
	private static partial Regex NbgvWord();

	/// <summary>
	/// A tool restore depends on the manifest existing whether or not it names nbgv: deleting the
	/// manifest turns <c>dotnet tool restore</c> into a hard error.
	/// </summary>
	[GeneratedRegex(@"dotnet\s+tool\s+restore", RegexOptions.IgnoreCase)]
	private static partial Regex ToolRestore();

	private static bool DependsOnManifest(string? content)
		=> content is not null
			&& content
				.Split('\n')
				.Select(line => line.Split('#')[0])
				.Any(code => NbgvWord().IsMatch(code) || ToolRestore().IsMatch(code));
}
