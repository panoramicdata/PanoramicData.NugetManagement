using System.Runtime.CompilerServices;
using PanoramicData.NugetManagement.Services;

namespace PanoramicData.NugetManagement.Test;

/// <summary>
/// Substitutes in-memory instances for the process-wide NuGet stores before any test can trigger
/// <see cref="RuleRegistry.Rules"/>'s first (and only) construction, so this test binary never reads
/// or writes the committed <c>nuget-floors.json</c> / <c>nuget-versions.json</c> files at the
/// repository root.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="RuleRegistry.Rules"/> is a process-wide <c>Lazy&lt;T&gt;</c>: whichever test runs first
/// triggers its one-time construction via reflection, and each PKG-05/06/07 rule
/// (<c>NuGetPackageUpdateRuleBase</c>) captures <see cref="NuGetVersionCache.Default"/> and
/// <see cref="NuGetFloorCatalog.Default"/> into a private readonly field at that moment. Substituting
/// <c>Default</c> later — from a test-class constructor, or a collection or class fixture that runs
/// once the suite is already under way — therefore has no effect: the rule instances already baked
/// into <see cref="RuleRegistry.Rules"/> keep referencing whatever store existed at that first access,
/// which by default resolves to the real committed files. A fixture scoped to
/// <see cref="SelfAssessmentTests"/>, <see cref="GitHubIntegrationTests"/> and
/// <see cref="FailArmyTests"/> was tried first and did not stop the pollution, for exactly this
/// reason: some other test class (for example one that merely enumerates
/// <see cref="RuleRegistry.Rules"/> for its rule ids or metadata) can win the race and construct the
/// registry against the real stores before the fixture ever runs.
/// </para>
/// <para>
/// A module initializer runs once when this test assembly loads — before test discovery, before any
/// test, and therefore before <see cref="RuleRegistry.Rules"/> can be touched — so it is the only
/// substitution point guaranteed to win that race. Both stores are constructed with a null path, per
/// their own documentation ("Assignable so tests can substitute an in-memory instance constructed
/// with a null path that never writes to the committed file"), so this test binary never performs
/// disk I/O against either committed file, regardless of test order or parallelism.
/// </para>
/// <para>
/// This buys freedom from disk I/O, not freedom from shared state: the two stores installed here are
/// each a single instance, shared by every rule-evaluating test in the process for the whole run, in
/// memory, exactly like <see cref="RuleRegistry.Rules"/> itself. There is no
/// <c>[CollectionBehavior(DisablesTestParallelization = true)]</c> in this project, so one test's
/// <c>Observe</c> call (for example <see cref="GitHubIntegrationTests"/> assessing the live
/// <c>main</c> branch, which can pin a package higher than this worktree does) can raise the shared
/// in-memory floor before another test in the same run reads it. Any assertion over a whole
/// assessment's pass/fail outcome must therefore still exclude the floor- and grace-dependent rules
/// (PKG-05/06/07) rather than assume this fixture makes their result depend only on the repository
/// under test — see the <c>_graceDependentRuleIds</c> filtering in <see cref="SelfAssessmentTests"/>
/// and <see cref="GitHubIntegrationTests"/>.
/// </para>
/// </remarks>
internal static class RuleAssessmentIsolation
{
	/// <summary>
	/// Runs once, before any test, to install the in-memory stores.
	/// </summary>
	[ModuleInitializer]
	internal static void UseInMemoryStores()
	{
		NuGetVersionCache.Default = new NuGetVersionCache(null);
		NuGetFloorCatalog.Default = new NuGetFloorCatalog(null);
		// CI-12 observes every action in every workflow of every repository a test assesses, so
		// without this the committed action-versions.json would gain an entry for each one.
		ActionVersionCatalog.Default = new ActionVersionCatalog(null);
	}
}
