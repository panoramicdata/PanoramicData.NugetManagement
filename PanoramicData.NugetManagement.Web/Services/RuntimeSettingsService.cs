using System.Text.Json;
using Microsoft.Extensions.Options;
using PanoramicData.NugetManagement.Web.Models;

namespace PanoramicData.NugetManagement.Web.Services;

/// <summary>
/// Service for reading and persisting runtime-editable settings (e.g., LocalReposRoot).
/// Settings are stored in a JSON file in the user's local app data folder.
/// </summary>
public class RuntimeSettingsService
{
	private readonly Lock _lock = new();
	private readonly string _settingsPath;
	private readonly ILogger<RuntimeSettingsService> _logger;
	private readonly AppSettings _appSettings;
	private readonly RuntimeSettings _runtimeSettings;

	/// <summary>
	/// What configuration asked for, kept because <see cref="SetLocalReposRoot"/> writes the override into
	/// the live <see cref="AppSettings"/> and so destroys it. Clearing the override has to put this back,
	/// not the override it is clearing.
	/// </summary>
	private readonly string? _configuredLocalReposRoot;

	/// <summary>
	/// Initializes a new instance of the <see cref="RuntimeSettingsService"/> class.
	/// </summary>
	public RuntimeSettingsService(IOptions<AppSettings> appSettings, ILogger<RuntimeSettingsService> logger)
		: this(appSettings, logger, DefaultSettingsPath())
	{
	}

	/// <summary>
	/// Initializes a new instance of the <see cref="RuntimeSettingsService"/> class against a specific
	/// settings file.
	/// </summary>
	/// <remarks>
	/// Where the settings live is a parameter rather than a fact of the machine. It has to be: on
	/// Windows <see cref="Environment.GetFolderPath(Environment.SpecialFolder)"/> asks the Known Folder
	/// API and pays no attention to the <c>LOCALAPPDATA</c> variable, so redirecting it is not something
	/// a caller can do from the outside — and a test that believed otherwise was reading and writing the
	/// developer's own settings.
	/// </remarks>
	public RuntimeSettingsService(
		IOptions<AppSettings> appSettings,
		ILogger<RuntimeSettingsService> logger,
		string settingsPath)
	{
		_appSettings = appSettings.Value;
		_configuredLocalReposRoot = _appSettings.LocalReposRoot;
		_logger = logger;
		_settingsPath = settingsPath;

		_runtimeSettings = LoadFromDisk();
	}

	/// <summary>
	/// The settings file's location under the user's local application data.
	/// </summary>
	public static string DefaultSettingsPath() => Path.Combine(
		Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
		"PanoramicData.NugetManagement",
		"runtime-settings.json");

	/// <summary>
	/// Gets the effective LocalReposRoot: runtime override first, then AppSettings, then null.
	/// </summary>
	public string? LocalReposRoot
	{
		get
		{
			lock (_lock)
			{
				return _runtimeSettings.LocalReposRoot ?? _appSettings.LocalReposRoot;
			}
		}
	}

	/// <summary>
	/// Sets the LocalReposRoot at runtime and persists to disk.
	/// </summary>
	public void SetLocalReposRoot(string? value)
	{
		lock (_lock)
		{
			_runtimeSettings.LocalReposRoot = value;
		}

		SaveToDisk();

		// Also update the AppSettings instance so LocalRepoService picks up the change immediately.
		// Clearing restores what configuration asked for: this used to coalesce to the *current* value,
		// which meant the reset button saved null, said "Saved", and left the override in force until the
		// application was restarted.
		_appSettings.LocalReposRoot = value ?? _configuredLocalReposRoot;
	}

	/// <summary>
	/// Gets the preferred IDE identifier, or null if none is set.
	/// </summary>
	public string? PreferredIdeId
	{
		get
		{
			lock (_lock)
			{
				return _runtimeSettings.PreferredIdeId;
			}
		}
	}

	/// <summary>
	/// Gets whether informational items should be included in the AI prompt.
	/// </summary>
	public bool IncludeInfoInAiPrompt
	{
		get
		{
			lock (_lock)
			{
				return _runtimeSettings.IncludeInfoInAiPrompt;
			}
		}
	}

	/// <summary>
	/// Sets whether informational items should be included in the AI prompt and persists to disk.
	/// </summary>
	public void SetIncludeInfoInAiPrompt(bool value)
	{
		lock (_lock)
		{
			_runtimeSettings.IncludeInfoInAiPrompt = value;
		}

		SaveToDisk();
	}

