using PanoramicData.NugetManagement.Models;

namespace PanoramicData.NugetManagement.Rules;

/// <summary>
/// Checks that the expected HTTP client package is used (configurable, defaults to Refit).
/// </summary>
public class ExpectedHttpClientPackageRule : RuleBase
{
	/// <inheritdoc />
	public override string RuleId => "HTTP-01";

	/// <inheritdoc />
	public override string RuleName => "Expected HTTP client package referenced";

	/// <inheritdoc />
	public override AssessmentCategory Category => AssessmentCategory.HttpClient;

	/// <inheritdoc />
	public override AssessmentSeverity Severity => AssessmentSeverity.Info;

	/// <summary>
	/// Packages whose presence indicates the repository does HTTP client work, and so is expected to
	/// use the configured HTTP client package. Deliberately limited to project-file references so the
	/// rule behaves identically for locally-cloned and remotely-assessed repositories (remote
	/// assessment only fetches project/props/workflow files, never source).
	/// </summary>
	private static readonly string[] HttpClientIndicators =
	[
		"Refit",
		"Microsoft.Extensions.Http",
		"System.Net.Http",
		"RestSharp",
		"Flurl"
	];

	/// <inheritdoc />
	public override Task<RuleResult> EvaluateAsync(RepositoryContext context, CancellationToken cancellationToken)
	{
		var expected = context.Options.ExpectedHttpClientPackage;
		var dirPackages = context.GetFileContent("Directory.Packages.props");
		if (ReferencesPackage(dirPackages, expected, includeVariants: true))
		{
			return Task.FromResult(Pass($"Expected HTTP client package \"{expected}\" is referenced."));
		}

		var csprojFiles = context.FindNonTestProjectFiles().ToList();

		if (csprojFiles.Any(csproj => ReferencesPackage(context.GetFileContent(csproj), expected, includeVariants: true)))
		{
			return Task.FromResult(Pass($"Expected HTTP client package \"{expected}\" is referenced."));
		}

		// The expected package is absent. Only require it where the repository actually does HTTP
		// client work — otherwise the rule does not apply (e.g. a templating or serialisation library).
		var dirBuild = context.GetFileContent("Directory.Build.props");
		var doesHttp = HttpClientIndicators.Any(indicator =>
			ReferencesPackage(dirPackages, indicator, includeVariants: true)
			|| ReferencesPackage(dirBuild, indicator, includeVariants: true)
			|| csprojFiles.Any(csproj => ReferencesPackage(context.GetFileContent(csproj), indicator, includeVariants: true)));

		return Task.FromResult(!doesHttp
			? NotApplicable(
				"No HTTP client usage detected in any non-test project, so an HTTP client package is not required.")
			: Fail(
				$"Expected HTTP client package \"{expected}\" is not referenced in any non-test project.",
				new RuleAdvisory
				{
					Summary = $"Add a {expected} package reference. Use {expected} for HTTP client interfaces.",
					Detail = $"The expected HTTP client package `{expected}` is not referenced in any non-test project. Add a `{expected}` package reference and use it for HTTP client interfaces.",
					Data = new()
					{
						["expected_package"] = expected
					}
				}));
	}
}
