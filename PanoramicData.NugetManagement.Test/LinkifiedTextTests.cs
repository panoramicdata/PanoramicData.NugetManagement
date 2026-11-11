using PanoramicData.NugetManagement.Web.Components;

namespace PanoramicData.NugetManagement.Test;

/// <summary>
/// A rule message that names a GitHub run must link to it, and only to http or https addresses.
/// </summary>
public class LinkifiedTextTests
{
	[Fact]
	public void AnAddressInAMessageBecomesALink()
	{
		var parts = LinkifiedText.Split("The release run failed: run 43031720155 ended as failure. https://github.com/panoramicdata/Blink1.Api/actions/runs/43031720155");

		parts.Should().HaveCount(2);
		parts[0].IsLink.Should().BeFalse();
		parts[1].Should().Be(new LinkifiedText.Part("https://github.com/panoramicdata/Blink1.Api/actions/runs/43031720155", true));
	}

	[Fact]
	public void PunctuationEndingASentenceIsNotPartOfTheAddress()
	{
		var parts = LinkifiedText.Split("See https://example.com/run/1. Then retry.");

		parts.Where(p => p.IsLink).Should().ContainSingle().Which.Text.Should().Be("https://example.com/run/1");
		string.Concat(parts.Select(p => p.Text)).Should().Be("See https://example.com/run/1. Then retry.", "splitting loses no text");
	}

	[Fact]
	public void SeveralAddressesAreEachLinked()
		=> LinkifiedText.Split("a http://x.test/1 b https://y.test/2 c").Count(p => p.IsLink).Should().Be(2);

	[Theory]
	[InlineData("javascript:alert(1)")]
	[InlineData("ftp://example.com/file")]
	[InlineData("no address here")]
	[InlineData("https://")]
	public void OnlyHttpAndHttpsAddressesAreLinked(string text)
		=> LinkifiedText.Split(text).Should().NotContain(p => p.IsLink);

	[Theory]
	[InlineData(null)]
	[InlineData("")]
	public void NothingToShowGivesNoParts(string? text)
		=> LinkifiedText.Split(text).Should().BeEmpty();
}
