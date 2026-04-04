using System.Diagnostics;
using PanoramicData.NugetManagement.Web.Models;

namespace PanoramicData.NugetManagement.Web.Services;

/// <summary>
/// Detects installed IDEs on the local machine and provides methods to open projects in them.
/// </summary>
public class IdeDetectionService
{
    private readonly ILogger<IdeDetectionService> _logger;
    private List<InstalledIde>? _detectedIdes;

    /// <summary>
    /// Initializes a new instance of the <see cref="IdeDetectionService"/> class.
    /// </summary>
    public IdeDetectionService(ILogger<IdeDetectionService> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Gets the list of IDEs detected on this machine. Results are cached after first call.
    /// </summary>
    public IReadOnlyList<InstalledIde> DetectedIdes => _detectedIdes ??= DetectIdes();

    /// <summary>
    /// Opens a repository folder in the specified IDE.
    /// For solution-based IDEs (Visual Studio), it searches for a .sln or .slnx file.
    /// For folder-based IDEs (VS Code), it opens the folder directly.
    /// </summary>
    public void OpenInIde(InstalledIde ide, string localPath)
    {
        try
        {
            string argument;

            if (ide.OpensSolutionFiles)
            {
                // Look for .slnx first, then .sln
                var solutionFile = Directory.EnumerateFiles(localPath, "*.slnx").FirstOrDefault()
                    ?? Directory.EnumerateFiles(localPath, "*.sln").FirstOrDefault();

                argument = solutionFile ?? localPath;
            }
            else
            {
                argument = localPath;
            }

            _logger.LogInformation("Opening {Path} in {Ide} ({Exe})", argument, ide.DisplayName, ide.ExecutablePath);

            Process.Start(new ProcessStartInfo
            {
                FileName = ide.ExecutablePath,
                Arguments = $"\"{argument}\"",
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to open {Path} in {Ide}", localPath, ide.DisplayName);
            throw;
        }
    }

    /// <summary>
    /// Forces re-detection of installed IDEs.
    /// </summary>
    public void Refresh() => _detectedIdes = null;

    private List<InstalledIde> DetectIdes()
    {
        var ides = new List<InstalledIde>();

        // Visual Studio installations (check common paths)
        DetectVisualStudio(ides);

        // VS Code installations
        DetectVsCode(ides);

        // JetBrains Rider
        DetectRider(ides);

        _logger.LogInformation("Detected {Count} IDE(s): {Ides}",
            ides.Count, string.Join(", ", ides.Select(i => i.DisplayName)));

        return ides;
    }

    private static void DetectVisualStudio(List<InstalledIde> ides)
    {
        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);

        // Visual Studio editions and year combinations, newest first
        var vsEditions = new (string Year, string Edition, string Id, string DisplaySuffix, bool IsPreview)[]
        {
            ("2026", "Preview", "vs2026-preview", " 2026 Preview", true),
            ("2026", "Enterprise", "vs2026-enterprise", " 2026 Enterprise", false),
            ("2026", "Professional", "vs2026-professional", " 2026 Professional", false),
            ("2026", "Community", "vs2026-community", " 2026 Community", false),
            ("2022", "Preview", "vs2022-preview", " 2022 Preview", true),
            ("2022", "Enterprise", "vs2022-enterprise", " 2022 Enterprise", false),
            ("2022", "Professional", "vs2022-professional", " 2022 Professional", false),
            ("2022", "Community", "vs2022-community", " 2022 Community", false),
        };

        foreach (var (year, edition, id, displaySuffix, isPreview) in vsEditions)
        {
            var devenvPath = Path.Combine(programFiles, "Microsoft Visual Studio", year, edition, "Common7", "IDE", "devenv.exe");
            if (File.Exists(devenvPath))
            {
                ides.Add(new InstalledIde
                {
                    Id = id,
                    DisplayName = $"Visual Studio{displaySuffix}",
                    ExecutablePath = devenvPath,
                    IconCss = isPreview ? "fas fa-laptop-code text-warning" : "fas fa-laptop-code text-info",
                    OpensSolutionFiles = true
                });
            }
        }
    }

    private static void DetectVsCode(List<InstalledIde> ides)
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);

        // VS Code — user install (most common)
        var vsCodeUserPath = Path.Combine(localAppData, "Programs", "Microsoft VS Code", "Code.exe");
        // VS Code — system install
        var vsCodeSystemPath = Path.Combine(programFiles, "Microsoft VS Code", "Code.exe");

        var vsCodePath = File.Exists(vsCodeUserPath) ? vsCodeUserPath
            : File.Exists(vsCodeSystemPath) ? vsCodeSystemPath
            : null;

        if (vsCodePath is not null)
        {
            ides.Add(new InstalledIde
            {
                Id = "vscode",
                DisplayName = "Visual Studio Code",
                ExecutablePath = vsCodePath,
                IconCss = "fas fa-code text-info",
                OpensSolutionFiles = false
            });
        }

        // VS Code Insiders — user install
        var vsCodeInsidersUserPath = Path.Combine(localAppData, "Programs", "Microsoft VS Code Insiders", "Code - Insiders.exe");
        // VS Code Insiders — system install
        var vsCodeInsidersSystemPath = Path.Combine(programFiles, "Microsoft VS Code Insiders", "Code - Insiders.exe");

        var vsCodeInsidersPath = File.Exists(vsCodeInsidersUserPath) ? vsCodeInsidersUserPath
            : File.Exists(vsCodeInsidersSystemPath) ? vsCodeInsidersSystemPath
            : null;

        if (vsCodeInsidersPath is not null)
        {
            ides.Add(new InstalledIde
            {
                Id = "vscode-insiders",
                DisplayName = "VS Code Insiders",
                ExecutablePath = vsCodeInsidersPath,
                IconCss = "fas fa-code text-success",
                OpensSolutionFiles = false
            });
        }
    }

