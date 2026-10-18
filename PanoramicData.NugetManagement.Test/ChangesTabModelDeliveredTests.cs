using PanoramicData.NugetManagement.Web.Services;

namespace PanoramicData.NugetManagement.Test;

/// <summary>
/// Tests for how <see cref="ChangesTabModel"/> shows a delivered change once the live clone has nothing left.
/// </summary>
public class ChangesTabModelDeliveredTests(ITestOutputHelper output) : TestWithOutput(output)
{
	private static readonly IReadOnlyDictionary<string, string> NoBaseline = new Dictionary<string, string>(StringComparer.Ordinal);

	private static string MissingClone() => Path.Combine(Path.GetTempPath(), $"missing-{Guid.NewGuid():n}");

	private static DeliveredChange Delivered() => new(
		[
			new DeliveredFile(new ChangedFile("src/A.cs", ChangeKind.Modified), new FilePair("old A", "new A", null)),
			new DeliveredFile(new ChangedFile("src/B.cs", ChangeKind.Added), new FilePair(string.Empty, "new B", null))
		],
		"Merged pull request #7.",
		"https://github.com/o/r/pull/7");

	private static ChangesTabModel.Snapshot Refreshed(DeliveredChange? delivered, string? selected = null)
	{
		var model = new ChangesTabModel(new CloneChangeReader(), TimeSpan.Zero);
		model.TryBeginRead(DateTime.UtcNow, force: true, out var generation).Should().BeTrue();
		model.Refresh(generation, MissingClone(), selected, NoBaseline, delivered);

		return model.Current;
	}

	[Fact]
	public void WhenTheCloneHasNothingLeft_TheDeliveredChangeIsShown()
	{
		var snapshot = Refreshed(Delivered());

		snapshot.Changes.Select(change => change.Path).Should().Equal("src/A.cs", "src/B.cs");
		snapshot.SelectedPath.Should().Be("src/A.cs");
		snapshot.Pair!.Modified.Should().Be("new A", "the diff is the one saved before the clone was reset");
		snapshot.Delivery!.PullRequestUrl.Should().Be("https://github.com/o/r/pull/7");
	}

	[Fact]
	public void TheSelectedFileComesFromTheDeliveredChange()
	{
		var snapshot = Refreshed(Delivered(), selected: "src/B.cs");

		snapshot.SelectedPath.Should().Be("src/B.cs");
		snapshot.Pair!.Modified.Should().Be("new B");
	}

	[Fact]
	public void WithNoDeliveredChange_AnEmptyCloneShowsNothing()
	{
		var snapshot = Refreshed(delivered: null);

		snapshot.Changes.Should().BeEmpty();
		snapshot.Delivery.Should().BeNull();
	}
}
