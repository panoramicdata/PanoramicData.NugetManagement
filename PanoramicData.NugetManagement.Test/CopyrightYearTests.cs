using System.Text;
using Microsoft.Extensions.Time.Testing;
using PanoramicData.NugetManagement.Models;
using PanoramicData.NugetManagement.Rules;
using PanoramicData.NugetManagement.Services;
using PanoramicData.NugetManagement.Web.Remediations;

namespace PanoramicData.NugetManagement.Test;

/// <summary>
/// Tests LIC-04: copyright notices naming the holder carry the current year, and the fix that updates them.
/// </summary>
public class CopyrightYearTests(ITestOutputHelper output) : TestWithOutput(output)
{
	private const string _holder = "Panoramic Data Limited";
	private const int _year = 2026;

	[Theory]
	[InlineData("Copyright © 2025 Panoramic Data Limited. All rights reserved.", "Copyright © 2026 Panoramic Data Limited. All rights reserved.")]
	[InlineData("Copyright (c) 2025 Panoramic Data Limited", "Copyright (c) 2026 Panoramic Data Limited")]
	[InlineData("Copyright 2025 Panoramic Data Limited", "Copyright 2026 Panoramic Data Limited")]
	[InlineData("©2025 Panoramic Data Limited", "©2026 Panoramic Data Limited")]
	[InlineData("Copyright © 2020-2025 Panoramic Data Limited", "Copyright © 2020-2026 Panoramic Data Limited")]
	[InlineData("Copyright © 2020 - 2025 Panoramic Data Limited", "Copyright © 2020 - 2026 Panoramic Data Limited")]
	[InlineData("Copyright © 2020–2025 Panoramic Data Limited", "Copyright © 2020–2026 Panoramic Data Limited")]
	[InlineData("copyright © 2025 panoramic data limited", "copyright © 2026 panoramic data limited")]
	[InlineData("<Copyright>Copyright © 2025 Panoramic Data Limited</Copyright>", "<Copyright>Copyright © 2026 Panoramic Data Limited</Copyright>")]
	[InlineData("Copyright © 2027 Panoramic Data Limited", "Copyright © 2026 Panoramic Data Limited")]
	[InlineData("Copyright © 2027-2028 Panoramic Data Limited", "Copyright © 2026 Panoramic Data Limited")]
	public void Correct_SetsTheCurrentYear(string line, string expected)
		=> CopyrightNotices.Correct(line, _holder, _year).Should().Be(expected);

	[Theory]
	[InlineData("Copyright © 2026 Panoramic Data Limited")]
	[InlineData("Copyright © 2020-2026 Panoramic Data Limited")]
	[InlineData("Copyright © $([System.DateTime]::Now.Year) Panoramic Data Limited")]
	[InlineData("Copyright (c) Panoramic Data Limited")]
	[InlineData("<Copyright>Copyright © $(Year) Panoramic Data Limited</Copyright>")]
	[InlineData("Copyright © 2025 Somebody Else Ltd")]
	[InlineData("Portions Copyright 2015 Somebody Else, used under licence from Panoramic Data Limited")]
	[InlineData("Released in 2025 by Panoramic Data Limited")]
	[InlineData("THE AUTHORS OR COPYRIGHT HOLDERS BE LIABLE")]
	public void Correct_LeavesAlone(string line)
		=> CopyrightNotices.Correct(line, _holder, _year).Should().Be(line);

	[Fact]
	public void Correct_SkipsFencedCodeAndKeepsLineEndings()
	{
		var text = "# T\r\n\r\n```\r\nCopyright © 2020 Panoramic Data Limited\r\n```\r\n\r\nCopyright © 2025 Panoramic Data Limited\r\n";

		CopyrightNotices.Correct(text, _holder, _year).Should().Be(
			"# T\r\n\r\n```\r\nCopyright © 2020 Panoramic Data Limited\r\n```\r\n\r\nCopyright © 2026 Panoramic Data Limited\r\n");
	}

	[Fact]
	public void Find_ReportsLineNumbersAndStaleness()
	{
		var notices = CopyrightNotices.Find("a\nCopyright © 2025 Panoramic Data Limited\nCopyright © 2026 Panoramic Data Limited\n", _holder, _year);

		notices.Select(n => (n.Line, n.IsStale)).Should().Equal((2, true), (3, false));
	}

