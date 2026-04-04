using PanoramicData.Blazor.Interfaces;
using PanoramicData.Blazor.Models;

namespace PanoramicData.NugetManagement.Web.Services;

/// <summary>
/// Real filesystem data provider for PDFileModal/PDFileExplorer.
/// Maps local drives to virtual paths (e.g., "C:\" → "/C").
/// </summary>
public class LocalFileSystemDataProvider : IDataProviderService<FileExplorerItem>
{
	/// <summary>
	/// Fetches directory/file entries for the PDFileExplorer.
	/// SearchText values: null = all (not used), "" = root (show drives), "/C/path" = children of that path.
	/// </summary>
	public Task<DataResponse<FileExplorerItem>> GetDataAsync(DataRequest<FileExplorerItem> request, CancellationToken cancellationToken)
	{
		var items = new List<FileExplorerItem>();

		if (string.IsNullOrEmpty(request.SearchText))
		{
			// Root level — list available drives
			foreach (var drive in DriveInfo.GetDrives().Where(d => d.IsReady))
			{
				var driveLetter = drive.Name.TrimEnd('\\', '/').TrimEnd(':');
				items.Add(new FileExplorerItem
				{
					Path = $"/{driveLetter}",
					Name = $"{driveLetter}: ({drive.VolumeLabel})",
					EntryType = FileExplorerItemType.Directory,
					HasSubFolders = true,
					DateCreated = DateTimeOffset.MinValue,
					DateModified = DateTimeOffset.MinValue
				});
			}
		}
		else
		{
			// Convert virtual path to real filesystem path
			var realPath = VirtualToRealPath(request.SearchText);

			if (Directory.Exists(realPath))
			{
				try
				{
					foreach (var dir in Directory.GetDirectories(realPath))
					{
						var dirInfo = new DirectoryInfo(dir);

						// Skip hidden/system directories
						if ((dirInfo.Attributes & (FileAttributes.Hidden | FileAttributes.System)) != 0)
						{
							continue;
						}

						var virtualPath = RealToVirtualPath(dir);
						var hasSubFolders = false;
						try
						{
							hasSubFolders = Directory.GetDirectories(dir).Length > 0;
						}
						catch
						{
							// Access denied — treat as no subfolders
						}

						items.Add(new FileExplorerItem
						{
							Path = virtualPath,
							Name = dirInfo.Name,
							EntryType = FileExplorerItemType.Directory,
							HasSubFolders = hasSubFolders,
							DateCreated = dirInfo.CreationTime,
							DateModified = dirInfo.LastWriteTime,
							IsHidden = (dirInfo.Attributes & FileAttributes.Hidden) != 0,
							IsReadOnly = (dirInfo.Attributes & FileAttributes.ReadOnly) != 0
						});
					}
				}
				catch (UnauthorizedAccessException)
				{
					// Silently skip inaccessible directories
				}
			}
		}

		items = [.. items.OrderBy(i => i.Name)];
		return Task.FromResult(new DataResponse<FileExplorerItem>(items, items.Count));
	}

	/// <inheritdoc />
	public Task<OperationResponse> DeleteAsync(FileExplorerItem item, CancellationToken cancellationToken)
		=> Task.FromResult(new OperationResponse { Success = false, ErrorMessage = "Delete not supported" });

	/// <inheritdoc />
	public Task<OperationResponse> UpdateAsync(FileExplorerItem item, IDictionary<string, object?> delta, CancellationToken cancellationToken)
		=> Task.FromResult(new OperationResponse { Success = false, ErrorMessage = "Update not supported" });

	/// <inheritdoc />
	public Task<OperationResponse> CreateAsync(FileExplorerItem item, CancellationToken cancellationToken)
		=> Task.FromResult(new OperationResponse { Success = false, ErrorMessage = "Create not supported" });

	/// <summary>
	/// Converts a virtual path (e.g., "/C/Users/david") to a real Windows path (e.g., "C:\Users\david").
	/// </summary>
	public static string VirtualToRealPath(string virtualPath)
	{
		// "/C" → "C:\"
		// "/C/Users/david" → "C:\Users\david"
		var trimmed = virtualPath.TrimStart('/');
		var parts = trimmed.Split('/');
		if (parts.Length == 0)
		{
			return string.Empty;
		}

		var driveLetter = parts[0];
		if (parts.Length == 1)
		{
			return $"{driveLetter}:\\";
		}

		return $"{driveLetter}:\\" + string.Join('\\', parts.Skip(1));
	}

	/// <summary>
	/// Converts a real Windows path (e.g., "C:\Users\david") to a virtual path (e.g., "/C/Users/david").
	/// </summary>
	public static string RealToVirtualPath(string realPath)
	{
		// "C:\Users\david" → "/C/Users/david"
		// "C:\" → "/C"
		var normalized = realPath.Replace('\\', '/').TrimEnd('/');
		if (normalized.Length >= 2 && normalized[1] == ':')
		{
			return "/" + normalized[0] + (normalized.Length > 2 ? normalized[2..] : string.Empty);
		}

		return "/" + normalized;
	}
}
