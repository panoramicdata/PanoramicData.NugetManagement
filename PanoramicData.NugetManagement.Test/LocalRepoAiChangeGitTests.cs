using PanoramicData.NugetManagement.Web.Services;
using PanoramicData.NugetManagement.Web.Services.AiChange;

namespace PanoramicData.NugetManagement.Test;

/// <summary>
/// Tests for <see cref="LocalRepoAiChangeGit"/> against a real clone of a real (local, bare) remote.
/// </summary>
public sealed class LocalRepoAiChangeGitTests : TestWithOutput, IDisposable
{
	private readonly string _root = Path.Combine(Path.GetTempPath(), "aichange-" + Guid.NewGuid().ToString("N"));
	private readonly string _clone;

	public LocalRepoAiChangeGitTests(ITestOutputHelper output)
		: base(output)
	{
		var remote = Path.Combine(_root, "remote.git");
		var seed = Path.Combine(_root, "seed");
		_clone = Path.Combine(_root, "clone");
		Directory.CreateDirectory(_root);

		Git(_root, "init", "--bare", "-b", "main", remote);
		Git(_root, "-c", "core.autocrlf=false", "clone", remote, seed);
		Configure(seed);
		File.WriteAllText(Path.Combine(seed, "a.cs"), "one\n");
		File.WriteAllText(Path.Combine(seed, "b.cs"), "keep\n");
		Git(seed, "add", "-A");
		Git(seed, "commit", "-m", "init");
		Git(seed, "push", "origin", "main");
		Git(_root, "-c", "core.autocrlf=false", "clone", remote, _clone);
		Configure(_clone);
	}

	public void Dispose()
	{
		foreach (var file in Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories))
		{
			File.SetAttributes(file, FileAttributes.Normal);
		}

