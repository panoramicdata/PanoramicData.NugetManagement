using PanoramicData.NugetManagement.Web.Services;

namespace PanoramicData.NugetManagement.Test;

/// <summary>
/// Tests for reading owner and name out of a nuspec's declared repository URL. Three of our own
/// packages — PanoramicData.ConsoleExtensions among them — declare the SCP-style
/// <c>git@github.com:owner/repo.git</c>, which is not a URI at all. Parsing it as one threw
/// "Invalid URI: The URI scheme is not valid.", and because that happened inside the discovery
/// loop it took the whole organisation's rediscovery down with it.
/// </summary>
public class GitHubRepositoryUrlTests(ITestOutputHelper output) : TestWithOutput(output)
{
	[Theory]
	[InlineData("https://github.com/panoramicdata/Meraki.Api")]
	[InlineData("https://github.com/panoramicdata/Meraki.Api.git")]
	[InlineData("https://github.com/panoramicdata/Meraki.Api/")]
	[InlineData("http://github.com/panoramicdata/Meraki.Api")]
	[InlineData("https://www.github.com/panoramicdata/Meraki.Api")]
	[InlineData("git://github.com/panoramicdata/Meraki.Api.git")]
	[InlineData("ssh://git@github.com/panoramicdata/Meraki.Api.git")]
	[InlineData("git@github.com:panoramicdata/Meraki.Api.git")]
	public void EveryFormOfTheSameRepositoryShouldNormaliseTheSameWay(string url)
		=> GitHubRepositoryUrl.Normalize(url)
			.Should().Be("https://github.com/panoramicdata/Meraki.Api");

	[Fact]
	public void TheOwnerAndNameShouldBeReadFromAnScpStyleUrl()
	{
		const string url = "git@github.com:panoramicdata/PanoramicData.ConsoleExtensions.git";

		GitHubRepositoryUrl.Owner(url).Should().Be("panoramicdata");
		GitHubRepositoryUrl.Name(url).Should().Be("PanoramicData.ConsoleExtensions");
	}

	[Fact]
	public void TheOwnerAndNameShouldBeReadFromAnHttpsUrl()
	{
		const string url = "https://github.com/panoramicdata/Meraki.Api";

		GitHubRepositoryUrl.Owner(url).Should().Be("panoramicdata");
		GitHubRepositoryUrl.Name(url).Should().Be("Meraki.Api");
	}

	[Theory]
	[InlineData(null)]
	[InlineData("")]
	[InlineData("   ")]
	[InlineData("not a url at all")]
	[InlineData("https://gitlab.com/panoramicdata/Meraki.Api")]
	[InlineData("https://github.com/panoramicdata")]
	[InlineData("git@github.com:panoramicdata")]
	public void AnythingThatIsNotAGitHubRepositoryShouldBeNullRatherThanThrow(string? url)
	{
		GitHubRepositoryUrl.Normalize(url).Should().BeNull();
		GitHubRepositoryUrl.Owner(url).Should().BeNull();
		GitHubRepositoryUrl.Name(url).Should().BeNull();
	}

	[Fact]
	public void ADeepLinkIntoTheRepositoryShouldStillNameTheRepository()
		=> GitHubRepositoryUrl.Normalize("https://github.com/panoramicdata/Meraki.Api/tree/main/src")
			.Should().Be("https://github.com/panoramicdata/Meraki.Api");
}
