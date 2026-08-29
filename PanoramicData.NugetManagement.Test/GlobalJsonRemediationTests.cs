using PanoramicData.NugetManagement.Models;
using PanoramicData.NugetManagement.Rules;
using PanoramicData.NugetManagement.Services;
using PanoramicData.NugetManagement.Web.Remediations.Versioning;

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
		result.Advisory!.Data["remediation_type"].Should().Be("ensure_json_properties");
		Properties(result).Should().ContainKey("sdk.version");
		result.Advisory!.Data.Should().NotContainKey("new_content");
	}

	[Fact]
	public async Task VER03_ShouldPass_WhenPinnedAheadOfTheFloor()
	{
		// The pin is a floor. A repository already on a later feature band is not less conformant than
		// one on the floor, and telling it to move down is nonsense — rollForward never rolls down.
		var context = CreateContext(
			testProject: _xunitV3,
			globalJson: """{"sdk":{"version":"10.0.400","rollForward":"latestMinor"}}""");

		var result = await Rule("VER-03").EvaluateAsync(context, CancellationToken.None);

		result.Passed.Should().BeTrue("10.0.400 is above the 10.0.100 floor");
	}

	[Fact]
	public async Task VER03_ShouldFail_WhenRollForwardIsMissing()
	{
		// Without rollForward the default is latestPatch, which cannot leave the pinned feature band:
		// a repository floored at 10.0.100 then refuses to build on a machine whose only SDK is 10.0.400.
		var context = CreateContext(
			testProject: _xunitV3,
			globalJson: "{\"sdk\":{\"version\":\"" + Standards.DotNetSdkPinVersion + "\"}}");

		var result = await Rule("VER-03").EvaluateAsync(context, CancellationToken.None);

		result.Passed.Should().BeFalse();
		Properties(result).Should().ContainKey("sdk.rollForward");
	}

	[Fact]
	public async Task VER03_ShouldFail_WhenRollForwardIsDisabled()
	{
		var context = CreateContext(
			testProject: _xunitV3,
			globalJson: "{\"sdk\":{\"version\":\"" + Standards.DotNetSdkPinVersion + "\",\"rollForward\":\"disable\"}}");

		var result = await Rule("VER-03").EvaluateAsync(context, CancellationToken.None);

		result.Passed.Should().BeFalse("disable pins the build to one exact SDK build");
	}

	[Theory]
	[InlineData("patch")]
	[InlineData("feature")]
	[InlineData("latestPatch")]
	[InlineData("latestFeature")]
	public async Task VER03_ShouldFail_WhenRollForwardCannotCrossFeatureBands(string rollForward)
	{
		var context = CreateContext(
			testProject: _xunitV3,
			globalJson: "{\"sdk\":{\"version\":\"" + Standards.DotNetSdkPinVersion + "\",\"rollForward\":\"" + rollForward + "\"}}");

		var result = await Rule("VER-03").EvaluateAsync(context, CancellationToken.None);

		result.Passed.Should().BeFalse($"{rollForward} cannot reach an SDK in a higher feature band");
	}

	[Theory]
	[InlineData("latestMinor")]
	[InlineData("latestMajor")]
	[InlineData("minor")]
	[InlineData("major")]
	public async Task VER03_ShouldPass_ForEveryBandCrossingRollForward(string rollForward)
	{
		var context = CreateContext(
			testProject: _xunitV3,
			globalJson: "{\"sdk\":{\"version\":\"" + Standards.DotNetSdkPinVersion + "\",\"rollForward\":\"" + rollForward + "\"}}");

		var result = await Rule("VER-03").EvaluateAsync(context, CancellationToken.None);

		result.Passed.Should().BeTrue($"{rollForward} can reach any SDK in the major");
	}

	[Fact]
	public async Task VER03_ShouldRemediateOnlyWhatIsWrong()
	{
		// A repository already above the floor should keep its version: only the missing rollForward
		// is added. Rewriting the version would move it down a band for no reason.
		var context = CreateContext(
			testProject: _xunitV3,
			globalJson: """{"sdk":{"version":"10.0.400"}}""");

		var result = await Rule("VER-03").EvaluateAsync(context, CancellationToken.None);

		result.Passed.Should().BeFalse();
		Properties(result).Should().ContainKey("sdk.rollForward");
		Properties(result).Should().NotContainKey("sdk.version", "10.0.400 is already above the floor");
	}

	[Fact]
	public async Task VER03_ShouldFail_WhenTheSdkVersionIsMissingEntirely()
	{
		var context = CreateContext(
			testProject: _xunitV3,
			globalJson: """{"sdk":{"rollForward":"latestMinor"}}""");

		var result = await Rule("VER-03").EvaluateAsync(context, CancellationToken.None);

		result.Passed.Should().BeFalse();
		Properties(result).Should().ContainKey("sdk.version");
	}

	[Fact]
	public async Task VER03_ShouldFail_WhenGlobalJsonIsNotValidJson()
	{
		var context = CreateContext(testProject: _xunitV3, globalJson: "{ not json");

		var result = await Rule("VER-03").EvaluateAsync(context, CancellationToken.None);

		result.Passed.Should().BeFalse();
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

	[Fact]
	public async Task VER03_Remediation_ShouldAddOnlyTheMissingProperty()
	{
		// End-to-end: the advisory now carries more than one property, and applying it must leave the
		// rest of the file — and a version already above the floor — exactly as it was.
		const string globalJson = """{"sdk":{"version":"10.0.400"},"msbuild-sdks":{"Contoso.Build":"1.2.3"}}""";
		var directory = Path.Combine(Path.GetTempPath(), "ver03-" + Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(directory);

		try
		{
			File.WriteAllText(Path.Combine(directory, "global.json"), globalJson);
			var result = await Rule("VER-03").EvaluateAsync(
				CreateContext(testProject: _xunitV3, globalJson: globalJson),
				CancellationToken.None);

			var remediation = new GlobalJsonRemediation();
			remediation.CanRemediate(result).Should().BeTrue();
			remediation.Apply(directory, result, [], null);

			var updated = File.ReadAllText(Path.Combine(directory, "global.json"));
			updated.Should().Contain(Standards.SdkRollForward);
			updated.Should().Contain("10.0.400", "a version above the floor must not be moved down");
			updated.Should().Contain("Contoso.Build", "the rule has no opinion on the rest of the file");
		}
		finally
		{
			Directory.Delete(directory, recursive: true);
		}
	}

	private static Dictionary<string, string> Properties(RuleResult result)
		=> result.Advisory!.Data["properties"].Should().BeOfType<Dictionary<string, string>>().Subject;

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
