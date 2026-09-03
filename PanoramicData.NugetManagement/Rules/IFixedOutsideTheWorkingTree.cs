namespace PanoramicData.NugetManagement.Rules;

/// <summary>
/// Implemented by a rule whose fix is not an edit to any file — a re-run, a credential, a setting on
/// a service — so nothing that edits the clone can address it.
/// </summary>
/// <remarks>
/// Distinct from <see cref="IRemotelyGraded"/>, and the two are easy to confuse. That one asks whether
/// a local edit can change the rule's <em>answer</em>; this one asks whether a local edit is the
/// <em>fix</em> at all. CQ-06 is graded remotely but fixed locally — you edit the file, and Codacy
/// re-grades later. CI-11 is neither: the newest tag is not on nuget.org, and no file in the clone can
/// make it so.
/// <para>
/// A rule carrying this is kept out of Fix with AI entirely, because the alternative is what happened
/// to CI-11 on AutoTask.Api: no playbook, an advisory that is by necessity a list of possible causes —
/// exhausted Actions budget, trusted publishing unconfigured, a case-sensitive filename, credentials
/// the runner lacks — and a 27b model handed all of it and asked to fix something. It read four files,
/// reasoned in circles through the list, ran a build, and changed nothing, because there was nothing
/// in the working tree to change. The advisory is a triage note for a person, and offering it to a
/// model spends GPU minutes to produce a transcript nobody can act on.
/// </para>
/// </remarks>
public interface IFixedOutsideTheWorkingTree
{
}
