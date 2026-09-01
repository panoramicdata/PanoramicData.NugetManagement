namespace PanoramicData.NugetManagement.Rules;

/// <summary>
/// Implemented by a rule whose verdict comes from a service reading the published default branch, and
/// so cannot answer a question about the working tree.
/// </summary>
/// <remarks>
/// Opt-in, as <see cref="IGovernsDependency"/> and <see cref="IRuleAiPlaybook"/> are, and for a sharper
/// reason than either: Fix with AI's whole loop is "edit the clone, re-run the rule, retry until it
/// passes". For a rule that asks Codacy about <c>main</c>, that loop cannot terminate in success. A
/// perfect fix leaves the rule saying exactly what it said before, the session spends every attempt,
/// reports failure, and the revert-on-failure path then throws the work away. The model was never
/// wrong; the question was unanswerable.
/// <para>
/// So a rule that says this is checked differently: the fix succeeds when the clone changed and still
/// builds, and the verdict is left to the next assessment after the change is pushed and re-analysed.
/// Retries are pointless too, because there is no fresh correction to feed back — the rule's message is
/// the same on every attempt.
/// </para>
/// <para>
/// This is not "the rule is expensive" or "the rule calls the network". <c>NuGetPackageUpdateRuleBase</c>
/// calls nuget.org and still reads the clone's project files, so it answers about the working tree and
/// must not implement this. The test is whether editing a file locally can change the answer.
/// </para>
/// </remarks>
public interface IRemotelyGraded
{
}