		Directory.Delete(_root, recursive: true);
	}

	private static void Configure(string directory)
	{
		Git(directory, "config", "user.email", "t@t");
		Git(directory, "config", "user.name", "t");
		Git(directory, "config", "core.autocrlf", "false");
	}

	private static void Git(string directory, params string[] arguments)
	{
		var (ok, text) = LocalRepoService.RunCommandWithStreamingAsync(directory, "git", arguments).GetAwaiter().GetResult();
		ok.Should().BeTrue($"git {string.Join(' ', arguments)} should work: {text}");
	}

	private LocalRepoAiChangeGit Adapter() => new(_clone);

	private LocalRepoAiChangeGit AdapterExpecting(string identity) => new(_clone, identity);

	private void PointOriginAt(string url) => Git(_clone, "remote", "set-url", "origin", url);

	[Fact]
	public async Task DeliverRefusal_RefusesACloneThatHoldsADifferentRepository()
	{
		PointOriginAt("https://github.com/o/r.git");

		(await AdapterExpecting("x/y").DeliverRefusalAsync(new HashSet<string>(), TestContext.Current.CancellationToken))
			.Should().Contain("o/r").And.Contain("x/y",
				"a pull request opened and merged on evidence from the wrong repository's clone is the worst outcome here");
	}

	[Fact]
	public async Task DeliverRefusal_AcceptsTheRightRepository_IgnoringCase()
	{
		PointOriginAt("https://github.com/o/r.git");

		(await AdapterExpecting("O/R").DeliverRefusalAsync(new HashSet<string>(), TestContext.Current.CancellationToken))
			.Should().BeNull();
	}

	[Fact]
	public async Task Refusal_RefusesACloneThatHoldsADifferentRepository()
	{
		PointOriginAt("https://github.com/o/r.git");

		(await AdapterExpecting("x/y").RefusalAsync(TestContext.Current.CancellationToken)).Should().Contain("o/r");
	}

	[Fact]
	public async Task DeliverRefusal_RefusesAnOriginThatIsNotGitHub()
	{
		PointOriginAt("https://example.com/o/r.git");

		(await AdapterExpecting("o/r").DeliverRefusalAsync(new HashSet<string>(), TestContext.Current.CancellationToken))
			.Should().Contain("not a GitHub");
	}

	[Fact]
	public async Task Refusal_IsNullForACleanCloneOnMain()
		=> (await Adapter().RefusalAsync(TestContext.Current.CancellationToken)).Should().BeNull();

	[Fact]
	public async Task Refusal_NamesADirtyClone()
	{
		File.WriteAllText(Path.Combine(_clone, "a.cs"), "changed\n");

		(await Adapter().RefusalAsync(TestContext.Current.CancellationToken)).Should().Contain("uncommitted");
	}

	[Fact]
	public async Task DeliverRefusal_IsNullWhenEveryChangeIsOneTheCallerMade()
	{
		File.WriteAllText(Path.Combine(_clone, "a.cs"), "changed\n");

		(await Adapter().DeliverRefusalAsync(new HashSet<string> { "a.cs" }, TestContext.Current.CancellationToken))
			.Should().BeNull();
	}

	[Fact]
	public async Task DeliverRefusal_NamesAChangeNobodyOwns()
	{
		File.WriteAllText(Path.Combine(_clone, "a.cs"), "changed\n");
		File.WriteAllText(Path.Combine(_clone, "notes.txt"), "mine\n");

		(await Adapter().DeliverRefusalAsync(new HashSet<string> { "a.cs" }, TestContext.Current.CancellationToken))
			.Should().Contain("notes.txt");
	}

	[Fact]
	public async Task DeliverRefusal_SeesFilesInsideAnUntrackedFolder()
	{
		Directory.CreateDirectory(Path.Combine(_clone, "docs"));
		File.WriteAllText(Path.Combine(_clone, "docs", "secret.md"), "mine\n");

		(await Adapter().DeliverRefusalAsync(new HashSet<string>(), TestContext.Current.CancellationToken))
			.Should().Contain("docs/secret.md", "an untracked folder is listed file by file, so one stray file cannot hide behind its owned neighbours");
	}

	[Fact]
	public async Task DeliverRefusal_NamesABranchThatIsNotMain()
	{
		await Adapter().CreateBranchAsync("ai/x", TestContext.Current.CancellationToken);

		(await Adapter().DeliverRefusalAsync(new HashSet<string>(), TestContext.Current.CancellationToken))
			.Should().Contain("main");
	}

	[Fact]
	public async Task ChangedFilesAsync_SeesCommittedAndUncommittedChangesAgainstMain()
	{
		var git = Adapter();
		await git.CreateBranchAsync("ai/x", TestContext.Current.CancellationToken);
		File.WriteAllText(Path.Combine(_clone, "a.cs"), "one\ntwo\n");

		var changed = await git.ChangedFilesAsync(TestContext.Current.CancellationToken);

		changed.Should().ContainSingle(file => file.Path == "a.cs")
			.Which.Lines.Should().BeEquivalentTo(new[] { 2 });
	}

	[Fact]
	public async Task ChangedFilesAsync_ListsDeletedFilesBinariesAndBothSidesOfARename()
	{
		var git = Adapter();
		await git.CreateBranchAsync("ai/x", TestContext.Current.CancellationToken);
		File.Delete(Path.Combine(_clone, "a.cs"));
		File.WriteAllBytes(Path.Combine(_clone, "tool.dll"), [0, 1, 2, 0, 255]);
		File.Move(Path.Combine(_clone, "b.cs"), Path.Combine(_clone, "c.cs"));

		var paths = (await git.ChangedFilesAsync(TestContext.Current.CancellationToken)).Select(file => file.Path).ToList();

		paths.Should().Contain("a.cs", "a deleted workflow or test must reach the path allowlist, not vanish because it has no new lines");
		paths.Should().Contain("tool.dll", "git prints a binary file with no line numbers at all, and the allowlist still has to see it");
		paths.Should().Contain("b.cs").And.Contain("c.cs", "a rename is a delete and an add, and both paths are touched");
	}

	[Fact]
	public async Task ReturnToMainAsync_DiscardsWorkAndDeletesTheBranch()
	{
		var git = Adapter();
		await git.CreateBranchAsync("ai/x", TestContext.Current.CancellationToken);
		File.WriteAllText(Path.Combine(_clone, "a.cs"), "scribble\n");

		var problem = await git.ReturnToMainAsync("ai/x", deleteRemoteBranch: false, TestContext.Current.CancellationToken);

		problem.Should().BeNull("everything went back cleanly");
		File.ReadAllText(Path.Combine(_clone, "a.cs")).Should().Be("one\n");
		(await Adapter().RefusalAsync(TestContext.Current.CancellationToken)).Should().BeNull("the clone is clean and back on main");
	}

	[Fact]
	public async Task ReturnToMainAsync_SaysSoWhenTheBranchCouldNotBeDeleted()
	{
		var problem = await Adapter().ReturnToMainAsync("ai/never-created", deleteRemoteBranch: false, TestContext.Current.CancellationToken);

		problem.Should().Contain("ai/never-created",
			"a step that failed must be reported, not silently dropped, or the clone is left in a state nobody was told about");
	}

	[Fact]
	public async Task CommitAndPush_PublishesTheBranch()
	{
		var git = Adapter();
		await git.CreateBranchAsync("ai/x", TestContext.Current.CancellationToken);
		File.WriteAllText(Path.Combine(_clone, "a.cs"), "one\ntwo\n");

		var sha = await git.CommitAsync("change", TestContext.Current.CancellationToken);
		await git.PushBranchAsync("ai/x", TestContext.Current.CancellationToken);

		sha.Should().HaveLength(40);
		var (_, remoteBranches) = await LocalRepoService.RunCommandWithStreamingAsync(
			_clone, "git", ["ls-remote", "--heads", "origin"], null, TestContext.Current.CancellationToken);
		remoteBranches.Should().Contain("refs/heads/ai/x");
	}
}