	[Fact]
	public async Task Rule_ShouldFail_WhenReadmeNoticeIsLastYears()
	{
		var result = await Evaluate(new() { ["README.md"] = "# Acme.Lib\n\nCopyright © 2025 Panoramic Data Limited. All rights reserved.\n" });

		result.Passed.Should().BeFalse();
		result.Message.Should().Contain("README.md:3");
		result.Advisory!.Data["year"].Should().Be("2026");
		result.Advisory.Data["remediation_type"].Should().Be("update_copyright_year");
		(result.Advisory.Data["files"] as string[]).Should().Equal("README.md");
	}

	[Fact]
	public async Task Rule_ShouldFail_ListingEveryFileWithAStaleNotice()
	{
		var result = await Evaluate(new()
		{
			["README.md"] = "Copyright © 2025 Panoramic Data Limited\n",
			["LICENSE"] = "MIT License\n\nCopyright (c) 2024 Panoramic Data Limited\n",
			["Directory.Build.props"] = "<Project><PropertyGroup><Copyright>Copyright © $([System.DateTime]::Now.Year) Panoramic Data Limited</Copyright></PropertyGroup></Project>"
		});

		result.Passed.Should().BeFalse();
		(result.Advisory!.Data["files"] as string[]).Should().Equal("README.md", "LICENSE");
	}

	[Fact]
	public async Task Rule_ShouldPass_WhenEveryNoticeIsCurrent()
	{
		var result = await Evaluate(new()
		{
			["README.md"] = "Copyright © 2026 Panoramic Data Limited\n",
			["LICENSE"] = "Copyright (c) 2020-2026 Panoramic Data Limited\n"
		});

		result.Passed.Should().BeTrue(result.Message);
		result.IsApplicable.Should().BeTrue();
	}

	[Fact]
	public async Task Rule_ShouldBeNotApplicable_WhenNoNoticeCarriesAYear()
	{
		var result = await Evaluate(new() { ["LICENSE"] = "MIT License\n\nCopyright (c) Panoramic Data Limited\n" });

		result.IsApplicable.Should().BeFalse();
	}

	[Fact]
	public async Task Rule_ShouldIgnoreOtherHolders()
	{
		var result = await Evaluate(new() { ["README.md"] = "Copyright © 2019 Somebody Else Ltd\n" });

		result.Passed.Should().BeTrue();
		result.IsApplicable.Should().BeFalse();
	}

	[Fact]
	public async Task Rule_ShouldUseConfiguredHolder()
	{
		var result = await Evaluate(
			new() { ["README.md"] = "Copyright © 2025 Acme Corp\n" },
			new RepoOptions { ExpectedCopyrightHolder = "Acme Corp" });

		result.Passed.Should().BeFalse();
	}

	[Fact]
	public async Task Rule_ShouldFollowTheClock()
	{
		var files = new Dictionary<string, string> { ["README.md"] = "Copyright © 2026 Panoramic Data Limited\n" };

		(await Evaluate(files, clock: new FakeTimeProvider(new DateTimeOffset(2026, 12, 31, 23, 0, 0, TimeSpan.Zero)))).Passed.Should().BeTrue();
		(await Evaluate(files, clock: new FakeTimeProvider(new DateTimeOffset(2027, 1, 1, 0, 0, 0, TimeSpan.Zero)))).Passed.Should().BeFalse();
	}

	[Fact]
	public void Rule_IsRegisteredInLicensing()
	{
		var rule = RuleRegistry.Rules.Single(r => r.RuleId == "LIC-04");

		rule.Category.Should().Be(AssessmentCategory.Licensing);
		rule.Severity.Should().Be(AssessmentSeverity.Warning);
	}

	[Fact]
	public async Task Remediation_UpdatesEveryFile_ThenTheRulePasses()
	{
		var root = NewRoot();
		try
		{
			File.WriteAllText(Path.Combine(root, "README.md"), "# Acme.Lib\r\n\r\nCopyright © 2025 Panoramic Data Limited. All rights reserved.\r\n");
			File.WriteAllText(Path.Combine(root, "LICENSE"), "MIT License\n\nCopyright (c) 2020-2025 Panoramic Data Limited\n");

			var failed = await Evaluate(ReadAll(root));
			var registry = new RemediationRegistry();
			registry.CanRemediate(failed).Should().BeTrue();

			var applied = new List<string>();
			registry.Get("LIC-04")!.Apply(root, failed, applied, null);

			applied.Should().BeEquivalentTo(["README.md", "LICENSE"]);
			File.ReadAllText(Path.Combine(root, "README.md")).Should().Be("# Acme.Lib\r\n\r\nCopyright © 2026 Panoramic Data Limited. All rights reserved.\r\n");
			File.ReadAllText(Path.Combine(root, "LICENSE")).Should().Be("MIT License\n\nCopyright (c) 2020-2026 Panoramic Data Limited\n");
			(await Evaluate(ReadAll(root))).Passed.Should().BeTrue();
		}
		finally
		{
			Directory.Delete(root, recursive: true);
		}
	}

