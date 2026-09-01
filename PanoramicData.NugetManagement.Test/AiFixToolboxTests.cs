using PanoramicData.NugetManagement.Web.Services;

namespace PanoramicData.NugetManagement.Test;

/// <summary>
/// Tests for <see cref="AiFixToolbox"/>: the only thing standing between a 27b model and the
/// filesystem. Every path it is given must resolve inside the clone or be refused.
/// </summary>
public class AiFixToolboxTests(ITestOutputHelper output) : TestWithOutput(output), IDisposable
{
	private readonly string _root = Path.Combine(
		Path.GetTempPath(),
		"nugetmanagement-tests",
		$"ai-{Guid.NewGuid():n}");

	public void Dispose()
	{
		if (Directory.Exists(_root))
		{
			Directory.Delete(_root, recursive: true);
		}

		GC.SuppressFinalize(this);
	}

	private AiFixToolbox NewToolbox()
	{
		Directory.CreateDirectory(Path.Combine(_root, "clone", "src"));
		File.WriteAllText(Path.Combine(_root, "clone", "README.md"), "# Sample\n");
		File.WriteAllText(Path.Combine(_root, "clone", "src", "Sample.csproj"), "<Project />");
		File.WriteAllText(Path.Combine(_root, "outside-secret.txt"), "do not read me");

		return new AiFixToolbox(Path.Combine(_root, "clone"));
	}

	private Task<AiToolResult> CallAsync(string tool, params (string Name, string Value)[] arguments)
		=> NewToolbox().ExecuteAsync(
			new AiToolCall(tool, arguments.ToDictionary(a => a.Name, a => a.Value, StringComparer.Ordinal)),
			TestContext.Current.CancellationToken);

	[Fact]
	public async Task ListFiles_ReturnsPathsRelativeToTheClone()
	{
		var result = await CallAsync("list_files").ConfigureAwait(true);

		result.IsError.Should().BeFalse();
		result.Content.Should().Contain("README.md").And.Contain("src/Sample.csproj");
		result.Content.Should().NotContain("outside-secret.txt");
		result.Content.Should().NotContain(_root, "an absolute path tells the model where it is not allowed to go");
	}

	[Fact]
	public async Task ListFiles_WithAGlob_FiltersToIt()
	{
		var result = await CallAsync("list_files", ("glob", "*.csproj")).ConfigureAwait(true);

		result.Content.Should().Contain("Sample.csproj").And.NotContain("README.md");
	}

	[Fact]
	public async Task ReadFile_ReturnsTheContent()
		=> (await CallAsync("read_file", ("path", "README.md")).ConfigureAwait(true))
			.Content.Should().Contain("# Sample");

	[Fact]
	public async Task ReadFile_MissingFile_IsAnErrorResultNotAnException()
	{
		var result = await CallAsync("read_file", ("path", "nope.txt")).ConfigureAwait(true);

		result.IsError.Should().BeTrue();
		result.Content.Should().Contain("nope.txt", "the model has to be able to read what went wrong and correct it");
	}

	[Fact]
	public async Task ReadFile_LargeFile_IsTruncatedWithAMarker()
	{
		var toolbox = NewToolbox();
		var big = new string('x', AiFixToolbox.MaxReadBytes + 5_000);
		await File.WriteAllTextAsync(Path.Combine(_root, "clone", "big.txt"), big, TestContext.Current.CancellationToken)
			.ConfigureAwait(true);

		var result = await toolbox
			.ExecuteAsync(
				new AiToolCall("read_file", new Dictionary<string, string> { ["path"] = "big.txt" }),
				TestContext.Current.CancellationToken)
			.ConfigureAwait(true);

		result.Content.Length.Should().BeLessThan(big.Length, "the context window is finite");
		result.Content.Should().Contain("truncated", "silently shortening a file would make the model edit half of it");
	}

	[Fact]
	public async Task WriteFile_WritesInsideTheClone()
	{
		var toolbox = NewToolbox();

		var result = await toolbox
			.ExecuteAsync(
				new AiToolCall("write_file", new Dictionary<string, string>
				{
					["path"] = "SECURITY.md",
					["content"] = "# Security Policy\n"
				}),
				TestContext.Current.CancellationToken)
			.ConfigureAwait(true);

		result.IsError.Should().BeFalse();
		(await File.ReadAllTextAsync(Path.Combine(_root, "clone", "SECURITY.md"), TestContext.Current.CancellationToken)
			.ConfigureAwait(true))
			.Should().Be("# Security Policy\n");
	}

	[Fact]
	public async Task WriteFile_RecordsWhatItWrote()
	{
		// For a rule Codacy grades on the published branch this is the only evidence of progress there
		// is: the rule cannot be asked whether the clone improved, so the question becomes whether the
		// model changed anything at all.
		var toolbox = NewToolbox();

		toolbox.FilesWritten.Should().BeEmpty("nothing has been written yet");

		await toolbox
			.ExecuteAsync(
				new AiToolCall("write_file", new Dictionary<string, string>
				{
					["path"] = "src\\Sample.csproj",
					["content"] = "<Project />"
				}),
				TestContext.Current.CancellationToken)
			.ConfigureAwait(true);

		toolbox.FilesWritten.Should().Equal(["src/Sample.csproj"],
			"the path is recorded as the clone spells it, not as the model typed it");
	}

