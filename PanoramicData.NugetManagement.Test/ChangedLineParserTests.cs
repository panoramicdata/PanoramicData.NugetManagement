using PanoramicData.NugetManagement.Web.Services.AiChange;

namespace PanoramicData.NugetManagement.Test;

/// <summary>
/// Tests for <see cref="ChangedLineParser"/>: turning <c>git diff -U0</c> into changed files and lines.
/// </summary>
public class ChangedLineParserTests(ITestOutputHelper output) : TestWithOutput(output)
{
	private const string Diff = """
		diff --git a/Lib/Parser.cs b/Lib/Parser.cs
		--- a/Lib/Parser.cs
		+++ b/Lib/Parser.cs
		@@ -10,0 +11,2 @@ class Parser
		+a
		+b
		@@ -20 +22 @@
		-old
		+new
		diff --git a/Lib/Gone.cs b/Lib/Gone.cs
		--- a/Lib/Gone.cs
		+++ /dev/null
		@@ -1,3 +0,0 @@
		-x
		""";

	[Fact]
	public void Parse_ReadsAddedAndModifiedLinesOnTheNewSide()
	{
		var files = ChangedLineParser.Parse(Diff);

		files.Should().ContainSingle(file => file.Path == "Lib/Parser.cs")
			.Which.Lines.Should().BeEquivalentTo(new[] { 11, 12, 22 });
	}

	[Fact]
	public void Parse_SkipsDeletedFiles()
		=> ChangedLineParser.Parse(Diff).Should().NotContain(file => file.Path == "Lib/Gone.cs",
			"a deleted file has no new-side lines to cover");
}
