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
	public override AssessmentSeverity Severity => AssessmentSeverity.Warning;

	/// <inheritdoc />
	public override Task<RuleResult> EvaluateAsync(RepositoryContext context, CancellationToken cancellationToken)
	{
		var expected = context.Options.ExpectedHttpClientPackage;
		var dirPackages = context.GetFileContent("Directory.Packages.props");
		if (Contains(dirPackages, expected))
		{
			return Task.FromResult(Pass($"Expected HTTP client package \"{expected}\" is referenced."));
		}

		var csprojFiles = context.FindFiles(".csproj")
			.Where(f => !f.Contains(".Test", StringComparison.OrdinalIgnoreCase));

		return Task.FromResult(csprojFiles.Any(csproj => Contains(context.GetFileContent(csproj), expected))
			? Pass($"Expected HTTP client package \"{expected}\" is referenced.")
			: Fail(
				$"Expected HTTP client package \"{expected}\" is not referenced in any non-test project.",
				new RuleAdvisory
				{
					Summary = $"Add a {expected} package reference. Use {expected} for HTTP client interfaces.",
					Detail = $"The expected HTTP client package `{expected}` is not referenced in any non-test project. Add a `{expected}` package reference and use it for HTTP client interfaces.",
					Data = new() { ["expected_package"] = expected }
				}));
	}
}
