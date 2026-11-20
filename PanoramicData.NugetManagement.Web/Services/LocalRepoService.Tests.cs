using System.Diagnostics;
using System.Text.RegularExpressions;
using PanoramicData.NugetManagement.Models;
using PanoramicData.NugetManagement.Services;
using PanoramicData.NugetManagement.Web.Models;

namespace PanoramicData.NugetManagement.Web.Services;

public partial class LocalRepoService
{
	/// <summary>
	/// Runs dotnet test on the repository.
	/// Uses --no-build because the Build step has already compiled the solution.
	/// This avoids the confusing "Build FAILED." MSBuild output that appears
	/// when tests fail (MSBuild's VSTest target reports its failure as a build failure).
	/// </summary>
	public async Task<(bool Success, string Output)> RunTestsAsync(
		string repoIdentity,
		Action<string>? onOutput,
		CancellationToken cancellationToken,
		bool sandbox,
		IReadOnlyList<string>? testClasses)
	{
		var path = GetLocalPath(repoIdentity);
		_logger.LogInformation("Running tests in {Path}", path);

		// See TestSandbox: a model-driven run sees CI's environment, not this machine's credentials.
		var environment = sandbox ? TestSandbox.Overrides(null) : null;

		var testProjects = GetConfiguredTestProjects(path);
		if (testProjects.Count == 0)
		{
			return await RunCommandWithStreamingAsync(path, "dotnet", ["test", "--no-build", "--no-restore", "--verbosity", "normal"], onOutput, cancellationToken, environment).ConfigureAwait(false);
		}

		var combinedOutput = new List<string>();
		var allPassed = true;
		var projectNumber = 0;

		foreach (var (projectPath, projectConfig) in testProjects)
		{
			projectNumber++;
			if (projectConfig?.DefaultTestingLevel == ProjectTestingLevel.None)
			{
				onOutput?.Invoke($"⏭️ Skipping tests for {projectPath} (DefaultTestingLevel=None).");
				continue;
			}

			var args = BuildTestArguments(path, projectPath, projectConfig, testClasses);

			onOutput?.Invoke($"▶ Test project {projectNumber} of {testProjects.Count}: {projectPath}");
			onOutput?.Invoke($"▶ dotnet {string.Join(' ', args)}");
			var (success, output) = await RunCommandWithStreamingAsync(path, "dotnet", args, onOutput, cancellationToken, environment).ConfigureAwait(false);
			combinedOutput.Add(output);
			allPassed &= success;
		}

		return (allPassed, string.Join('\n', combinedOutput));
	}

	private static List<string> BuildTestArguments(string path, string projectPath, NugetManagementProjectConfig? projectConfig, IReadOnlyList<string>? testClasses)
	{
		var args = new List<string> { "test", projectPath, "--no-build", "--no-restore", "--verbosity", "normal" };
		var filter = BuildTestFilter(projectConfig);

		if (testClasses is { Count: > 0 })
		{
			// Only the classes asked for, in whichever form this project's runner takes. Replaces the
			// project's own filter rather than adding to it: two filters on one run fight.
			var projectFile = Path.Combine(path, projectPath);
			var packages = Path.Combine(path, "Directory.Packages.props");

			args.AddRange(CoverageMeasurement.ClassFilterArguments(
				CoverageMeasurement.UsesTestingPlatform(
					File.Exists(projectFile) ? File.ReadAllText(projectFile) : string.Empty,
					File.Exists(packages) ? File.ReadAllText(packages) : string.Empty),
				testClasses));
		}
		else if (!string.IsNullOrWhiteSpace(filter))
		{
			args.Add("--filter");
			args.Add(filter);
		}

		return args;
	}

	/// <summary>
	/// The arguments for running one test project under the code-coverage collector.
	/// </summary>
	/// <param name="testProject">The test project path, relative to the clone.</param>
	/// <param name="outputFile">Where the Cobertura report is to be written.</param>
	/// <param name="filter">A test filter, or null for every test.</param>
	public static IReadOnlyList<string> BuildCoverageArguments(string testProject, string outputFile, string? filter)
	{
		var args = new List<string>
		{
			"test", testProject, "--no-restore", "--coverage",
			"--coverage-output-format", "cobertura",
			"--coverage-output", outputFile
		};

		if (!string.IsNullOrWhiteSpace(filter))
		{
			args.Add("--filter");
			args.Add(filter);
		}

		return args;
	}

	/// <summary>
	/// Runs the repository's tests with coverage and returns the Cobertura report.
	/// </summary>
	/// <param name="repoIdentity">The repository.</param>
	/// <param name="cancellationToken">A cancellation token.</param>
	/// <returns>The report's XML, or null if the run produced none.</returns>
	/// <remarks>
	/// Always sandboxed (see <see cref="TestSandbox"/>): this runs tests a model wrote, so they see CI's
	/// environment rather than this machine's credentials.
	/// </remarks>
	public async Task<string?> RunTestsWithCoverageAsync(string repoIdentity, CancellationToken cancellationToken)
	{
		var path = GetLocalPath(repoIdentity);
		var environment = TestSandbox.Overrides(null);
		var reports = new List<string>();

		foreach (var (projectPath, _) in GetConfiguredTestProjects(path))
		{
			var output = Path.Combine(Path.GetTempPath(), $"aichange-cov-{Guid.NewGuid():N}.xml");

			await RunCommandWithStreamingAsync(path, "dotnet", BuildCoverageArguments(projectPath, output, null), null, cancellationToken, environment).ConfigureAwait(false);

			if (File.Exists(output))
			{
				reports.Add(await File.ReadAllTextAsync(output, cancellationToken).ConfigureAwait(false));
				File.Delete(output);
			}
		}

		// One root element so several test project reports parse as a single document.
		return reports.Count == 0
			? null
			: "<reports>" + string.Concat(reports.Select(StripXmlDeclaration)) + "</reports>";
	}

