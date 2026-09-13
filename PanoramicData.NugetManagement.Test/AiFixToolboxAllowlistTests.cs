using PanoramicData.NugetManagement.Web.Services;

namespace PanoramicData.NugetManagement.Test;

/// <summary>
/// Tests for the write allowlist <see cref="AiFixToolbox"/> accepts when a session was created from a
/// human-raised issue.
/// </summary>
/// <remarks>
/// A rule-driven fix is bounded by a rule that re-evaluates; an issue-driven one has no such oracle,
/// so its bound is this list. It is the containment that still holds when everything upstream has
/// failed — when the analysis was fooled, the gate was passed and the brief was wrong, a session can
/// still only damage the handful of files that were screened before it started.
/// <para>
/// Reads stay unrestricted on purpose. The clone is our own content, and a model that cannot look
/// around it writes worse changes to the files it is allowed to touch.
/// </para>
/// </remarks>
public class AiFixToolboxAllowlistTests(ITestOutputHelper output) : TestWithOutput(output), IDisposable
{
	private readonly string _root = Directory.CreateTempSubdirectory("toolbox-allowlist-").FullName;

	private string GivenFile(string relativePath, string content = "// before")
	{
		var full = Path.Combine(_root, relativePath.Replace('/', Path.DirectorySeparatorChar));
		Directory.CreateDirectory(Path.GetDirectoryName(full)!);
		File.WriteAllText(full, content);
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

	private static AiToolCall Write(string path, string content)
		=> new("write_file", new Dictionary<string, string> { ["path"] = path, ["content"] = content });

	[Fact]
	public async Task WriteFile_AllowsAFileOnTheAllowlist()
	{
		GivenFile("src/Documents.cs");
		var toolbox = new AiFixToolbox(_root, writablePaths: ["src/Documents.cs"]);

		var result = await toolbox.ExecuteAsync(
			Write("src/Documents.cs", "// after"), TestContext.Current.CancellationToken);

		result.IsError.Should().BeFalse();
		File.ReadAllText(Path.Combine(_root, "src", "Documents.cs")).Should().Be("// after");
	}

	[Fact]
	public async Task WriteFile_RefusesAFileTheBriefNeverNamed()
	{
		GivenFile("src/Documents.cs");
		GivenFile("src/Secrets.cs", "// untouched");
		var toolbox = new AiFixToolbox(_root, writablePaths: ["src/Documents.cs"]);

		var result = await toolbox.ExecuteAsync(
			Write("src/Secrets.cs", "// after"), TestContext.Current.CancellationToken);

		result.IsError.Should().BeTrue(
			"the allowlist is the last containment an issue-driven fix has, so it refuses rather "
				+ "than trusting the model to have stayed where it was pointed");
		File.ReadAllText(Path.Combine(_root, "src", "Secrets.cs")).Should().Be("// untouched",
			"a refusal that still wrote the file would be no containment at all");
	}

	[Fact]
	public async Task WriteFile_TellsTheModelWhichFilesItMayWrite()
	{
		GivenFile("src/Documents.cs");
		var toolbox = new AiFixToolbox(_root, writablePaths: ["src/Documents.cs"]);

		var result = await toolbox.ExecuteAsync(
			Write("src/Other.cs", "// after"), TestContext.Current.CancellationToken);

		result.Content.Should().Contain("src/Documents.cs",
			"a refusal a model can act on saves the attempt; one it cannot just burns a turn");
	}

	[Fact]
	public async Task ReadFile_StaysUnrestricted()
	{
		GivenFile("src/Elsewhere.cs", "// readable");
		var toolbox = new AiFixToolbox(_root, writablePaths: ["src/Documents.cs"]);

		var result = await toolbox.ExecuteAsync(
			new AiToolCall("read_file", new Dictionary<string, string> { ["path"] = "src/Elsewhere.cs" }),
			TestContext.Current.CancellationToken);

		result.IsError.Should().BeFalse(
			"the clone is our own content; a model that cannot look around it writes worse changes "
				+ "to the files it is allowed to touch");
	}

	[Fact]
	public async Task WriteFile_IsUnrestrictedWhenNoAllowlistWasGiven()
	{
		GivenFile("src/Anything.cs");
		var toolbox = new AiFixToolbox(_root);

		var result = await toolbox.ExecuteAsync(
			Write("src/Anything.cs", "// after"), TestContext.Current.CancellationToken);

		result.IsError.Should().BeFalse(
			"rule-driven fixes are bounded by a rule that re-evaluates, and narrowing them here "
				+ "would change behaviour this feature has no business changing");
	}
}
