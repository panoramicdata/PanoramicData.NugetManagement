using PanoramicData.NugetManagement.Models;
using PanoramicData.NugetManagement.Web.Services;

namespace PanoramicData.NugetManagement.Test;

/// <summary>
/// Tests for <see cref="IssueFixPrompt"/>: what the writing model is told about an issue, which is
/// never the issue.
/// </summary>
/// <remarks>
/// The brief is a paraphrase produced by a model that held no tools. Building the task from its fields
/// — and from nothing else — is the step that keeps a stranger's own sentences away from the model
/// that can write to the clone. A test that the prose does not reach it is therefore a test of the
/// design and not of the formatting.
/// </remarks>
public class IssueFixPromptTests(ITestOutputHelper output) : TestWithOutput(output)
{
	private static readonly IssueFixBrief Brief = new(
		"Expose GetDocumentsAsync on the client interface.",
		["src/Documents.cs"],
		"The interface declares it and the implementation forwards to the existing private method.",
		null);

	[Fact]
	public void BuildTask_StatesTheGoalAndTheEndState()
	{
		var task = IssueFixPrompt.BuildTask(Brief, "panoramicdata/OpenProject.Api", 1);

		task.Should().Contain("Expose GetDocumentsAsync on the client interface.")
			.And.Contain("The interface declares it");
	}

	[Fact]
	public void BuildTask_NamesTheFilesTheSessionMayWrite()
	{
		var task = IssueFixPrompt.BuildTask(Brief, "panoramicdata/OpenProject.Api", 1);

		task.Should().Contain("src/Documents.cs");
	}

	[Fact]
	public void BuildTask_CarriesNoneOfTheReportersOwnWords()
	{
		var task = IssueFixPrompt.BuildTask(Brief, "panoramicdata/OpenProject.Api", 1);

		task.Should().NotContain("UNTRUSTED",
			"there is nothing untrusted left to fence off: everything here was written by the "
				+ "analysis, which is the whole point of passing a brief rather than a body");
	}

	[Fact]
	public void BuildTask_SaysTheChangeWillBeReviewedRatherThanTrusted()
	{
		var task = IssueFixPrompt.BuildTask(Brief, "panoramicdata/OpenProject.Api", 1);

		task.Should().Contain("review",
			"this is the one fix path with no rule to check it, so the model is told a human is the "
				+ "check — a model that believes it is the last word writes more ambitiously");
	}
}