	private static string StripXmlDeclaration(string xml)
	{
		var trimmed = xml.TrimStart('﻿', ' ', '\r', '\n', '\t');

		if (!trimmed.StartsWith("<?xml", StringComparison.Ordinal))
		{
			return trimmed;
		}

		var end = trimmed.IndexOf("?>", StringComparison.Ordinal);

		return end < 0 ? trimmed : trimmed[(end + 2)..];
	}

	internal static List<(string ProjectPath, NugetManagementProjectConfig? Config)> GetConfiguredTestProjects(string localPath)
	{
		var configPath = Path.Combine(localPath, NugetManagementRepositoryConfig.FileName);
		var rawConfig = File.Exists(configPath) ? File.ReadAllText(configPath) : null;
		var repositoryConfig = NugetManagementRepositoryConfigParser.Parse(rawConfig);

		var projects = Directory
			.EnumerateFiles(localPath, "*.csproj", SearchOption.AllDirectories)
			.Select(fullPath => Path.GetRelativePath(localPath, fullPath).Replace('\\', '/'))
			.Where(relativePath => !relativePath.Contains("/bin/", StringComparison.OrdinalIgnoreCase))
			.Where(relativePath => !relativePath.Contains("/obj/", StringComparison.OrdinalIgnoreCase))
			.ToList();

		var selected = new List<(string ProjectPath, NugetManagementProjectConfig? Config)>();
		foreach (var project in projects)
		{
			var projectConfig = repositoryConfig?.GetProjectConfig(project);
			if (IsSelectedForTesting(project, projectConfig))
			{
				selected.Add((project, projectConfig));
			}
		}

		return selected;
	}

	private static bool IsSelectedForTesting(string project, NugetManagementProjectConfig? projectConfig)
	{
		var testing = projectConfig?.TestingTreatment;
		if (testing is ProjectTreatment.Exclude or ProjectTreatment.Include)
		{
			return testing == ProjectTreatment.Include;
		}

		return projectConfig?.Treatment != ProjectTreatment.Exclude
			&& IsLikelyTestProject(project)
			&& IsAutoIncludedProject(project);
	}

	private static string BuildTestFilter(NugetManagementProjectConfig? config)
	{
		if (config is null)
		{
			return string.Empty;
		}

		var baseExpression = string.Join('&', CategoryClauses(config));

		var includeTests = TestRuleExpressions(config, ProjectTreatment.Include, "FullyQualifiedName~");
		var excludeTests = TestRuleExpressions(config, ProjectTreatment.Exclude, "FullyQualifiedName!~");

		var combined = baseExpression;
		if (includeTests.Length > 0)
		{
			var explicitTestIncludeExpression = "(" + string.Join('|', includeTests) + ")";
			combined = string.IsNullOrWhiteSpace(baseExpression)
				? explicitTestIncludeExpression
				: $"(({baseExpression})|{explicitTestIncludeExpression})";
		}

		foreach (var exclusion in excludeTests)
		{
			combined = string.IsNullOrWhiteSpace(combined)
				? exclusion
				: $"({combined})&({exclusion})";
		}

		return combined;
	}

	private static List<string> CategoryClauses(NugetManagementProjectConfig config)
	{
		var baseClauses = new List<string>();

		if (config.DefaultTestingLevel == ProjectTestingLevel.Smoke)
		{
			baseClauses.Add("(TestCategory!=Integration&Category!=Integration)");
			baseClauses.Add("(TestCategory!=Slow&Category!=Slow)");
		}

		var includeExpression = CollectionNames(config, ProjectTreatment.Include)
			.Select(value => $"(TestCategory={value}|Category={value})")
			.ToArray();

		if (includeExpression.Length > 0)
		{
			baseClauses.Add("(" + string.Join("|", includeExpression) + ")");
		}

		baseClauses.AddRange(CollectionNames(config, ProjectTreatment.Exclude)
			.Select(value => $"(TestCategory!={value}&Category!={value})"));

		return baseClauses;
	}

	private static string[] CollectionNames(NugetManagementProjectConfig config, ProjectTreatment treatment)
		=> [.. config.CollectionTreatments
			.Where(rule => !string.IsNullOrWhiteSpace(rule.Name) && rule.Treatment == treatment)
			.Select(rule => rule.Name.Trim())];

	private static string[] TestRuleExpressions(NugetManagementProjectConfig config, ProjectTreatment treatment, string prefix)
		=> [.. config.TestTreatments
			.Where(rule => !string.IsNullOrWhiteSpace(rule.Id) && rule.Treatment == treatment)
			.Select(rule => $"{prefix}{rule.Id.Trim()}")];

	private static bool IsLikelyTestProject(string projectPath)
	{
		var fileName = Path.GetFileName(projectPath);
		return fileName.Contains(".Test", StringComparison.OrdinalIgnoreCase)
			|| fileName.Contains(".Tests", StringComparison.OrdinalIgnoreCase)
			|| fileName.EndsWith("Tests.csproj", StringComparison.OrdinalIgnoreCase)
			|| fileName.EndsWith("Test.csproj", StringComparison.OrdinalIgnoreCase);
	}

	private static bool IsAutoIncludedProject(string projectPath)
		=> !projectPath.Contains("/Fixtures/", StringComparison.OrdinalIgnoreCase)
			&& !projectPath.Contains("/TestData/", StringComparison.OrdinalIgnoreCase)
			&& !projectPath.Contains("/Samples/", StringComparison.OrdinalIgnoreCase);
}
