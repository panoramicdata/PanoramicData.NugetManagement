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
	private List<PackageDashboardRow>? _cachedRows;
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
	/// Initializes the cache service and loads any persisted state from disk.
	/// </summary>
	public DashboardCacheService(ILogger<DashboardCacheService> logger)
	{
		_logger = logger;
		_cachePath = Path.Combine(
			Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
			"PanoramicData.NugetManagement",
			"dashboard-cache.json");

		LoadFromDisk();
	}

	/// <summary>
	/// Gets the cached dashboard rows, or null if no cache exists.
	/// </summary>
	public List<PackageDashboardRow>? GetCachedRows()
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
	public void Update(List<PackageDashboardRow> rows)
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
	public void SetRows(List<PackageDashboardRow> rows)
	{
		lock (_lock)
		{
			_cachedRows = rows;
		}
	}

	/// <summary>
	/// Gets a single cached row by package ID.
	/// </summary>
	public PackageDashboardRow? GetRow(string packageId)
	{
		lock (_lock)
		{
			return _cachedRows?.FirstOrDefault(r =>
				string.Equals(r.PackageId, packageId, StringComparison.OrdinalIgnoreCase));
		}
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

	private void SaveToDisk()
	{
		try
		{
			List<PackageDashboardRow>? rows;
			DateTimeOffset ts;

			lock (_lock)
			{
				rows = _cachedRows;
				ts = _lastRefreshUtc;
			}

			if (rows is null)
			{
				return;
			}

			var envelope = new CacheEnvelope
			{
				LastRefreshUtc = ts,
				Rows = rows
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

			lock (_lock)
			{
				_cachedRows = envelope.Rows;
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
		public DateTimeOffset LastRefreshUtc { get; set; }
		public List<PackageDashboardRow> Rows { get; set; } = [];
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
