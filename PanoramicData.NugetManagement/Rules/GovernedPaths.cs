namespace PanoramicData.NugetManagement.Rules;

/// <summary>
/// Tells governed repository paths from test-fixture data, which is deliberately non-conformant and
/// must not be assessed or auto-fixed. Judge paths relative to the repository root being assessed.
/// </summary>
public static class GovernedPaths
{
	private static readonly string[] _fixtureFolders =
		["Fixtures", "Fixture", "TestData", "TestFiles", "TestAssets"];

	/// <summary>
	/// True when any folder in the repository-relative path is a whole-name match for a fixture folder.
	/// </summary>
	/// <param name="relativePath">The path relative to the repository root, with either separator.</param>
	public static bool IsTestFixture(string relativePath)
	{
		var segments = relativePath.Replace('\\', '/').Split('/', StringSplitOptions.None);
		return segments.Take(segments.Length - 1)
			.Any(segment => _fixtureFolders.Contains(segment, StringComparer.OrdinalIgnoreCase));
	}
}