	[Fact]
	public async Task Remediation_KeepsBomAndNonUtf8Bytes()
	{
		var root = NewRoot();
		try
		{
			var bomPath = Path.Combine(root, "README.md");
			File.WriteAllBytes(bomPath, [.. new UTF8Encoding(true).GetPreamble(), .. Encoding.UTF8.GetBytes("Copyright © 2025 Panoramic Data Limited\n")]);
			var latin1Path = Path.Combine(root, "LICENSE");
			File.WriteAllBytes(latin1Path, Encoding.Latin1.GetBytes("Copyright © 2025 Panoramic Data Limited, café\n"));

			var failed = await Evaluate(new()
			{
				["README.md"] = "Copyright © 2025 Panoramic Data Limited\n",
				["LICENSE"] = "Copyright © 2025 Panoramic Data Limited\n"
			});
			new RemediationRegistry().Get("LIC-04")!.Apply(root, failed, [], null);

			File.ReadAllBytes(bomPath).Should().Equal([.. new UTF8Encoding(true).GetPreamble(), .. Encoding.UTF8.GetBytes("Copyright © 2026 Panoramic Data Limited\n")]);
			File.ReadAllBytes(latin1Path).Should().Equal(Encoding.Latin1.GetBytes("Copyright © 2026 Panoramic Data Limited, café\n"));
		}
		finally
		{
			Directory.Delete(root, recursive: true);
		}
	}

	[Fact]
	public async Task Remediation_RefusesAPathOutsideTheRepository()
	{
		var root = NewRoot();
		var outside = Path.Combine(Path.GetTempPath(), "lic04-outside-" + Guid.NewGuid().ToString("N") + ".md");
		try
		{
			File.WriteAllText(outside, "Copyright © 2025 Panoramic Data Limited\n");
			var failed = await Evaluate(new() { ["README.md"] = "Copyright © 2025 Panoramic Data Limited\n" });
			failed.Advisory!.Data["files"] = new[] { Path.Combine("..", Path.GetFileName(outside)) };

			var applied = new List<string>();
			new RemediationRegistry().Get("LIC-04")!.Apply(root, failed, applied, null);

			applied.Should().BeEmpty();
			File.ReadAllText(outside).Should().Contain("2025");
		}
		finally
		{
			Directory.Delete(root, recursive: true);
			File.Delete(outside);
		}
	}

	[Fact]
	public void Remediation_CannotRemediate_WithoutTheDataItReads()
	{
		var registry = new RemediationRegistry();
		var bare = new RuleResult
		{
			RuleId = "LIC-04",
			RuleName = "x",
			Category = AssessmentCategory.Licensing,
			Severity = AssessmentSeverity.Warning,
			Passed = false,
			Message = "x",
			Advisory = new RuleAdvisory { Summary = "x", Detail = "x", Data = new() { ["remediation_type"] = "update_copyright_year" } }
		};

		registry.CanRemediate(bare).Should().BeFalse();
	}

	private static string NewRoot()
	{
		var root = Path.Combine(Path.GetTempPath(), "lic04-" + Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(root);
		return root;
	}

	private static Dictionary<string, string> ReadAll(string root)
		=> Directory.GetFiles(root).ToDictionary(path => Path.GetFileName(path), path => File.ReadAllText(path));

	private static async Task<RuleResult> Evaluate(
		Dictionary<string, string> files,
		RepoOptions? options = null,
		TimeProvider? clock = null)
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

		clock ??= new FakeTimeProvider(new DateTimeOffset(_year, 6, 1, 12, 0, 0, TimeSpan.Zero));

		return await new CopyrightYearRule(clock).EvaluateAsync(context, CancellationToken.None).ConfigureAwait(false);
	}
}
