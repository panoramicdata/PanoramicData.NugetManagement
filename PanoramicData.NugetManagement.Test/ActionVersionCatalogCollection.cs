namespace PanoramicData.NugetManagement.Test;

/// <summary>
/// Serialises the test classes that substitute the process-wide
/// <see cref="PanoramicData.NugetManagement.Services.ActionVersionCatalog.Default"/>.
/// </summary>
/// <remarks>
/// <para>
/// The rules read <c>Default</c> at evaluation time, so a test can seed a floor by assigning it —
/// but the assignment is process-wide, and xUnit runs test classes in parallel. One class installing
/// its own catalog part-way through another's test silently changes the floor that test is asserting
/// against: CI-12's round trip seeds codeql-action at v4, another class installs an empty catalog a
/// moment later, the floor drops to v0, and a fixture pinned at v2 is suddenly compliant. It passed
/// repeatedly before it failed, which is the worst way for this to be found.
/// </para>
/// <para>
/// Naming one collection puts these classes in the same parallelisation group, so they run one after
/// another and each one's substitution lasts as long as it is needed. The rest of the suite still
/// runs in parallel around them.
/// </para>
/// </remarks>
[CollectionDefinition(Name)]
public sealed class ActionVersionCatalogCollection
{
	/// <summary>The collection name. Referenced by every class that assigns the shared catalog.</summary>
	public const string Name = "ActionVersionCatalog";
}
