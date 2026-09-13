using PanoramicData.NugetManagement.Services;

namespace PanoramicData.NugetManagement.Test;

/// <summary>
/// Tests for <see cref="IssuePathScreen"/>: which files a brief derived from a stranger's issue is
/// allowed to name.
/// </summary>
/// <remarks>
/// This is the last deterministic check between an untrusted issue body and a model holding
/// <c>write_file</c> on a real clone, so it is tested as a security boundary rather than as
/// validation: every refusal below is a path somebody could ask for on purpose.
/// </remarks>
public class IssuePathScreenTests(ITestOutputHelper output) : TestWithOutput(output), IDisposable
{
	private readonly string _root = Directory.CreateTempSubdirectory("issue-path-screen-").FullName;

	private string GivenFile(string relativePath)
	{
		var full = Path.Combine(_root, relativePath.Replace('/', Path.DirectorySeparatorChar));
		Directory.CreateDirectory(Path.GetDirectoryName(full)!);
		File.WriteAllText(full, "// content");
		return full;
	}

	public void Dispose()
	{
		GC.SuppressFinalize(this);

		if (Directory.Exists(_root))
		{
			Directory.Delete(_root, recursive: true);
		}
	}

	[Fact]
	public void Screen_AcceptsAnOrdinarySourceFileThatExists()
	{
		GivenFile("src/Documents.cs");

		var result = IssuePathScreen.Screen(_root, ["src/Documents.cs"]);

		result.Accepted.Should().BeTrue(
			"an ordinary source file inside the clone is exactly what a valid issue fix touches");
	}

	[Fact]
	public void Screen_RefusesAPathThatEscapesTheCloneRoot()
	{
		var result = IssuePathScreen.Screen(_root, ["../../secrets.txt"]);

		result.Accepted.Should().BeFalse(
			"a brief that reaches outside the clone is the traversal attempt this screen exists for");
		result.Refusal.Should().Contain("outside");
	}

	[Fact]
	public void Screen_RefusesAnAbsolutePath()
	{
		var result = IssuePathScreen.Screen(_root, [Path.Combine(Path.GetTempPath(), "anywhere.cs")]);

		result.Accepted.Should().BeFalse(
			"paths in a brief are repo-relative by contract; an absolute one is not a near miss");
	}

	[Fact]
	public void Screen_RefusesAFileThatDoesNotExist()
	{
		var result = IssuePathScreen.Screen(_root, ["src/NeverWritten.cs"]);

		result.Accepted.Should().BeFalse(
			"a fix naming files this repository does not have is either obsolete or invented, and "
				+ "both are reasons for a human to look rather than for a model to start writing");
	}

	[Fact]
	public void Screen_RefusesAWorkflowFile()
	{
		GivenFile(".github/workflows/publish.yml");

		var result = IssuePathScreen.Screen(_root, [".github/workflows/publish.yml"]);

		result.Accepted.Should().BeFalse(
			"CI and publish are the surfaces worth attacking, so they are refused structurally "
				+ "rather than on the model's opinion of the request");
	}

	[Fact]
	public void Screen_RefusesAnEmptyPathList()
	{
		var result = IssuePathScreen.Screen(_root, []);

		result.Accepted.Should().BeFalse(
			"a fix that names no files has nothing to restrict writes to, so it cannot be contained");
	}
}
