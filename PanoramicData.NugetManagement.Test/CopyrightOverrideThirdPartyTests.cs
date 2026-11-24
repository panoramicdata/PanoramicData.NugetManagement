using System.Text;
using PanoramicData.NugetManagement.Models;
using PanoramicData.NugetManagement.Rules;
using PanoramicData.NugetManagement.Web.Remediations;

namespace PanoramicData.NugetManagement.Test;

/// <summary>
/// LIC-05 flags and fixes only a Copyright naming our own holder; third-party attribution is never
/// failed and never edited.
/// </summary>
public class CopyrightOverrideThirdPartyTests(ITestOutputHelper output) : TestWithOutput(output)
{
	public static TheoryData<string> ThirdPartyValues =>
	[
		"Copyright © 2013 Federico Silva Armas; Copyright © 2025-$([System.DateTime]::Now.Year) Panoramic Data Limited",
		"Copyright © 2016 force; Copyright © 2025-$([System.DateTime]::Now.Year) Panoramic Data Limited",
		"Copyright © 2009-2020 Jan Källman, Mats Alm and EPPlus contributors; Rimland 2020; Panoramic Data Limited 2024-2026",
		"Copyright © Acme Corp",
		"Panoramic Data Limited and Acme Corp 2025",
		"Panoramic Data Limited 2025; Panoramic Data Limited 2026",
		"$(CopyrightText) Panoramic Data Limited",
		"Panoramic Data Ltd 2025",
		"2025"
	];

	public static TheoryData<string> OwnValues =>
	[
		"Panoramic Data Limited 2025",
		"Copyright © Panoramic Data Limited 2021-2026",
		"© 2025 Panoramic Data Limited",
		"Copyright (c) 2025-$([System.DateTime]::Now.Year) Panoramic Data Limited."
	];

	[Theory]
	[MemberData(nameof(ThirdPartyValues))]
	public async Task Rule_ShouldPass_AndNameTheFile_WhenCopyrightIsThirdParty(string value)
	{
		var result = await Evaluate(Project(value));

		result.Passed.Should().BeTrue(result.Message);
		result.Message.Should().Contain("intentionally left alone").And.Contain("Acme.Lib.csproj");
	}

	[Theory]
	[MemberData(nameof(OwnValues))]
	public async Task Rule_ShouldFail_WhenCopyrightIsOnlyOurs(string value)
	{
		var result = await Evaluate(Project(value));

		result.Passed.Should().BeFalse();
		result.Advisory!.Detail.Should().Contain("intentionally left alone");
		(result.Advisory.Data["projects"] as string[]).Should().Equal("Acme.Lib.csproj");
	}

	[Fact]
	public async Task Rule_ShouldFailOnlyTheOwnProject_WhenMixed()
	{
		var files = new Dictionary<string, string>
		{
			["Own/Own.csproj"] = Project("Panoramic Data Limited 2025"),
			["Their/Their.csproj"] = Project("Copyright © 2013 Federico Silva Armas; Copyright © 2025 Panoramic Data Limited")
		};

		var result = await Evaluate(files);

		result.Passed.Should().BeFalse();
		(result.Advisory!.Data["projects"] as string[]).Should().Equal("Own/Own.csproj");
		result.Message.Should().Contain("Their/Their.csproj");
	}

	[Fact]
	public async Task Rule_UsesTheConfiguredHolder()
	{
		var files = new Dictionary<string, string> { ["Acme.Lib.csproj"] = Project("Acme Corp 2025") };

		var result = await Evaluate(files, new RepoOptions { ExpectedCopyrightHolder = "Acme Corp" });

		result.Passed.Should().BeFalse();
	}

	[Theory]
	[MemberData(nameof(ThirdPartyValues))]
	public async Task Remediation_NeverEdits_ThirdPartyAttribution_EvenWhenInvokedDirectly(string value)
	{
		var root = NewRoot();
		try
		{
			var path = Path.Combine(root, "Acme.Lib.csproj");
			var bytes = new UTF8Encoding(true).GetPreamble().Concat(Encoding.UTF8.GetBytes(Project(value))).ToArray();
			File.WriteAllBytes(path, bytes);

			var failed = await Evaluate(Project("Panoramic Data Limited 2025"));
			failed.Advisory!.Data["projects"] = new[] { "Acme.Lib.csproj" };

			var applied = new List<string>();
			new RemediationRegistry().Get("LIC-05")!.Apply(root, failed, applied, null);

			applied.Should().BeEmpty();
			File.ReadAllBytes(path).Should().Equal(bytes);
		}
		finally
		{
			Directory.Delete(root, recursive: true);
		}
	}

	[Theory]
	[MemberData(nameof(OwnValues))]
	public async Task Remediation_RemovesOwnNoticeOnly(string value)
	{
		var root = NewRoot();
		try
		{
			var path = Path.Combine(root, "Acme.Lib.csproj");
			File.WriteAllBytes(path, Encoding.UTF8.GetBytes(Project(value)));

			var failed = await Evaluate(Project(value));
			var applied = new List<string>();
			new RemediationRegistry().Get("LIC-05")!.Apply(root, failed, applied, null);

			applied.Should().Equal("Acme.Lib.csproj");
			File.ReadAllText(path).Should().NotContain("<Copyright>");
		}
		finally
		{
			Directory.Delete(root, recursive: true);
		}
	}

	[Fact]
	public async Task Remediation_FixesOwnProject_AndSkipsTheThirdPartyOneInTheSameRun()
	{
		var root = NewRoot();
		try
		{
			var theirs = Project("Copyright © 2016 force; Copyright © 2025 Panoramic Data Limited");
			File.WriteAllText(Path.Combine(root, "Own.csproj"), Project("Panoramic Data Limited 2025"));
			File.WriteAllText(Path.Combine(root, "Their.csproj"), theirs);

			var failed = await Evaluate(Project("Panoramic Data Limited 2025"));
			failed.Advisory!.Data["projects"] = new[] { "Own.csproj", "Their.csproj" };

			var applied = new List<string>();
			new RemediationRegistry().Get("LIC-05")!.Apply(root, failed, applied, null);

			applied.Should().Equal("Own.csproj");
			File.ReadAllText(Path.Combine(root, "Their.csproj")).Should().Be(theirs);
		}
		finally
		{
			Directory.Delete(root, recursive: true);
		}
	}

	private static string Project(string copyright)
		=> $"<Project Sdk=\"Microsoft.NET.Sdk\">\r\n\t<PropertyGroup>\r\n\t\t<TargetFramework>net10.0</TargetFramework>\r\n\t\t<Copyright>{copyright}</Copyright>\r\n\t</PropertyGroup>\r\n</Project>\r\n";

	private static string NewRoot()
	{
		var root = Path.Combine(Path.GetTempPath(), "lic05tp-" + Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(root);
		return root;
	}

	private static Task<RuleResult> Evaluate(string csproj)
		=> Evaluate(new Dictionary<string, string> { ["Acme.Lib.csproj"] = csproj });

	private static async Task<RuleResult> Evaluate(Dictionary<string, string> files, RepoOptions? options = null)
	{
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

		return await new CopyrightOverrideRule().EvaluateAsync(context, CancellationToken.None).ConfigureAwait(false);
	}
}
