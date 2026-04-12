using System.Diagnostics;
using System.Runtime.InteropServices;
using PanoramicData.NugetManagement.Web.Models;

namespace PanoramicData.NugetManagement.Web.Services;

/// <summary>
/// Detects installed IDEs on the local machine and provides default selection logic.
/// </summary>
public class IdeDetectionService
{
	private List<InstalledIde>? _detectedIdes;

	/// <summary>
	/// Gets the list of detected IDE installations on the local machine.
	/// </summary>
	public IReadOnlyList<InstalledIde> DetectedIdes => _detectedIdes ??= Detect();

	/// <summary>
	/// Returns the IDE id that should be used as the default when the user has not yet chosen.
	/// Priority order: VS Code Insiders, VS 2026 Insiders, VS Code, VS 2026 (any edition), Rider.
	/// Returns null if no IDE is detected.
	/// </summary>
	public string? GetDefaultIdeId()
	{
		var ides = DetectedIdes;
		if (ides.Count == 0)
		{
			return null;
		}

		string[] priorityOrder =
		[
			"vscode-insiders",
			"vs2026-insiders",
			"vscode",
			"vs2026-enterprise",
			"vs2026-professional",
			"vs2026-community",
			"rider"
		];

		foreach (var id in priorityOrder)
		{
			if (ides.Any(i => i.Id == id))
			{
				return id;
			}
		}

		// Fallback to first detected
		return ides[0].Id;
	}

	/// <summary>
	/// Clears the cached IDE list so the next access to <see cref="DetectedIdes"/> re-detects.
	/// </summary>
	public void Refresh() => _detectedIdes = null;

	/// <summary>
	/// Opens the specified local path in the given IDE.
	/// </summary>
	/// <param name="ide">The IDE to open.</param>
	/// <param name="localPath">The local repository path to open.</param>
	public Process? OpenInIde(InstalledIde ide, string localPath)
	{
		// Find a .slnx or .sln file in the local path
		string? targetPath = null;

		if (ide.OpensSolutionFiles)
		{
			targetPath = Directory.EnumerateFiles(localPath, "*.slnx").FirstOrDefault()
				?? Directory.EnumerateFiles(localPath, "*.sln").FirstOrDefault();
		}

		targetPath ??= localPath;

		var psi = new ProcessStartInfo
		{
			FileName = ide.ExecutablePath,
			Arguments = $"\"{targetPath}\"",
			UseShellExecute = true,
			WindowStyle = ProcessWindowStyle.Maximized
		};

		return Process.Start(psi);
	}

	private static List<InstalledIde> Detect()
	{
		var ides = new List<InstalledIde>();

		if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
		{
			return ides;
		}

		DetectVsCodeInsiders(ides);
		DetectVsCode(ides);
		DetectVisualStudio2026(ides);
		DetectRider(ides);

		return ides;
	}

	private static void DetectVisualStudio2026(List<InstalledIde> ides)
	{
		var basePath = @"C:\Program Files\Microsoft Visual Studio\18";

		(string folder, string id, string displayName)[] editions =
		[
			("Enterprise", "vs2026-enterprise", "Visual Studio 2026 Enterprise"),
			("Professional", "vs2026-professional", "Visual Studio 2026 Professional"),
			("Community", "vs2026-community", "Visual Studio 2026 Community"),
			("Insiders", "vs2026-insiders", "Visual Studio 2026 Insiders"),
		];

		foreach (var (folder, id, displayName) in editions)
		{
			var exePath = Path.Combine(basePath, folder, "Common7", "IDE", "devenv.exe");
			if (File.Exists(exePath))
			{
				ides.Add(new InstalledIde
				{
					Id = id,
					DisplayName = displayName,
					ExecutablePath = exePath,
					IconCss = "fa-brands fa-microsoft",
					OpensSolutionFiles = true
				});
			}
		}
	}

	private static void DetectVsCode(List<InstalledIde> ides)
	{
		var candidates = new List<string>();

		// User install
		var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
		candidates.Add(Path.Combine(localAppData, "Programs", "Microsoft VS Code", "Code.exe"));

		// System install
		candidates.Add(@"C:\Program Files\Microsoft VS Code\Code.exe");

		foreach (var path in candidates)
		{
			if (File.Exists(path))
			{
				ides.Add(new InstalledIde
				{
					Id = "vscode",
					DisplayName = "Visual Studio Code",
					ExecutablePath = path,
					IconCss = "fa-solid fa-code",
					OpensSolutionFiles = false
				});
				return; // Only add once
			}
		}
	}

	private static void DetectVsCodeInsiders(List<InstalledIde> ides)
	{
		var candidates = new List<string>();

		// User install
		var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
		candidates.Add(Path.Combine(localAppData, "Programs", "Microsoft VS Code Insiders", "Code - Insiders.exe"));

		// System install
		candidates.Add(@"C:\Program Files\Microsoft VS Code Insiders\Code - Insiders.exe");

		foreach (var path in candidates)
		{
			if (File.Exists(path))
			{
				ides.Add(new InstalledIde
				{
					Id = "vscode-insiders",
					DisplayName = "VS Code Insiders",
					ExecutablePath = path,
					IconCss = "fa-solid fa-code",
					OpensSolutionFiles = false
				});
				return; // Only add once
			}
		}
	}

	private static void DetectRider(List<InstalledIde> ides)
	{
		// JetBrains Toolbox install
		var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
		var toolboxApps = Path.Combine(localAppData, "JetBrains", "Toolbox", "apps", "rider");
		if (Directory.Exists(toolboxApps))
		{
			var riderExe = Directory.EnumerateFiles(toolboxApps, "rider64.exe", SearchOption.AllDirectories)
				.FirstOrDefault();
			if (riderExe is not null)
			{
				ides.Add(new InstalledIde
				{
					Id = "rider",
					DisplayName = "JetBrains Rider",
					ExecutablePath = riderExe,
					IconCss = "fa-solid fa-horse",
					OpensSolutionFiles = true
				});
				return;
			}
		}

		// Standalone install
		var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
		var jetBrainsDir = Path.Combine(programFiles, "JetBrains");
		if (Directory.Exists(jetBrainsDir))
		{
			var riderDirs = Directory.EnumerateDirectories(jetBrainsDir, "JetBrains Rider*");
			foreach (var dir in riderDirs)
			{
				var exePath = Path.Combine(dir, "bin", "rider64.exe");
				if (File.Exists(exePath))
				{
					ides.Add(new InstalledIde
					{
						Id = "rider",
						DisplayName = "JetBrains Rider",
						ExecutablePath = exePath,
						IconCss = "fa-solid fa-horse",
						OpensSolutionFiles = true
					});
					return;
				}
			}
		}
	}
}
