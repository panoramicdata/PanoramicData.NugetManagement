using Microsoft.Extensions.Options;
using Octokit;
using PanoramicData.NugetManagement.Models;
using PanoramicData.NugetManagement.Rules;
using PanoramicData.NugetManagement.Services;
using PanoramicData.NugetManagement.Web.Models;
using System.Xml.Linq;

namespace PanoramicData.NugetManagement.Web.Services;

/// <summary>
/// Orchestrates package discovery, assessment, remediation, testing, and publishing.
/// </summary>
public class DashboardService
{
	private readonly NuGetDiscoveryService _nuget;
	private readonly LocalRepoService _localRepo;
	private readonly AppSettings _settings;
	private readonly ILogger<DashboardService> _logger;

	/// <summary>
	/// Initializes a new instance of the <see cref="DashboardService"/> class.
	/// </summary>
	public DashboardService(
		NuGetDiscoveryService nuget,
		LocalRepoService localRepo,
		IOptions<AppSettings> settings,
		ILogger<DashboardService> logger)
	{
		_nuget = nuget;
		_localRepo = localRepo;
		_settings = settings.Value;
		_logger = logger;
	}

	/// <summary>
	/// Discovers all packages and builds initial dashboard rows.
	/// </summary>
	public async Task<List<PackageDashboardRow>> DiscoverPackagesAsync(CancellationToken cancellationToken = default)
	{
		var packages = await _nuget.DiscoverOrganizationPackagesAsync(cancellationToken).ConfigureAwait(false);
		var rows = new List<PackageDashboardRow>();

		foreach (var pkg in packages)
		{
			var repoName = pkg.RepositoryName;
			var isCloned = repoName is not null && _localRepo.IsClonedLocally(repoName);

			var row = new PackageDashboardRow
			{
				PackageId = pkg.PackageId,
				LatestVersion = pkg.LatestVersion,
				RepositoryFullName = repoName is not null ? $"{_settings.GitHubOrganization}/{repoName}" : null,
				RepositoryUrl = pkg.RepositoryUrl,
				IsClonedLocally = isCloned,
				LocalPath = repoName is not null ? _localRepo.GetLocalPath(repoName) : null,
				SlnxPath = isCloned && repoName is not null ? _localRepo.FindSlnxFile(repoName) : null,
				Status = isCloned ? PackageStatus.NotAssessed : PackageStatus.NotCloned
			};

			if (isCloned && repoName is not null)
			{
				row.CurrentBranch = await _localRepo.GetCurrentBranchAsync(repoName, cancellationToken).ConfigureAwait(false);
				row.IsWorkingTreeClean = await _localRepo.IsWorkingTreeCleanAsync(repoName, cancellationToken).ConfigureAwait(false);
			}

			rows.Add(row);
		}

		return rows;
	}

	/// <summary>
	/// Clones a repository locally.
	/// </summary>
	public async Task CloneRepositoryAsync(
		PackageDashboardRow row,
		Action<string>? onOutput = null,
		CancellationToken cancellationToken = default)
	{
		if (row.RepositoryUrl is null)
		{
			row.Status = PackageStatus.Error;
			row.StatusMessage = "No repository URL available.";
			return;
		}

		var repoName = ExtractRepoName(row.RepositoryUrl);
		if (repoName is null)
		{
			row.Status = PackageStatus.Error;
			row.StatusMessage = "Cannot determine repo name from URL.";
			return;
		}

		row.Status = PackageStatus.Cloning;
		row.StatusMessage = "Cloning...";

		var cloneUrl = $"https://github.com/{_settings.GitHubOrganization}/{repoName}.git";
		var (success, output) = await _localRepo.CloneAsync(cloneUrl, repoName, onOutput, cancellationToken).ConfigureAwait(false);

		if (success)
		{
			row.IsClonedLocally = true;
			row.LocalPath = _localRepo.GetLocalPath(repoName);
			row.CurrentBranch = await _localRepo.GetCurrentBranchAsync(repoName, cancellationToken).ConfigureAwait(false);
			row.IsWorkingTreeClean = await _localRepo.IsWorkingTreeCleanAsync(repoName, cancellationToken).ConfigureAwait(false);
			row.Status = PackageStatus.NotAssessed;
			row.StatusMessage = "Cloned successfully.";
		}
		else
		{
			row.Status = PackageStatus.Error;
			row.StatusMessage = $"Clone failed: {output}";
		}
	}

