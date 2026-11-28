using PanoramicData.NugetManagement.Services;
using PanoramicData.NugetManagement.Web.Models;

namespace PanoramicData.NugetManagement.Web.Services;

/// <summary>
/// Turns a pushed governance branch into a pull request into main and sees it on its way: auto-merge
/// when GitHub accepts it, an immediate squash merge when there is no required check to wait for, and
/// otherwise an open pull request for a person.
/// </summary>
/// <remarks>
/// Not cancellable: the branch is already on origin, and a branch with no pull request is the one state
/// nobody would notice.
/// </remarks>
internal static class PullRequestLander
{
	private const string BaseBranch = "main";

	/// <summary>Opens the pull request and lands it as far as GitHub allows.</summary>
	/// <param name="github">The GitHub write API.</param>
	/// <param name="repoIdentity">The repository, as owner/name.</param>
	/// <param name="delivery">The pushed branch.</param>
	/// <param name="onOutput">Receives progress, including the pull request's URL.</param>
	public static async Task<CommitAndPushOutcome> OpenAndLandAsync(
		IGitHubWriteApi github,
		string repoIdentity,
		BranchDelivery delivery,
		Action<string>? onOutput)
	{
		var (owner, name) = Split(repoIdentity);
		var branch = delivery.Branch!;
		int number;

		try
		{
			number = await github
				.CreatePullRequestAsync(owner, name, branch, BaseBranch, delivery.Subject, delivery.Message, CancellationToken.None)
				.ConfigureAwait(false);
		}
		catch (Exception ex) when (ex is not OperationCanceledException)
		{
			var reason = $"Branch {branch} is on origin, but the pull request into {BaseBranch} could not be opened ({ex.Message}). Open it by hand.";
			onOutput?.Invoke($"❌ {reason}");
			return CommitAndPushOutcome.FailedBecause(reason);
		}

		var url = $"https://github.com/{repoIdentity}/pull/{number}";
		onOutput?.Invoke($"🔀 Opened pull request #{number} into {BaseBranch}: {url}");

		var (landing, note) = await LandAsync(github, owner, name, number, delivery.HeadSha!).ConfigureAwait(false);
		onOutput?.Invoke(Describe(landing, note));

		return CommitAndPushOutcome.Delivered(new PullRequestDelivery(branch, delivery.HeadSha!, number, url, landing, note));
	}

	/// <summary>What to do with a pull request, given GitHub's answer to auto-merge.</summary>
	/// <param name="result">GitHub's answer.</param>
	internal static PullRequestLanding Decide(AutoMergeResult result) => result.State switch
	{
		AutoMergeState.Enabled => PullRequestLanding.AutoMergeEnabled,
		AutoMergeState.AlreadyMergeable => PullRequestLanding.Merged,
		_ => PullRequestLanding.LeftOpen
	};

	/// <summary>One line saying where the pull request was left.</summary>
	/// <param name="landing">Where it was left.</param>
	/// <param name="note">Why, when it was left open.</param>
	internal static string Describe(PullRequestLanding landing, string? note) => landing switch
	{
		PullRequestLanding.AutoMergeEnabled => "⏳ Auto-merge is on: GitHub squash-merges it once the required checks pass.",
		PullRequestLanding.Merged => "✅ Squash-merged: the repository has no required checks to wait for.",
		_ => $"⚠️ Left open for a person to merge: {note}"
	};

	private static async Task<(PullRequestLanding Landing, string? Note)> LandAsync(
		IGitHubWriteApi github,
		string owner,
		string name,
		int number,
		string headSha)
	{
		AutoMergeResult result;

		try
		{
			result = await github.EnableAutoMergeAsync(owner, name, number, headSha, CancellationToken.None).ConfigureAwait(false);
		}
		catch (Exception ex) when (ex is not OperationCanceledException)
		{
			result = new AutoMergeResult(AutoMergeState.Refused, ex.Message);
		}

		var landing = Decide(result);
		return landing == PullRequestLanding.Merged
			? await MergeNowAsync(github, owner, name, number, headSha).ConfigureAwait(false)
			: (landing, landing == PullRequestLanding.LeftOpen ? result.Message : null);
	}

	private static async Task<(PullRequestLanding Landing, string? Note)> MergeNowAsync(
		IGitHubWriteApi github,
		string owner,
		string name,
		int number,
		string headSha)
	{
		try
		{
			await github.MergePullRequestAsync(owner, name, number, headSha, CancellationToken.None).ConfigureAwait(false);
			return (PullRequestLanding.Merged, null);
		}
		catch (Exception ex) when (ex is not OperationCanceledException)
		{
			return (PullRequestLanding.LeftOpen, $"GitHub would not merge it: {ex.Message}");
		}
	}

	private static (string Owner, string Name) Split(string repoIdentity)
	{
		var slash = repoIdentity.IndexOf('/', StringComparison.Ordinal);
		return (repoIdentity[..slash], repoIdentity[(slash + 1)..]);
	}
}
