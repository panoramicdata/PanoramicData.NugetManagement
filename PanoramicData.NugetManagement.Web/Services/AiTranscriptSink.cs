using PanoramicData.NugetManagement.Web.Models;

namespace PanoramicData.NugetManagement.Web.Services;

/// <summary>
/// Turns a model's stream into transcript lines.
/// </summary>
/// <remarks>
/// The one place the model's two channels are mapped onto the transcript's kinds. Separate from both
/// so the session need not know what a transcript is, and the transcript need not know that models
/// exist.
/// </remarks>
public static class AiTranscriptSink
{
	/// <summary>
	/// A sink that writes a model's fragments into the given transcript.
	/// </summary>
	/// <param name="transcript">The transcript to write to.</param>
	public static Action<AiStreamDelta> For(WorkTranscript transcript)
		=> delta => transcript.AppendDelta(
			delta.Kind switch
			{
				AiDeltaKind.Thinking => WorkLineKind.Thinking,
				_ => WorkLineKind.Model
			},
			delta.Text);
}
