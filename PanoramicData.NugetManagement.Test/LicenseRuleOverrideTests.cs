using PanoramicData.NugetManagement.Models;
using PanoramicData.NugetManagement.Services;
using PanoramicData.NugetManagement.Web.Remediations;

namespace PanoramicData.NugetManagement.Test;

/// <summary>
/// Tests LIC-01, LIC-02, README-04 and COM-02 against repositories whose licence is not the default.
/// </summary>
/// <remarks>
/// The LICENSE, README and CONTRIBUTING excerpts are taken from panoramicdata/Jira.Api and
/// panoramicdata/PanoramicData.EPPlus as fetched on 2026-11-20.
/// </remarks>
public class LicenseRuleOverrideTests(ITestOutputHelper output) : TestWithOutput(output)
{
	private const string _compound = "MIT AND BSD-2-Clause";
	private const string _lgpl = "LGPL-2.1-or-later";

	private const string _jiraLicense = """
		Jira.Api is a fork of the Atlassian.NET SDK by Federico Silva Armas
		(https://bitbucket.org/farmas/atlassian.net-sdk). It is distributed under two licences:

		- Code inherited from the Atlassian.NET SDK remains under the BSD 2-Clause License,
		  whose notice is reproduced in full below and must be retained.
		- Modifications and new code by Panoramic Data Limited are under the MIT License.

		SPDX-License-Identifier: MIT AND BSD-2-Clause
		""";

	private const string _epplusLicense = """
		                  GNU LESSER GENERAL PUBLIC LICENSE
		                       Version 2.1, February 1999
		""";

	private const string _jiraBadge = "[![License: MIT AND BSD-2-Clause](https://img.shields.io/badge/License-MIT_AND_BSD--2--Clause-blue.svg)](LICENSE)";
	private const string _epplusBadge = "[![License: LGPL v2.1 or later](https://img.shields.io/badge/License-LGPL_v2.1+-blue.svg)](LICENSE)";
	private const string _jiraContributingNote = "Code inherited from the Atlassian.NET SDK stays under its BSD 2-Clause License; keep its notice in [LICENSE](LICENSE).";

	[Theory]
	[InlineData(_compound, _jiraLicense)]
	[InlineData(_lgpl, _epplusLicense)]
	public async Task LIC01_Passes_ForTheRepositorysOwnLicenceText(string license, string text)
	{
		var result = await Evaluate("LIC-01", license, ("LICENSE", text));

		result.Passed.Should().BeTrue(result.Message);
	}

	[Fact]
	public async Task LIC01_Fails_ForMitTextUnderAnLgplOverride_AndOffersNoFix()
	{
		var result = await Evaluate("LIC-01", _lgpl, ("LICENSE", Standards.MitLicenseContent));

		result.Passed.Should().BeFalse();
		result.Message.Should().Contain("GNU LESSER GENERAL PUBLIC LICENSE");
		result.Advisory!.Data.Should().NotContainKeys("new_content", "template_content", "remediation_type");
		new RemediationRegistry().CanRemediate(result).Should().BeFalse("only MIT text can be written, and it would replace the LGPL");
	}

	[Fact]
	public async Task LIC01_MissingFileUnderAnOverride_OffersNoFix()
	{
		var result = await Evaluate("LIC-01", _compound);

		result.Passed.Should().BeFalse();
		new RemediationRegistry().CanRemediate(result).Should().BeFalse();
	}

	[Fact]
	public async Task LIC01_UnderTheDefault_StillOffersTheMitFix()
	{
		var result = await Evaluate("LIC-01", "MIT", ("LICENSE", "Some other licence"));

		result.Passed.Should().BeFalse();
		result.Message.Should().Contain("\"MIT License\"");
		new RemediationRegistry().CanRemediate(result).Should().BeTrue();
	}

	[Fact]
	public async Task LIC01_AnExplicitMarker_ReplacesTheDerivedOnes()
	{
		var options = new RepoOptions { ExpectedLicense = _lgpl, ExpectedLicenseFileText = "Version 2.1, February 1999" };

		var result = await Evaluate("LIC-01", options, ("LICENSE", _epplusLicense));

		result.Passed.Should().BeTrue(result.Message);
	}

	[Theory]
	[InlineData(_compound)]
	[InlineData(_lgpl)]
	public async Task LIC02_Passes_WhenTheCsprojDeclaresTheOverride(string license)
	{
		var result = await Evaluate("LIC-02", license, ("Lib/Lib.csproj", Csproj(license)));

		result.Passed.Should().BeTrue(result.Message);
	}

	[Theory]
	[InlineData(_compound)]
	[InlineData(_lgpl)]
	public async Task LIC02_Fails_ForAnMitCsprojUnderAnOverride_AndItsFixWritesTheOverride(string license)
	{
		var result = await Evaluate("LIC-02", license, ("Lib/Lib.csproj", Csproj("MIT")));

		result.Passed.Should().BeFalse();
		result.Advisory!.Data["property_value"].Should().Be(license);
	}

	[Theory]
	[InlineData(_compound, _jiraBadge)]
	[InlineData(_lgpl, _epplusBadge)]
	[InlineData(_lgpl, "[![License: LGPL-2.1-or-later](https://img.shields.io/badge/License-LGPL--2.1--or--later-blue.svg)](LICENSE)")]
	public async Task README04_Passes_ForTheRepositorysBadge(string license, string badge)
	{
		var result = await Evaluate("README-04", license, ("README.md", $"{badge}\n\n# Title\n"));

		result.Passed.Should().BeTrue(result.Message);
	}

