using Microsoft.Extensions.Logging.Abstractions;
using PanoramicData.NugetManagement.Models;
using PanoramicData.NugetManagement.Services;

namespace PanoramicData.NugetManagement.Test;

/// <summary>
/// Guards against AI-01/AI-02 silently evaluating <c>null</c> content: a repository that has
/// CLAUDE.md/AGENTS.md at its root must have their content actually fetched, not just their path
/// listed, or the rules report "not found" even when the file exists.
/// </summary>
/// <remarks>
/// This is exactly the bug that shipped once: <c>CLAUDE.md</c>/<c>AGENTS.md</c> were listed as
/// existing (<see cref="RepositoryContext.FilePaths"/>) but never added to the content-fetch
/// allow-list, so <see cref="RepositoryContext.GetFileContent(string)"/> returned <c>null</c> for
/// every repository — including this one — and both rules always took the "file not found" branch.
/// </remarks>
public class AiRuleContentFetchTests(ITestOutputHelper output) : TestWithOutput(output), IDisposable
{
	private readonly string _root = Path.Combine(
		Path.GetTempPath(),
		"nugetmanagement-tests",
		$"ai-rule-content-{Guid.NewGuid():N}");

	private void Write(string relativePath, string content)
	{
		var path = Path.Combine(_root, relativePath);
		Directory.CreateDirectory(Path.GetDirectoryName(path)!);
		File.WriteAllText(path, content);
	}

	private RepositoryContext Build()
		=> new LocalRepositoryContextBuilder(NullLogger<LocalRepositoryContextBuilder>.Instance)
			.Build(_root, "panoramicdata/SomeRepo", new RepoOptions());

	[Fact]
	public void ClaudeMd_ContentIsFetched()
	{
		Write("CLAUDE.md", "@../PanoramicData.Skills/.github/skills/copilot-instructions.md");

		Build().GetFileContent("CLAUDE.md").Should().NotBeNull();
	}

	[Fact]
	public void AgentsMd_ContentIsFetched()
	{
		Write("AGENTS.md", "../PanoramicData.Skills/.github/skills/copilot-instructions.md");

		Build().GetFileContent("AGENTS.md").Should().NotBeNull();
	}

	public void Dispose()
	{
		GC.SuppressFinalize(this);

		if (Directory.Exists(_root))
		{
			Directory.Delete(_root, recursive: true);
		}
	}
}
