using Microsoft.Extensions.Options;
using Octokit;
using PanoramicData.NugetManagement.Models;
using PanoramicData.NugetManagement.Rules;
using PanoramicData.NugetManagement.Services;
using PanoramicData.NugetManagement.Web.Models;

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
    /// Generates an AI remediation prompt from failed rules.
    /// </summary>
    public static string GenerateRemediationPrompt(PackageDashboardRow row)
    {
        if (row.Assessment is null)
        {
            return string.Empty;
        }

        var failures = row.Assessment.RuleResults.Where(r => !r.Passed).ToList();
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

    private static Dictionary<AssessmentCategory, CategorySummary> BuildCategorySummaries(List<RuleResult> results)
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
