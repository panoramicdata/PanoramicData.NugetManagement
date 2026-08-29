using PanoramicData.NugetManagement.Models;
using PanoramicData.NugetManagement.Rules;
using PanoramicData.NugetManagement.Services;

namespace PanoramicData.NugetManagement.Test;

/// <summary>
/// Tests for the boundary between two decisions that used to travel together: pinning the SDK, which
/// belongs to every repository, and opting into Microsoft.Testing.Platform, which belongs only to
/// repositories that can run on it. VER-03 handed out both, so remediating an SDK pin on an xunit v2
/// repository left `dotnet test` unable to run anything.
/// </summary>
public class GlobalJsonRemediationTests(ITestOutputHelper output) : TestWithOutput(output)
{
	private const string _xunitV3 =
		"<Project><ItemGroup><PackageReference Include=\"xunit.v3\" Version=\"4.0.0\" /></ItemGroup></Project>";

	private const string _xunitV2 =
		"<Project><ItemGroup><PackageReference Include=\"xunit\" Version=\"2.9.3\" /></ItemGroup></Project>";

	[Fact]
	public async Task VER03_ShouldNotOfferTheTestRunnerToAVsTestRepository()
	{
		var context = CreateContext(testProject: _xunitV2, globalJson: null);

		var result = await Rule("VER-03").EvaluateAsync(context, CancellationToken.None);

		result.Passed.Should().BeFalse();
		result.Advisory!.Data["template_content"].Should().BeOfType<string>()
			.Which.Should().NotContain("Microsoft.Testing.Platform",
				"an opt-in this repository cannot satisfy stops dotnet test finding any tests");
	}

	[Fact]
	public async Task VER03_ShouldOfferTheTestRunnerToAnXunitV3Repository()
	{
		var context = CreateContext(testProject: _xunitV3, globalJson: null);

		var result = await Rule("VER-03").EvaluateAsync(context, CancellationToken.None);

		result.Advisory!.Data["template_content"].Should().BeOfType<string>()
			.Which.Should().Contain("Microsoft.Testing.Platform");
	}

	[Fact]
	public async Task VER03_ShouldUpdateTheSdkPinWithoutRewritingTheWholeFile()
	{
		// Replacing the file discarded whatever else lived there — msbuild-sdks, a test runner the
		// repository had already configured — none of which this rule has an opinion on.
		var context = CreateContext(
			testProject: _xunitV3,
			globalJson: """{"sdk":{"version":"9.0.100"},"msbuild-sdks":{"Contoso.Build":"1.2.3"}}""");

		var result = await Rule("VER-03").EvaluateAsync(context, CancellationToken.None);

		result.Passed.Should().BeFalse();
		result.Advisory!.Data["remediation_type"].Should().Be("ensure_json_property");
		result.Advisory!.Data["property_path"].Should().Be("sdk.version");
		result.Advisory!.Data.Should().NotContainKey("new_content");
	}

	[Fact]
	public async Task TST06_ShouldRemoveAnOptInFromARepositoryThatCannotRunOnIt()
	{
		// The repositories a remediation has already broken: the key is present, the tests are v2.
		var context = CreateContext(
			testProject: _xunitV2,
			globalJson: """{"sdk":{"version":"10.0.400"},"test":{"runner":"Microsoft.Testing.Platform"}}""");

		var result = await Rule("TST-06").EvaluateAsync(context, CancellationToken.None);

		result.Passed.Should().BeFalse("the opt-in leaves dotnet test with nothing it can execute");
		result.Advisory!.Data["remediation_type"].Should().Be("remove_json_property");
		result.Advisory!.Data["property_path"].Should().Be("test");
	}

	[Fact]
	public async Task TST06_ShouldStayQuietForAVsTestRepositoryWithNoOptIn()
	{
		var context = CreateContext(testProject: _xunitV2, globalJson: """{"sdk":{"version":"10.0.400"}}""");

		var result = await Rule("TST-06").EvaluateAsync(context, CancellationToken.None);

		result.IsApplicable.Should().BeFalse();
	}

	[Fact]
	public void VER03_ShouldPinTheFeatureBandFloor_NotTheMachinesNewestSdk()
	{
		// Issue 76: LatestDotNetSdkVersion is whatever the machine running this tool has installed.
		// Pinning it makes one machine's install list a build requirement for every other machine,
		// because rollForward never rolls down.
		// Asserted on shape, not on a comparison with the detected SDK: on a machine whose newest
		// band happens to be the floor the two legitimately coincide, and a test that fails there
		// would be coupled to the host's install list - the very fault this fixes.
		Standards.DotNetSdkPinVersion.Should().EndWith(".100");
		Standards.DotNetSdkPinVersion.Should().StartWith(
			string.Join('.', Standards.LatestDotNetSdkVersion.Split('.').Take(2)) + ".");
	}

	[Theory]
	[InlineData(true)]
	[InlineData(false)]
	public void VER03_TemplateShouldRollForwardWithinTheMajor_NotWithinAFeatureBand(bool includeTestRunner)
	{
		var content = Standards.GetGlobalJsonContent(includeTestRunner);

		content.Should().Contain("latestMinor");
		content.Should().NotContain("latestFeature", "latestFeature cannot roll down to an installed band");
		content.Should().Contain(Standards.DotNetSdkPinVersion);
	}

	[Fact]
	public async Task VER03_ShouldPass_WhenGlobalJsonAlreadyPinsTheFloor()
	{
		var context = CreateContext(
			testProject: _xunitV3,
			globalJson: "{\"sdk\":{\"version\":\"" + Standards.DotNetSdkPinVersion + "\",\"rollForward\":\"latestMinor\"}}");

		var result = await Rule("VER-03").EvaluateAsync(context, CancellationToken.None);

		result.Passed.Should().BeTrue();
	}

	private static IRule Rule(string ruleId) => RuleRegistry.Rules.First(r => r.RuleId == ruleId);

	private static RepositoryContext CreateContext(string testProject, string? globalJson)
	{
		var files = new Dictionary<string, string>
		{
			["Acme.Widget/Acme.Widget.csproj"] = "<Project><PropertyGroup><GeneratePackageOnBuild>true</GeneratePackageOnBuild></PropertyGroup></Project>",
			["Acme.Widget.Test/Acme.Widget.Test.csproj"] = testProject
		};

		if (globalJson is not null)
		{
			files["global.json"] = globalJson;
		}

		return new RepositoryContext
		{
			FullName = "test-org/Acme.Widget",
			Name = "Acme.Widget",
			DefaultBranch = "main",
			CurrentBranch = "main",
			Options = new RepoOptions(),
			FilePaths = [.. files.Keys],
			FileContents = files
		};
	}
}
