using PanoramicData.NugetManagement.Models;
using PanoramicData.NugetManagement.Rules;

namespace PanoramicData.NugetManagement.Test;

/// <summary>
/// Tests for <see cref="ClientOptionsIsReadOnlyRule"/> (HTTP-02).
/// </summary>
public class ClientOptionsIsReadOnlyRuleTests
{
	private const string _optionsPath = "Foo.Api/Foo.Api/FooClientOptions.cs";

	[Fact]
	public async Task NotApplicable_WhenThereIsNoClientOptionsFile()
	{
		var result = await Evaluate(new() { ["Foo.Api/Foo.Api/Foo.csproj"] = "<Project />" });

		result.Passed.Should().BeTrue();
		result.IsApplicable.Should().BeFalse();
	}

	[Fact]
	public async Task NotApplicable_WhenOnlyATestProjectHasClientOptions()
	{
		var result = await Evaluate(new() { ["Foo.Api.Test/FooClientOptions.cs"] = "public class FooClientOptions { }" });

		result.IsApplicable.Should().BeFalse();
	}

	[Fact]
	public async Task Passes_WhenIsReadOnlyIsDeclared()
	{
		var result = await Evaluate(Options("public bool IsReadOnly { get; set; }"));

		result.Passed.Should().BeTrue();
		result.IsApplicable.Should().BeTrue();
	}

	[Fact]
	public async Task Fails_WhenOptionsHaveNoReadOnlyProperty()
	{
		var result = await Evaluate(Options("public string? Token { get; set; }"));

		result.Passed.Should().BeFalse();
		result.Message.Should().Contain("IsReadOnly");
		result.Advisory!.Data["options_files"].Should().BeEquivalentTo(new[] { _optionsPath });
	}

	[Fact]
	public async Task Fails_WhenOnlyTheLegacyReadOnlyExists()
	{
		var result = await Evaluate(Options("public bool ReadOnly { get; set; }"));

		result.Passed.Should().BeFalse();
	}

	[Fact]
	public async Task Passes_WhenLegacyReadOnlyIsAnObsoleteAlias()
	{
		var result = await Evaluate(Options("""
			public bool IsReadOnly { get; set; }

			[Obsolete("Use IsReadOnly")]
			public bool ReadOnly
			{
				get => IsReadOnly;
				set => IsReadOnly = value;
			}
			"""));

		result.Passed.Should().BeTrue();
	}

	[Fact]
	public async Task Fails_WhenLegacyReadOnlyIsNotObsolete()
	{
		var result = await Evaluate(Options("""
			public bool IsReadOnly { get; set; }

			public bool ReadOnly { get; set; }
			"""));

		result.Passed.Should().BeFalse();
		result.Message.Should().Contain("not marked [Obsolete]");
	}

	[Fact]
	public async Task Passes_WhenAnotherOptionsFileDeclaresIsReadOnly()
	{
		// Rapid7 and Joker have several options classes; one carrying the option is enough.
		var files = Options("public bool IsReadOnly { get; set; }");
		files["Foo.Api/Foo.Api/FooMcpClientOptions.cs"] = "public class FooMcpClientOptions { }";

		var result = await Evaluate(files);

		result.Passed.Should().BeTrue();
	}

	[Fact]
	public async Task NotApplicable_WhenTheOptionsFileWasNotRead()
	{
		var context = Context(new() { [_optionsPath] = "unused" }, filePaths: [_optionsPath], contents: []);

		var result = await new ClientOptionsIsReadOnlyRule().EvaluateAsync(context, TestContext.Current.CancellationToken);

		result.IsApplicable.Should().BeFalse();
	}

	private static Dictionary<string, string> Options(string members)
		=> new() { [_optionsPath] = $"public class FooClientOptions\n{{\n{members}\n}}" };

	private static Task<RuleResult> Evaluate(Dictionary<string, string> files)
		=> new ClientOptionsIsReadOnlyRule().EvaluateAsync(Context(files), TestContext.Current.CancellationToken);

	private static RepositoryContext Context(
		Dictionary<string, string> files,
		List<string>? filePaths = null,
		Dictionary<string, string>? contents = null)
		=> new()
		{
			FullName = "panoramicdata/Foo.Api",
			Name = "Foo.Api",
			DefaultBranch = "main",
			CurrentBranch = "main",
			Options = new RepoOptions(),
			FilePaths = filePaths ?? [.. files.Keys],
			FileContents = contents ?? files
		};
}
