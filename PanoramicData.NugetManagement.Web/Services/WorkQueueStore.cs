using System.Text.Json;
using System.Text.Json.Serialization;
using PanoramicData.NugetManagement.Web.Models;

namespace PanoramicData.NugetManagement.Web.Services;

/// <summary>One outstanding item as it is written to disk.</summary>
/// <param name="Title">What the user saw in the tree.</param>
/// <param name="Descriptor">What the work will do.</param>
/// <param name="DedupKey">Identifies a repeat of this work.</param>
/// <param name="Step">The workflow step it performs, or null.</param>
/// <param name="ConsoleNodeKey">The console its output belongs to.</param>
/// <param name="WasRunning">Whether it was executing when the process stopped.</param>
public sealed record PersistedWorkItem(
	string Title,
	WorkDescriptor Descriptor,
	string DedupKey,
	WorkflowStep? Step,
	string? ConsoleNodeKey,
	bool WasRunning);

/// <summary>
/// Reads and writes the outstanding work queue, so that closing the application does not throw away
/// what the user asked for.
/// </summary>
/// <remarks>
/// Beside the runtime settings, and for the same reason: it is state about this machine's session
/// rather than about any repository, and it must not end up committed to one.
/// </remarks>
public sealed class WorkQueueStore(string path, ILogger<WorkQueueStore> logger)
{
	private readonly Lock _lock = new();

	private static readonly JsonSerializerOptions _options = new()
	{
		WriteIndented = true,
		Converters = { new JsonStringEnumConverter() }
	};

	/// <summary>The queue file's location under the user's local application data.</summary>
	public static string DefaultPath() => Path.Combine(
		Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
		"PanoramicData.NugetManagement",
		"work-queue.json");

	/// <summary>Writes the outstanding work, replacing whatever was there.</summary>
	/// <param name="items">What is outstanding.</param>
	public void Save(IReadOnlyList<PersistedWorkItem> items)
	{
		lock (_lock)
		{
			try
			{
				var directory = Path.GetDirectoryName(path)!;
				Directory.CreateDirectory(directory);
				File.WriteAllText(path, JsonSerializer.Serialize(items, _options));
			}
			catch (Exception ex)
			{
				// Losing the queue file costs the user their pending work on the next restart. Failing
				// the operation that triggered the save would cost them the work they are doing now.
				logger.LogWarning(ex, "Failed to save the work queue to {Path}", path);
			}
		}
	}

	/// <summary>Reads the outstanding work saved by a previous run, or nothing when there is none.</summary>
	public IReadOnlyList<PersistedWorkItem> Load()
	{
		lock (_lock)
		{
			try
			{
				if (!File.Exists(path))
				{
					return [];
				}

				return JsonSerializer.Deserialize<List<PersistedWorkItem>>(File.ReadAllText(path), _options) ?? [];
			}
			catch (Exception ex)
			{
				// A queue file that cannot be read must not stop the application starting. The cost of
				// ignoring it is the pending work; the cost of throwing is the application.
				logger.LogWarning(ex, "Failed to load the work queue from {Path}; starting with an empty queue", path);
				return [];
			}
		}
	}
}
