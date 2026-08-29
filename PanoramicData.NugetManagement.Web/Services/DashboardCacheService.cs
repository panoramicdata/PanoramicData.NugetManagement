using System.Text.Json;
using System.Text.Json.Serialization;
using PanoramicData.NugetManagement.Web.Models;

namespace PanoramicData.NugetManagement.Web.Services;

/// <summary>
/// Singleton cache for dashboard state, backed by a JSON file on disk.
/// On startup the previous cache is loaded so the UI is immediately populated.
/// </summary>
public class DashboardCacheService
{
	private readonly Lock _lock = new();
	private readonly string _cachePath;
	private readonly ILogger<DashboardCacheService> _logger;
	private List<RepositoryDashboardRow>? _cachedRows;
	private List<UngovernedPackage> _ungovernedPackages = [];
	private DateTimeOffset _lastRefreshUtc = DateTimeOffset.MinValue;

	private static readonly JsonSerializerOptions _jsonOptions = new()
	{
		WriteIndented = false,
		DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
		PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
		Converters = { new ObjectDictionaryConverter() }
	};

	/// <summary>
	/// The duration after which the cache is considered stale and should be refreshed.
	/// </summary>
	public static readonly TimeSpan CacheDuration = TimeSpan.FromHours(1);

	/// <summary>
	/// Which understanding of governance produced the rows in the cache file.
	/// </summary>
	/// <remarks>
	/// Bump this whenever a change alters what discovery would produce for the same estate — a new rule,
	/// a changed rule, a change to which repositories are governed at all. A cache stamped with anything
	/// else is discarded on load, so the screen is rebuilt rather than showing rows the current rules
	/// would never have produced. Without it, the rows that governed rimland/EPPlus survived the fix that
	/// stopped them being produced, and went on offering actions against somebody else's repository.
	///
	/// 1: repositories outside the configured organisations are no longer governed.
	/// 2: the row is the repository, not the package, and ungoverned packages are held separately.
	/// </remarks>
	public const int DiscoveryVersion = 2;

	/// <summary>
	/// Initializes the cache service and loads any persisted state from disk.
	/// </summary>
	public DashboardCacheService(ILogger<DashboardCacheService> logger)
		: this(logger, DefaultCachePath())
	{
	}

	/// <summary>
	/// Initializes the cache service against a specific cache file, and loads any state it holds.
	/// </summary>
	/// <remarks>
	/// Where the cache lives is a parameter rather than a fact of the machine. It has to be: on Windows
	/// <see cref="Environment.GetFolderPath(Environment.SpecialFolder)"/> asks the Known Folder API and
	/// pays no attention to the <c>LOCALAPPDATA</c> variable, so redirecting it is not something a caller
	/// can do from the outside.
	/// </remarks>
	public DashboardCacheService(ILogger<DashboardCacheService> logger, string cachePath)
	{
		_logger = logger;
		_cachePath = cachePath;

		LoadFromDisk();
	}

	/// <summary>
	/// The cache file's location under the user's local application data.
	/// </summary>
	public static string DefaultCachePath() => Path.Combine(
		Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
		"PanoramicData.NugetManagement",
		"dashboard-cache.json");

	/// <summary>
	/// Gets the cached dashboard rows, or null if no cache exists.
	/// </summary>
	public List<RepositoryDashboardRow>? GetCachedRows()
	{
		lock (_lock)
		{
			return _cachedRows;
		}
	}

	/// <summary>
	/// Gets the UTC time of the last successful refresh.
	/// </summary>
	public DateTimeOffset LastRefreshUtc
	{
		get
		{
			lock (_lock)
			{
				return _lastRefreshUtc;
			}
		}
	}

	/// <summary>
	/// Whether the cache is stale and needs refreshing.
	/// </summary>
	public bool IsStale
	{
		get
		{
			lock (_lock)
			{
				return _cachedRows is null || DateTimeOffset.UtcNow - _lastRefreshUtc > CacheDuration;
			}
		}
	}

	/// <summary>
	/// Updates the cached rows and refresh timestamp, then persists to disk.
	/// Called when a full refresh cycle completes.
	/// </summary>
	public void Update(List<RepositoryDashboardRow> rows)
	{
		lock (_lock)
		{
			_cachedRows = rows;
			_lastRefreshUtc = DateTimeOffset.UtcNow;
		}

		SaveToDisk();
	}

	/// <summary>
	/// Sets the cached rows without updating the refresh timestamp.
	/// Used for incremental updates (e.g. after discovering packages but before full assessment).
	/// </summary>
	public void SetRows(List<RepositoryDashboardRow> rows)
	{
		lock (_lock)
		{
			_cachedRows = rows;
		}
	}

	/// <summary>
	/// Gets a single cached row by repository full name.
	/// </summary>
	/// <param name="repositoryFullName">The repository, as "owner/name".</param>
	public RepositoryDashboardRow? GetRow(string repositoryFullName)
	{
		lock (_lock)
		{
			return _cachedRows?.FirstOrDefault(r =>
				string.Equals(r.RepositoryFullName, repositoryFullName, StringComparison.OrdinalIgnoreCase));
		}
	}

