using System.Text.Json.Serialization;

namespace PanoramicData.NugetManagement.Models;

/// <summary>
/// Controls whether a project is selected by policy.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ProjectTreatment
{
	/// <summary>
	/// Use built-in heuristics.
	/// </summary>
	Auto,

	/// <summary>
	/// Force include.
	/// </summary>
	Include,

	/// <summary>
	/// Force exclude.
	/// </summary>
	Exclude
}