	[Theory]
	[InlineData(_compound)]
	[InlineData(_lgpl)]
	public async Task README04_Fails_ForAnMitBadgeUnderAnOverride(string license)
	{
		var mitBadge = LicenseExpressions.BadgeLine("MIT");

		var result = await Evaluate("README-04", license, ("README.md", $"{mitBadge}\n"));

		result.Passed.Should().BeFalse();
	}

	[Fact]
	public async Task README04_UnderTheDefault_RejectsACompoundBadgeAndKeepsItsBadgeLine()
	{
		var result = await Evaluate("README-04", "MIT", ("README.md", $"{_jiraBadge}\n"));

		result.Passed.Should().BeFalse("\"License: MIT\" is only the start of that badge's label");
		result.Advisory!.Data["line_content"].Should().Be(
			"[![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](https://opensource.org/licenses/MIT)");
	}

	[Fact]
	public void BadgeLines_ForOverrides_AreValidShieldsUrls()
	{
		LicenseExpressions.BadgeLine(_compound).Should().Be(_jiraBadge);
		LicenseExpressions.BadgeLine(_lgpl).Should().Be(
			"[![License: LGPL-2.1-or-later](https://img.shields.io/badge/License-LGPL--2.1--or--later-blue.svg)](https://spdx.org/licenses/LGPL-2.1-or-later.html)");
	}

	[Fact]
	public void FileMarkers_AreDerivedPerLicence()
	{
		LicenseExpressions.FileMarkers("MIT").Should().Equal("MIT License");
		LicenseExpressions.FileMarkers(_compound).Should().Equal("MIT License", "BSD 2-Clause License");
		LicenseExpressions.FileMarkers(_lgpl).Should().Equal("GNU LESSER GENERAL PUBLIC LICENSE");
		LicenseExpressions.FileMarkers("(Apache-2.0 OR MIT)").Should().Equal("Apache License", "MIT License");
	}

	[Theory]
	[InlineData("MIT")]
	[InlineData(_compound)]
	public async Task COM02_Passes_WithALicenceNoteAfterTheStandardSentence(string license)
	{
		var result = await Evaluate("COM-02", license, ("CONTRIBUTING.md", $"{Standards.ContributingMdContent}\n{_jiraContributingNote}\n"));

		result.Passed.Should().BeTrue(result.Message);
	}

	[Fact]
	public async Task COM02_StillFails_WhenTheLicenceSectionDropsTheStandardSentenceUnderTheDefault()
	{
		var contributing = Standards.ContributingMdContent.Replace(
			"licensed under the MIT License.",
			"licensed under the GPL.",
			StringComparison.Ordinal);

		var result = await Evaluate("COM-02", "MIT", ("CONTRIBUTING.md", contributing));

		result.Passed.Should().BeFalse();
		result.Advisory!.Data["new_content"].Should().Be(Standards.ContributingMdContent);
	}

	[Fact]
	public async Task COM02_Fix_KeepsTheLicenceNoteWhenTheRestHasDrifted()
	{
		var drifted = Standards.ContributingMdContent.Replace("Use file-scoped namespaces", "Use whatever namespaces", StringComparison.Ordinal)
			+ $"\n{_jiraContributingNote}\n";

		var result = await Evaluate("COM-02", _compound, ("CONTRIBUTING.md", drifted));

		result.Passed.Should().BeFalse();
		var fixedContent = (string)result.Advisory!.Data["new_content"];
		fixedContent.Should().Contain(_jiraContributingNote).And.Contain("Use file-scoped namespaces");
		(await Evaluate("COM-02", _compound, ("CONTRIBUTING.md", fixedContent))).Passed.Should().BeTrue();
	}

	[Fact]
	public async Task COM02_UnderAnLgplOverride_TheBareMitSentenceFails_AndTheFixNamesNoOtherLicence()
	{
		var result = await Evaluate("COM-02", _lgpl, ("CONTRIBUTING.md", Standards.ContributingMdContent));

		result.Passed.Should().BeFalse();
		var fixedContent = (string)result.Advisory!.Data["new_content"];
		fixedContent.Should().NotContain("MIT License").And.Contain(_lgpl);
		(await Evaluate("COM-02", _lgpl, ("CONTRIBUTING.md", fixedContent))).Passed.Should().BeTrue();
	}

	private static string Csproj(string license)
		=> $"<Project><PropertyGroup><GeneratePackageOnBuild>true</GeneratePackageOnBuild><PackageLicenseExpression>{license}</PackageLicenseExpression></PropertyGroup></Project>";

	private static Task<RuleResult> Evaluate(string ruleId, string license, params (string Path, string Content)[] files)
		=> Evaluate(ruleId, new RepoOptions { ExpectedLicense = license }, files);

	private static async Task<RuleResult> Evaluate(string ruleId, RepoOptions options, params (string Path, string Content)[] files)
	{
		var map = files.ToDictionary(file => file.Path, file => file.Content);
		var context = new RepositoryContext
		{
			FullName = "panoramicdata/Lib",
			Name = "Lib",
			DefaultBranch = "main",
			CurrentBranch = "main",
			Options = options,
			FilePaths = [.. map.Keys],
			FileContents = map
		};

		return await RuleRegistry.Rules.Single(rule => rule.RuleId == ruleId)
			.EvaluateAsync(context, CancellationToken.None)
			.ConfigureAwait(false);
	}
}