	/// <summary>
	/// The cached repository whose packages include the given id, or null when none does.
	/// </summary>
	/// <remarks>
	/// For callers that still hold a package id — a remediation prompt, a deep link — now that the row
	/// they want is keyed on the repository that publishes it.
	/// </remarks>
	/// <param name="packageId">The NuGet package identifier.</param>
	public RepositoryDashboardRow? GetRowByPackageId(string packageId)
	{
		lock (_lock)
		{
			return _cachedRows?.FirstOrDefault(row => row.Packages
				.Any(package => string.Equals(package.PackageId, packageId, StringComparison.OrdinalIgnoreCase)));
		}
	}

	/// <summary>
	/// The packages that belong to no repository we govern, and why.
	/// </summary>
	public IReadOnlyList<UngovernedPackage> GetUngovernedPackages()
	{
		lock (_lock)
		{
			return [.. _ungovernedPackages];
		}
	}

	/// <summary>
	/// Replaces the ungoverned packages recorded by the last discovery.
	/// </summary>
	/// <param name="packages">The packages that belong to no repository we govern.</param>
	public void SetUngovernedPackages(List<UngovernedPackage> packages)
	{
		lock (_lock)
		{
			_ungovernedPackages = packages;
		}
	}

	/// <summary>
	/// Inserts or replaces a single cached row by repository full name and persists to disk.
	/// </summary>
	public void UpsertRow(RepositoryDashboardRow row)
	{
		lock (_lock)
		{
			_cachedRows ??= [];

			var index = _cachedRows.FindIndex(existing =>
				string.Equals(existing.RepositoryFullName, row.RepositoryFullName, StringComparison.OrdinalIgnoreCase));

			if (index >= 0)
			{
				_cachedRows[index] = row;
			}
			else
			{
				_cachedRows.Add(row);
			}

			_lastRefreshUtc = DateTimeOffset.UtcNow;
		}

		SaveToDisk();
	}

	/// <summary>
	/// Notifies that a single row's assessment was updated — persists to disk.
	/// </summary>
	public void NotifyRowUpdated()
	{
		lock (_lock)
		{
			_lastRefreshUtc = DateTimeOffset.UtcNow;
		}

		SaveToDisk();
	}

	/// <summary>
	/// Removes a row by repository full name from the cache and persists to disk.
	/// Returns true if the row was found and removed.
	/// </summary>
	public bool RemoveRow(string repositoryFullName)
	{
		bool removed;
		lock (_lock)
		{
			if (_cachedRows is null)
			{
				return false;
			}

			removed = _cachedRows.RemoveAll(r =>
				string.Equals(r.RepositoryFullName, repositoryFullName, StringComparison.OrdinalIgnoreCase)) > 0;

			if (removed)
			{
				_lastRefreshUtc = DateTimeOffset.UtcNow;
			}
		}

		if (removed)
		{
			_logger.LogInformation("Removed repository '{RepositoryFullName}' from cache", repositoryFullName);
			SaveToDisk();
		}

		return removed;
	}

	private void SaveToDisk()
	{
		try
		{
			List<RepositoryDashboardRow>? rows;
			List<UngovernedPackage> ungoverned;
			DateTimeOffset ts;

			lock (_lock)
			{
				rows = _cachedRows;
				ungoverned = _ungovernedPackages;
				ts = _lastRefreshUtc;
			}

			if (rows is null)
			{
				return;
			}

			var envelope = new CacheEnvelope
			{
				DiscoveryVersion = DiscoveryVersion,
				LastRefreshUtc = ts,
				Rows = rows,
				UngovernedPackages = ungoverned
			};

			var dir = Path.GetDirectoryName(_cachePath);
			if (dir is not null && !Directory.Exists(dir))
			{
				Directory.CreateDirectory(dir);
			}

			var json = JsonSerializer.Serialize(envelope, _jsonOptions);
			File.WriteAllText(_cachePath, json);
			_logger.LogDebug("Dashboard cache persisted to {Path} ({Count} rows)", _cachePath, rows.Count);
		}
		catch (Exception ex)
		{
			_logger.LogWarning(ex, "Failed to persist dashboard cache to disk");
		}
	}

	private void LoadFromDisk()
	{
		try
		{
			if (!File.Exists(_cachePath))
			{
				_logger.LogInformation("No persisted dashboard cache found at {Path}", _cachePath);
				return;
			}

			var json = File.ReadAllText(_cachePath);
			var envelope = JsonSerializer.Deserialize<CacheEnvelope>(json, _jsonOptions);
			if (envelope?.Rows is null)
			{
				return;
			}

			if (envelope.DiscoveryVersion != DiscoveryVersion)
			{
				_logger.LogInformation(
					"Discarding the dashboard cache: it was written by discovery version {Found}, and this is version {Current}. Rediscovery will rebuild it.",
					envelope.DiscoveryVersion,
					DiscoveryVersion);
				return;
			}

			lock (_lock)
			{
				_cachedRows = envelope.Rows;
				_ungovernedPackages = envelope.UngovernedPackages;
				_lastRefreshUtc = envelope.LastRefreshUtc;
			}

			_logger.LogInformation(
				"Loaded {Count} cached rows from disk (last refresh: {Time})",
				envelope.Rows.Count,
				envelope.LastRefreshUtc);
		}
		catch (Exception ex)
		{
			_logger.LogWarning(ex, "Failed to load dashboard cache from disk — starting fresh");
		}
	}

