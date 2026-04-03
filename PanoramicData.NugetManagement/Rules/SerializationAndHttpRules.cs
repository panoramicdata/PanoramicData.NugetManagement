using PanoramicData.NugetManagement.Models;

namespace PanoramicData.NugetManagement.Rules;

/// <summary>
/// Checks that System.Text.Json is preferred over Newtonsoft.Json.
/// </summary>
public class SystemTextJsonPreferredRule : RuleBase
{
	/// <inheritdoc />
	public override string RuleId => "SER-01";

	/// <inheritdoc />
	public override string RuleName => "System.Text.Json preferred over Newtonsoft";

	/// <inheritdoc />
	public override AssessmentCategory Category => AssessmentCategory.Serialization;

	/// <inheritdoc />
	public override AssessmentSeverity Severity => AssessmentSeverity.Warning;

	/// <inheritdoc />
	public override Task<RuleResult> EvaluateAsync(RepositoryContext context, CancellationToken cancellationToken)
	{
		var dirPackages = context.GetFileContent("Directory.Packages.props");
		if (Contains(dirPackages, "Newtonsoft.Json"))
		{
			return Task.FromResult(Fail(
				"Newtonsoft.Json is referenced in Directory.Packages.props.",
				new RuleAdvisory
				{
					Summary = "Migrate to System.Text.Json. Remove Newtonsoft.Json references.",
					Detail = "Newtonsoft.Json is referenced in `Directory.Packages.props`. Migrate all serialization code to `System.Text.Json` and remove the Newtonsoft.Json package reference.",
					Data = new() { ["file"] = "Directory.Packages.props", ["package"] = "Newtonsoft.Json" }
				}));
		}

		var csprojFiles = context.FindFiles(".csproj");
		foreach (var csproj in csprojFiles)
		{
			var content = context.GetFileContent(csproj);
			if (Contains(content, "Newtonsoft.Json"))
			{
				return Task.FromResult(Fail(
					$"Newtonsoft.Json is referenced in {csproj}.",
					new RuleAdvisory
					{
						Summary = "Migrate to System.Text.Json. Remove Newtonsoft.Json references.",
						Detail = $"Newtonsoft.Json is referenced in `{csproj}`. Migrate all serialization code to `System.Text.Json` and remove the Newtonsoft.Json package reference.",
						Data = new() { ["file"] = csproj, ["package"] = "Newtonsoft.Json" }
					}));
			}
		}

		return Task.FromResult(Pass("No Newtonsoft.Json references found."));
	}
}

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