	/// <summary>
	/// Assesses a single repository against all governance rules using GitHub API.
	/// </summary>
	public async Task AssessRepositoryAsync(
		PackageDashboardRow row,
		IGitHubClient github,
		CancellationToken cancellationToken = default)
	{
		if (row.RepositoryFullName is null)
		{
			row.Status = PackageStatus.Error;
			row.StatusMessage = "No repository identified.";
			return;
		}

		row.Status = PackageStatus.Assessing;
		row.StatusMessage = "Assessing...";

		try
		{
			var parts = row.RepositoryFullName.Split('/');
			if (parts.Length != 2)
			{
				row.Status = PackageStatus.Error;
				row.StatusMessage = "Invalid repository full name.";
				return;
			}

			var repo = await github.Repository.Get(parts[0], parts[1]).ConfigureAwait(false);
			var repoOptions = new RepoOptions
			{
				ExpectedLicense = _settings.ExpectedLicense,
				ExpectedCopyrightHolder = _settings.CopyrightHolder,
			};

			if (!string.IsNullOrEmpty(_settings.CodacyApiToken))
			{
				repoOptions.Codacy = new CodacyOptions
				{
					ApiToken = _settings.CodacyApiToken
				};
			}

			using var loggerFactory = LoggerFactory.Create(b => b.AddConsole());
			using var contextBuilder = new RepositoryContextBuilder(github, loggerFactory.CreateLogger<RepositoryContextBuilder>());
			var context = await contextBuilder.BuildAsync(repo, repoOptions, cancellationToken).ConfigureAwait(false);

			var rules = RuleRegistry.Rules;
			var results = new List<RuleResult>();

			foreach (var rule in rules)
			{
				if (repoOptions.SuppressedRules.Contains(rule.RuleId))
				{
					continue;
				}

				var result = await rule.EvaluateAsync(context, cancellationToken).ConfigureAwait(false);
				results.Add(result);
			}

			row.Assessment = new RepoAssessment
			{
				RepositoryFullName = row.RepositoryFullName,
				DefaultBranch = context.DefaultBranch,
				AssessedAtUtc = DateTimeOffset.UtcNow,
				RuleResults = results
			};

			// Build category summaries
			row.CategorySummaries = BuildCategorySummaries(results);
			row.Status = PackageStatus.Assessed;
			row.StatusMessage = $"{row.TotalFailures} issue(s) found.";
		}
		catch (Exception ex)
		{
			_logger.LogError(ex, "Failed to assess {Repo}", row.RepositoryFullName);
			row.Status = PackageStatus.Error;
			row.StatusMessage = $"Assessment failed: {ex.Message}";
		}
	}