	[Fact]
	public async Task WriteFile_RecordsNothing_WhenThePathIsRefused()
	{
		var toolbox = NewToolbox();

		await toolbox
			.ExecuteAsync(
				new AiToolCall("write_file", new Dictionary<string, string>
				{
					["path"] = "../outside-secret.txt",
					["content"] = "no"
				}),
				TestContext.Current.CancellationToken)
			.ConfigureAwait(true);

		toolbox.FilesWritten.Should().BeEmpty(
			"a refused write is not progress, and counting it would call a session that achieved nothing a success");
	}

	[Fact]
	public async Task WriteFile_CreatesMissingDirectories()
	{
		var toolbox = NewToolbox();

		await toolbox
			.ExecuteAsync(
				new AiToolCall("write_file", new Dictionary<string, string>
				{
					["path"] = ".github/workflows/ci.yml",
					["content"] = "name: ci\n"
				}),
				TestContext.Current.CancellationToken)
			.ConfigureAwait(true);

		File.Exists(Path.Combine(_root, "clone", ".github", "workflows", "ci.yml")).Should().BeTrue(
			"a rule whose fix is a new workflow needs the folder it lives in");
	}

	[Theory]
	[InlineData("../outside-secret.txt")]
	[InlineData("..\\outside-secret.txt")]
	[InlineData("src/../../outside-secret.txt")]
	public async Task ReadFile_EscapingTheClone_IsRefused(string path)
	{
		var result = await CallAsync("read_file", ("path", path)).ConfigureAwait(true);

		result.IsError.Should().BeTrue();
		result.Content.Should().NotContain("do not read me", "refusing has to mean not reading it");
	}

	[Fact]
	public async Task WriteFile_EscapingTheClone_WritesNothing()
	{
		var target = Path.Combine(_root, "written-outside.txt");

		var result = await CallAsync(
				"write_file",
				("path", "../written-outside.txt"),
				("content", "should not exist"))
			.ConfigureAwait(true);

		result.IsError.Should().BeTrue();
		File.Exists(target).Should().BeFalse();
	}

	[Fact]
	public async Task WriteFile_AnAbsolutePath_IsRefused()
	{
		var target = Path.Combine(_root, "absolute.txt");

		var result = await CallAsync("write_file", ("path", target), ("content", "no")).ConfigureAwait(true);

		result.IsError.Should().BeTrue();
		File.Exists(target).Should().BeFalse();
	}

	[Fact]
	public async Task AnUnknownTool_IsAnErrorResultNamingWhatIsAvailable()
	{
		var result = await CallAsync("delete_everything").ConfigureAwait(true);

		result.IsError.Should().BeTrue();
		result.Content.Should().Contain("read_file", "a model that invented a tool needs the real list back");
	}

	[Fact]
	public async Task Finish_IsRecognisedAndCarriesItsSummary()
	{
		var result = await CallAsync("finish", ("summary", "Added SECURITY.md")).ConfigureAwait(true);

		result.IsError.Should().BeFalse();
		result.IsFinish.Should().BeTrue();
		result.Content.Should().Contain("Added SECURITY.md");
	}

	[Fact]
	public async Task ARequiredArgumentThatIsMissing_IsAnErrorResult()
	{
		var result = await CallAsync("read_file").ConfigureAwait(true);

		result.IsError.Should().BeTrue();
		result.Content.Should().Contain("path");
	}

	[Fact]
	public async Task RunBuild_ReturnsWhateverTheBuildSaid()
	{
		Directory.CreateDirectory(Path.Combine(_root, "clone"));
		var toolbox = new AiFixToolbox(
			Path.Combine(_root, "clone"),
			build: _ => Task.FromResult("error CS1002: ; expected"));

		var result = await toolbox
			.ExecuteAsync(new AiToolCall("run_build", new Dictionary<string, string>()), TestContext.Current.CancellationToken)
			.ConfigureAwait(true);

		result.Content.Should().Contain("CS1002", "seeing the compiler error is most of why a weak model can fix anything");
	}

	[Fact]
	public async Task RunBuild_WhenNoBuildWasWiredUp_SaysSoRatherThanFailingSilently()
	{
		Directory.CreateDirectory(Path.Combine(_root, "clone"));

		var result = await new AiFixToolbox(Path.Combine(_root, "clone"))
			.ExecuteAsync(new AiToolCall("run_build", new Dictionary<string, string>()), TestContext.Current.CancellationToken)
			.ConfigureAwait(true);

		result.IsError.Should().BeTrue();
		result.Content.Should().Contain("not available");
	}

	[Fact]
	public void TheToolDefinitions_CoverEveryToolTheToolboxExecutes()
		=> AiFixToolbox.ToolNames.Should().BeEquivalentTo(
			["list_files", "read_file", "write_file", "run_build", "run_tests", "finish"],
			"a tool described to the model but not executed wastes a turn, and one executed but not "
			+ "described will never be called");
}
