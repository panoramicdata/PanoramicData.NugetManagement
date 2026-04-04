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
    private RuntimeSettings _runtimeSettings;

    /// <summary>
    /// Initializes a new instance of the <see cref="RuntimeSettingsService"/> class.
    /// </summary>
    public RuntimeSettingsService(IOptions<AppSettings> appSettings, ILogger<RuntimeSettingsService> logger)
    {
        _appSettings = appSettings.Value;
        _logger = logger;
        _settingsPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "PanoramicData.NugetManagement",
            "runtime-settings.json");

        _runtimeSettings = LoadFromDisk();
    }

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

        // Also update the AppSettings instance so LocalRepoService picks up the change immediately
        _appSettings.LocalReposRoot = value ?? _appSettings.LocalReposRoot;
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
                snapshot = new RuntimeSettings
                {
                    LocalReposRoot = _runtimeSettings.LocalReposRoot
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
}