	/// <summary>
	/// Assesses a single repository against all governance rules using the local filesystem.
	/// This reads files directly from disk so that changes made by remediations are
	/// immediately visible without pushing to GitHub first.
	/// </summary>
	public async Task AssessLocalRepositoryAsync(
		PackageDashboardRow row,
		CancellationToken cancellationToken = default)
	{
		if (row.RepositoryFullName is null || row.LocalPath is null)
		{
			row.Status = PackageStatus.Error;
			row.StatusMessage = "No repository or local path identified.";
			return;
		}

		row.Status = PackageStatus.Assessing;
		row.StatusMessage = "Assessing (local)...";

		try
		{
			var repoOptions = new RepoOptions
			{
				ExpectedLicense = _settings.ExpectedLicense,
				ExpectedCopyrightHolder = _settings.CopyrightHolder,
			};

			if (!string.IsNullOrEmpty(_settings.CodacyApiToken))
			{
				repoOptions.Codacy = new CodacyOptions
				{
					ApiToken = _settings.CodacyApiToken
				};
			}

			using var loggerFactory = LoggerFactory.Create(b => b.AddConsole());
			var localBuilder = new LocalRepositoryContextBuilder(loggerFactory.CreateLogger<LocalRepositoryContextBuilder>());
			var context = localBuilder.Build(row.LocalPath, row.RepositoryFullName, repoOptions);

			var rules = RuleRegistry.Rules;
			var results = new List<RuleResult>();

			foreach (var rule in rules)
			{
				if (repoOptions.SuppressedRules.Contains(rule.RuleId))
				{
					continue;
				}

				var result = await rule.EvaluateAsync(context, cancellationToken).ConfigureAwait(false);
				results.Add(result);
			}

			row.Assessment = new RepoAssessment
			{
				RepositoryFullName = row.RepositoryFullName,
				DefaultBranch = context.DefaultBranch,
				AssessedAtUtc = DateTimeOffset.UtcNow,
				RuleResults = results
			};

			row.CategorySummaries = BuildCategorySummaries(results);
			row.Status = PackageStatus.Assessed;
			row.StatusMessage = $"{row.TotalFailures} issue(s) found (local).";
		}
		catch (Exception ex)
		{
			_logger.LogError(ex, "Failed to locally assess {Repo}", row.RepositoryFullName);
			row.Status = PackageStatus.Error;
			row.StatusMessage = $"Local assessment failed: {ex.Message}";
		}
	}

	/// <summary>
	/// Generates an AI remediation prompt from failed rules.
	/// </summary>
	public static string GenerateRemediationPrompt(PackageDashboardRow row)
	{
		if (row.Assessment is null)
		{
			return string.Empty;
		}

		var failures = row.Assessment.RuleResults.Where(r => !r.Passed).ToList();
		return GeneratePromptFromFailures(row, failures);
	}

	/// <summary>
	/// Generates an AI remediation prompt for a specific category's failed rules.
	/// </summary>
	public static string GenerateCategoryRemediationPrompt(PackageDashboardRow row, AssessmentCategory category)
	{
		if (row.Assessment is null)
		{
			return string.Empty;
		}

		var failures = row.Assessment.RuleResults.Where(r => !r.Passed && r.Category == category).ToList();
		return GeneratePromptFromFailures(row, failures);
	}

	private static string GeneratePromptFromFailures(PackageDashboardRow row, List<RuleResult> failures)
	{
		if (failures.Count == 0)
		{
			return string.Empty;
		}

		var lines = new List<string>
		{
			$"# Remediation Instructions for {row.PackageId}",
			$"Repository: {row.RepositoryFullName}",
			$"Local path: {row.LocalPath}",
			"",
			"Please fix the following governance issues:",
			""
		};

		foreach (var failure in failures)
		{
			lines.Add($"## [{failure.RuleId}] {failure.RuleName}");
			lines.Add($"- **Issue**: {failure.Message}");
			if (failure.Advisory is not null)
			{
				lines.Add($"- **Fix**: {failure.Advisory.Detail}");
				if (failure.Advisory.Data.Count > 0)
				{
					foreach (var (key, value) in failure.Advisory.Data)
					{
						lines.Add($"  - `{key}`: {FormatDataValue(value)}");
					}
				}
			}

			lines.Add("");
		}

		return string.Join('\n', lines);
	}