	/// <summary>
	/// Sets the preferred IDE identifier at runtime and persists to disk.
	/// </summary>
	public void SetPreferredIdeId(string? value)
	{
		lock (_lock)
		{
			_runtimeSettings.PreferredIdeId = value;
		}

		SaveToDisk();
	}

	/// <summary>
	/// Gets the organisations to manage, in display order. Falls back to the single configured
	/// organisation when none have been added explicitly, so an existing single-organisation
	/// setup behaves exactly as it did before.
	/// </summary>
	public IReadOnlyList<string> Organizations
	{
		get
		{
			lock (_lock)
			{
				return _runtimeSettings.Organizations.Count > 0
					? [.. _runtimeSettings.Organizations]
					: [_appSettings.NuGetOrganization];
			}
		}
	}

	/// <summary>
	/// Adds an organisation and persists the list. Returns false when the name is blank or already
	/// present (compared case-insensitively, as GitHub organisation names are case-insensitive).
	/// </summary>
	/// <remarks>
	/// When no organisations have been persisted yet the effective list is implicitly the single
	/// configured organisation, so that one is seeded first. Without this, adding a second
	/// organisation would silently replace the configured one rather than joining it.
	/// </remarks>
	public bool AddOrganization(string name)
	{
		var trimmed = name?.Trim();
		if (string.IsNullOrEmpty(trimmed))
		{
			return false;
		}

		lock (_lock)
		{
			SeedOrganizationsFromConfigurationIfEmpty();

			if (_runtimeSettings.Organizations.Contains(trimmed, StringComparer.OrdinalIgnoreCase))
			{
				return false;
			}

			_runtimeSettings.Organizations.Add(trimmed);
		}

		SaveToDisk();
		_logger.LogInformation("Added organisation '{Organization}'.", trimmed);
		return true;
	}

	/// <summary>
	/// Whether a repository has been excluded from governance.
	/// </summary>
	/// <param name="repositoryFullName">The repository, as "owner/name".</param>
	public bool IsRepositoryExcluded(string? repositoryFullName)
	{
		if (string.IsNullOrWhiteSpace(repositoryFullName))
		{
			return false;
		}

		lock (_lock)
		{
			return _runtimeSettings.ExcludedRepositories
				.Contains(repositoryFullName, StringComparer.OrdinalIgnoreCase);
		}
	}

	/// <summary>
	/// Every excluded repository, for callers that need the whole set at once.
	/// </summary>
	public IReadOnlySet<string> ExcludedRepositories
	{
		get
		{
			lock (_lock)
			{
				return _runtimeSettings.ExcludedRepositories.ToHashSet(StringComparer.OrdinalIgnoreCase);
			}
		}
	}

	/// <summary>
	/// Excludes a repository from governance, or brings it back, and persists the decision.
	/// </summary>
	/// <param name="repositoryFullName">The repository, as "owner/name".</param>
	/// <param name="excluded">True to exclude it; false to govern it again.</param>
	public void SetRepositoryExcluded(string repositoryFullName, bool excluded)
	{
		var trimmed = repositoryFullName?.Trim();
		if (string.IsNullOrEmpty(trimmed))
		{
			return;
		}

		lock (_lock)
		{
			var existing = _runtimeSettings.ExcludedRepositories
				.FindIndex(name => string.Equals(name, trimmed, StringComparison.OrdinalIgnoreCase));

			if (excluded && existing < 0)
			{
				_runtimeSettings.ExcludedRepositories.Add(trimmed);
			}
			else if (!excluded && existing >= 0)
			{
				_runtimeSettings.ExcludedRepositories.RemoveAt(existing);
			}
			else
			{
				return;
			}
		}

		SaveToDisk();
		_logger.LogInformation(
			excluded ? "Excluded {Repository} from governance." : "Brought {Repository} back into governance.",
			trimmed);
	}

	/// <summary>
	/// Removes an organisation and persists the list. Returns false when the organisation is not
	/// present, or when it is the only one left — at least one organisation must remain, otherwise
	/// the list would fall back to the configured organisation and the removal would appear to
	/// have silently failed.
	/// </summary>
	public bool RemoveOrganization(string name)
	{
		var trimmed = name?.Trim();
		if (string.IsNullOrEmpty(trimmed))
		{
			return false;
		}

		lock (_lock)
		{
			SeedOrganizationsFromConfigurationIfEmpty();

			if (_runtimeSettings.Organizations.Count <= 1)
			{
				return false;
			}

			var match = _runtimeSettings.Organizations
				.FirstOrDefault(o => string.Equals(o, trimmed, StringComparison.OrdinalIgnoreCase));

			if (match is null)
			{
				return false;
			}

			_runtimeSettings.Organizations.Remove(match);
		}

		SaveToDisk();
		_logger.LogInformation("Removed organisation '{Organization}'.", trimmed);
		return true;
	}

