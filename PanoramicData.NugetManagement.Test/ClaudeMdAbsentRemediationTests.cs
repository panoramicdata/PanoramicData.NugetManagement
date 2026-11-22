using Microsoft.Extensions.Logging.Abstractions;
using PanoramicData.NugetManagement.Models;
using PanoramicData.NugetManagement.Rules;
using PanoramicData.NugetManagement.Services;
using PanoramicData.NugetManagement.Web.Remediations.AI;

namespace PanoramicData.NugetManagement.Test;

/// <summary>
/// Fixing AI-01 deletes CLAUDE.md, which would leave a missing file in the solution's Solution Items
/// folder if the .slnx still listed it.
/// </summary>
public class ClaudeMdAbsentRemediationTests(ITestOutputHelper output) : TestWithOutput(output), IDisposable
{
	private readonly string _root = Path.Combine(
		Path.GetTempPath(),
		"nugetmanagement-tests",
		$"claude-absent-{Guid.NewGuid():N}");

	private const string _slnx = "<Solution>\n\t<Folder Name=\"/Solution Items/\">\n\t\t<File Path=\"AGENTS.md\" />\n\t\t<File Path=\"CLAUDE.md\" />\n\t\t<File Path=\"README.md\" />\n\t</Folder>\n</Solution>\n";

	private void Write(string relativePath, string content)
	{
		var path = Path.Combine(_root, relativePath);
		Directory.CreateDirectory(Path.GetDirectoryName(path)!);
		File.WriteAllText(path, content);
	}

	private async Task<List<string>> FixAsync()
	{
		var context = new LocalRepositoryContextBuilder(NullLogger<LocalRepositoryContextBuilder>.Instance)
			.Build(_root, "panoramicdata/SomeRepo", new RepoOptions(), new LocalContextFacts());
		var result = await new ClaudeMdAbsentRule().EvaluateAsync(context, CancellationToken.None).ConfigureAwait(true);
		var applied = new List<string>();
		new ClaudeMdAbsentRemediation().Apply(_root, result, applied, null);
		return applied;
	}

	[Fact]
	public async Task Fix_ShouldDeleteClaudeMdAndItsSolutionItem_LeavingTheRestOfTheSolutionAlone()
	{
		Write("CLAUDE.md", Standards.PreviousClaudeMdContent);
		Write("Repo.slnx", _slnx);

		var applied = await FixAsync().ConfigureAwait(true);

		File.Exists(Path.Combine(_root, "CLAUDE.md")).Should().BeFalse();
		File.ReadAllText(Path.Combine(_root, "Repo.slnx")).Should().Be(_slnx.Replace("\t\t<File Path=\"CLAUDE.md\" />\n", string.Empty, StringComparison.Ordinal));
		applied.Should().Contain("CLAUDE.md").And.Contain("Repo.slnx");
	}

	[Fact]
	public async Task Fix_ShouldWorkWhenThereIsNoSolutionFile()
	{
		Write("CLAUDE.md", Standards.PreviousClaudeMdContent);

		var applied = await FixAsync().ConfigureAwait(true);

		File.Exists(Path.Combine(_root, "CLAUDE.md")).Should().BeFalse();
		applied.Should().Equal("CLAUDE.md");
	}

	[Fact]
	public void PublicTemplate_ShouldEndWithANewline()
		=> Standards.AgentsMdContent.Should().EndWith("\n");

	public void Dispose()
	{
		GC.SuppressFinalize(this);

		if (Directory.Exists(_root))
		{
			Directory.Delete(_root, recursive: true);
		}
	}
}
