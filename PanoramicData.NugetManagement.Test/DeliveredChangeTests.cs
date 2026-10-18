using System.Diagnostics;
using PanoramicData.NugetManagement.Web.Services;
using PanoramicData.NugetManagement.Web.Services.AiChange;

namespace PanoramicData.NugetManagement.Test;

/// <summary>
/// Tests for <see cref="DeliveredChange"/>: the change set kept on a work item before the pipeline resets
/// the clone, so the Changes tab can still show what was delivered.
/// </summary>
public class DeliveredChangeTests : TestWithOutput, IDisposable
{
	private readonly string _root = Path.Combine(Path.GetTempPath(), "nugetmanagement-tests", $"delivered-{Guid.NewGuid():n}");
	private readonly CloneChangeReader _reader = new();

	public DeliveredChangeTests(ITestOutputHelper output)
		: base(output)
	{
		Directory.CreateDirectory(_root);
		Git("init", "-q");
		Git("config", "user.email", "t@example.com");
		Git("config", "user.name", "t");
		Git("config", "core.autocrlf", "false");
		File.WriteAllText(Path.Combine(_root, "keep.txt"), "one\n");
		File.WriteAllText(Path.Combine(_root, "gone.txt"), "bye\n");
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
	public void Capture_KeepsEachChangedFileWithBothSidesOfItsDiff()
	{
		File.WriteAllText(Path.Combine(_root, "keep.txt"), "two\n");
		File.WriteAllText(Path.Combine(_root, "new.txt"), "fresh\n");
		File.Delete(Path.Combine(_root, "gone.txt"));

		var delivered = DeliveredChange.Capture(_reader, _root);

		delivered.Files.Select(file => (file.File.Path, file.File.Kind)).Should().BeEquivalentTo(
			[("keep.txt", ChangeKind.Modified), ("new.txt", ChangeKind.Added), ("gone.txt", ChangeKind.Deleted)]);

		var kept = delivered.Files.Single(file => file.File.Path == "keep.txt").Pair;
		kept.Original.Should().Be("one\n");
		kept.Modified.Should().Be("two\n", "the clone is reset to main after delivery, so the text has to be kept now");
	}

	[Fact]
	public void Capture_OfACleanCloneHasNoFiles()
		=> DeliveredChange.Capture(_reader, _root).Files.Should().BeEmpty();

	[Fact]
	public void Capture_StopsKeepingTextOnceItsBudgetIsSpent()
	{
		File.WriteAllText(Path.Combine(_root, "keep.txt"), new string('a', 40));
		File.WriteAllText(Path.Combine(_root, "new.txt"), new string('b', 40));

		var delivered = DeliveredChange.Capture(_reader, _root, maxCharacters: 50);

		delivered.Files.Should().HaveCount(2, "every changed file is still listed");
		delivered.Files.Count(file => file.Pair.NotDiffableReason is not null).Should().Be(1,
			"memory held for a finished item is bounded, so the file that does not fit says so instead of being dropped");
	}

	[Fact]
	public void WithOutcome_BuildsThePullRequestLinkFromTheRepositoryAndTheNumber()
	{
		var outcome = new AiChangeOutcome(AiChangeOutcomeKind.Merged, "Merged pull request #7.", 7);

		var delivered = DeliveredChange.Capture(_reader, _root).WithOutcome(outcome, "panoramicdata/Sample.Api");

		delivered.PullRequestUrl.Should().Be("https://github.com/panoramicdata/Sample.Api/pull/7");
		delivered.Summary.Should().Be("Merged pull request #7.");
	}

	[Fact]
	public void WithOutcome_HasNoLinkWhenNoPullRequestWasOpened()
		=> DeliveredChange.Capture(_reader, _root)
			.WithOutcome(new AiChangeOutcome(AiChangeOutcomeKind.LocalGateFailed, "The tests did not pass."), "o/r")
			.PullRequestUrl.Should().BeNull();

	[Fact]
	public void WithOutcome_AddsTheSummaryAndThePullRequestLink()
	{
		var delivered = DeliveredChange.Capture(_reader, _root).WithOutcome("Merged pull request #7.", "https://github.com/o/r/pull/7");

		delivered.Summary.Should().Be("Merged pull request #7.");
		delivered.PullRequestUrl.Should().Be("https://github.com/o/r/pull/7");
	}
}
