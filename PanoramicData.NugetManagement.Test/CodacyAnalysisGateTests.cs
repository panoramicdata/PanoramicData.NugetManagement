using System.Text.Json;
using PanoramicData.NugetManagement.Services;

namespace PanoramicData.NugetManagement.Test;

/// <summary>
/// Tests for <see cref="CodacyAnalysisGate"/>: findings are acted on only while they describe the commit that is checked out.
/// </summary>
public class CodacyAnalysisGateTests
{
	private static Dictionary<string, object> Data(string? sha, bool? analysing = null)
	{
		var data = new Dictionary<string, object>();

		if (sha is not null)
		{
			data["codacy_analysed_sha"] = sha;
		}

		if (analysing is { } flag)
		{
			data["codacy_is_analysing"] = flag;
		}

		return data;
	}

	[Fact]
	public void Allows_WhenCodacyAnalysedTheCheckedOutCommit()
		=> CodacyAnalysisGate.Refusal(Data("0123456789abcdef"), "0123456789abcdef").Should().BeNull();

	[Fact]
	public void Allows_WhenOnlyTheAbbreviationDiffers()
		=> CodacyAnalysisGate.Refusal(Data("0123456"), "0123456789abcdef").Should().BeNull();

	[Fact]
	public void Refuses_WhenCodacyAnalysedAnOlderCommit_AndNamesBoth()
	{
		var reason = CodacyAnalysisGate.Refusal(Data("1111111aaaa"), "2222222bbbb");

		reason.Should().Contain("1111111").And.Contain("2222222").And.Contain("already have been fixed");
	}

	[Fact]
	public void Refuses_WhileCodacyIsStillAnalysing()
		=> CodacyAnalysisGate.Refusal(Data("0123456", analysing: true), "0123456").Should().Contain("re-analysing");

	[Fact]
	public void Allows_WhenNothingIsKnownAboutFreshness()
	{
		CodacyAnalysisGate.Refusal(null, "0123456").Should().BeNull();
		CodacyAnalysisGate.Refusal(Data(null), "0123456").Should().BeNull("an unknown freshness is not evidence of staleness");
	}

	[Fact]
	public void Allows_WhenTheCloneHeadIsUnknown()
		=> CodacyAnalysisGate.Refusal(Data("1111111"), null).Should().BeNull();

	[Fact]
	public void ReadsValuesThatCameBackFromTheCacheAsJson()
	{
		// An assessment reloaded from disk holds JsonElements, not strings and booleans.
		var data = new Dictionary<string, object>
		{
			["codacy_analysed_sha"] = JsonSerializer.SerializeToElement("1111111aaaa"),
			["codacy_is_analysing"] = JsonSerializer.SerializeToElement(false)
		};

		CodacyAnalysisGate.Refusal(data, "2222222bbbb").Should().Contain("1111111");

		data["codacy_is_analysing"] = JsonSerializer.SerializeToElement(true);
		CodacyAnalysisGate.Refusal(data, "2222222bbbb").Should().Contain("re-analysing");
	}
}