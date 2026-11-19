using PanoramicData.NugetManagement.Models;
using PanoramicData.NugetManagement.Rules;

namespace PanoramicData.NugetManagement.Test;

public class RepositoryUrlSetRulePrivacyTests
{
	private const string _csproj = "Src/Widget.csproj";
	private const string _unset = "<Project><PropertyGroup><GeneratePackageOnBuild>true</GeneratePackageOnBuild></PropertyGroup></Project>";
	private const string _set = "<Project><PropertyGroup><GeneratePackageOnBuild>true</GeneratePackageOnBuild><RepositoryUrl>https://github.com/org/repo</RepositoryUrl></PropertyGroup></Project>";

	[Fact]
	public async Task PrivateRepository_WithoutRepositoryUrl_IsNotApplicableAndOffersNoRemediation()
	{
		var result = await Evaluate(isPrivate: true, _unset);

		result.Passed.Should().BeTrue();
		result.IsApplicable.Should().BeFalse();
		result.Advisory.Should().BeNull("a passing result gives the remediation nothing to add");
	}

	[Fact]
	public async Task PrivateRepository_WithRepositoryUrl_IsNotApplicable()
		=> (await Evaluate(isPrivate: true, _set)).IsApplicable.Should().BeFalse();

	[Fact]
	public async Task PublicRepository_WithoutRepositoryUrl_StillFailsWithRemediationData()
	{
		var result = await Evaluate(isPrivate: false, _unset);

		result.Passed.Should().BeFalse();
		result.Advisory!.Data["property_name"].Should().Be("RepositoryUrl");
		result.Advisory.Data["property_value"].Should().Be("https://github.com/test-org/Acme.Widget");
	}

	[Fact]
	public async Task UnknownPrivacy_WithoutRepositoryUrl_StillFails()
		=> (await Evaluate(isPrivate: null, _unset)).Passed.Should().BeFalse();

	[Fact]
	public async Task PublicRepository_WithRepositoryUrl_Passes()
	{
		var result = await Evaluate(isPrivate: false, _set);

		result.Passed.Should().BeTrue();
		result.IsApplicable.Should().BeTrue();
	}

	private static Task<RuleResult> Evaluate(bool? isPrivate, string csproj)
		=> new RepositoryUrlSetRule().EvaluateAsync(
			new RepositoryContext
			{
				FullName = "test-org/Acme.Widget",
				Name = "Acme.Widget",
				DefaultBranch = "main",
				Options = new RepoOptions(),
				IsPrivateRepository = isPrivate,
				FilePaths = [_csproj],
				FileContents = new() { [_csproj] = csproj }
			},
			CancellationToken.None);
}
