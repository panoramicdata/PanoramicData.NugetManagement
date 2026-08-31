using System.Text;
using Ollama.Api;
using PanoramicData.NugetManagement.Web.Models;
using PanoramicData.NugetManagement.Web.Services;

namespace PanoramicData.NugetManagement.Test;

/// <summary>
/// End-to-end tests of an AI fix against a real Ollama server and a real model.
/// </summary>
/// <remarks>
/// Everything else in this suite tests the harness against a scripted model, which proves the loop and
/// says nothing about whether the prompt works. These are the tests that answer that, and the answer
/// depends on the model — so they are staged deliberately: reachability, then tool calling, then a real
/// fix. A failure in the first says the server is wrong; a failure in the second says the model cannot
/// use tools at all; only a failure in the third is about the prompt.
/// <para>
/// Skipped unless <c>Ollama:BaseUrl</c> and <c>Ollama:Model</c> are configured, so a developer without a
/// server sees them skipped rather than red.
/// </para>
/// </remarks>
public class AiFixIntegrationTests(ITestOutputHelper output) : TestWithOutput(output)
{
	/// <summary>
	/// Whether a server and model are configured. Referenced by <c>SkipUnless</c> on each test.
	/// </summary>
	public static bool IsOllamaConfigured => OllamaIntegrationSettings.IsConfigured;

	private static OllamaClient CreateClient()
		=> new(new OllamaClientOptions
		{
			Uri = new Uri(OllamaIntegrationSettings.BaseUrl),
			ApiKey = OllamaIntegrationSettings.ApiKey,
			Timeout = TimeSpan.FromMinutes(10)
		});

	private static IChatModel CreateModel(OllamaClient client)
		=> new OllamaChatModel(
			client,
			OllamaIntegrationSettings.Model,
			OllamaIntegrationSettings.ContextWindow);

	/// <summary>
	/// The configured model answers at all. If this fails, the URL, the key or the model name is wrong
	/// and nothing below it is worth reading.
	/// </summary>
	[Fact(SkipUnless = nameof(IsOllamaConfigured), Skip = "Ollama:BaseUrl and Ollama:Model are not configured")]
	public async Task TheModelIsReachable()
	{
		using var client = CreateClient();

		// With a sink, so this also covers the streaming path — the one piece of the model port that
		// cannot be tested without a server, since Ollama.Api builds its own HttpClient and there is
		// nowhere to hang a stub handler.
		var streamed = new List<AiStreamDelta>();

		var turn = await CreateModel(client)
			.NextAsync(
				"Answer in one word.",
				[new AiMessage("user", "Say the word ready.")],
				[],
				streamed.Add,
				TestContext.Current.CancellationToken)
			.ConfigureAwait(true);

		Output.WriteLine($"Model said: {turn.Text}");
		Output.WriteLine($"Streamed {streamed.Count} fragment(s), "
			+ $"{streamed.Count(d => d.Kind == AiDeltaKind.Thinking)} of them thinking.");

		turn.Should().NotBeNull();
		streamed.Should().NotBeEmpty("a streamed turn that reports no fragments is not streaming");
		string.Concat(streamed.Where(d => d.Kind == AiDeltaKind.Content).Select(d => d.Text))
			.Should().Be(turn.Text ?? string.Empty,
				"the fragments have to add up to the answer, or the pane and the result disagree");
	}

	/// <summary>
	/// The model can call one of our tools. If this fails, the model does not do tool calling usefully
	/// and no prompt will save it — try a different one.
	/// </summary>
	[Fact(SkipUnless = nameof(IsOllamaConfigured), Skip = "Ollama:BaseUrl and Ollama:Model are not configured")]
	public async Task TheModelCanCallATool()
	{
		using var copy = FailArmyWorkingCopy.Create();
		using var client = CreateClient();

		var turn = await CreateModel(client)
			.NextAsync(
				AiFixPrompt.SystemPrompt,
				[new AiMessage("user", "List the files in this repository. Use the list_files tool.")],
				AiFixSession.ToolSpecs,
				null,
				TestContext.Current.CancellationToken)
			.ConfigureAwait(true);

		Output.WriteLine($"Text: {turn.Text}");
		Output.WriteLine($"Tool calls: {string.Join(", ", turn.ToolCalls.Select(c => c.Name))}");

		turn.ToolCalls.Should().NotBeEmpty(
			$"{OllamaIntegrationSettings.Model} must be able to call a tool for any of this to work");
	}

