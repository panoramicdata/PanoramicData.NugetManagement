namespace PanoramicData.NugetManagement.Web.Models;

/// <summary>
/// A step in the per-repository workflow, in the order the toolbar offers them: sync the clone,
/// assess it, fix what can be fixed, build, test, push, publish.
/// </summary>
/// <remarks>
/// The declaration order is the workflow order and is relied upon — <see cref="Services.WorkflowGate"/>
/// compares steps to decide what a queued step puts out of reach. Reordering these members reorders
/// the workflow.
/// </remarks>
public enum WorkflowStep
{
	/// <summary>Fetch, rebase and bring the local clone into line with origin.</summary>
	GitSync,

	/// <summary>Re-run the governance rules against the repository.</summary>
	Reassess,

	/// <summary>Apply the automatic remediations for the rules that failed.</summary>
	Fix,

	/// <summary>
	/// Hand the failures to an AI agent instead. The same point in the workflow as <see cref="Fix"/>.
	/// </summary>
	FixWithAi,

	/// <summary>Build the solution.</summary>
	Build,

	/// <summary>Run the unit tests.</summary>
	Test,

	/// <summary>Commit what changed, then fetch, rebase and push to origin.</summary>
	CommitAndPush,

	/// <summary>Tag and publish the package to NuGet.</summary>
	Publish
}