	/// <summary>
	/// Materialises the implicit single-organisation default into the persisted list.
	/// Callers must already hold <see cref="_lock"/>.
	/// </summary>
	private void SeedOrganizationsFromConfigurationIfEmpty()
	{
		if (_runtimeSettings.Organizations.Count == 0 && !string.IsNullOrWhiteSpace(_appSettings.NuGetOrganization))
		{
			_runtimeSettings.Organizations.Add(_appSettings.NuGetOrganization);
		}
	}

	private RuntimeSettings LoadFromDisk()
	{
		try
		{
			if (File.Exists(_settingsPath))
			{
				var json = File.ReadAllText(_settingsPath);
				var settings = JsonSerializer.Deserialize<RuntimeSettings>(json);
				if (settings is not null)
				{
					_logger.LogInformation("Loaded runtime settings from {Path}", _settingsPath);

					// A settings file written before multi-organisation support, or one hand-edited to
					// "Organizations": null, would otherwise leave the list null and throw on first use.
					settings.Organizations ??= [];

					// Apply the persisted LocalReposRoot to AppSettings so
					// LocalRepoService uses it from the start
					if (settings.LocalReposRoot is not null)
					{
						_appSettings.LocalReposRoot = settings.LocalReposRoot;
					}

					return settings;
				}
			}
		}
		catch (Exception ex)
		{
			_logger.LogWarning(ex, "Failed to load runtime settings from {Path}", _settingsPath);
		}

		return new RuntimeSettings();
	}

	private void SaveToDisk()
	{
		try
		{
			var dir = Path.GetDirectoryName(_settingsPath)!;
			if (!Directory.Exists(dir))
			{
				Directory.CreateDirectory(dir);
			}

			RuntimeSettings snapshot;
			lock (_lock)
			{
				// Every property of RuntimeSettings must be copied here. This snapshot is what gets
				// written, so a property left out is not merely stale on disk — it is erased, and erased
				// by every unrelated setter too, since they all save the whole file. ExcludedRepositories
				// was missing, so excluding a repository appeared to work and was gone by the next run.
				snapshot = new RuntimeSettings
				{
					LocalReposRoot = _runtimeSettings.LocalReposRoot,
					PreferredIdeId = _runtimeSettings.PreferredIdeId,
					IncludeInfoInAiPrompt = _runtimeSettings.IncludeInfoInAiPrompt,
					Organizations = [.. _runtimeSettings.Organizations],
					ExcludedRepositories = [.. _runtimeSettings.ExcludedRepositories]
				};
			}

			var json = JsonSerializer.Serialize(snapshot, new JsonSerializerOptions { WriteIndented = true });
			File.WriteAllText(_settingsPath, json);
			_logger.LogInformation("Saved runtime settings to {Path}", _settingsPath);
		}
		catch (Exception ex)
		{
			_logger.LogError(ex, "Failed to save runtime settings to {Path}", _settingsPath);
		}
	}
}

/// <summary>
/// Runtime-editable settings persisted to a local JSON file.
/// </summary>
public class RuntimeSettings
{
	/// <summary>
	/// The local root directory where sibling repos are cloned.
	/// </summary>
	public string? LocalReposRoot { get; set; }

	/// <summary>
	/// The ID of the user's preferred IDE (e.g. "vs2022-professional", "vscode").
	/// </summary>
	public string? PreferredIdeId { get; set; }

	/// <summary>
	/// The GitHub/NuGet organisations to manage. When empty, the single organisation configured
	/// in <see cref="AppSettings.NuGetOrganization"/> is used, so existing single-organisation
	/// setups behave exactly as they did before multi-organisation support was added.
	/// </summary>
	public List<string> Organizations { get; set; } = [];

	/// <summary>
	/// Whether to include informational (Info-severity) items in the AI prompt.
	/// Defaults to false.
	/// </summary>
	public bool IncludeInfoInAiPrompt { get; set; }

	/// <summary>
	/// Repositories excluded from governance, by full name.
	/// </summary>
	/// <remarks>
	/// Held here rather than in each repository's own config file, because the decision is ours about
	/// our estate rather than a fact about the repository — and because the repositories most in need
	/// of exclusion are the ones we cannot commit to. A package can declare a repository belonging to
	/// somebody else; that is exactly when governance must be told to leave it alone.
	/// </remarks>
	public List<string> ExcludedRepositories { get; set; } = [];
}
