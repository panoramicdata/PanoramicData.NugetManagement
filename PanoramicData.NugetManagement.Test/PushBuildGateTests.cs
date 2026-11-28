using PanoramicData.NugetManagement.Web.Services;

namespace PanoramicData.NugetManagement.Test;

/// <summary>
/// A push the repository does not build with must be refused, wherever it comes from.
/// </summary>
public class PushBuildGateTests
{
	[Fact]
	public async Task RefusalAsync_ReturnsNull_WhenTheRepositoryBuilds()
	{
		var refusal = await PushBuildGate.RefusalAsync("panoramicdata/Lifx.Api", () => Task.FromResult((true, "Build succeeded.")));

		refusal.Should().BeNull();
	}

	[Fact]
	public async Task RefusalAsync_NamesTheRepositoryAndTheFirstErrors_WhenTheBuildFails()
	{
		const string output = """
			Lifx.Api.Test\Lan\LanProtocolTestCollection.cs(11,14): error xUnit1073: Collection definition 'LAN Protocol Tests' is never used. [Lifx.Api.Test.csproj]
			Lifx.Api.Test\Lan\LanProtocolTestCollection.cs(11,14): error xUnit1073: Collection definition 'LAN Protocol Tests' is never used. [Lifx.Api.Test.csproj]
			    1 Error(s)
			""";

		var refusal = await PushBuildGate.RefusalAsync("panoramicdata/Lifx.Api", () => Task.FromResult((false, output)));

		refusal.Should().NotBeNull();
		refusal.Should().Contain("panoramicdata/Lifx.Api");
		refusal.Should().Contain("xUnit1073");
		refusal.Should().Contain("does not build");
		refusal!.Split("xUnit1073").Length.Should().Be(2, "the same error reported twice is shown once");
	}

	[Fact]
	public async Task RefusalAsync_StillRefuses_WhenTheOutputHasNoErrorLines()
	{
		var refusal = await PushBuildGate.RefusalAsync("panoramicdata/Lifx.Api", () => Task.FromResult((false, string.Empty)));

		refusal.Should().NotBeNull();
		refusal.Should().Contain("does not build");
	}

	[Fact]
	public async Task RefusalAsync_NeverRunsMoreThanTheCapOfBuildsAtOnce()
	{
		var running = 0;
		var peak = 0;

		async Task<(bool, string)> Build()
		{
			var now = Interlocked.Increment(ref running);
			InterlockedMax(ref peak, now);
			await Task.Delay(50).ConfigureAwait(false);
			Interlocked.Decrement(ref running);
			return (true, string.Empty);
		}

		await Task.WhenAll(Enumerable.Range(0, 20).Select(i => PushBuildGate.RefusalAsync($"panoramicdata/Repo{i}", Build)));

		peak.Should().BeLessThanOrEqualTo(PushBuildGate.MaxConcurrentBuilds);
		peak.Should().BeGreaterThan(1, "the cap limits builds, it must not serialise them");
	}

	private static void InterlockedMax(ref int target, int value)
	{
		int seen;
		do
		{
			seen = Volatile.Read(ref target);
		}
		while (value > seen && Interlocked.CompareExchange(ref target, value, seen) != seen);
	}

	[Fact]
	public async Task RefusalAsync_ReportsProgress()
	{
		var lines = new List<string>();

		await PushBuildGate.RefusalAsync("panoramicdata/Lifx.Api", () => Task.FromResult((false, "x error CS0001: boom")), lines.Add);

		lines.Should().HaveCount(2);
		lines[0].Should().Contain("Verifying");
		lines[1].Should().Contain("Not pushed");
	}

	/// <summary>
	/// Branch protection rejects a push to main, so no git command in the app may attempt one: a bare
	/// push sends the checked-out branch, which for a governance clone is main.
	/// </summary>
	[Theory]
	[InlineData("\"push\", \"origin\", \"main\"")]
	[InlineData("\"push\", \"-u\", \"origin\", \"main\"")]
	[InlineData("[\"push\"]")]
	public void NoGitCommandPushesMain(string arguments)
	{
		var offenders = WebSources()
			.Where(entry => entry.Value.Contains(arguments, StringComparison.Ordinal))
			.Select(entry => Path.GetFileName(entry.Key));

		offenders.Should().BeEmpty("main changes only through a pull request");
	}

	/// <summary>
	/// The gate protects nothing if one delivery path can skip it. Every governance branch is made by
	/// DashboardService.CommitAndPushAsync, which builds before committing, and none may opt out.
	/// </summary>
	[Fact]
	public void EveryPullRequestBranchIsBuiltFirst()
	{
		var sources = WebSources();

		sources.Values.Sum(text => CountOf(text, "_localRepo.CommitToPullRequestBranchAsync("))
			.Should().Be(1, "a second place that delivers would not be built first");

		var dashboard = sources.Single(entry => entry.Key.EndsWith("DashboardService.Git.cs", StringComparison.Ordinal)).Value;
		var start = dashboard.IndexOf("public async Task<CommitAndPushOutcome> CommitAndPushAsync(", StringComparison.Ordinal);
		start.Should().BeGreaterThan(0);
		var end = dashboard.IndexOf("\n\t}", start, StringComparison.Ordinal);

		var gate = dashboard.IndexOf("VerifyChangesBuildAsync(", start, StringComparison.Ordinal);
		var delivery = dashboard.IndexOf("_localRepo.CommitToPullRequestBranchAsync(", start, StringComparison.Ordinal);
		gate.Should().BeGreaterThan(0, "the delivery path must build first");
		gate.Should().BeLessThan(delivery, "the build must come before the branch is made");
		delivery.Should().BeLessThan(end, "the delivery must be inside the wrapper that builds");

		sources.Values.Sum(text => CountOf(text, "verifyBuildFirst: false"))
			.Should().Be(0, "no delivery path may opt out of the build");
	}

	private static Dictionary<string, string> WebSources()
		=> Directory
			.EnumerateFiles(FindWebProject(), "*.cs", SearchOption.AllDirectories)
			.Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
				&& !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))
			.ToDictionary(path => path, File.ReadAllText);

	private static int CountOf(string text, string value)
	{
		var count = 0;
		var index = 0;
		while ((index = text.IndexOf(value, index, StringComparison.Ordinal)) >= 0)
		{
			count++;
			index += value.Length;
		}

		return count;
	}

	private static string FindWebProject()
	{
		var directory = new DirectoryInfo(AppContext.BaseDirectory);
		while (directory is not null)
		{
			var candidate = Path.Combine(directory.FullName, "PanoramicData.NugetManagement.Web");
			if (Directory.Exists(candidate) && File.Exists(Path.Combine(candidate, "PanoramicData.NugetManagement.Web.csproj")))
			{
				return candidate;
			}

			directory = directory.Parent;
		}

		throw new InvalidOperationException("Could not find PanoramicData.NugetManagement.Web above " + AppContext.BaseDirectory);
	}
}
