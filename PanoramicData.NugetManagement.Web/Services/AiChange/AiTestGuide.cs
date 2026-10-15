namespace PanoramicData.NugetManagement.Web.Services.AiChange;

/// <summary>
/// How to write tests in this estate, given to the model whenever it may write or create a test project.
/// </summary>
/// <remarks>
/// This restates what the TST rules grade (TST-01 to TST-06 and TST-08), so a project the model builds
/// from it passes them. If a rule changes, <c>AiTestGuideTests</c> is where the drift shows.
/// </remarks>
public static class AiTestGuide
{
	/// <summary>The guide, as it appears in a prompt.</summary>
	public const string Text = """
		Testing conventions for this repository (these are graded by automated rules):

		- Use xunit v3. The test project sets <OutputType>Exe</OutputType>, because xunit v3 builds its own runner executable.
		- Run tests with Microsoft.Testing.Platform: global.json contains "test": { "runner": "Microsoft.Testing.Platform" }.
		- Do not reference xunit.runner.visualstudio. It is the old VSTest adapter, has no role under Microsoft.Testing.Platform, and breaks restore under central package management.
		- Reference Microsoft.NET.Test.Sdk and Microsoft.Testing.Extensions.CodeCoverage.
		- Assert with AwesomeAssertions (using AwesomeAssertions;). Never use FluentAssertions.
		- Put an xunit.runner.json next to the tests that contains { "failSkips": true } and copy it to the output directory, so a skipped test fails instead of hiding.
		- Package versions live in Directory.Packages.props; add PackageVersion entries there and reference packages without a Version in the project file.
		- One test class per class under test, named <ClassUnderTest>Tests. Test method names say the behaviour, for example Parse_DropsNothingWhenTheLastRowIsBlank.
		- A test that reproduces a bug must fail by an assertion while the bug is present. A test that does not compile, or that matches no test, proves nothing.
		- Use tabs for indentation in C# and project files.
		""";
}
