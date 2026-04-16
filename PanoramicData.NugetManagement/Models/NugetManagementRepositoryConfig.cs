namespace PanoramicData.NugetManagement.Models;

/// <summary>
/// Optional repository-level overrides loaded from PanoramicData.NugetManagement.config.json.
/// </summary>
public sealed class NugetManagementRepositoryConfig
{
	/// <summary>
	/// Config file name expected at repository root.
	/// </summary>
	public const string FileName = "PanoramicData.NugetManagement.config.json";

	/// <summary>
	/// Optional JSON schema URI.
	/// </summary>
	public string? Schema { get; set; }

	/// <summary>
	/// Config version.
	/// </summary>
	public int Version { get; set; } = 1;

	/// <summary>
	/// Per-project settings keyed by relative project path or project file name.
	/// </summary>
	public Dictionary<string, NugetManagementProjectConfig> Projects { get; set; } = [];

	/// <summary>
	/// Returns the matching project configuration for a relative path.
	/// </summary>
	public NugetManagementProjectConfig? GetProjectConfig(string relativePath)
	{
		if (Projects.Count == 0)
		{
			return null;
		}

		var normalized = NormalizeProjectPath(relativePath);
		if (Projects.TryGetValue(normalized, out var exact))
		{
			return exact;
		}

		var fileName = Path.GetFileName(normalized);
		return Projects.TryGetValue(fileName, out var byFileName) ? byFileName : null;
	}

	/// <summary>
	/// Normalizes project path keys to a stable forward-slash form.
	/// </summary>
	public static string NormalizeProjectPath(string path)
		=> path.Replace('\\', '/').TrimStart('/').Trim();
}

/// <summary>
/// Per-project behavior overrides.
/// </summary>
public sealed class NugetManagementProjectConfig
{
	/// <summary>
	/// Controls default rule/remediation project selection.
	/// </summary>
	public ProjectTreatment Treatment { get; set; } = ProjectTreatment.Auto;

	/// <summary>
	/// Controls whether this project is selected for test-oriented operations.
	/// </summary>
	public ProjectTreatment TestingTreatment { get; set; } = ProjectTreatment.Auto;

	/// <summary>
	/// Default testing level for this project.
	/// </summary>
	public ProjectTestingLevel DefaultTestingLevel { get; set; } = ProjectTestingLevel.Auto;

	/// <summary>
	/// Optional treatment rules for test collections.
	/// </summary>
	public CollectionTreatment[] CollectionTreatments { get; set; } = [];

	/// <summary>
	/// Optional treatment rules for individual tests (matched against FullyQualifiedName).
	/// </summary>
	public TestTreatment[] TestTreatments { get; set; } = [];
}

/// <summary>
/// Treatment rule for a test collection.
/// </summary>
public sealed class CollectionTreatment
{
	/// <summary>
	/// Collection name (e.g. Category/TestCategory value).
	/// </summary>
	public string Name { get; set; } = string.Empty;

	/// <summary>
	/// Whether to include/exclude this collection, or use auto behavior.
	/// </summary>
	public ProjectTreatment Treatment { get; set; } = ProjectTreatment.Auto;
}

/// <summary>
/// Treatment rule for an individual test ID/pattern.
/// </summary>
public sealed class TestTreatment
{
	/// <summary>
	/// Test identifier/pattern matched against FullyQualifiedName.
	/// </summary>
	public string Id { get; set; } = string.Empty;

	/// <summary>
	/// Whether to include/exclude this test, or use auto behavior.
	/// </summary>
	public ProjectTreatment Treatment { get; set; } = ProjectTreatment.Auto;
}
