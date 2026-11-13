using System.Text.Json;
using PanoramicData.NugetManagement.Models;
using PanoramicData.NugetManagement.Web.Remediations.Documentation;

namespace PanoramicData.NugetManagement.Test;

/// <summary>
/// DOC-02 fix: the supported-versions sentence goes into README.md, stale plain versions are updated when
/// exactly one is supported, and everything that needs judgement is left alone.
/// </summary>
public sealed class SupportedDotNetVersionsDocumentedRemediationTests : IDisposable
{
	private const string Phrase = "Supports .NET 11.0";

	private readonly string _root = Path.Combine(Path.GetTempPath(), "doc02-tests", Guid.NewGuid().ToString("n"));
	private readonly SupportedDotNetVersionsDocumentedRemediation _remediation = new();

	public SupportedDotNetVersionsDocumentedRemediationTests() => Directory.CreateDirectory(_root);

	public void Dispose()
	{
		if (Directory.Exists(_root))
		{
			Directory.Delete(_root, recursive: true);
		}

		GC.SuppressFinalize(this);
	}

	private static RuleResult Failing(string[] supported, string[] mentions, object? phrase = null, bool passed = false) => new()
	{
		RuleId = "DOC-02",
		RuleName = "Documentation states the supported .NET versions",
		Category = AssessmentCategory.Documentation,
		Severity = AssessmentSeverity.Warning,
		Passed = passed,
		Message = "not documented correctly",
		Advisory = new RuleAdvisory
		{
			Summary = "s",
			Detail = "d",
			Data = new()
			{
				["file"] = "README.md",
				["required_phrase"] = phrase ?? Phrase,
				["supported_versions"] = supported,
				["unsupported_mentions"] = mentions
			}
		}
	};

	private string Path_(string name) => Path.Combine(_root, name);

	private string Read(string name) => File.ReadAllText(Path_(name));

	private void Write(string name, string content)
	{
		Directory.CreateDirectory(Path.GetDirectoryName(Path_(name))!);
		File.WriteAllText(Path_(name), content);
	}

	private List<string> Apply(RuleResult result)
	{
		var applied = new List<string>();
		_remediation.Apply(_root, result, applied, null);
		return applied;
	}

	[Fact]
	public void TheSentenceGoesAfterTheTitleAndItsBadges()
	{
		Write("README.md", "# Acme.Widget\n[![NuGet](https://x/y.svg)](https://x)\n![Codacy](https://x/c.svg)\n\nA widget library.\n");

		var applied = Apply(Failing([".NET 11.0"], []));

		applied.Should().Equal("README.md");
		Read("README.md").Should().Be("# Acme.Widget\n[![NuGet](https://x/y.svg)](https://x)\n![Codacy](https://x/c.svg)\n\nSupports .NET 11.0.\n\nA widget library.\n");
	}

	[Fact]
	public void TheSentenceIsAddedWhenThereIsNoTitle_AndOnlyOnce()
	{
		Write("README.md", "Just some text.\n");

		Apply(Failing([".NET 11.0"], [])).Should().Equal("README.md");
		Apply(Failing([".NET 11.0"], [])).Should().BeEmpty("the second run finds the sentence already there");

		Read("README.md").Should().StartWith("Supports .NET 11.0.\n").And.Contain("Just some text.");
	}

	[Fact]
	public void LineEndingsAndTheByteOrderMarkSurvive()
	{
		var path = Path_("README.md");
		File.WriteAllText(path, "# Acme\r\n\r\nText\r\n", new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: true));

		Apply(Failing([".NET 11.0"], []));

