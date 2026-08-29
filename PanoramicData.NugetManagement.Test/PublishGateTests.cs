using PanoramicData.NugetManagement.Web.Models;
using PanoramicData.NugetManagement.Web.Services;

namespace PanoramicData.NugetManagement.Test;

/// <summary>
/// Tests for the Publish button's gate: normally a repository is publishable only once its tests
/// have passed, but the estate-wide "allow publish without running tests" setting waives that one
/// requirement — and only that one. A build that has not succeeded, a failure of either kind, an
/// absent clone and a clone behind origin all still block, setting or no setting.
/// </summary>
public class PublishGateTests(ITestOutputHelper output) : TestWithOutput(output)
{
	private static RepositoryDashboardRow Row(
		PackageStatus status,
		bool isClonedLocally = true,
		bool? isSyncedWithOrigin = true) => new()
		{
			RepositoryFullName = "panoramicdata/Athonet.Api",
			Status = status,
			IsClonedLocally = isClonedLocally,
			IsSyncedWithOrigin = isSyncedWithOrigin
		};

	[Fact]
	public void IsEnabled_TestsPassed_IsEnabledWhicheverWayTheSettingIsSet()
	{
		PublishGate.IsEnabled(Row(PackageStatus.TestsPassed), allowWithoutTests: false).Should().BeTrue();
		PublishGate.IsEnabled(Row(PackageStatus.TestsPassed), allowWithoutTests: true).Should().BeTrue();
	}

	[Fact]
	public void IsEnabled_BuiltButNotTested_IsBlockedUntilTheSettingIsOn()
	{
		PublishGate.IsEnabled(Row(PackageStatus.BuildSucceeded), allowWithoutTests: false).Should().BeFalse();
		PublishGate.IsEnabled(Row(PackageStatus.BuildSucceeded), allowWithoutTests: true).Should().BeTrue();
	}

	[Theory]
	[InlineData(PackageStatus.TestsFailed)]
	[InlineData(PackageStatus.BuildFailed)]
	public void IsEnabled_AFailure_IsBlockedEvenWithTheSettingOn(PackageStatus status)
		=> PublishGate.IsEnabled(Row(status), allowWithoutTests: true).Should().BeFalse();

	[Theory]
	[InlineData(PackageStatus.GitSynced)]
	[InlineData(PackageStatus.Assessed)]
	[InlineData(PackageStatus.Remediated)]
	[InlineData(PackageStatus.NotAssessed)]
	public void IsEnabled_NotYetBuilt_IsBlockedEvenWithTheSettingOn(PackageStatus status)
		=> PublishGate.IsEnabled(Row(status), allowWithoutTests: true).Should().BeFalse();

	[Fact]
	public void IsEnabled_NotCloned_IsBlockedEvenWithTheSettingOn()
		=> PublishGate.IsEnabled(Row(PackageStatus.TestsPassed, isClonedLocally: false), allowWithoutTests: true)
			.Should().BeFalse();

	[Fact]
	public void IsEnabled_BehindOrigin_IsBlockedEvenWithTheSettingOn()
		=> PublishGate.IsEnabled(Row(PackageStatus.TestsPassed, isSyncedWithOrigin: false), allowWithoutTests: true)
			.Should().BeFalse();

	/// <summary>
	/// An unknown sync state is not a positive answer that the clone is behind, so — matching every
	/// other step in the toolbar — it blocks nothing.
	/// </summary>
	[Fact]
	public void IsEnabled_SyncStateUnknown_DoesNotBlock()
		=> PublishGate.IsEnabled(Row(PackageStatus.TestsPassed, isSyncedWithOrigin: null), allowWithoutTests: false)
			.Should().BeTrue();

	[Fact]
	public void IsEnabled_NoRowSelected_IsBlocked()
		=> PublishGate.IsEnabled(null, allowWithoutTests: true).Should().BeFalse();

	/// <summary>
	/// The tooltip has to say when the test gate is being waived: a green Publish button on a
	/// repository whose tests never ran is exactly the state a user must not misread.
	/// </summary>
	[Fact]
	public void WaivesTests_PublishingOffABuildWithTheSettingOn_IsTrue()
		=> PublishGate.WaivesTests(Row(PackageStatus.BuildSucceeded), allowWithoutTests: true).Should().BeTrue();

	[Fact]
	public void WaivesTests_TestsActuallyPassed_IsFalse()
		=> PublishGate.WaivesTests(Row(PackageStatus.TestsPassed), allowWithoutTests: true).Should().BeFalse();

	[Fact]
	public void WaivesTests_SettingOff_IsFalse()
		=> PublishGate.WaivesTests(Row(PackageStatus.BuildSucceeded), allowWithoutTests: false).Should().BeFalse();
}
