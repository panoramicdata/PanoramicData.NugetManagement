using PanoramicData.NugetManagement.Web.Services.DocComments;

namespace PanoramicData.NugetManagement.Test.DocComments;

/// <summary>Tests for <see cref="DocResponseParser"/>: only <c>///</c> lines under an <c>@@@ n</c> marker survive.</summary>
public class DocResponseParserTests(ITestOutputHelper output) : TestWithOutput(output)
{
	[Fact]
	public void ReadsBlocksByNumber()
	{
		var result = DocResponseParser.Parse("@@@ 1\r\n/// <summary>\r\n/// One.\r\n/// </summary>\r\n@@@ 2\r\n/// <summary>Two.</summary>");

		result.Keys.Should().BeEquivalentTo([1, 2]);
		result[1].Should().Equal("/// <summary>", "/// One.", "/// </summary>");
	}

	[Fact]
	public void DiscardsProseFencesAndCode()
	{
		var result = DocResponseParser.Parse("Sure!\n@@@ 1\n```csharp\npublic int X;\n/// <summary>Hi there.</summary>\n```\ncommentary");

		result[1].Should().Equal("/// <summary>Hi there.</summary>");
	}

	[Fact]
	public void IgnoresLinesBeforeTheFirstMarker()
		=> DocResponseParser.Parse("/// <summary>orphan</summary>").Should().BeEmpty();

	[Fact]
	public void DropsEmptyBlocksAndFourSlashLines()
		=> DocResponseParser.Parse("@@@ 1\n////commented out\n@@@ 2\n").Should().BeEmpty();

	[Fact]
	public void NormalisesTheSpaceAfterTheSlashes()
		=> DocResponseParser.Parse("@@@ 1\n///<summary>x y z</summary>   ")[1].Should().Equal("/// <summary>x y z</summary>");

	[Fact]
	public void TheFirstBlockForANumberWins()
		=> DocResponseParser.Parse("@@@ 1\n/// <summary>first</summary>\n@@@ 1\n/// <summary>second</summary>")[1]
			.Should().Equal("/// <summary>first</summary>");
}
