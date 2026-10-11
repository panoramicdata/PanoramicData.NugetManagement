using PanoramicData.NugetManagement.Models;
using PanoramicData.NugetManagement.Rules;
using PanoramicData.NugetManagement.Web.Remediations;
using PanoramicData.NugetManagement.Web.Remediations.CiWorkflow;

namespace PanoramicData.NugetManagement.Test;

/// <summary>
/// CI-16 and its Fix, end to end.
/// </summary>
public class CoverageSettingsPathAbsoluteRuleTests(ITestOutputHelper output) : TestWithOutput(output), IDisposable
{
	private const string _path = ".github/workflows/ci.yml";

	private const string _broken = "jobs:\n  c:\n    steps:\n    - name: Cover\n      run: >-\n        ./T.Test\n        --coverage\n        --coverage-settings coverage.config\n";

	private const string _dotnetTest = "jobs:\n  c:\n    steps:\n    - name: Cover\n      run: dotnet test --coverage --coverage-settings coverage.config\n";

	private readonly string _root = Directory.CreateTempSubdirectory("covpath").FullName;

	public void Dispose()
	{
		Directory.Delete(_root, recursive: true);
		GC.SuppressFinalize(this);
	}

	[Fact]
	public void RuleIdIsCi16()
		=> new CoverageSettingsPathAbsoluteRule().RuleId.Should().Be("CI-16");

	[Fact]
	public void ShouldBeRegisteredForCi16()
		=> new RemediationRegistry().Get("CI-16").Should().BeOfType<CoverageSettingsPathAbsoluteRemediation>();

	[Fact]
	public async Task ShouldBeNotApplicable_WithNoWorkflows()
	{
		var result = await EvaluateAsync(null).ConfigureAwait(true);

		result.IsApplicable.Should().BeFalse();
	}

	[Fact]
	public async Task ShouldFail_ForARelativePathPassedToTheTestHost()
	{
		var result = await EvaluateAsync(_broken).ConfigureAwait(true);

		result.Passed.Should().BeFalse();
		result.Advisory!.Data["workflow_files"].Should().BeEquivalentTo(new[] { _path });
		new RemediationRegistry().CanRemediate(result).Should().BeTrue();
	}

	[Fact]
	public async Task ShouldPass_ForADotnetTestStep()
	{
		var result = await EvaluateAsync(_dotnetTest).ConfigureAwait(true);

		result.Passed.Should().BeTrue("dotnet test resolves a relative path, so there is nothing wrong");
	}

	[Fact]
	public async Task ShouldIgnoreAWorkflowWhoseContentWasNeverFetched()
	{
		var context = Context(new Dictionary<string, string>(), [_path]);

		var result = await new CoverageSettingsPathAbsoluteRule().EvaluateAsync(context, CancellationToken.None).ConfigureAwait(true);

		result.Passed.Should().BeTrue("an unread file says nothing about its contents");
	}

	[Fact]
	public async Task Fix_ShouldMakeTheRulePass()
	{
		var failing = await EvaluateAsync(_broken).ConfigureAwait(true);
		var file = Path.Combine(_root, _path);
		Directory.CreateDirectory(Path.GetDirectoryName(file)!);
		File.WriteAllText(file, _broken);
		var applied = new List<string>();

		new CoverageSettingsPathAbsoluteRemediation().Apply(_root, failing, applied, null);

		applied.Should().ContainSingle().Which.Should().Be(_path);
		var after = await EvaluateAsync(File.ReadAllText(file)).ConfigureAwait(true);
		after.Passed.Should().BeTrue();
	}

	[Fact]
	public async Task Fix_ShouldLeaveAFileThatNoLongerNeedsIt()
	{
		var failing = await EvaluateAsync(_broken).ConfigureAwait(true);
		var file = Path.Combine(_root, _path);
		Directory.CreateDirectory(Path.GetDirectoryName(file)!);
		File.WriteAllText(file, _dotnetTest);
		var applied = new List<string>();

		new CoverageSettingsPathAbsoluteRemediation().Apply(_root, failing, applied, null);

		applied.Should().BeEmpty();
		File.ReadAllText(file).Should().Be(_dotnetTest);
	}

	[Fact]
	public async Task Fix_ShouldRefuseAPathThatLeavesTheClone()
	{
		var failing = await EvaluateAsync(_broken).ConfigureAwait(true);
		failing.Advisory!.Data["workflow_files"] = new[] { "../outside.yml" };
		var outside = Path.Combine(Path.GetDirectoryName(_root)!, "outside.yml");
		File.WriteAllText(outside, _broken);
		try
		{
			Directory.CreateDirectory(Path.Combine(_root, "clone"));
			var applied = new List<string>();

			new CoverageSettingsPathAbsoluteRemediation().Apply(Path.Combine(_root, "clone"), failing, applied, null);

			applied.Should().BeEmpty();
			File.ReadAllText(outside).Should().Be(_broken);
		}
		finally
		{
			File.Delete(outside);
		}
	}

	private static async Task<RuleResult> EvaluateAsync(string? workflow)
		=> await new CoverageSettingsPathAbsoluteRule()
			.EvaluateAsync(
				workflow is null
					? Context([], [])
					: Context(new Dictionary<string, string> { [_path] = workflow }, [_path]),
				CancellationToken.None)
			.ConfigureAwait(false);

	private static RepositoryContext Context(Dictionary<string, string> contents, List<string> paths) => new()
	{
		FullName = "test-org/Acme",
		Name = "Acme",
		DefaultBranch = "main",
		CurrentBranch = "main",
		Options = new RepoOptions(),
		FilePaths = paths,
		FileContents = contents
	};
}
