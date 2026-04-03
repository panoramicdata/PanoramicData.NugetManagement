using Microsoft.Extensions.Logging;
using Octokit;
using PanoramicData.NugetManagement.Models;
using PanoramicData.NugetManagement.Rules;

namespace PanoramicData.NugetManagement.Services;

/// <summary>
/// Assesses all repositories in a GitHub organization against best practice rules.
/// </summary>
public class OrganizationAssessor : IDisposable
{
	private readonly IGitHubClient _github;
	private readonly RepositoryContextBuilder _contextBuilder;
	private readonly IReadOnlyList<IRule> _rules;
	private readonly ILogger<OrganizationAssessor> _logger;

	/// <summary>
	/// Initializes a new instance of the <see cref="OrganizationAssessor"/> class.
	/// </summary>
	/// <param name="github">The GitHub client configured with authentication.</param>
	/// <param name="logger">The logger.</param>
	/// <param name="contextBuilderLogger">The logger for RepositoryContextBuilder.</param>
	public OrganizationAssessor(
		IGitHubClient github,
		ILogger<OrganizationAssessor> logger,
		ILogger<RepositoryContextBuilder> contextBuilderLogger)
	{
		_github = github;
		_contextBuilder = new RepositoryContextBuilder(github, contextBuilderLogger);
		_rules = RuleRegistry.Rules;
		_logger = logger;
	}

	/// <inheritdoc />
	public void Dispose()
	{
		_contextBuilder.Dispose();
		GC.SuppressFinalize(this);
	}

	/// <summary>
	/// Assesses all repositories in the configured organization.
	/// </summary>
	/// <param name="options">The assessment options.</param>
	/// <param name="cancellationToken">Cancellation token.</param>
	/// <returns>The organization-level assessment result.</returns>
	public async Task<OrganizationAssessmentResult> AssessAsync(
		AssessmentOptions options,
		CancellationToken cancellationToken = default)
	{
		_logger.LogInformation("Starting assessment of organization '{Organization}'...", options.OrganizationName);

		var repositories = await GetRepositoriesAsync(options, cancellationToken).ConfigureAwait(false);
		_logger.LogInformation("Found {Count} repositories to assess.", repositories.Count);

		var semaphore = new SemaphoreSlim(options.MaxConcurrency);
		var assessments = new List<RepoAssessment>();

		var tasks = repositories.Select(async repo =>
		{
			await semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
			try
			{
				var assessment = await AssessRepositoryAsync(repo, options, cancellationToken).ConfigureAwait(false);
				lock (assessments)
				{
					assessments.Add(assessment);
				}
			}
			catch (Exception ex)
			{
				_logger.LogError(ex, "Failed to assess repository {FullName}", repo.FullName);
			}
			finally
			{
				semaphore.Release();
			}
		});

		await Task.WhenAll(tasks).ConfigureAwait(false);

		return new OrganizationAssessmentResult
		{
			OrganizationName = options.OrganizationName,
			AssessedAtUtc = DateTimeOffset.UtcNow,
			RepositoryAssessments = [.. assessments.OrderBy(a => a.RepositoryFullName, StringComparer.OrdinalIgnoreCase)]
		};
	}

	private async Task<IReadOnlyList<Repository>> GetRepositoriesAsync(
		AssessmentOptions options,
		CancellationToken _)
	{
		var allRepos = await _github.Repository.GetAllForOrg(options.OrganizationName).ConfigureAwait(false);

		return [.. allRepos
			.Where(r => !r.Archived && !r.Fork)
			.Where(r =>
			{
				if (options.RepositoryOptions.TryGetValue(r.Name, out var repoOptions))
				{
					return !repoOptions.Exclude;
				}

				return true;
			})
			.OrderBy(r => r.FullName, StringComparer.OrdinalIgnoreCase)];
	}

	private async Task<RepoAssessment> AssessRepositoryAsync(
		Repository repository,
		AssessmentOptions options,
		CancellationToken cancellationToken)
	{
		var repoOptions = options.RepositoryOptions.TryGetValue(repository.Name, out var opts)
			? opts
			: new RepoOptions();

		_logger.LogInformation("Assessing {FullName}...", repository.FullName);

		var context = await _contextBuilder.BuildAsync(repository, repoOptions, cancellationToken).ConfigureAwait(false);

		var ruleResults = new List<RuleResult>();
		foreach (var rule in _rules)
		{
			if (repoOptions.SuppressedRules.Contains(rule.RuleId, StringComparer.OrdinalIgnoreCase))
			{
				_logger.LogDebug("Rule {RuleId} suppressed for {FullName}", rule.RuleId, repository.FullName);
				continue;
			}

			try
			{
				var result = await rule.EvaluateAsync(context, cancellationToken).ConfigureAwait(false);
				ruleResults.Add(result);
			}
			catch (Exception ex)
			{
				_logger.LogWarning(ex, "Rule {RuleId} threw for {FullName}", rule.RuleId, repository.FullName);
				ruleResults.Add(new RuleResult
				{
					RuleId = rule.RuleId,
					RuleName = rule.RuleName,
					Category = rule.Category,
					Severity = rule.Severity,
					Passed = false,
					Message = $"Rule threw an exception: {ex.Message}"
				});
			}
		}

		_logger.LogInformation(
			"Assessment of {FullName}: {Passed}/{Total} passed ({Errors} errors, {Warnings} warnings)",
			repository.FullName,
			ruleResults.Count(r => r.Passed),
			ruleResults.Count,
			ruleResults.Count(r => !r.Passed && r.Severity == AssessmentSeverity.Error),
			ruleResults.Count(r => !r.Passed && r.Severity == AssessmentSeverity.Warning));

		return new RepoAssessment
		{
			RepositoryFullName = repository.FullName,
			DefaultBranch = context.DefaultBranch,
			AssessedAtUtc = DateTimeOffset.UtcNow,
			RuleResults = ruleResults
		};
	}
}