	/// <summary>
	/// Serialization envelope for the persisted cache file.
	/// </summary>
	private sealed class CacheEnvelope
	{
		/// <summary>
		/// Which <see cref="DashboardCacheService.DiscoveryVersion"/> wrote this file. Absent in files
		/// written before versioning existed, which deserialize as zero and are therefore discarded.
		/// </summary>
		public int DiscoveryVersion { get; set; }

		public DateTimeOffset LastRefreshUtc { get; set; }
		public List<RepositoryDashboardRow> Rows { get; set; } = [];

		/// <summary>
		/// The packages that belong to no repository we govern. Absent in files written before the
		/// repository layer, which deserialize as empty — harmless, since such a file is discarded for
		/// its version anyway.
		/// </summary>
		public List<UngovernedPackage> UngovernedPackages { get; set; } = [];
	}

	/// <summary>
	/// Handles <see cref="Dictionary{TKey, TValue}"/> where TValue is <see cref="object"/>
	/// by reading JSON values as their natural types (string, string[], etc.).
	/// </summary>
	private sealed class ObjectDictionaryConverter : JsonConverter<Dictionary<string, object>>
	{
		public override Dictionary<string, object>? Read(
			ref Utf8JsonReader reader,
			Type typeToConvert,
			JsonSerializerOptions options)
		{
			if (reader.TokenType != JsonTokenType.StartObject)
			{
				throw new JsonException();
			}

			var dict = new Dictionary<string, object>();

			while (reader.Read())
			{
				if (reader.TokenType == JsonTokenType.EndObject)
				{
					return dict;
				}

				var key = reader.GetString()!;
				reader.Read();
				dict[key] = ReadValue(ref reader);
			}

			return dict;
		}

		public override void Write(
			Utf8JsonWriter writer,
			Dictionary<string, object> value,
			JsonSerializerOptions options)
		{
			writer.WriteStartObject();

			foreach (var (key, val) in value)
			{
				writer.WritePropertyName(key);
				WriteValue(writer, val);
			}

			writer.WriteEndObject();
		}

		private static object ReadValue(ref Utf8JsonReader reader) => reader.TokenType switch
		{
			JsonTokenType.String => reader.GetString()!,
			JsonTokenType.Number => reader.GetDouble(),
			JsonTokenType.True => true,
			JsonTokenType.False => false,
			JsonTokenType.Null => null!,
			JsonTokenType.StartArray => ReadArray(ref reader),
			JsonTokenType.StartObject => ReadObject(ref reader),
			_ => throw new JsonException($"Unexpected token {reader.TokenType}")
		};

		private static string[] ReadArray(ref Utf8JsonReader reader)
		{
			var list = new List<string>();
			while (reader.Read())
			{
				if (reader.TokenType == JsonTokenType.EndArray)
				{
					return [.. list];
				}

				if (reader.TokenType == JsonTokenType.String)
				{
					list.Add(reader.GetString()!);
				}
				else
				{
					// For non-string array elements, convert to string representation
					using var doc = JsonDocument.ParseValue(ref reader);
					list.Add(doc.RootElement.ToString());
				}
			}

			return [.. list];
		}

		private static Dictionary<string, object> ReadObject(ref Utf8JsonReader reader)
		{
			var dict = new Dictionary<string, object>();
			while (reader.Read())
			{
				if (reader.TokenType == JsonTokenType.EndObject)
				{
					return dict;
				}

				var key = reader.GetString()!;
				reader.Read();
				dict[key] = ReadValue(ref reader);
			}

			return dict;
		}

		private static void WriteValue(Utf8JsonWriter writer, object? val)
		{
			switch (val)
			{
				case null:
					writer.WriteNullValue();
					break;
				case string s:
					writer.WriteStringValue(s);
					break;
				case bool b:
					writer.WriteBooleanValue(b);
					break;
				case int i:
					writer.WriteNumberValue(i);
					break;
				case long l:
					writer.WriteNumberValue(l);
					break;
				case double d:
					writer.WriteNumberValue(d);
					break;
				case string[] arr:
					writer.WriteStartArray();
					foreach (var item in arr)
					{
						writer.WriteStringValue(item);
					}

					writer.WriteEndArray();
					break;
				case object[] arr:
					writer.WriteStartArray();
					foreach (var item in arr)
					{
						WriteValue(writer, item);
					}

					writer.WriteEndArray();
					break;
				case Dictionary<string, object> dict:
					writer.WriteStartObject();
					foreach (var (k, v) in dict)
					{
						writer.WritePropertyName(k);
						WriteValue(writer, v);
					}

					writer.WriteEndObject();
					break;
				default:
					writer.WriteStringValue(val.ToString());
					break;
			}
		}
	}
}