	/// <summary>
	/// Applies automatic file-based remediations for all failed rules that have
	/// a recognized <c>remediation_type</c> or <c>expected_path</c> + <c>template_content</c>.
	/// Returns the list of files created/modified.
	/// </summary>
	public Task<List<string>> ApplyRemediationsAsync(
		PackageDashboardRow row,
		Action<string>? onOutput = null,
		CancellationToken cancellationToken = default)
	{
		var applied = new List<string>();

		if (row.Assessment is null || row.LocalPath is null)
		{
			onOutput?.Invoke("⚠️ No assessment data or local path — cannot apply remediations.");
			return Task.FromResult(applied);
		}

		var failures = row.Assessment.RuleResults.Where(r => !r.Passed && r.Advisory is not null).ToList();

		foreach (var failure in failures)
		{
			cancellationToken.ThrowIfCancellationRequested();
			ApplySingleRemediation(row.LocalPath, failure, applied, onOutput);
		}

		if (applied.Count == 0)
		{
			onOutput?.Invoke("ℹ️ No auto-remediable issues found.");
		}
		else
		{
			onOutput?.Invoke($"✅ Applied {applied.Count} remediation(s).");
		}

		return Task.FromResult(applied);
	}

	/// <summary>
	/// Applies automatic remediations for a specific category.
	/// </summary>
	public Task<List<string>> ApplyCategoryRemediationsAsync(
		PackageDashboardRow row,
		AssessmentCategory category,
		Action<string>? onOutput = null,
		CancellationToken cancellationToken = default)
	{
		var applied = new List<string>();

		if (row.Assessment is null || row.LocalPath is null)
		{
			onOutput?.Invoke("⚠️ No assessment data or local path — cannot apply remediations.");
			return Task.FromResult(applied);
		}

		var failures = row.Assessment.RuleResults
			.Where(r => !r.Passed && r.Category == category && r.Advisory is not null)
			.ToList();

		foreach (var failure in failures)
		{
			cancellationToken.ThrowIfCancellationRequested();
			ApplySingleRemediation(row.LocalPath, failure, applied, onOutput);
		}

		if (applied.Count == 0)
		{
			onOutput?.Invoke($"ℹ️ No auto-remediable issues found in {category}.");
		}

		return Task.FromResult(applied);
	}

	/// <summary>
	/// Checks if a specific failed rule can be auto-remediated.
	/// </summary>
	public static bool IsAutoRemediable(RuleResult result)
	{
		if (result.Passed || result.Advisory is null)
		{
			return false;
		}

		var data = result.Advisory.Data;

		// create_file: expected_path + template_content
		if (data.ContainsKey("expected_path") && data.ContainsKey("template_content"))
		{
			return true;
		}

		// All other types identified by remediation_type key
		if (data.TryGetValue("remediation_type", out var rtObj) && rtObj is string rt)
		{
			return rt is "ensure_xml_property"
				or "ensure_csproj_property"
				or "append_line"
				or "prepend_line"
				or "add_slnx_file_entries";
		}

		return false;
	}

	/// <summary>
	/// Public entry point for applying a single remediation from outside the service.
	/// </summary>
	public void ApplySingleRemediationPublic(
		string localPath,
		RuleResult failure,
		List<string> applied,
		Action<string>? onOutput)
		=> ApplySingleRemediation(localPath, failure, applied, onOutput);

	/// <summary>
	/// Applies a single remediation based on the advisory data.
	/// </summary>
	private void ApplySingleRemediation(
		string localPath,
		RuleResult failure,
		List<string> applied,
		Action<string>? onOutput)
	{
		var advisory = failure.Advisory!;
		var data = advisory.Data;

		// Determine remediation type
		var remediationType = data.TryGetValue("remediation_type", out var rtObj) && rtObj is string rt ? rt : null;

		// Fallback: create_file from expected_path + template_content
		if (remediationType is null && data.ContainsKey("expected_path") && data.ContainsKey("template_content"))
		{
			remediationType = "create_file";
		}

		if (remediationType is null)
		{
			return;
		}

		try
		{
			switch (remediationType)
			{
				case "create_file":
					ApplyCreateFile(localPath, failure, data, applied, onOutput);
					break;
				case "ensure_xml_property":
					ApplyEnsureXmlProperty(localPath, failure, data, applied, onOutput);
					break;
				case "ensure_csproj_property":
					ApplyEnsureCsprojProperty(localPath, failure, data, applied, onOutput);
					break;
				case "append_line":
					ApplyAppendLine(localPath, failure, data, applied, onOutput);
					break;
				case "prepend_line":
					ApplyPrependLine(localPath, failure, data, applied, onOutput);
					break;
				case "add_slnx_file_entries":
					ApplyAddSlnxFileEntries(localPath, failure, data, applied, onOutput);
					break;
				default:
					_logger.LogDebug("Unknown remediation_type '{Type}' for rule {RuleId}", remediationType, failure.RuleId);
					break;
			}
		}
		catch (Exception ex)
		{
			_logger.LogWarning(ex, "Failed remediation for rule {RuleId}", failure.RuleId);
			onOutput?.Invoke($"❌ [{failure.RuleId}] Failed: {ex.Message}");
		}
	}

