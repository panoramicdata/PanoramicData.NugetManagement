using PanoramicData.NugetManagement.Models;

namespace PanoramicData.NugetManagement.Rules;

/// <summary>
/// Checks that a test project exists.
/// </summary>
public class TestProjectExistsRule : RuleBase
{
	/// <inheritdoc />
	public override string RuleId => "TST-01";

	/// <inheritdoc />
	public override string RuleName => "Test project exists";

	/// <inheritdoc />
	public override AssessmentCategory Category => AssessmentCategory.Testing;

	/// <inheritdoc />
	public override AssessmentSeverity Severity => AssessmentSeverity.Error;

	/// <inheritdoc />
	public override Task<RuleResult> EvaluateAsync(RepositoryContext context, CancellationToken cancellationToken)
	{
		var hasTestProject = context.FindFiles(".csproj")
			.Any(f => f.Contains(".Test", StringComparison.OrdinalIgnoreCase) ||
					  f.Contains(".Tests", StringComparison.OrdinalIgnoreCase));

		return Task.FromResult(hasTestProject
			? Pass("Test project found.")
			: Fail(
				"No test project found (*.Test.csproj or *.Tests.csproj).",
				new RuleAdvisory
				{
					Summary = "Create a test project using xUnit v3.",
					Detail = "No test project was found matching `*.Test.csproj` or `*.Tests.csproj`. Create a test project using xUnit v3.",
					Data = new() { ["expected_pattern"] = "*.Test.csproj or *.Tests.csproj" }
				}));
	}
}

/// <summary>
/// Checks that xUnit v3 is referenced (not xUnit v2).
/// </summary>
public class XunitV3Rule : RuleBase
{
	/// <inheritdoc />
	public override string RuleId => "TST-02";

	/// <inheritdoc />
	public override string RuleName => "xUnit v3 referenced";

	/// <inheritdoc />
	public override AssessmentCategory Category => AssessmentCategory.Testing;

	/// <inheritdoc />
	public override AssessmentSeverity Severity => AssessmentSeverity.Warning;

	/// <inheritdoc />
	public override Task<RuleResult> EvaluateAsync(RepositoryContext context, CancellationToken cancellationToken)
	{
		var dirPackages = context.GetFileContent("Directory.Packages.props");

		// Check for xunit.v3 in centralized packages
		if (Contains(dirPackages, "xunit.v3"))
		{
			return Task.FromResult(Pass("xUnit v3 is referenced."));
		}

		// Check individual test .csproj files
		var testProjects = context.FindFiles(".csproj")
			.Where(f => f.Contains(".Test", StringComparison.OrdinalIgnoreCase));

		foreach (var tp in testProjects)
		{
			var content = context.GetFileContent(tp);
			if (Contains(content, "xunit.v3"))
			{
				return Task.FromResult(Pass("xUnit v3 is referenced."));
			}
		}

		return Task.FromResult(Fail(
			"xUnit v3 is not referenced. Legacy xUnit v2 may be in use.",
			new RuleAdvisory
			{
				Summary = "Replace xunit/xunit.core/xunit.runner.visualstudio v2 references with xunit.v3 and xunit.runner.visualstudio v3.",
				Detail = "xUnit v3 is not referenced in `Directory.Packages.props` or any test project. Replace legacy `xunit`/`xunit.core`/`xunit.runner.visualstudio` v2 references with `xunit.v3` and `xunit.runner.visualstudio` v3.",
				Data = new() { ["expected_package"] = "xunit.v3" }
			}));
	}
}

/// <summary>
/// Checks that Microsoft.NET.Test.Sdk is referenced.
/// </summary>
public class TestSdkPresentRule : RuleBase
{
	/// <inheritdoc />
	public override string RuleId => "TST-03";

	/// <inheritdoc />
	public override string RuleName => "Microsoft.NET.Test.Sdk referenced";

	/// <inheritdoc />
	public override AssessmentCategory Category => AssessmentCategory.Testing;

	/// <inheritdoc />
	public override AssessmentSeverity Severity => AssessmentSeverity.Error;

	/// <inheritdoc />
	public override Task<RuleResult> EvaluateAsync(RepositoryContext context, CancellationToken cancellationToken)
	{
		var dirPackages = context.GetFileContent("Directory.Packages.props");
		if (Contains(dirPackages, "Microsoft.NET.Test.Sdk"))
		{
			return Task.FromResult(Pass("Microsoft.NET.Test.Sdk is referenced."));
		}

		var testProjects = context.FindFiles(".csproj")
			.Where(f => f.Contains(".Test", StringComparison.OrdinalIgnoreCase));

		foreach (var tp in testProjects)
		{
			var content = context.GetFileContent(tp);
			if (Contains(content, "Microsoft.NET.Test.Sdk"))
			{
				return Task.FromResult(Pass("Microsoft.NET.Test.Sdk is referenced."));
			}
		}

		return Task.FromResult(Fail(
			"Microsoft.NET.Test.Sdk is not referenced.",
			new RuleAdvisory
			{
				Summary = "Add Microsoft.NET.Test.Sdk to the test project.",
				Detail = "Microsoft.NET.Test.Sdk is not referenced in `Directory.Packages.props` or any test project. Add a reference to enable test discovery and execution.",
				Data = new() { ["expected_package"] = "Microsoft.NET.Test.Sdk" }
			}));
	}
}

/// <summary>
/// Checks that coverlet.collector is referenced for code coverage.
/// </summary>
public class CoverletCollectorRule : RuleBase
{
	/// <inheritdoc />
	public override string RuleId => "TST-04";

	/// <inheritdoc />
	public override string RuleName => "coverlet.collector referenced";

	/// <inheritdoc />
	public override AssessmentCategory Category => AssessmentCategory.Testing;

	/// <inheritdoc />
	public override AssessmentSeverity Severity => AssessmentSeverity.Warning;

	/// <inheritdoc />
	public override Task<RuleResult> EvaluateAsync(RepositoryContext context, CancellationToken cancellationToken)
	{
		var dirPackages = context.GetFileContent("Directory.Packages.props");
		if (Contains(dirPackages, "coverlet.collector"))
		{
			return Task.FromResult(Pass("coverlet.collector is referenced."));
		}

		var testProjects = context.FindFiles(".csproj")
			.Where(f => f.Contains(".Test", StringComparison.OrdinalIgnoreCase));

		return Task.FromResult(testProjects.Any(tp => Contains(context.GetFileContent(tp), "coverlet.collector"))
			? Pass("coverlet.collector is referenced.")
			: Fail(
				"coverlet.collector is not referenced.",
				new RuleAdvisory
				{
					Summary = "Add coverlet.collector to the test project for code coverage collection.",
					Detail = "coverlet.collector is not referenced in `Directory.Packages.props` or any test project. Add it to enable code coverage collection.",
					Data = new() { ["expected_package"] = "coverlet.collector" }
				}));
	}
}
