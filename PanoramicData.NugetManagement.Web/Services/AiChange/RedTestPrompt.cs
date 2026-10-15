namespace PanoramicData.NugetManagement.Web.Services.AiChange;

/// <summary>
/// The task given to the model when it must reproduce a bug before fixing it.
/// </summary>
public static class RedTestPrompt
{
	/// <summary>
	/// Builds the prompt.
	/// </summary>
	/// <param name="request">What is being fixed.</param>
	/// <param name="issueText">The report, as written.</param>
	/// <param name="feedback">Why the previous attempt was rejected, or null on the first.</param>
	public static string Build(AiChangeRequest request, string issueText, string? feedback)
	{
		var lines = new List<string>
		{
			$"Write ONE automated test that reproduces this reported problem in {request.Owner}/{request.Name}.",
			"The test must fail while the problem is present. Do not change any production code in this step.",
			string.Empty,
			"The report:",
			issueText,
			string.Empty,
			AiTestGuide.Text
		};

		if (feedback is not null)
		{
			lines.Add(string.Empty);
			lines.Add("Your previous attempt was rejected:");
			lines.Add(feedback);
		}

		lines.Add(string.Empty);
		lines.Add("When the test is written and compiles, finish with a last line of the form:");
		lines.Add("FILTER: <a dotnet test --filter expression that selects only your new test>");

		return string.Join('\n', lines);
	}
}
