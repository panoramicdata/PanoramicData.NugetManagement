using System.Text.Json.Serialization;

namespace PanoramicData.NugetManagement.Models;

/// <summary>
/// Defines the default test depth for a project.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ProjectTestingLevel
{
	/// <summary>
	/// Use default behavior.
	/// </summary>
	Auto,

	/// <summary>
	/// Fast subset of tests.
	/// </summary>
	Smoke,

	/// <summary>
	/// Full test execution.
	/// </summary>
	Full,

	/// <summary>
	/// Skip tests for this project.
	/// </summary>
	None
}