	/// <summary>
	/// META-04 fails against a fresh copy of the fixture, the model fixes it, and the rule then passes.
	/// This is the test that says whether the prompt works.
	/// </summary>
	[Fact(SkipUnless = nameof(IsOllamaConfigured), Skip = "Ollama:BaseUrl and Ollama:Model are not configured")]
	public async Task ItFixesTheMissingPackageProjectUrl()
		=> await AssertFixesRuleAsync("META-04").ConfigureAwait(true);

	/// <summary>
	/// The same, for META-05, which needs two edits in one file that have to agree — a harder ask.
	/// </summary>
	[Fact(SkipUnless = nameof(IsOllamaConfigured), Skip = "Ollama:BaseUrl and Ollama:Model are not configured")]
	public async Task ItFixesTheMissingPackageIcon()
		=> await AssertFixesRuleAsync("META-05").ConfigureAwait(true);

	/// <summary>
	/// Runs a real AI fix for one rule against a throwaway copy of the fixture, and asserts the rule
	/// goes from failing to passing.
	/// </summary>
	/// <param name="ruleId">The rule to fix.</param>
	private async Task AssertFixesRuleAsync(string ruleId)
	{
		var cancellationToken = TestContext.Current.CancellationToken;

		using var copy = FailArmyWorkingCopy.Create();
		using var client = CreateClient();

		// The fixture has to start out failing, or the assertion at the end proves nothing.
		var before = await copy.EvaluateAsync(ruleId, cancellationToken).ConfigureAwait(true);
		before.Passed.Should().BeFalse($"{ruleId} must fail against a fresh fixture for this test to mean anything");

		var transcript = new StringBuilder();
		var playbook = new AiPlaybookRegistry().For(ruleId);

		Output.WriteLine($"Playbook: {(playbook is null ? "none — using the advisory fallback" : playbook.GetType().Name)}");

		var options = new AiFixOptions { MaxTurnsPerAttempt = 12, MaxAttempts = 3 };

		var session = new AiFixSession(
			CreateModel(client),
			new AiFixToolbox(copy.Root),
			options,
			line =>
			{
				transcript.AppendLine(line);
				Output.WriteLine(line);
			});

		var request = new AiFixRequest(
			"panoramicdata/PanoramicData.NugetFailArmy",
			ruleId,
			before.RuleName,
			AiFixPrompt.BuildTask(before, "panoramicdata/PanoramicData.NugetFailArmy", playbook),
			AiFixPrompt.SystemPrompt);

		var outcome = await session
			.RunAsync(
				request,
				async token =>
				{
					var result = await copy.EvaluateAsync(ruleId, token).ConfigureAwait(false);
					return new AiRuleCheck(result.Passed, result.Message);
				},
				cancellationToken)
			.ConfigureAwait(true);

		var after = await copy.EvaluateAsync(ruleId, cancellationToken).ConfigureAwait(true);

		Output.WriteLine($"Outcome: succeeded={outcome.Succeeded} attempts={outcome.Attempts}");
		Output.WriteLine($"After: {after.Message}");

		after.Passed.Should().BeTrue(
			$"{OllamaIntegrationSettings.Model} did not satisfy {ruleId} in {options.MaxAttempts} attempt(s). "
			+ $"That is a fact about the prompt or the model, not about the harness. Transcript:\n{transcript}");

		// A satisfied rule is not the same as a sane edit. Both of these rules are fixed by adding a
		// property to a project file, so the file's existing contents must survive — a model that
		// replaced it with a minimal project containing only the new property would pass the rule above
		// and have destroyed the repository.
		AssertTheProjectFileWasEditedNotReplaced(copy, transcript.ToString());
	}

	/// <summary>
	/// The fixture's project file still contains what it contained before.
	/// </summary>
	/// <param name="copy">The working copy.</param>
	/// <param name="transcript">The session transcript, quoted on failure.</param>
	private static void AssertTheProjectFileWasEditedNotReplaced(FailArmyWorkingCopy copy, string transcript)
	{
		var projectPath = Directory
			.EnumerateFiles(copy.Root, "*.csproj", SearchOption.AllDirectories)
			.Single();

		var project = File.ReadAllText(projectPath);

		project.Should().Contain("Newtonsoft.Json", $"the existing package references must survive.\n{transcript}");
		project.Should().Contain("RestSharp", $"all of them, not just the first.\n{transcript}");
		project.Should().Contain("IsPackable", $"the properties that were already there must survive.\n{transcript}");
		project.Should().Contain("TargetFramework", $"including the one that makes it a project at all.\n{transcript}");
	}
}
