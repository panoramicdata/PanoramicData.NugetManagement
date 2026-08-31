namespace PanoramicData.NugetManagement.Rules;

/// <summary>
/// Concrete, model-facing instructions for fixing one rule.
/// </summary>
/// <remarks>
/// Opt-in, exactly as <see cref="IGovernsDependency"/> is, and for the same reason: most rules do not
/// need one, and a mandatory interface would be implemented badly rather than not at all.
/// <para>
/// This exists because a small model does not reason its way from a rule's prose advisory to an edit.
/// It follows instructions. So a playbook is not a description of the problem — it is a script: here is
/// the goal, here are the files, here is what "done" looks like, here is one worked example. Written
/// for a reader with no context and no judgement.
/// </para>
/// <para>
/// A rule without a playbook still works: the prompt falls back to its <c>RuleAdvisory</c>. The
/// difference is the success rate, and the opt-in integration tests are what say whether a given rule
/// needs one.
/// </para>
/// </remarks>
public interface IRuleAiPlaybook
{
	/// <summary>The rule this playbook is for.</summary>
	string RuleId { get; }

	/// <summary>
	/// What to achieve, in one imperative sentence. No context, no justification.
	/// </summary>
	string Goal { get; }

	/// <summary>
	/// The files that may need changing, relative to the repository root.
	/// </summary>
	/// <remarks>
	/// Naming them is most of the value: it stops the model exploring, and exploring is where a small
	/// model spends its turns and loses its way.
	/// </remarks>
	IReadOnlyList<string> Files { get; }

	/// <summary>
	/// What the repository looks like when the rule passes. The model's own success criterion.
	/// </summary>
	string ExpectedEndState { get; }

	/// <summary>
	/// One concrete example of the change, close enough to copy.
	/// </summary>
	string WorkedExample { get; }
}
