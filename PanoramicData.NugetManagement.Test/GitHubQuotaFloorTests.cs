using PanoramicData.NugetManagement.Web.Services;

namespace PanoramicData.NugetManagement.Test;

/// <summary>
/// A refresh must stop fanning out work, and say when quota returns, before it drains the shared limit.
/// </summary>
public class GitHubQuotaFloorTests(ITestOutputHelper output) : TestWithOutput(output)
{
	private static readonly DateTimeOffset Reset = new(2026, 11, 23, 15, 0, 0, TimeSpan.Zero);

	[Fact]
	public void BelowTheFloor_ReportsRemainingAndResetTime()
	{
		var message = GitHubQuotaFloor.PauseMessage(remaining: 1200, reset: Reset);

		message.Should().NotBeNull();
		message.Should().Contain("1200").And.Contain("15:00");
	}

	[Theory]
	[InlineData(1500)]
	[InlineData(4999)]
	public void AtOrAboveTheFloor_DoesNotPause(int remaining)
		=> GitHubQuotaFloor.PauseMessage(remaining, Reset).Should().BeNull();

	[Fact]
	public void UnknownQuota_DoesNotPause()
		=> GitHubQuotaFloor.PauseMessage(remaining: null, reset: null).Should().BeNull();
}