		var bytes = File.ReadAllBytes(path);
		bytes[..3].Should().Equal(0xEF, 0xBB, 0xBF);
		var text = System.Text.Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3);
		text.Replace("\r\n", string.Empty).Should().NotContain("\n", "every line ending stayed CRLF");
		text.Should().Contain("Supports .NET 11.0.");
	}

	[Fact]
	public void StalePlainVersionsBecomeTheSupportedOne_InMarkdownAndProjectDescriptions()
	{
		Write("README.md", "# Acme\n\nSupports .NET 11.0.\n\nBuilt for .NET 10 and net10.0, tested on .NET 8.0.\n");
		Write(".github/copilot-instructions.md", "Targets net9.0.\n");
		Write("Acme/Acme.csproj", "<Project><PropertyGroup><Description>A library for .NET 10.</Description></PropertyGroup></Project>");

		var applied = Apply(Failing(
			[".NET 11.0"],
			[
				"README.md: .NET 10", "README.md: net10.0", "README.md: .NET 8.0",
				".github/copilot-instructions.md: net9.0",
				"Acme/Acme.csproj <Description>: .NET 10"
			]));

		applied.Should().BeEquivalentTo("README.md", ".github/copilot-instructions.md", "Acme/Acme.csproj");
		Read("README.md").Should().Contain("Built for .NET 11.0 and net11.0, tested on .NET 11.0.");
		Read(".github/copilot-instructions.md").Should().Be("Targets net11.0.\n");
		Read("Acme/Acme.csproj").Should().Contain("A library for .NET 11.0.");
	}

	[Fact]
	public void MentionsThatNeedJudgementAreLeftAlone()
	{
		const string original = "# Acme\n\nSupports .NET 11.0.\n\nOld: .NET Framework 4.8, .NET Standard 2.0, .NET Core 3.1, netstandard2.0, net10.0-windows, net48.\n";
		Write("README.md", original);

		var applied = Apply(Failing(
			[".NET 11.0"],
			["README.md: .NET Framework 4.8", "README.md: .NET Standard 2.0", "README.md: .NET Core 3.1", "README.md: netstandard2.0", "README.md: net10.0", "README.md: net48"]));

		applied.Should().BeEmpty();
		Read("README.md").Should().Be(original);
	}

	[Fact]
	public void SeveralSupportedVersionsMeanNoSingleReplacement_OnlyTheSentenceIsAdded()
	{
		Write("README.md", "# Acme\n\nBuilt for .NET 7.\n");

		var applied = Apply(Failing([".NET 8.0", ".NET 10.0"], ["README.md: .NET 7"], "Supports .NET 8.0 and .NET 10.0"));

		applied.Should().Equal("README.md");
		Read("README.md").Should().Contain("Supports .NET 8.0 and .NET 10.0.").And.Contain("Built for .NET 7.");
	}

	[Fact]
	public void ADocumentedVersionIsNeverRewritten()
	{
		Write("README.md", "# Acme\n\nSupports .NET 11.0.\n\nUses .NET 11 and net11.0 and .NET 11.0.\n");

		Apply(Failing([".NET 11.0"], ["README.md: .NET 10"])).Should().BeEmpty();
	}

	[Fact]
	public void ANonUtf8ReadmeKeepsItsOtherBytes()
	{
		var path = Path_("README.md");
		var original = new byte[] { (byte)'#', (byte)' ', (byte)'A', (byte)'\n', (byte)'\n', 0xA9, (byte)' ', (byte)'2', (byte)'0', (byte)'2', (byte)'6', (byte)'\n' };
		File.WriteAllBytes(path, original);

		Apply(Failing([".NET 11.0"], [])).Should().Equal("README.md");

		var bytes = File.ReadAllBytes(path);
		bytes.Should().Contain(0xA9).And.NotContain(0xEF, "no U+FFFD replacement was written");
		System.Text.Encoding.Latin1.GetString(bytes).Should().Contain("Supports .NET 11.0.").And.Contain("© 2026");
	}

	[Fact]
	public void AMissingReadmeIsNotAnError()
		=> Apply(Failing([".NET 11.0"], [])).Should().BeEmpty();

	[Fact]
	public void TheFixWorksOnDataAsTheRowCacheReadsItBack()
	{
		Write("README.md", "# Acme\n\nBuilt for .NET 10.\n");
		var result = Failing([".NET 11.0"], ["README.md: .NET 10"]);
		foreach (var key in new[] { "supported_versions", "unsupported_mentions" })
		{
			result.Advisory!.Data[key] = JsonSerializer.Deserialize<JsonElement>(JsonSerializer.Serialize(result.Advisory.Data[key]));
		}

		result.Advisory!.Data["required_phrase"] = JsonSerializer.Deserialize<JsonElement>("\"Supports .NET 11.0\"");

		Apply(result).Should().BeEquivalentTo("README.md");
		Read("README.md").Should().Contain("Supports .NET 11.0.").And.Contain("Built for .NET 11.0.");
	}

	[Fact]
	public void ItCannotRemediateWithoutThePhrase_OrAPassingResult()
	{
		var noData = Failing([".NET 11.0"], []);
		noData.Advisory!.Data.Remove("required_phrase");
		_remediation.CanRemediate(noData).Should().BeFalse();

		_remediation.CanRemediate(Failing([".NET 11.0"], [], passed: true)).Should().BeFalse();

		_remediation.CanRemediate(Failing([".NET 11.0"], [])).Should().BeTrue();
	}
}
