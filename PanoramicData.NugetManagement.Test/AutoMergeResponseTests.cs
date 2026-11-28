using PanoramicData.NugetManagement.Services;

namespace PanoramicData.NugetManagement.Test;

/// <summary>
/// Reading GitHub's answer to <c>enablePullRequestAutoMerge</c>, which reports a refusal as HTTP 200
/// with an <c>errors</c> array.
/// </summary>
public class AutoMergeResponseTests(ITestOutputHelper output) : TestWithOutput(output)
{
	[Fact]
	public void AnAutoMergeRequest_IsEnabled()
		=> OctokitGitHubWriteApi.ParseAutoMergeResponse(
				"""{"data":{"enablePullRequestAutoMerge":{"pullRequest":{"autoMergeRequest":{"enabledAt":"2026-11-28T12:00:00Z"}}}}}""")
			.State.Should().Be(AutoMergeState.Enabled);

	[Theory]
	[InlineData("Pull request Pull request is in clean status")]
	[InlineData("Pull request Pull request is in unstable status")]
	public void AMergeablePullRequest_IsAlreadyMergeable(string message)
	{
		var result = OctokitGitHubWriteApi.ParseAutoMergeResponse(
			$$"""{"data":{"enablePullRequestAutoMerge":null},"errors":[{"type":"UNPROCESSABLE","message":"{{message}}"}]}""");

		result.State.Should().Be(AutoMergeState.AlreadyMergeable);
		result.Message.Should().Be(message);
	}

	[Fact]
	public void AnyOtherError_IsARefusal()
	{
		var result = OctokitGitHubWriteApi.ParseAutoMergeResponse(
			"""{"data":{"enablePullRequestAutoMerge":null},"errors":[{"message":"Pull request Auto merge is not allowed for this repository"}]}""");

		result.State.Should().Be(AutoMergeState.Refused);
		result.Message.Should().Contain("not allowed");
	}

	[Theory]
	[InlineData(null)]
	[InlineData("")]
	[InlineData("not json")]
	[InlineData("""{"data":{"enablePullRequestAutoMerge":null}}""")]
	public void AnAnswerThatConfirmsNothing_IsARefusal(string? json)
		=> OctokitGitHubWriteApi.ParseAutoMergeResponse(json).State.Should().Be(AutoMergeState.Refused);
}