	private static void ApplyCreateFile(
		string localPath,
		RuleResult failure,
		Dictionary<string, object> data,
		List<string> applied,
		Action<string>? onOutput)
	{
		if (data["expected_path"] is not string expectedPath || data["template_content"] is not string templateContent)
		{
			return;
		}

		var fullPath = Path.Combine(localPath, expectedPath.Replace('/', Path.DirectorySeparatorChar));
		if (File.Exists(fullPath))
		{
			onOutput?.Invoke($"⏭️ [{failure.RuleId}] {expectedPath} already exists — skipping.");
			return;
		}

		var dir = Path.GetDirectoryName(fullPath);
		if (dir is not null && !Directory.Exists(dir))
		{
			Directory.CreateDirectory(dir);
		}

		File.WriteAllText(fullPath, templateContent);
		applied.Add(expectedPath);
		onOutput?.Invoke($"✅ [{failure.RuleId}] Created {expectedPath}");
	}

	private static void ApplyEnsureXmlProperty(
		string localPath,
		RuleResult failure,
		Dictionary<string, object> data,
		List<string> applied,
		Action<string>? onOutput)
	{
		if (data["property_name"] is not string propName || data["property_value"] is not string propValue)
		{
			return;
		}

		var file = data.TryGetValue("file", out var fObj) && fObj is string f ? f : "Directory.Build.props";
		var fullPath = Path.Combine(localPath, file.Replace('/', Path.DirectorySeparatorChar));

		EnsureXmlPropertyInFile(fullPath, file, propName, propValue, failure, applied, onOutput);
	}

	private static void ApplyEnsureCsprojProperty(
		string localPath,
		RuleResult failure,
		Dictionary<string, object> data,
		List<string> applied,
		Action<string>? onOutput)
	{
		if (data["property_name"] is not string propName || data["property_value"] is not string propValue)
		{
			return;
		}

		// Single csproj file
		if (data.TryGetValue("file", out var fObj) && fObj is string file)
		{
			var fullPath = Path.Combine(localPath, file.Replace('/', Path.DirectorySeparatorChar));
			EnsureXmlPropertyInFile(fullPath, file, propName, propValue, failure, applied, onOutput);
			return;
		}

		// Multiple projects (from "projects" array)
		if (data.TryGetValue("projects", out var projObj) && projObj is string[] projects)
		{
			foreach (var proj in projects)
			{
				var fullPath = Path.Combine(localPath, proj.Replace('/', Path.DirectorySeparatorChar));
				EnsureXmlPropertyInFile(fullPath, proj, propName, propValue, failure, applied, onOutput);
			}
		}
	}

