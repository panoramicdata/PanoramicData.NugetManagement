using System.Diagnostics;
using PanoramicData.NugetManagement.Web.Models;
using PanoramicData.NugetManagement.Web.Services;

namespace PanoramicData.NugetManagement.Test;

/// <summary>Tests for <see cref="CloneChangeReader"/>: git HEAD against the working tree of a clone.</summary>
public class CloneChangeReaderTests : TestWithOutput, IDisposable
{
	private readonly string _root = Path.Combine(Path.GetTempPath(), "nugetmanagement-tests", $"changes-{Guid.NewGuid():n}");
	private readonly CloneChangeReader _reader = new();

	public CloneChangeReaderTests(ITestOutputHelper output) : base(output)
	{
		Directory.CreateDirectory(_root);
		Git("init", "-q");
		Git("config", "user.email", "t@example.com");
		Git("config", "user.name", "t");
		Git("config", "core.autocrlf", "false");
		File.WriteAllText(Path.Combine(_root, "keep.txt"), "one\n");
		File.WriteAllText(Path.Combine(_root, "gone.txt"), "bye\n");
		File.WriteAllText(Path.Combine(_root, "sp ace é.txt"), "x\n");
		Git("add", "-A");
		Git("commit", "-q", "-m", "init");
	}

	public void Dispose()
	{
		foreach (var file in Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories))
		{
			File.SetAttributes(file, FileAttributes.Normal);
		}

