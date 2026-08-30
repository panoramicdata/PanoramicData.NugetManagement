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
	}
}