	private static void EnsureXmlPropertyInFile(
		string fullPath,
		string relativePath,
		string propName,
		string propValue,
		RuleResult failure,
		List<string> applied,
		Action<string>? onOutput)
	{
		if (!File.Exists(fullPath))
		{
			// Create a minimal Directory.Build.props or .csproj with the property
			var isProps = relativePath.EndsWith(".props", StringComparison.OrdinalIgnoreCase);
			var rootElement = isProps ? "Project" : "Project";
			var content = $"""
                <{rootElement}>
                  <PropertyGroup>
                    <{propName}>{propValue}</{propName}>
                  </PropertyGroup>
                </{rootElement}>
                """;
			var dir = Path.GetDirectoryName(fullPath);
			if (dir is not null && !Directory.Exists(dir))
			{
				Directory.CreateDirectory(dir);
			}

			File.WriteAllText(fullPath, content);
			applied.Add(relativePath);
			onOutput?.Invoke($"✅ [{failure.RuleId}] Created {relativePath} with <{propName}>{propValue}</{propName}>");
			return;
		}

		// Parse existing XML and add property if missing
		var xml = File.ReadAllText(fullPath);
		if (xml.Contains($"<{propName}>", StringComparison.OrdinalIgnoreCase))
		{
			onOutput?.Invoke($"⏭️ [{failure.RuleId}] {relativePath} already has <{propName}> — skipping.");
			return;
		}

		try
		{
			var doc = XDocument.Parse(xml);
			var propertyGroup = doc.Root?.Elements("PropertyGroup").FirstOrDefault();
			if (propertyGroup is null)
			{
				propertyGroup = new XElement("PropertyGroup");
				doc.Root?.AddFirst(propertyGroup);
			}

			propertyGroup.Add(new XElement(propName, propValue));
			doc.Save(fullPath);
			applied.Add(relativePath);
			onOutput?.Invoke($"✅ [{failure.RuleId}] Added <{propName}>{propValue}</{propName}> to {relativePath}");
		}
		catch (Exception ex)
		{
			onOutput?.Invoke($"❌ [{failure.RuleId}] Failed to modify {relativePath}: {ex.Message}");
		}
	}

	private static void ApplyAppendLine(
		string localPath,
		RuleResult failure,
		Dictionary<string, object> data,
		List<string> applied,
		Action<string>? onOutput)
	{
		if (data["line_content"] is not string lineContent)
		{
			return;
		}

		var file = data.TryGetValue("file", out var fObj) && fObj is string f ? f : null;
		if (file is null)
		{
			return;
		}

		var fullPath = Path.Combine(localPath, file.Replace('/', Path.DirectorySeparatorChar));
		if (!File.Exists(fullPath))
		{
			onOutput?.Invoke($"⏭️ [{failure.RuleId}] {file} does not exist — cannot append.");
			return;
		}

		var content = File.ReadAllText(fullPath);
		if (content.Contains(lineContent, StringComparison.OrdinalIgnoreCase))
		{
			onOutput?.Invoke($"⏭️ [{failure.RuleId}] {file} already contains '{lineContent}' — skipping.");
			return;
		}

		if (!content.EndsWith('\n'))
		{
			content += Environment.NewLine;
		}

		content += lineContent + Environment.NewLine;
		File.WriteAllText(fullPath, content);
		applied.Add(file);
		onOutput?.Invoke($"✅ [{failure.RuleId}] Appended '{lineContent}' to {file}");
	}

	private static void ApplyPrependLine(
		string localPath,
		RuleResult failure,
		Dictionary<string, object> data,
		List<string> applied,
		Action<string>? onOutput)
	{
		if (data["line_content"] is not string lineContent)
		{
			return;
		}

		var file = data.TryGetValue("file", out var fObj) && fObj is string f ? f : null;
		if (file is null)
		{
			return;
		}

		var fullPath = Path.Combine(localPath, file.Replace('/', Path.DirectorySeparatorChar));
		if (!File.Exists(fullPath))
		{
			onOutput?.Invoke($"⏭️ [{failure.RuleId}] {file} does not exist — cannot prepend.");
			return;
		}

		var content = File.ReadAllText(fullPath);
		if (content.Contains(lineContent, StringComparison.OrdinalIgnoreCase))
		{
			onOutput?.Invoke($"⏭️ [{failure.RuleId}] {file} already contains '{lineContent}' — skipping.");
			return;
		}

		content = lineContent + Environment.NewLine + Environment.NewLine + content;
		File.WriteAllText(fullPath, content);
		applied.Add(file);
		onOutput?.Invoke($"✅ [{failure.RuleId}] Prepended '{lineContent}' to {file}");
	}

