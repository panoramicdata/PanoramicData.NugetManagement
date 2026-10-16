using PanoramicData.NugetManagement.Web.Services;

namespace PanoramicData.NugetManagement.Test;

/// <summary>Tests for <see cref="ChangesTabModel"/>: labels, selection fallback and the read throttle.</summary>
public class ChangesTabModelTests(ITestOutputHelper output) : TestWithOutput(output)
{
	private static readonly IReadOnlyDictionary<string, string> PreExisting
		= new Dictionary<string, string>(StringComparer.Ordinal) { ["a.txt"] = "fp1" };

	[Fact]
	public void LabelIsPreExistingWhenFingerprintMatches()
		=> ChangesTabModel.LabelFor("a.txt", PreExisting, () => "fp1").Should().Be(ChangesTabModel.PreExistingLabel);

	[Fact]
	public void LabelIsEditedWhenFingerprintDiffers()
		=> ChangesTabModel.LabelFor("a.txt", PreExisting, () => "fp2").Should().Be(ChangesTabModel.EditedLabel);

	[Fact]
	public void NoLabelWhenNotPreExistingAndNoFingerprintComputed()
	{
		var computed = false;
		ChangesTabModel.LabelFor("b.txt", PreExisting, () => { computed = true; return "x"; }).Should().BeNull();
		computed.Should().BeFalse();
	}

	[Fact]
	public void SelectionKeptWhenStillChanged()
	{
		IReadOnlyList<ChangedFile> changes = [new("a.txt", ChangeKind.Modified), new("b.txt", ChangeKind.Added)];
		ChangesTabModel.ResolveSelection("b.txt", changes).Should().Be("b.txt");
	}

	[Fact]
	public void SelectionFallsBackToFirstWhenFileDisappears()
	{
		IReadOnlyList<ChangedFile> changes = [new("a.txt", ChangeKind.Modified)];
		ChangesTabModel.ResolveSelection("gone.txt", changes).Should().Be("a.txt");
		ChangesTabModel.ResolveSelection(null, changes).Should().Be("a.txt");
		ChangesTabModel.ResolveSelection("a.txt", []).Should().BeNull();
	}

	private static string MissingClone() => Path.Combine(Path.GetTempPath(), $"missing-{Guid.NewGuid():n}");

	[Fact]
	public void ThrottleAllowsOneReadPerIntervalAndNeverOverlaps()
	{
		var model = new ChangesTabModel(new CloneChangeReader(), TimeSpan.FromSeconds(1));
		var t0 = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

		model.TryBeginRead(t0, false, out var generation).Should().BeTrue();
		model.TryBeginRead(t0.AddSeconds(5), true, out _).Should().BeFalse("a read is still under way");

		// Finishing the read (the clone does not exist, so nothing is read) releases the guard.
		model.Refresh(generation, MissingClone(), null, PreExisting);

		model.TryBeginRead(t0.AddMilliseconds(500), false, out _).Should().BeFalse("under a second since the last read");
		model.TryBeginRead(t0.AddMilliseconds(500), true, out generation).Should().BeTrue("force skips the interval");
		model.Refresh(generation, MissingClone(), null, PreExisting);
		model.TryBeginRead(t0.AddSeconds(2), false, out _).Should().BeTrue();
	}

	[Fact]
	public void LateResultFromThePreviousGenerationIsDiscarded()
	{
		var root = NewRepo(("a.txt", "one\n"));
		try
		{
			File.WriteAllText(Path.Combine(root, "a.txt"), "two\n");
			var model = new ChangesTabModel(new CloneChangeReader(), TimeSpan.Zero);
			var t0 = DateTime.UtcNow;

			model.TryBeginRead(t0, false, out var oldGeneration).Should().BeTrue();
			model.Reset();
			model.Refresh(oldGeneration, root, null, PreExisting);

			model.Current.Changes.Should().BeEmpty("the read belonged to the item that was switched away from");

			// The stale read must not have released the guard of the new generation either.
			model.TryBeginRead(t0, false, out var newGeneration).Should().BeTrue();
			model.TryBeginRead(t0, false, out _).Should().BeFalse();
			model.Refresh(newGeneration, root, null, PreExisting);
			model.Current.Changes.Should().ContainSingle();
		}
		finally
		{
			DeleteRepo(root);
		}
	}

