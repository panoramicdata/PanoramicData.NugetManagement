using PanoramicData.NugetManagement.Web.Services;

namespace PanoramicData.NugetManagement.Test;

/// <summary>
/// Tests for <see cref="TestSandbox"/>, which exists because a coverage session ran a library's tests with
/// this machine's real credentials, reached a live vendor API and printed the key into a transcript.
/// </summary>
public class TestSandboxTests(ITestOutputHelper output) : TestWithOutput(output)
{
	[Theory]
	[InlineData("GITHUB_TOKEN")]
	[InlineData("CODACY_PROJECT_TOKEN")]
	[InlineData("Joker__ApiKey")]
	[InlineData("JOKER_API_KEY")]
	[InlineData("SERVICE_PASSWORD")]
	[InlineData("AWS_SECRET_ACCESS_KEY")]
	[InlineData("ConnectionStrings__Default")]
	[InlineData("GH_PAT")]
	[InlineData("codacy.api.token")]
	public void ANameThatSaysItHoldsACredential_IsRecognised(string name)
		=> TestSandbox.HoldsACredential(name).Should().BeTrue();

	[Theory]
	[InlineData("PATH")]
	[InlineData("PATHEXT")]
	[InlineData("USERPROFILE")]
	[InlineData("TEMP")]
	[InlineData("DOTNET_ROOT")]
	[InlineData("NUGET_PACKAGES")]
	[InlineData("COMPUTERNAME")]
	[InlineData("KEYBOARD_LAYOUT")]
	public void AnOrdinaryName_IsLeftAlone(string name)
		=> TestSandbox.HoldsACredential(name).Should().BeFalse(
			"removing PATH would break the very process the sandbox is meant to run");

	[Fact]
	public void Overrides_RemoveCredentialVariables_AndRedirectAppData()
	{
		var environment = new Dictionary<string, string?>
		{
			["PATH"] = "x",
			["GITHUB_TOKEN"] = "secret",
			["Joker__ApiKey"] = "secret"
		};

		var overrides = TestSandbox.Overrides(environment);

		overrides.Should().ContainKey("GITHUB_TOKEN").WhoseValue.Should().BeNull();
		overrides.Should().ContainKey("Joker__ApiKey").WhoseValue.Should().BeNull();
		overrides.Should().NotContainKey("PATH");
		overrides["APPDATA"].Should().NotBeNullOrWhiteSpace();
		Directory.Exists(overrides["APPDATA"]).Should().BeTrue();
		Directory.EnumerateFileSystemEntries(overrides["APPDATA"]!).Should().BeEmpty(
			"user secrets are read from under APPDATA, so it has to hold nothing");
	}

	[Fact]
	public void TheOverridesReachTheStartedProcess_AndRemoveRatherThanBlank()
	{
		var startInfo = LocalRepoService.BuildProcessStartInfo(
			@"C:\",
			"dotnet",
			["--version"],
			new Dictionary<string, string?> { ["SOME_TOKEN"] = null, ["APPDATA"] = @"C:\empty" });

		startInfo.Environment.ContainsKey("SOME_TOKEN").Should().BeFalse();
		startInfo.Environment["APPDATA"].Should().Be(@"C:\empty");
		startInfo.Environment["NO_COLOR"].Should().Be("1", "the existing setting survives");
	}
}
