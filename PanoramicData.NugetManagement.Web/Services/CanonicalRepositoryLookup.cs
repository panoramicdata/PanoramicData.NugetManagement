using System.Collections.Concurrent;
using Octokit;

namespace PanoramicData.NugetManagement.Web.Services;

/// <summary>
/// Answers what a repository is really called, given the name a package declared for it.
/// </summary>
/// <remarks>
/// A package's repository URL is publisher-supplied text, and the identity of every row on the
/// dashboard is read out of it. GitHub routes case-insensitively and follows renames, so a declared
/// URL can be wrong in ways nobody notices there: Dell.CloudIq.Api's own csproj declares
/// <c>Dell.CloudIQ.Api</c>, and MicrosoftAzure.Api's declares <c>MicrosoftAzureSentinel.Api</c>,
/// which is the repository's name from before it was renamed. Everything downstream inherited the
/// declared spelling — the clone directory, the clone's remote, and the Codacy lookup, which is
/// case-sensitive and answered 404 for a repository Codacy holds perfectly well.
///
/// Asking GitHub converts a name that merely resolves into the name the repository actually has.
/// </remarks>
public interface ICanonicalRepositoryLookup
{
	/// <summary>
	/// The repository's canonical <c>owner/name</c>, or <see langword="null"/> when GitHub does not
	/// know it or could not be asked.
	/// </summary>
	/// <param name="owner">The owner as declared.</param>
	/// <param name="name">The repository name as declared.</param>
	/// <param name="cancellationToken">A cancellation token.</param>
	Task<string?> GetFullNameAsync(string owner, string name, CancellationToken cancellationToken);
}

/// <summary>
/// An <see cref="ICanonicalRepositoryLookup"/> backed by GitHub, asking once per declared name.
/// </summary>
/// <remarks>
/// Memoised because discovery asks per package, not per repository: an organisation publishing two
/// hundred packages from eighty repositories would otherwise spend two hundred calls of its hourly
/// budget establishing eighty answers. Failures are memoised too — a repository that is not there
/// is not there for the rest of the discovery either.
/// </remarks>
public sealed class CanonicalRepositoryLookup(
	IGitHubClient github,
	ILogger<CanonicalRepositoryLookup> logger) : ICanonicalRepositoryLookup
{
	private readonly ConcurrentDictionary<string, Task<string?>> _answers =
		new(StringComparer.OrdinalIgnoreCase);

	/// <inheritdoc />
	public Task<string?> GetFullNameAsync(string owner, string name, CancellationToken cancellationToken)
		=> _answers.GetOrAdd($"{owner}/{name}", _ => AskAsync(owner, name));

	private async Task<string?> AskAsync(string owner, string name)
	{
		try
		{
			var repository = await github.Repository.Get(owner, name).ConfigureAwait(false);
			return repository.FullName;
		}
		catch (NotFoundException)
		{
			// A declared repository that does not exist is the caller's finding to report, not an
			// error here: the declared name is all anybody has, so it stands.
			logger.LogWarning("GitHub does not know a repository at {Owner}/{Name}.", owner, name);
			return null;
		}
		catch (Exception ex) when (ex is not OperationCanceledException)
		{
			// A rate limit or an outage must not fail discovery for the whole estate. The declared
			// name stands, exactly as it did before anybody thought to ask.
			logger.LogWarning(ex, "Could not ask GitHub what {Owner}/{Name} is called.", owner, name);
			return null;
		}
	}
}