	[Fact]
	public void ResetClearsThePreviousItemsChanges()
	{
		var root = NewRepo(("a.txt", "one\n"));
		try
		{
			File.WriteAllText(Path.Combine(root, "a.txt"), "two\n");
			var model = new ChangesTabModel(new CloneChangeReader(), TimeSpan.Zero);
			model.TryBeginRead(DateTime.UtcNow, false, out var generation).Should().BeTrue();
			model.Refresh(generation, root, null, PreExisting);
			model.Current.Changes.Should().ContainSingle();

			// An item with no repository is never read: after the reset it shows nothing, not the previous item's files.
			model.Reset();

			model.Current.Should().BeSameAs(ChangesTabModel.Snapshot.Empty);
			model.Current.Changes.Should().BeEmpty();
			model.Current.SelectedPath.Should().BeNull();
			model.Current.Pair.Should().BeNull();
		}
		finally
		{
			DeleteRepo(root);
		}
	}

	[Fact]
	public void RefreshPublishesLabelsSelectionAndPair()
	{
		var root = NewRepo(("keep.txt", "k\n"), ("pre.txt", "p\n"), ("edited.txt", "e\n"));
		try
		{
			var reader = new CloneChangeReader();
			File.WriteAllText(Path.Combine(root, "pre.txt"), "p2\n");
			File.WriteAllText(Path.Combine(root, "edited.txt"), "e2\n");
			var baseline = reader.Baseline(root);

			// The session then edits one pre-existing file and adds a new one.
			File.WriteAllText(Path.Combine(root, "edited.txt"), "e3\n");
			File.WriteAllText(Path.Combine(root, "new.txt"), "n\n");

			var model = new ChangesTabModel(reader, TimeSpan.Zero);
			model.TryBeginRead(DateTime.UtcNow, false, out var generation).Should().BeTrue();
			model.Refresh(generation, root, "gone.txt", baseline);

			var snapshot = model.Current;
			snapshot.Changes.Select(c => c.Path).Should().BeEquivalentTo("pre.txt", "edited.txt", "new.txt");
			snapshot.Labels.Should().HaveCount(2);
			snapshot.Labels["pre.txt"].Should().Be(ChangesTabModel.PreExistingLabel);
			snapshot.Labels["edited.txt"].Should().Be(ChangesTabModel.EditedLabel);
			snapshot.SelectedPath.Should().Be(snapshot.Changes[0].Path, "the requested file is not changed, so the first one is selected");
			snapshot.Pair.Should().NotBeNull();
			snapshot.Pair!.NotDiffableReason.Should().BeNull();
		}
		finally
		{
			DeleteRepo(root);
		}
	}

	private static string NewRepo(params (string Name, string Content)[] files)
	{
		var root = Path.Combine(Path.GetTempPath(), "nugetmanagement-tests", $"tabmodel-{Guid.NewGuid():n}");
		Directory.CreateDirectory(root);
		RunGitIn(root, "init", "-q");
		RunGitIn(root, "config", "user.email", "t@example.com");
		RunGitIn(root, "config", "user.name", "t");
		RunGitIn(root, "config", "core.autocrlf", "false");
		foreach (var (name, content) in files)
		{
			File.WriteAllText(Path.Combine(root, name), content);
		}

		RunGitIn(root, "add", "-A");
		RunGitIn(root, "commit", "-q", "-m", "init");
		return root;
	}

	private static void DeleteRepo(string root)
	{
		foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
		{
			File.SetAttributes(file, FileAttributes.Normal);
		}

		Directory.Delete(root, recursive: true);
	}

	private static void RunGitIn(string directory, params string[] args)
	{
		var info = new System.Diagnostics.ProcessStartInfo("git") { WorkingDirectory = directory, RedirectStandardOutput = true, RedirectStandardError = true };
		foreach (var arg in args)
		{
			info.ArgumentList.Add(arg);
		}

		using var process = System.Diagnostics.Process.Start(info)!;
		process.StandardOutput.ReadToEnd();
		process.StandardError.ReadToEnd();
		process.WaitForExit();
		process.ExitCode.Should().Be(0);
	}
}
