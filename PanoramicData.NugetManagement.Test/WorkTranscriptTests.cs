using PanoramicData.NugetManagement.Web.Models;
using PanoramicData.NugetManagement.Web.Services;

namespace PanoramicData.NugetManagement.Test;

/// <summary>
/// Tests for the per-item transcript, which is what gives a work item something of its own to show.
/// </summary>
/// <remarks>
/// Output used to go only to the one shared console, so an item's pane had nothing to render and a
/// second item running alongside interleaved its lines into the same place.
/// </remarks>
public class WorkTranscriptTests(ITestOutputHelper output) : TestWithOutput(output)
{
	[Fact]
	public void Append_KeepsLinesInTheOrderTheyWereSaid()
	{
		var transcript = new WorkTranscript();

		transcript.Append(WorkLineKind.Output, "first");
		transcript.Append(WorkLineKind.Output, "second");

		transcript.Snapshot().Select(line => line.Text).Should().Equal("first", "second");
	}

	[Fact]
	public void Append_KeepsEachLinesKind()
	{
		var transcript = new WorkTranscript();

		transcript.Append(WorkLineKind.Thinking, "weighing it up");
		transcript.Append(WorkLineKind.Model, "I will edit the csproj");

		// The pane renders a model's thinking differently from what it says.
		transcript.Snapshot().Select(line => line.Kind)
			.Should().Equal([WorkLineKind.Thinking, WorkLineKind.Model]);
	}

	[Fact]
	public void Append_PastTheCap_DropsTheOldestAndKeepsTheNewest()
	{
		// A streamed session is unbounded — a long agentic run would otherwise hold every token it ever
		// produced for as long as the item lives.
		var transcript = new WorkTranscript(capacity: 3);

		foreach (var index in Enumerable.Range(1, 5))
		{
			transcript.Append(WorkLineKind.Output, $"line {index}");
		}

		transcript.Snapshot().Select(line => line.Text).Should().Equal("line 3", "line 4", "line 5");
	}

	[Fact]
	public void Snapshot_IsNotAffectedByLaterAppends()
	{
		// The pane renders a snapshot on the UI thread while the executor is still writing on its own.
		var transcript = new WorkTranscript();
		transcript.Append(WorkLineKind.Output, "before");

		var snapshot = transcript.Snapshot();
		transcript.Append(WorkLineKind.Output, "after");

		snapshot.Select(line => line.Text).Should().Equal("before");
	}

	[Fact]
	public void AppendDelta_RunsStreamedFragmentsIntoOneLine()
	{
		// A streaming model arrives token by token. One transcript line per token would be unreadable
		// and would blow the cap in seconds.
		var transcript = new WorkTranscript();

		transcript.AppendDelta(WorkLineKind.Model, "I will ");
		transcript.AppendDelta(WorkLineKind.Model, "edit the ");
		transcript.AppendDelta(WorkLineKind.Model, "csproj");

		transcript.Snapshot().Should().ContainSingle()
			.Which.Text.Should().Be("I will edit the csproj");
	}

	[Fact]
	public void AppendDelta_StartsANewLineWhenTheKindChanges()
	{
		var transcript = new WorkTranscript();

		transcript.AppendDelta(WorkLineKind.Thinking, "the rule wants an icon");
		transcript.AppendDelta(WorkLineKind.Model, "adding one");

		transcript.Snapshot().Select(line => (line.Kind, line.Text))
			.Should().Equal(
				(WorkLineKind.Thinking, "the rule wants an icon"),
				(WorkLineKind.Model, "adding one"));
	}

	[Fact]
	public void Append_AfterDeltas_LeavesTheStreamedLineAlone()
	{
		// A tool call logged mid-stream must not be glued onto whatever the model was saying.
		var transcript = new WorkTranscript();

		transcript.AppendDelta(WorkLineKind.Model, "reading the file");
		transcript.Append(WorkLineKind.Output, "→ read_file(Acme.csproj)");
		transcript.AppendDelta(WorkLineKind.Model, "now writing");

		transcript.Snapshot().Select(line => line.Text)
			.Should().Equal("reading the file", "→ read_file(Acme.csproj)", "now writing");
	}
}

/// <summary>
/// Tests for the sink that turns a model's stream into transcript lines.
/// </summary>
public class AiTranscriptSinkTests(ITestOutputHelper output) : TestWithOutput(output)
{
	[Fact]
	public void Thinking_AndSpeech_LandAsDifferentKinds()
	{
		// The whole point of keeping them apart: the pane subdues reasoning and shows what the model
		// actually said. One kind for both would make a long think look like a long answer.
		var transcript = new WorkTranscript();
		var sink = AiTranscriptSink.For(transcript);

		sink(new AiStreamDelta(AiDeltaKind.Thinking, "weighing it up"));
		sink(new AiStreamDelta(AiDeltaKind.Content, "editing the csproj"));

		transcript.Snapshot().Select(line => (line.Kind, line.Text)).Should().Equal(
			(WorkLineKind.Thinking, "weighing it up"),
			(WorkLineKind.Model, "editing the csproj"));
	}

	[Fact]
	public void ConsecutiveFragments_OfOneKind_BecomeOneLine()
	{
		var transcript = new WorkTranscript();
		var sink = AiTranscriptSink.For(transcript);

		sink(new AiStreamDelta(AiDeltaKind.Content, "editing "));
		sink(new AiStreamDelta(AiDeltaKind.Content, "the csproj"));

		transcript.Snapshot().Should().ContainSingle().Which.Text.Should().Be("editing the csproj");
	}
}
