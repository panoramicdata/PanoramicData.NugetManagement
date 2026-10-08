using PanoramicData.NugetManagement.Models;
using PanoramicData.NugetManagement.Services;

namespace PanoramicData.NugetManagement.Web.Remediations.CiWorkflow;

/// <summary>
/// Makes a repository's CI workflow collect coverage and upload it to Codacy.
/// </summary>
/// <remarks>
/// The rule only names a workflow and runner when the edit is safe, and this re-checks against the
/// file as it is now: the workflow editor returns null for anything it cannot edit with certainty, in
/// which case nothing is written. It adds no secret — CODACY_PROJECT_TOKEN is CQ-07's concern and
/// cannot be created by editing files.
/// </remarks>
public sealed class CodacyCoverageUploadRemediation : IRemediation
{
	/// <inheritdoc />
	public string RuleId => "CI-15";

	/// <inheritdoc />
	public bool CanRemediate(RuleResult result)
		=> !result.Passed && result.Advisory is not null && TryRead(result, out _, out _);

	/// <inheritdoc />
	public void Apply(string localPath, RuleResult result, List<string> applied, Action<string>? onOutput)
	{
		if (!TryRead(result, out var workflowFile, out var runner))
		{
			onOutput?.Invoke("⏭️ [CI-15] No safe edit was identified for this repository — nothing to do.");
			return;
		}

		var root = Path.GetFullPath(localPath);
		var fullPath = Path.GetFullPath(Path.Combine(root, workflowFile));

		// The path comes from advisory data; never let it leave the clone.
		if (!fullPath.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) || !File.Exists(fullPath))
		{
			onOutput?.Invoke($"⏭️ [CI-15] {workflowFile} is not a file in this repository — skipping.");
			return;
		}

		var edited = CodacyCoverageWorkflowEditor.AddUpload(File.ReadAllText(fullPath), runner);
		if (edited is null)
		{
			onOutput?.Invoke($"⚠️ [CI-15] {workflowFile} can no longer be edited safely and was left unchanged — it needs a person or Fix with AI.");
			return;
		}

		File.WriteAllText(fullPath, edited);
		applied.Add(workflowFile);
		onOutput?.Invoke($"✅ [CI-15] {workflowFile} now collects coverage and uploads it to Codacy (needs the CODACY_PROJECT_TOKEN secret — see CQ-07)");
	}

	private static bool TryRead(RuleResult result, out string workflowFile, out CoverageRunner runner)
	{
		workflowFile = string.Empty;
		runner = default;

		var data = result.Advisory?.Data;
		if (data is null
			|| !data.TryGetValue("workflow_file", out var file) || file is not string path
			|| !data.TryGetValue("coverage_runner", out var kind) || kind is not string runnerName)
		{
			return false;
		}

		switch (runnerName)
		{
			case "mtp":
				runner = CoverageRunner.MicrosoftTestingPlatform;
				break;
			case "vstest":
				runner = CoverageRunner.VsTest;
				break;
			default:
				return false;
		}

		workflowFile = path;
		return true;
	}
}
