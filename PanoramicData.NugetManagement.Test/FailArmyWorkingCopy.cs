using PanoramicData.NugetManagement.Models;
using PanoramicData.NugetManagement.Services;

namespace PanoramicData.NugetManagement.Test;

/// <summary>
/// A throwaway, writable copy of the FailArmy fixture.
/// </summary>
/// <remarks>
/// The AI fix writes files, so it cannot be pointed at the fixture in the repository — a test run would
/// leave the fixture fixed and every subsequent run would assert against a repository that no longer
/// fails anything. Each test gets its own copy in a temporary directory and deletes it afterwards, which
/// is what makes the before-and-after assertion mean something twice.
/// </remarks>
internal sealed class FailArmyWorkingCopy : IDisposable
{
	private const string _fixtureRelativePath =
		"PanoramicData.NugetManagement.Test/Fixtures/PanoramicData.NugetFailArmy";

	private FailArmyWorkingCopy(string root) => Root = root;

	/// <summary>The copy's root directory, which is what the toolbox is scoped to.</summary>
	public string Root { get; }

	/// <summary>
	/// Copies the fixture to a fresh temporary directory.
	/// </summary>
	public static FailArmyWorkingCopy Create()
	{
		var repoRoot = FindRepositoryRoot(AppContext.BaseDirectory)
			?? throw new InvalidOperationException(
				$"Could not find the repository root from {AppContext.BaseDirectory}.");

		var source = Path.Combine(repoRoot, _fixtureRelativePath.Replace('/', Path.DirectorySeparatorChar));

		if (!Directory.Exists(source))
		{
			throw new DirectoryNotFoundException($"FailArmy fixture not found at {source}.");
		}

		var destination = Path.Combine(
			Path.GetTempPath(),
			"nugetmanagement-tests",
			$"failarmy-{Guid.NewGuid():n}");

		CopyDirectory(source, destination);

		return new FailArmyWorkingCopy(destination);
	}

	/// <summary>
	/// A context built from the copy as it now stands.
	/// </summary>
	/// <remarks>
	/// Rebuilt on each call rather than cached: the whole point is to read the files back after the model
	/// has changed them, and a cached context would report the state before the fix and pass regardless.
	/// </remarks>
	public RepositoryContext BuildContext()
		=> LocalRepositoryContextFactory.Build(
			Root,
			"panoramicdata/PanoramicData.NugetFailArmy",
			new RepoOptions
			{
				IsPackable = true,
				EnforceRequiredProperties = true
			},
			defaultBranch: "master");

	/// <summary>
	/// Evaluates one rule against the copy as it now stands.
	/// </summary>
	/// <param name="ruleId">The rule to evaluate.</param>
	/// <param name="cancellationToken">A cancellation token.</param>
	public async Task<RuleResult> EvaluateAsync(string ruleId, CancellationToken cancellationToken)
	{
		var rule = RuleRegistry.Rules.Single(r => string.Equals(r.RuleId, ruleId, StringComparison.OrdinalIgnoreCase));

		return await rule.EvaluateAsync(BuildContext(), cancellationToken).ConfigureAwait(false);
	}

	/// <inheritdoc />
	public void Dispose()
	{
		if (Directory.Exists(Root))
		{
			Directory.Delete(Root, recursive: true);
		}
	}

	private static void CopyDirectory(string source, string destination)
	{
		Directory.CreateDirectory(destination);

		foreach (var file in Directory.EnumerateFiles(source))
		{
			File.Copy(file, Path.Combine(destination, Path.GetFileName(file)));
		}

		foreach (var directory in Directory.EnumerateDirectories(source))
		{
			CopyDirectory(directory, Path.Combine(destination, Path.GetFileName(directory)));
		}
	}

	private static string? FindRepositoryRoot(string startingDirectory)
	{
		var directory = startingDirectory;

		while (directory is not null)
		{
			if (File.Exists(Path.Combine(directory, "PanoramicData.NugetManagement.slnx")))
			{
				return directory;
			}

			directory = Directory.GetParent(directory)?.FullName;
		}

		return null;
	}
}
