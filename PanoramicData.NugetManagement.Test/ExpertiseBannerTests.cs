using PanoramicData.NugetManagement.Models;
using PanoramicData.NugetManagement.Rules;
using PanoramicData.NugetManagement.Services;
using PanoramicData.NugetManagement.Web.Remediations;

namespace PanoramicData.NugetManagement.Test;

/// <summary>
/// Tests README-05: the expertise statement at the top of the README, its configurable template and its fix.
/// </summary>
public class ExpertiseBannerTests(ITestOutputHelper output) : TestWithOutput(output)
{
	private static readonly string _banner = ReadmeExpertiseBanner.Render(Standards.ExpertiseBannerTemplate, "Acme.Lib");

	[Fact]
	public void DefaultTemplate_NamesExpertiseAndBothPages()
	{
		_banner.Should().Contain("experts in APIs and .NET");
		_banner.Should().Contain("https://www.panoramicdata.com/contact-us");
		_banner.Should().Contain("https://www.panoramicdata.com/services/software");
	}

	[Fact]
	public async Task ShouldPass_WhenBannerFollowsTitleAndBadges()
	{
		var readme = $"# Acme.Lib\n\n[![NuGet](x)](y)\n\n{_banner}\n\n## Overview\n";

		var result = await Evaluate(readme);

		result.Passed.Should().BeTrue(result.Message);
	}

	[Fact]
	public async Task ShouldFail_WhenBannerIsMissing()
	{
		var result = await Evaluate("# Acme.Lib\n\nA library.\n");

		result.Passed.Should().BeFalse();
		result.Advisory!.Data["banner"].Should().Be(_banner);
	}

	[Fact]
	public async Task ShouldFail_WhenBannerIsBuriedBelowASection()
	{
		var result = await Evaluate($"# Acme.Lib\n\n## Overview\n\n{_banner}\n");

		result.Passed.Should().BeFalse();
	}

	[Fact]
	public async Task ShouldUseConfiguredTemplate()
	{
		var result = await Evaluate(
			"# Acme.Lib\n\nHire us for Acme.Lib work.\n\n## Overview\n",
			new RepoOptions { ExpertiseBannerTemplate = "Hire us for {PackageName} work." });

		result.Passed.Should().BeTrue(result.Message);
	}

	[Fact]
	public async Task ShouldBeNotApplicable_WhenReadmeIsMissing()
	{
		var result = await Evaluate(null);

		result.IsApplicable.Should().BeFalse();
	}

	[Theory]
	[InlineData("\n")]
	[InlineData("\r\n")]
	public void Insert_PlacesBannerUnderTitleAndBadges_KeepingLineEndings(string eol)
	{
		var readme = string.Join(eol, "# Acme.Lib", "", "[![NuGet](x)](y)", "[![MIT](x)](y)", "", "A library.", "", "## Overview", "");

		var fixedReadme = ReadmeExpertiseBanner.Insert(readme, _banner);

		fixedReadme.Should().Be(string.Join(eol, "# Acme.Lib", "", "[![NuGet](x)](y)", "[![MIT](x)](y)", "", _banner, "", "A library.", "", "## Overview", ""));
		ReadmeExpertiseBanner.IsAtTop(fixedReadme, _banner).Should().BeTrue();
	}

	[Fact]
	public void Insert_MovesABuriedBanner_AndIsIdempotent()
	{
		var readme = $"# Acme.Lib\n\nA library.\n\n## Overview\n\n{_banner}\n";

		var once = ReadmeExpertiseBanner.Insert(readme, _banner);
		var twice = ReadmeExpertiseBanner.Insert(once, _banner);

		once.Split(_banner).Length.Should().Be(2, "the banner appears exactly once");
		ReadmeExpertiseBanner.IsAtTop(once, _banner).Should().BeTrue();
		twice.Should().Be(once);
	}

	[Fact]
	public async Task Remediation_FixesTheReadme_ThenTheRulePasses()
	{
		var root = Path.Combine(Path.GetTempPath(), "readme05-" + Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(root);
		try
		{
			var path = Path.Combine(root, "README.md");
			File.WriteAllText(path, "# Acme.Lib\n\nA library.\n");

			var failed = await Evaluate(File.ReadAllText(path));
			var registry = new RemediationRegistry();
			registry.CanRemediate(failed).Should().BeTrue();

			var applied = new List<string>();
			registry.Get("README-05")!.Apply(root, failed, applied, null);

			applied.Should().Equal("README.md");
			var after = await Evaluate(File.ReadAllText(path));
			after.Passed.Should().BeTrue(after.Message);
		}
		finally
		{
			Directory.Delete(root, recursive: true);
		}
	}

	private static async Task<RuleResult> Evaluate(string? readme, RepoOptions? options = null)
	{
		var files = readme is null ? new Dictionary<string, string>() : new() { ["README.md"] = readme };
		var context = new RepositoryContext
		{
			FullName = "test-org/Acme.Lib",
			Name = "Acme.Lib",
			DefaultBranch = "main",
			CurrentBranch = "main",
			Options = options ?? new RepoOptions(),
			FilePaths = [.. files.Keys],
			FileContents = files
		};

		return await RuleRegistry.Rules.Single(r => r.RuleId == "README-05")
			.EvaluateAsync(context, CancellationToken.None)
			.ConfigureAwait(false);
	}
}