    private static void DetectRider(List<InstalledIde> ides)
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var jetBrainsToolbox = Path.Combine(localAppData, "JetBrains", "Toolbox", "apps");

        // Rider installed via JetBrains Toolbox
        if (Directory.Exists(jetBrainsToolbox))
        {
            try
            {
                // Toolbox stores Rider under apps/rider/ch-0/<version>/bin/rider64.exe
                var riderAppsDir = Path.Combine(jetBrainsToolbox, "rider");
                if (Directory.Exists(riderAppsDir))
                {
                    var riderExe = Directory.EnumerateFiles(riderAppsDir, "rider64.exe", SearchOption.AllDirectories)
                        .OrderByDescending(p => p) // newest version first
                        .FirstOrDefault();

                    if (riderExe is not null)
                    {
                        ides.Add(new InstalledIde
                        {
                            Id = "rider",
                            DisplayName = "JetBrains Rider",
                            ExecutablePath = riderExe,
                            IconCss = "fas fa-horse text-danger",
                            OpensSolutionFiles = true
                        });
                        return;
                    }
                }
            }
            catch
            {
                // Ignore errors scanning Toolbox directory
            }
        }

        // Rider installed standalone — check Program Files
        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var jetBrainsDir = Path.Combine(programFiles, "JetBrains");
        if (Directory.Exists(jetBrainsDir))
        {
            try
            {
                var riderExe = Directory.EnumerateDirectories(jetBrainsDir, "JetBrains Rider*")
                    .OrderByDescending(d => d) // newest version first
                    .Select(d => Path.Combine(d, "bin", "rider64.exe"))
                    .FirstOrDefault(File.Exists);

                if (riderExe is not null)
                {
                    ides.Add(new InstalledIde
                    {
                        Id = "rider",
                        DisplayName = "JetBrains Rider",
                        ExecutablePath = riderExe,
                        IconCss = "fas fa-horse text-danger",
                        OpensSolutionFiles = true
                    });
                }
            }
            catch
            {
                // Ignore errors scanning JetBrains directory
            }
        }
    }
}
