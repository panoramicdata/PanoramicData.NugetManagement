using System.Text.Json;
using System.Text.Json.Serialization;
using PanoramicData.NugetManagement.Models;

namespace PanoramicData.NugetManagement.Services;

/// <summary>
/// Parses repository-level assessment and testing overrides.
/// </summary>
public static class NugetManagementRepositoryConfigParser
{
	private static readonly JsonSerializerOptions _jsonOptions = new()
	{
		PropertyNameCaseInsensitive = true,
		ReadCommentHandling = JsonCommentHandling.Skip,
		AllowTrailingCommas = true,
		Converters = { new JsonStringEnumConverter() }
	};

	/// <summary>
	/// Parses the raw JSON string into a config instance.
	/// Returns null for null/whitespace or invalid content.
	/// </summary>
	public static NugetManagementRepositoryConfig? Parse(string? rawJson)
	{
		if (string.IsNullOrWhiteSpace(rawJson))
		{
			return null;
		}

		try
		{
			var config = JsonSerializer.Deserialize<NugetManagementRepositoryConfig>(rawJson, _jsonOptions);
			if (config is null)
			{
				return null;
			}

			config.Projects = NormalizeProjects(config.Projects);
			return config;
		}
		catch
		{
			return null;
		}
	}

	private static Dictionary<string, NugetManagementProjectConfig> NormalizeProjects(Dictionary<string, NugetManagementProjectConfig>? projects)
	{
		var normalized = new Dictionary<string, NugetManagementProjectConfig>(StringComparer.OrdinalIgnoreCase);
		if (projects is null)
		{
			return normalized;
		}

		foreach (var (key, value) in projects)
		{
			if (string.IsNullOrWhiteSpace(key))
			{
				continue;
			}

			normalized[NugetManagementRepositoryConfig.NormalizeProjectPath(key)] = value ?? new NugetManagementProjectConfig();
		}

		return normalized;
	}
}
