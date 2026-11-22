using PanoramicData.NugetManagement.Models;
using PanoramicData.NugetManagement.Rules;

namespace PanoramicData.NugetManagement.Test;

public class CodacyBadgeRulePrivacyTests
{
	private const string _withoutBadge = "# Acme.Widget\n\nA widget.";
	private const string _withBadge = "[![Codacy Badge](https://app.codacy.com/project/badge/Grade/896c2da514fe4bcfbf271f54cbec908b)](https://app.codacy.com/gh/test-org/Acme.Widget/dashboard)\n# Acme.Widget";

	[Fact]
	public async Task PrivateRepository_WithoutBadge_IsNotApplicableAndOffersNoRemediation()
	{
		var result = await Evaluate(isPrivate: true, _withoutBadge);

		result.Passed.Should().BeTrue();
		result.IsApplicable.Should().BeFalse();
		result.Advisory.Should().BeNull("a passing result gives the remediation nothing to add");
	}

	[Fact]
	public async Task PrivateRepository_WithBadge_IsNotApplicable()
		=> (await Evaluate(isPrivate: true, _withBadge)).IsApplicable.Should().BeFalse();

	[Fact]
	public async Task PublicRepository_WithoutBadge_StillFails()
	{
		var result = await Evaluate(isPrivate: false, _withoutBadge);

		result.Passed.Should().BeFalse();
		result.Advisory!.Data.Should().NotContainKey("remediation_type", "without a Codacy token the real badge id is unknown, so no badge can be written");
	}

	[Fact]
	public async Task UnknownPrivacy_WithoutBadge_StillFails()
		=> (await Evaluate(isPrivate: null, _withoutBadge)).Passed.Should().BeFalse();

	[Fact]
	public async Task PublicRepository_WithBadge_Passes()
	{
		var result = await Evaluate(isPrivate: false, _withBadge);

		result.Passed.Should().BeTrue();
		result.IsApplicable.Should().BeTrue();
	}

	private static Task<RuleResult> Evaluate(bool? isPrivate, string readme)
		=> new CodacyBadgeRule().EvaluateAsync(
			new RepositoryContext
			{
				FullName = "test-org/Acme.Widget",
				Name = "Acme.Widget",
				DefaultBranch = "main",
				Options = new RepoOptions(),
				IsPrivateRepository = isPrivate,
				FilePaths = ["README.md"],
				FileContents = new() { ["README.md"] = readme }
			},
			CancellationToken.None);
}
