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
	/// Gets whether Publish is offered for a repository that built but was never tested.
	/// </summary>
	public bool AllowPublishWithoutTests
	{
		get
		{
			lock (_lock)
			{
				return _runtimeSettings.AllowPublishWithoutTests;
			}
		}
	}

	/// <summary>
	/// Sets whether Publish is offered for a repository that built but was never tested, and persists
	/// to disk.
	/// </summary>
	public void SetAllowPublishWithoutTests(bool value)
	{
		lock (_lock)
		{
			_runtimeSettings.AllowPublishWithoutTests = value;
		}

		SaveToDisk();
	}

	/// <summary>How many repository lanes may run at once.</summary>
	public int MaxConcurrentLanes
	{
		get
		{
			lock (_lock)
			{
				return Math.Max(1, _runtimeSettings.MaxConcurrentLanes ?? 20);
			}
		}
	}

	/// <summary>
	/// The Ollama configuration, never null: an unconfigured installation gets defaults whose
	/// <see cref="OllamaOptions.IsConfigured"/> is false, so callers ask that rather than checking for
	/// null.
	/// </summary>
	public OllamaOptions Ollama
	{
		get
		{
			lock (_lock)
			{
				return _runtimeSettings.Ollama ?? new OllamaOptions();
			}
		}
	}

	/// <summary>
	/// Sets the Ollama configuration and persists it.
	/// </summary>
	/// <param name="value">The new configuration; numeric fields are clamped to workable ranges.</param>
	public void SetOllama(OllamaOptions value)
	{
		ArgumentNullException.ThrowIfNull(value);

		lock (_lock)
		{
			_runtimeSettings.Ollama = value.Normalised();
		}

		SaveToDisk();
	}

	/// <summary>Sets the lane concurrency cap and persists it.</summary>
	/// <param name="value">The new cap; values below one are clamped.</param>
	public void SetMaxConcurrentLanes(int value)
	{
		lock (_lock)
		{
			_runtimeSettings.MaxConcurrentLanes = Math.Max(1, value);
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
				.Contains(repositoryFullName, StringComparer.OrdinalIgnoreCase)
				|| _autoExcluded.ContainsKey(repositoryFullName);
		}
	}

	/// <summary>
	/// Repositories excluded because of what GitHub says they are — "archived" or "fork" — rather than
	/// because anyone chose to. Held in memory only and re-derived from the rows on every discovery and
	/// reconciliation, so it is deliberately absent from the persisted snapshot: a repository that is
	/// unarchived must come back without anyone editing a file.
	/// </summary>
	private readonly Dictionary<string, string> _autoExcluded = new(StringComparer.OrdinalIgnoreCase);

	/// <summary>
	/// Records, or clears, the reason a repository is excluded by what it is.
	/// </summary>
	/// <param name="repositoryFullName">The repository, as "owner/name".</param>
	/// <param name="reason">"archived" or "fork"; null when it is neither.</param>
	public void SetAutoExcluded(string repositoryFullName, string? reason)
	{
		lock (_lock)
		{
			if (reason is null)
			{
				_autoExcluded.Remove(repositoryFullName);
			}
			else
			{
				_autoExcluded[repositoryFullName] = reason;
			}
		}
	}

	/// <summary>
	/// Why a repository is excluded without having been excluded by the user, or null when it is not.
	/// </summary>
	/// <param name="repositoryFullName">The repository, as "owner/name".</param>
	public string? AutoExclusionReason(string? repositoryFullName)
	{
		if (string.IsNullOrWhiteSpace(repositoryFullName))
		{
			return null;
		}

		lock (_lock)
		{
			return _autoExcluded.GetValueOrDefault(repositoryFullName);
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
	/// The application-wide licence default set on the settings page, or null to use configuration.
	/// </summary>
	public LicenseOverride? DefaultLicense
	{
		get
		{
			lock (_lock)
			{
				return Copy(_runtimeSettings.DefaultLicense);
			}
		}
	}

	/// <summary>
	/// Sets the application-wide licence default and persists it; blank clears it.
	/// </summary>
	/// <param name="value">The default, or null.</param>
	public void SetDefaultLicense(LicenseOverride? value)
	{
		lock (_lock)
		{
			_runtimeSettings.DefaultLicense = value?.Normalised();
		}

		SaveToDisk();
	}

	/// <summary>
	/// An organisation's licence override, or null when it inherits.
	/// </summary>
	/// <param name="organization">The organisation name.</param>
	public LicenseOverride? GetOrganizationLicense(string? organization)
	{
		lock (_lock)
		{
			return Lookup(_runtimeSettings.OrganizationLicenses, organization);
		}
	}

	/// <summary>
	/// Sets an organisation's licence override and persists it; blank clears it.
	/// </summary>
	/// <param name="organization">The organisation name.</param>
	/// <param name="value">The override, or null.</param>
	public void SetOrganizationLicense(string organization, LicenseOverride? value)
		=> SetOverride(_runtimeSettings.OrganizationLicenses, organization, value);

	/// <summary>
	/// A repository's licence override, or null when it inherits.
	/// </summary>
	/// <param name="repositoryFullName">The repository, as "owner/name".</param>
	public LicenseOverride? GetRepositoryLicense(string? repositoryFullName)
	{
		lock (_lock)
		{
			return Lookup(_runtimeSettings.RepositoryLicenses, repositoryFullName);
		}
	}

	/// <summary>
	/// Sets a repository's licence override and persists it; blank clears it.
	/// </summary>
	/// <param name="repositoryFullName">The repository, as "owner/name".</param>
	/// <param name="value">The override, or null.</param>
	public void SetRepositoryLicense(string repositoryFullName, LicenseOverride? value)
		=> SetOverride(_runtimeSettings.RepositoryLicenses, repositoryFullName, value);

	/// <summary>
	/// The licence a repository is held to: its own override, else its owner's, else the application
	/// default, else configuration.
	/// </summary>
	/// <remarks>
	/// The LICENSE marker comes from the level that supplied the licence or a more specific one, so an
	/// organisation's marker never follows its licence onto a repository that has its own.
	/// </remarks>
	/// <param name="repositoryFullName">The repository, as "owner/name".</param>
	public EffectiveLicense ResolveLicense(string? repositoryFullName)
		=> Resolve(repositoryFullName, LicenseSource.Repository);

	/// <summary>
	/// What a level would inherit if it set nothing: the resolution of the levels below it.
	/// </summary>
	/// <param name="repositoryFullName">The repository, or for an organisation any "owner/" name.</param>
	/// <param name="level">The level asking; must be above <see cref="LicenseSource.Configuration"/>.</param>
	public EffectiveLicense ResolveInherited(string? repositoryFullName, LicenseSource level)
		=> Resolve(repositoryFullName, level == LicenseSource.Configuration ? level : level - 1);

	private EffectiveLicense Resolve(string? repositoryFullName, LicenseSource mostSpecific)
	{
		var owner = repositoryFullName?.Split('/')[0];
		var allLevels = new List<(LicenseSource Source, LicenseOverride? Override)>
		{
			(LicenseSource.Repository, GetRepositoryLicense(repositoryFullName)),
			(LicenseSource.Organization, GetOrganizationLicense(owner)),
			(LicenseSource.Application, DefaultLicense),
			(LicenseSource.Configuration, new LicenseOverride
			{
				License = string.IsNullOrWhiteSpace(_appSettings.ExpectedLicense)
					? PanoramicData.NugetManagement.Models.Standards.LicenseType
					: _appSettings.ExpectedLicense.Trim()
			})
		};

		var levels = allLevels.Where(level => level.Source <= mostSpecific).ToList();
		var supplier = levels.FindIndex(level => !string.IsNullOrWhiteSpace(level.Override?.License));
		var marker = levels
			.Take(supplier + 1)
			.Select(level => level.Override?.LicenseFileText)
			.FirstOrDefault(text => !string.IsNullOrWhiteSpace(text));

		return new EffectiveLicense(levels[supplier].Override!.License!, marker, levels[supplier].Source);
	}

	private static LicenseOverride? Copy(LicenseOverride? value)
		=> value is null ? null : new LicenseOverride { License = value.License, LicenseFileText = value.LicenseFileText };

	private static LicenseOverride? Lookup(Dictionary<string, LicenseOverride> overrides, string? key)
		=> string.IsNullOrWhiteSpace(key) ? null : Copy(overrides.GetValueOrDefault(key.Trim()));

	private void SetOverride(Dictionary<string, LicenseOverride> overrides, string key, LicenseOverride? value)
	{
		var trimmed = key?.Trim();
		if (string.IsNullOrEmpty(trimmed))
		{
			return;
		}

		var normalised = value?.Normalised();
		lock (_lock)
		{
			if (normalised is null)
			{
				overrides.Remove(trimmed);
			}
			else
			{
				overrides[trimmed] = normalised;
			}
		}

		SaveToDisk();
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

	private static Dictionary<string, LicenseOverride> CaseInsensitive(Dictionary<string, LicenseOverride>? source)
	{
		var copy = new Dictionary<string, LicenseOverride>(StringComparer.OrdinalIgnoreCase);
		foreach (var (key, value) in source ?? [])
		{
			if (value?.Normalised() is { } normalised)
			{
				copy[key] = normalised;
			}
		}

		return copy;
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

					// The deserialiser builds these with the default, case-sensitive comparer.
					settings.OrganizationLicenses = CaseInsensitive(settings.OrganizationLicenses);
					settings.RepositoryLicenses = CaseInsensitive(settings.RepositoryLicenses);

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
					AllowPublishWithoutTests = _runtimeSettings.AllowPublishWithoutTests,
					MaxConcurrentLanes = _runtimeSettings.MaxConcurrentLanes,
					Ollama = _runtimeSettings.Ollama,
					Organizations = [.. _runtimeSettings.Organizations],
					ExcludedRepositories = [.. _runtimeSettings.ExcludedRepositories],
					DefaultLicense = Copy(_runtimeSettings.DefaultLicense),
					OrganizationLicenses = CaseInsensitive(_runtimeSettings.OrganizationLicenses),
					RepositoryLicenses = CaseInsensitive(_runtimeSettings.RepositoryLicenses)
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
	/// Whether Publish is offered for a repository that has built successfully but whose tests have
	/// not been run. Defaults to false.
	/// </summary>
	/// <remarks>
	/// A waiver of the test gate, not of the build gate: see <see cref="PublishGate"/>. Kept here
	/// rather than per repository because it is a decision about how this estate is managed, and one
	/// applied per repository would be forgotten in exactly the repository it was set on.
	/// </remarks>
	public bool AllowPublishWithoutTests { get; set; }

	/// <summary>
	/// Where the local model is and how hard to work it, or null when none is configured.
	/// </summary>
	public OllamaOptions? Ollama { get; set; }

	/// <summary>
	/// How many repository lanes may run at once. Defaults to 20.
	/// </summary>
	/// <remarks>
	/// A lane can run <c>dotnet build</c>, <c>dotnet test</c>, a clone and GitHub API calls, so the
	/// cap is what stops a large estate saturating the machine. Twenty is a working default for a
	/// developer machine, not a measured optimum.
	/// </remarks>
	public int? MaxConcurrentLanes { get; set; }

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

	/// <summary>
	/// The licence every repository is held to unless its organisation or itself says otherwise; null
	/// uses <see cref="AppSettings.ExpectedLicense"/>.
	/// </summary>
	public LicenseOverride? DefaultLicense { get; set; }

	/// <summary>
	/// Licence overrides per organisation, by name.
	/// </summary>
	public Dictionary<string, LicenseOverride> OrganizationLicenses { get; set; } = new(StringComparer.OrdinalIgnoreCase);

	/// <summary>
	/// Licence overrides per repository, by full name.
	/// </summary>
	public Dictionary<string, LicenseOverride> RepositoryLicenses { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}
