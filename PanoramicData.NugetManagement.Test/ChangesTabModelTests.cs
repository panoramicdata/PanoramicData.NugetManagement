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

	[Fact]
	public void ThrottleAllowsOneReadPerIntervalAndNeverOverlaps()
	{
		var model = new ChangesTabModel(new CloneChangeReader(), TimeSpan.FromSeconds(1));
		var t0 = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

		model.TryBeginRead(t0).Should().BeTrue();
		model.TryBeginRead(t0.AddSeconds(5), force: true).Should().BeFalse("a read is still under way");

		// Finishing the read (the clone does not exist, so nothing is read) releases the guard.
		model.Refresh(Path.Combine(Path.GetTempPath(), $"missing-{Guid.NewGuid():n}"), null, PreExisting);

		model.TryBeginRead(t0.AddMilliseconds(500)).Should().BeFalse("under a second since the last read");
		model.TryBeginRead(t0.AddMilliseconds(500), force: true).Should().BeTrue("force skips the interval");
		model.Refresh(Path.Combine(Path.GetTempPath(), $"missing-{Guid.NewGuid():n}"), null, PreExisting);
		model.TryBeginRead(t0.AddSeconds(2)).Should().BeTrue();
	}
}