		Directory.Delete(_root, recursive: true);
		GC.SuppressFinalize(this);
	}

	private void Git(params string[] args)
	{
		var info = new ProcessStartInfo("git") { WorkingDirectory = _root, RedirectStandardOutput = true, RedirectStandardError = true };
		foreach (var arg in args)
		{
			info.ArgumentList.Add(arg);
		}

		using var process = Process.Start(info)!;
		process.StandardOutput.ReadToEnd();
		process.StandardError.ReadToEnd();
		process.WaitForExit();
		process.ExitCode.Should().Be(0);
	}

	[Fact]
	public void CleanTree_HasNoChanges()
		=> _reader.ReadChanges(_root).Should().BeEmpty();

	[Fact]
	public void Modified_Added_Deleted_AreReported()
	{
		File.WriteAllText(Path.Combine(_root, "keep.txt"), "two\n");
		File.WriteAllText(Path.Combine(_root, "new.txt"), "n\n");
		File.Delete(Path.Combine(_root, "gone.txt"));

		var changes = _reader.ReadChanges(_root).ToDictionary(c => c.Path);

		changes["keep.txt"].Kind.Should().Be(ChangeKind.Modified);
		changes["new.txt"].Kind.Should().Be(ChangeKind.Added);
		changes["gone.txt"].Kind.Should().Be(ChangeKind.Deleted);
	}

	[Fact]
	public void Fingerprint_IsStableForSameBytes_AndDiffersAfterEdit()
	{
		File.WriteAllText(Path.Combine(_root, "keep.txt"), "two\n");
		var change = new ChangedFile("keep.txt", ChangeKind.Modified);

		var first = _reader.Fingerprint(_root, change);
		_reader.Fingerprint(_root, change).Should().Be(first);
		first.Should().MatchRegex("^[0-9a-f]{64}$");

		File.WriteAllText(Path.Combine(_root, "keep.txt"), "three\n");
		_reader.Fingerprint(_root, change).Should().NotBe(first);
	}

	[Fact]
	public void Fingerprint_MissingOrDeletedFile_IsDeleted()
	{
		_reader.Fingerprint(_root, new ChangedFile("nope.txt", ChangeKind.Modified)).Should().Be("deleted");
		_reader.Fingerprint(_root, new ChangedFile("keep.txt", ChangeKind.Deleted)).Should().Be("deleted");
	}

	[Fact]
	public void Fingerprint_HugeFile_UsesSizeAndTimeStandIn()
	{
		var path = Path.Combine(_root, "huge.bin");
		File.WriteAllBytes(path, new byte[1_048_577]);

		var fingerprint = _reader.Fingerprint(_root, new ChangedFile("huge.bin", ChangeKind.Added));

		fingerprint.Should().Be($"size:1048577:{new FileInfo(path).LastWriteTimeUtc.Ticks}");
	}

	[Fact]
	public void Baseline_ContainsDirtyFilesOnly()
	{
		File.WriteAllText(Path.Combine(_root, "keep.txt"), "dirty\n");

		var baseline = _reader.Baseline(_root);

		baseline.Keys.Should().BeEquivalentTo(["keep.txt"]);
		baseline["keep.txt"].Should().Be(_reader.Fingerprint(_root, new ChangedFile("keep.txt", ChangeKind.Modified)));
	}

	[Fact]
	public void Baseline_MissingClone_IsEmpty()
		=> _reader.Baseline(Path.Combine(_root, "nope")).Should().BeEmpty();

	[Fact]
	public async Task RecordPreExistingChangesAsync_NotesOnlyWhatIsAlreadyDirty()
	{
		File.WriteAllText(Path.Combine(_root, "keep.txt"), "dirty before the session\n");
		var item = NewItem();

		await WorkExecutors.RecordPreExistingChangesAsync(item, _root, TestContext.Current.CancellationToken);
		File.WriteAllText(Path.Combine(_root, "later.txt"), "made by the session\n");

		item.PreExistingChanges.Keys.Should().BeEquivalentTo(["keep.txt"]);
	}

	[Fact]
	public async Task RecordPreExistingChangesAsync_MissingClone_OverwritesWithEmptyAndDoesNotThrow()
	{
		var item = NewItem();
		item.PreExistingChanges = new Dictionary<string, string> { ["stale.txt"] = "abc" };

		await WorkExecutors.RecordPreExistingChangesAsync(item, Path.Combine(_root, "nope"), TestContext.Current.CancellationToken);

		item.PreExistingChanges.Should().BeEmpty();
	}

	[Fact]
	public async Task RecordPreExistingChangesAsync_CancelledFixIsNotDelayed()
	{
		using var cts = new CancellationTokenSource();
		await cts.CancelAsync();

		var act = () => WorkExecutors.RecordPreExistingChangesAsync(NewItem(), _root, cts.Token);

		await act.Should().ThrowAsync<OperationCanceledException>();
	}

	private static WorkItem NewItem() => new()
	{
		Id = "1",
		Title = "Fix",
		Descriptor = WorkDescriptor.ForRepository(WorkKind.FixWithAiRule, "panoramicdata", "Repo"),
		DedupKey = "fix:Repo"
	};

	[Fact]
	public void ReadPair_ModifiedFile_ReturnsHeadAndDisk()
	{
		File.WriteAllText(Path.Combine(_root, "keep.txt"), "two\n");
		var pair = _reader.ReadPair(_root, new ChangedFile("keep.txt", ChangeKind.Modified));
		pair.Original.Should().Be("one\n");
		pair.Modified.Should().Be("two\n");
		pair.NotDiffableReason.Should().BeNull();
	}

	[Fact]
	public void ReadPair_Deleted_HasEmptyModified_Added_HasEmptyOriginal()
	{
		File.Delete(Path.Combine(_root, "gone.txt"));
		File.WriteAllText(Path.Combine(_root, "new.txt"), "n\n");

		var deleted = _reader.ReadPair(_root, new ChangedFile("gone.txt", ChangeKind.Deleted));
		deleted.Original.Should().Be("bye\n");
		deleted.Modified.Should().BeEmpty();

		var added = _reader.ReadPair(_root, new ChangedFile("new.txt", ChangeKind.Added));
		added.Original.Should().BeEmpty();
		added.Modified.Should().Be("n\n");
	}

	[Fact]
	public void PathWithSpacesAndNonAscii_IsReadVerbatim()
	{
		File.WriteAllText(Path.Combine(_root, "sp ace é.txt"), "y\n");
		var change = _reader.ReadChanges(_root).Single();
		change.Path.Should().Be("sp ace é.txt");
		_reader.ReadPair(_root, change).Modified.Should().Be("y\n");
	}

	[Fact]
	public void BinaryFile_IsNotDiffable()
	{
		File.WriteAllBytes(Path.Combine(_root, "b.bin"), [1, 0, 2, 0, 3]);
		var pair = _reader.ReadPair(_root, new ChangedFile("b.bin", ChangeKind.Added));
		pair.NotDiffableReason.Should().StartWith("binary file");
		pair.Modified.Should().BeEmpty();
	}

	[Fact]
	public void HugeFile_IsNotDiffable()
	{
		File.WriteAllText(Path.Combine(_root, "big.txt"), new string('a', 1_048_577));
		var pair = _reader.ReadPair(_root, new ChangedFile("big.txt", ChangeKind.Added));
		pair.NotDiffableReason.Should().StartWith("file too large");
	}

	[Fact]
	public void Rename_ReadsOriginalFromOldPath()
	{
		Git("mv", "keep.txt", "renamed.txt");
		File.WriteAllText(Path.Combine(_root, "renamed.txt"), "one\nmore\n");
		var change = _reader.ReadChanges(_root).Single(c => c.Path == "renamed.txt");
		change.OriginalPath.Should().Be("keep.txt");
		_reader.ReadPair(_root, change).Original.Should().Be("one\n");
	}

	[Fact]
	public void MissingDirectory_AndNonRepo_AreEmpty()
	{
		_reader.ReadChanges(Path.Combine(_root, "nope")).Should().BeEmpty();
		var plain = Path.Combine(Path.GetTempPath(), "nugetmanagement-tests", $"plain-{Guid.NewGuid():n}");
		Directory.CreateDirectory(plain);
		try
		{
			_reader.ReadChanges(plain).Should().BeEmpty();
		}
		finally
		{
			Directory.Delete(plain);
		}
	}

	[Fact]
	public void RepoWithNoCommits_IsEmpty()
	{
		var fresh = Path.Combine(Path.GetTempPath(), "nugetmanagement-tests", $"fresh-{Guid.NewGuid():n}");
		Directory.CreateDirectory(fresh);
		try
		{
			RunGitIn(fresh, "init", "-q");
			File.WriteAllText(Path.Combine(fresh, "a.txt"), "a\n");
			_reader.ReadChanges(fresh).Should().BeEmpty();
		}
		finally
		{
			Directory.Delete(fresh, recursive: true);
		}
	}

	[Fact]
	public void ReadPair_CloneVanishes_DoesNotThrow()
	{
		var gone = Path.Combine(Path.GetTempPath(), "nugetmanagement-tests", $"vanished-{Guid.NewGuid():n}");
		var pair = _reader.ReadPair(gone, new ChangedFile("keep.txt", ChangeKind.Modified));
		pair.NotDiffableReason.Should().Be("could not read file");
		pair.Original.Should().BeEmpty();
		pair.Modified.Should().BeEmpty();
	}

	[Fact]
	public void ReadPair_ModifiedFileMissingFromHead_SurfacesFailure()
	{
		File.WriteAllText(Path.Combine(_root, "keep.txt"), "two\n");
		var pair = _reader.ReadPair(_root, new ChangedFile("keep.txt", ChangeKind.Modified, "no-such.txt"));
		pair.NotDiffableReason.Should().Be("could not read file");
	}

	[Fact]
	public void HugeFile_ReportsSizeWithoutReturningContent()
	{
		File.WriteAllText(Path.Combine(_root, "big.txt"), new string('a', 1_048_577));
		var pair = _reader.ReadPair(_root, new ChangedFile("big.txt", ChangeKind.Added));
		pair.NotDiffableReason.Should().Be("file too large (1048577 bytes)");
		pair.Original.Should().BeEmpty();
		pair.Modified.Should().BeEmpty();
	}

	[Fact]
	public void Deleted_WithBinaryOrHugeHead_IsNotDiffable()
	{
		File.WriteAllBytes(Path.Combine(_root, "b.bin"), [1, 0, 2]);
		File.WriteAllText(Path.Combine(_root, "big.txt"), new string('a', 1_048_577));
		Git("add", "-A");
		Git("commit", "-q", "-m", "more");
		File.Delete(Path.Combine(_root, "b.bin"));
		File.Delete(Path.Combine(_root, "big.txt"));

		_reader.ReadPair(_root, new ChangedFile("b.bin", ChangeKind.Deleted)).NotDiffableReason.Should().StartWith("binary file");
		_reader.ReadPair(_root, new ChangedFile("big.txt", ChangeKind.Deleted)).NotDiffableReason.Should().StartWith("file too large");
	}

	[Fact]
	public void CrlfWorkingCopy_OriginalMatchesDiskForm()
	{
		Git("config", "core.autocrlf", "true");
		File.WriteAllBytes(Path.Combine(_root, "crlf.txt"), "l1\r\nl2\r\nl3\r\n"u8.ToArray());
		Git("add", "crlf.txt");
		Git("commit", "-q", "-m", "crlf");
		File.WriteAllBytes(Path.Combine(_root, "crlf.txt"), "l1\r\nCHANGED\r\nl3\r\n"u8.ToArray());

		var pair = _reader.ReadPair(_root, new ChangedFile("crlf.txt", ChangeKind.Modified));

		pair.NotDiffableReason.Should().BeNull();
		var original = pair.Original.Split('\n');
		var modified = pair.Modified.Split('\n');
		original.Should().HaveCount(modified.Length);
		original.Zip(modified).Count(p => p.First != p.Second).Should().Be(1);
	}

	[Fact]
	public void ReadChanges_TakesNoOptionalLocksAndLeavesTheIndexAlone()
	{
		// Make the stat information stale, which is when a plain `git status` refreshes (rewrites) the
		// index, taking index.lock to do it. That is what would collide with the app's own git commands.
		var tracked = Path.Combine(_root, "keep.txt");
		File.SetLastWriteTimeUtc(tracked, DateTime.UtcNow.AddHours(-3));
		File.WriteAllText(Path.Combine(_root, "new.txt"), "x\n");

		var index = Path.Combine(_root, ".git", "index");
		var before = File.ReadAllBytes(index);
		var beforeTime = File.GetLastWriteTimeUtc(index);

		// Control: git without the option does rewrite the index here, so the assertions below can fail.
		var control = _root + "-control";
		CopyDirectory(_root, control);
		try
		{
			var controlInfo = new ProcessStartInfo("git", "status --porcelain") { WorkingDirectory = control, RedirectStandardOutput = true };
			controlInfo.Environment.Remove("GIT_OPTIONAL_LOCKS");
			using (var p = Process.Start(controlInfo)!)
			{
				p.StandardOutput.ReadToEnd();
				p.WaitForExit();
			}

			File.ReadAllBytes(Path.Combine(control, ".git", "index")).Should().NotEqual(before, "plain git status refreshes a stale index");
		}
		finally
		{
			DeleteDirectory(control);
		}

		var changes = _reader.ReadChanges(_root);

		changes.Select(c => c.Path).Should().Contain("new.txt");
		File.ReadAllBytes(index).Should().Equal(before);
		File.GetLastWriteTimeUtc(index).Should().Be(beforeTime);
		File.Exists(Path.Combine(_root, ".git", "index.lock")).Should().BeFalse();
	}

	[Fact]
	public void ReadChanges_KillsAGitThatNeverFinishes()
	{
		var shim = Path.Combine(_root, OperatingSystem.IsWindows() ? "hang-git.cmd" : "hang-git.sh");
		File.WriteAllText(shim, OperatingSystem.IsWindows()
			? "@echo off\r\nping -n 60 127.0.0.1 > nul\r\n"
			: "#!/bin/sh\nsleep 60\n");
		if (!OperatingSystem.IsWindows())
		{
			File.SetUnixFileMode(shim, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
		}

		var reader = new CloneChangeReader(TimeSpan.FromMilliseconds(300), shim);
		var stopwatch = Stopwatch.StartNew();

		var changes = reader.ReadChanges(_root);

		stopwatch.Stop();
		changes.Should().BeEmpty();
		stopwatch.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(20), "the command is abandoned at the timeout, not waited for");
	}

	private static void CopyDirectory(string from, string to)
	{
		foreach (var dir in Directory.EnumerateDirectories(from, "*", SearchOption.AllDirectories))
		{
			Directory.CreateDirectory(Path.Combine(to, Path.GetRelativePath(from, dir)));
		}

		Directory.CreateDirectory(to);
		foreach (var file in Directory.EnumerateFiles(from, "*", SearchOption.AllDirectories))
		{
			File.Copy(file, Path.Combine(to, Path.GetRelativePath(from, file)), overwrite: true);
		}
	}

	private static void DeleteDirectory(string path)
	{
		foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
		{
			File.SetAttributes(file, FileAttributes.Normal);
		}

		Directory.Delete(path, recursive: true);
	}

	private static void RunGitIn(string directory, params string[] args)
	{
		var info = new ProcessStartInfo("git") { WorkingDirectory = directory, RedirectStandardOutput = true, RedirectStandardError = true };
		foreach (var arg in args)
		{
			info.ArgumentList.Add(arg);
		}

		using var process = Process.Start(info)!;
		process.StandardOutput.ReadToEnd();
		process.StandardError.ReadToEnd();
		process.WaitForExit();
		process.ExitCode.Should().Be(0);
	}
}