	private static void ApplyAddSlnxFileEntries(
		string localPath,
		RuleResult failure,
		Dictionary<string, object> data,
		List<string> applied,
		Action<string>? onOutput)
	{
		var file = data.TryGetValue("file", out var fObj) && fObj is string f ? f : null;
		if (file is null)
		{
			return;
		}

		if (data["missing_files"] is not string[] missingFiles || missingFiles.Length == 0)
		{
			return;
		}

		var fullPath = Path.Combine(localPath, file.Replace('/', Path.DirectorySeparatorChar));
		if (!File.Exists(fullPath))
		{
			onOutput?.Invoke($"⏭️ [{failure.RuleId}] {file} does not exist — cannot add entries.");
			return;
		}

		try
		{
			var doc = XDocument.Load(fullPath);
			var solutionItemsFolder = doc.Root?
				.Elements("Folder")
				.FirstOrDefault(f =>
				{
					var name = f.Attribute("Name")?.Value;
					return name is not null &&
						name.Contains("Solution Items", StringComparison.OrdinalIgnoreCase);
				});

			if (solutionItemsFolder is null)
			{
				solutionItemsFolder = new XElement("Folder", new XAttribute("Name", "/Solution Items/"));
				doc.Root?.Add(solutionItemsFolder);
			}

			var addedCount = 0;
			foreach (var missingFile in missingFiles)
			{
				var alreadyExists = solutionItemsFolder.Elements("File")
					.Any(el => string.Equals(el.Attribute("Path")?.Value, missingFile, StringComparison.OrdinalIgnoreCase));
				if (alreadyExists)
				{
					continue;
				}

				solutionItemsFolder.Add(new XElement("File", new XAttribute("Path", missingFile)));
				addedCount++;
			}

			if (addedCount > 0)
			{
				doc.Save(fullPath);
				applied.Add(file);
				onOutput?.Invoke($"✅ [{failure.RuleId}] Added {addedCount} file entries to Solution Items in {file}");
			}
			else
			{
				onOutput?.Invoke($"⏭️ [{failure.RuleId}] All files already in Solution Items — skipping.");
			}
		}
		catch (Exception ex)
		{
			onOutput?.Invoke($"❌ [{failure.RuleId}] Failed to modify {file}: {ex.Message}");
		}
	}

	/// <summary>
	/// Builds a local repository.
	/// </summary>
	public async Task BuildAsync(
		PackageDashboardRow row,
		Action<string>? onOutput = null,
		CancellationToken cancellationToken = default)
	{
		var repoName = ExtractRepoName(row.RepositoryUrl);
		if (repoName is null)
		{
			row.Status = PackageStatus.Error;
			row.StatusMessage = "Cannot determine repo name.";
			return;
		}

		row.Status = PackageStatus.Building;
		row.StatusMessage = "Building...";

		var (success, _) = await _localRepo.BuildAsync(repoName, onOutput, cancellationToken).ConfigureAwait(false);

		row.Status = success ? PackageStatus.BuildSucceeded : PackageStatus.BuildFailed;
		row.StatusMessage = success ? "Build succeeded." : "Build failed.";
	}

	/// <summary>
	/// Syncs a local repository with remote (fetch, pull --rebase, push).
	/// </summary>
	public async Task GitSyncAsync(
		PackageDashboardRow row,
		Action<string>? onOutput = null,
		CancellationToken cancellationToken = default)
	{
		var repoName = ExtractRepoName(row.RepositoryUrl);
		if (repoName is null)
		{
			row.Status = PackageStatus.Error;
			row.StatusMessage = "Cannot determine repo name.";
			return;
		}

		row.Status = PackageStatus.GitSyncing;
		row.StatusMessage = "Syncing with remote...";

		var (success, _) = await _localRepo.GitSyncAsync(repoName, onOutput, cancellationToken).ConfigureAwait(false);

		if (success)
		{
			// Refresh git status after sync
			row.CurrentBranch = await _localRepo.GetCurrentBranchAsync(repoName, cancellationToken).ConfigureAwait(false);
			row.IsWorkingTreeClean = await _localRepo.IsWorkingTreeCleanAsync(repoName, cancellationToken).ConfigureAwait(false);
		}

		row.Status = success ? PackageStatus.GitSynced : PackageStatus.Error;
		row.StatusMessage = success ? "Synced with remote." : "Git sync failed.";
	}

	/// <summary>
	/// Refreshes the git status for a row (branch and working tree clean state).
	/// </summary>
	public async Task RefreshGitStatusAsync(PackageDashboardRow row, CancellationToken cancellationToken = default)
	{
		var repoName = ExtractRepoName(row.RepositoryUrl);
		if (repoName is null || !row.IsClonedLocally)
		{
			return;
		}

		row.CurrentBranch = await _localRepo.GetCurrentBranchAsync(repoName, cancellationToken).ConfigureAwait(false);
		row.IsWorkingTreeClean = await _localRepo.IsWorkingTreeCleanAsync(repoName, cancellationToken).ConfigureAwait(false);
	}

	/// <summary>
	/// Runs tests on a local repository.
	/// </summary>
	public async Task RunTestsAsync(
		PackageDashboardRow row,
		Action<string>? onOutput = null,
		CancellationToken cancellationToken = default)
	{
		var repoName = ExtractRepoName(row.RepositoryUrl);
		if (repoName is null)
		{
			row.Status = PackageStatus.Error;
			row.StatusMessage = "Cannot determine repo name.";
			return;
		}

		row.Status = PackageStatus.Testing;
		row.StatusMessage = "Running tests...";

		var (success, _) = await _localRepo.RunTestsAsync(repoName, onOutput, cancellationToken).ConfigureAwait(false);

		row.Status = success ? PackageStatus.TestsPassed : PackageStatus.TestsFailed;
		row.StatusMessage = success ? "All tests passed." : "Tests failed.";
	}

	/// <summary>
	/// Runs the publish script on a local repository.
	/// </summary>
	public async Task RunPublishAsync(
		PackageDashboardRow row,
		Action<string>? onOutput = null,
		CancellationToken cancellationToken = default)
	{
		var repoName = ExtractRepoName(row.RepositoryUrl);
		if (repoName is null)
		{
			row.Status = PackageStatus.Error;
			row.StatusMessage = "Cannot determine repo name.";
			return;
		}

		row.Status = PackageStatus.Publishing;
		row.StatusMessage = "Publishing...";

		var (success, _) = await _localRepo.RunPublishScriptAsync(repoName, onOutput, cancellationToken).ConfigureAwait(false);

		row.Status = success ? PackageStatus.Published : PackageStatus.Error;
		row.StatusMessage = success ? "Published successfully." : "Publish failed.";
	}

	internal static Dictionary<AssessmentCategory, CategorySummary> BuildCategorySummaries(List<RuleResult> results)
	{
		var summaries = new Dictionary<AssessmentCategory, CategorySummary>();

		foreach (var group in results.GroupBy(r => r.Category))
		{
			summaries[group.Key] = new CategorySummary
			{
				Passed = group.Count(r => r.Passed),
				Errors = group.Count(r => !r.Passed && r.Severity == AssessmentSeverity.Error),
				Warnings = group.Count(r => !r.Passed && r.Severity == AssessmentSeverity.Warning),
				Infos = group.Count(r => !r.Passed && r.Severity == AssessmentSeverity.Info),
			};
		}

		return summaries;
	}

	private static string? ExtractRepoName(string? url)
	{
		if (url is null)
		{
			return null;
		}

		try
		{
			var uri = new Uri(url);
			var segments = uri.AbsolutePath.Trim('/').Split('/');
			return segments.Length >= 2 ? segments[1] : null;
		}
		catch
		{
			return null;
		}
	}

	private static string FormatDataValue(object value) => value switch
	{
		string s => s,
		string[] arr => string.Join(", ", arr),
		IEnumerable<object> list => string.Join(", ", list),
		_ => value.ToString() ?? string.Empty
	};
}
