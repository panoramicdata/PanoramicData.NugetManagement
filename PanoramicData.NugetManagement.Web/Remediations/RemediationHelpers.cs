using System.Xml.Linq;
using PanoramicData.NugetManagement.Models;

namespace PanoramicData.NugetManagement.Web.Remediations;

/// <summary>
/// Shared helper methods used by multiple remediation implementations.
/// </summary>
internal static class RemediationHelpers
{
    /// <summary>
    /// Creates a file from a template, creating directories as needed.
    /// </summary>
    public static void CreateFile(
        string localPath,
        string relativePath,
        string content,
        RuleResult result,
        List<string> applied,
        Action<string>? onOutput)
    {
        var fullPath = ResolvePath(localPath, relativePath);
        if (File.Exists(fullPath))
        {
            onOutput?.Invoke($"⏭️ [{result.RuleId}] {relativePath} already exists — skipping.");
            return;
        }

        EnsureDirectory(fullPath);
        File.WriteAllText(fullPath, content);
        applied.Add(relativePath);
        onOutput?.Invoke($"✅ [{result.RuleId}] Created {relativePath}");
    }

    /// <summary>
    /// Ensures an XML property exists in a .props or .csproj file.
    /// Creates the file if it doesn't exist.
    /// </summary>
    public static void EnsureXmlProperty(
        string localPath,
        string relativePath,
        string propertyName,
        string propertyValue,
        RuleResult result,
        List<string> applied,
        Action<string>? onOutput)
    {
        var fullPath = ResolvePath(localPath, relativePath);
        if (!File.Exists(fullPath))
        {
            EnsureDirectory(fullPath);
            var content = $"""
                <Project>
                  <PropertyGroup>
                    <{propertyName}>{propertyValue}</{propertyName}>
                  </PropertyGroup>
                </Project>
                """;
            File.WriteAllText(fullPath, content);
            applied.Add(relativePath);
            onOutput?.Invoke($"✅ [{result.RuleId}] Created {relativePath} with <{propertyName}>{propertyValue}</{propertyName}>");
            return;
        }

        var xml = File.ReadAllText(fullPath);
        if (xml.Contains($"<{propertyName}>", StringComparison.OrdinalIgnoreCase))
        {
            onOutput?.Invoke($"⏭️ [{result.RuleId}] {relativePath} already has <{propertyName}> — skipping.");
            return;
        }

        try
        {
            var doc = XDocument.Parse(xml);
            var propertyGroup = doc.Root?.Elements("PropertyGroup").FirstOrDefault();
            if (propertyGroup is null)
            {
                propertyGroup = new XElement("PropertyGroup");
                doc.Root?.AddFirst(propertyGroup);
            }

            propertyGroup.Add(new XElement(propertyName, propertyValue));
            doc.Save(fullPath);
            applied.Add(relativePath);
            onOutput?.Invoke($"✅ [{result.RuleId}] Added <{propertyName}>{propertyValue}</{propertyName}> to {relativePath}");
        }
        catch (Exception ex)
        {
            onOutput?.Invoke($"❌ [{result.RuleId}] Failed to modify {relativePath}: {ex.Message}");
        }
    }

    /// <summary>
    /// Appends a line to a file if it doesn't already contain it.
    /// </summary>
    public static void AppendLine(
        string localPath,
        string relativePath,
        string lineContent,
        RuleResult result,
        List<string> applied,
        Action<string>? onOutput)
    {
        var fullPath = ResolvePath(localPath, relativePath);
        if (!File.Exists(fullPath))
        {
            onOutput?.Invoke($"⏭️ [{result.RuleId}] {relativePath} does not exist — cannot append.");
            return;
        }

        var content = File.ReadAllText(fullPath);
        if (content.Contains(lineContent, StringComparison.OrdinalIgnoreCase))
        {
            onOutput?.Invoke($"⏭️ [{result.RuleId}] {relativePath} already contains '{lineContent}' — skipping.");
            return;
        }

        if (!content.EndsWith('\n'))
        {
            content += Environment.NewLine;
        }

        content += lineContent + Environment.NewLine;
        File.WriteAllText(fullPath, content);
        applied.Add(relativePath);
        onOutput?.Invoke($"✅ [{result.RuleId}] Appended to {relativePath}");
    }

    /// <summary>
    /// Prepends a line to a file if it doesn't already contain it.
    /// </summary>
    public static void PrependLine(
        string localPath,
        string relativePath,
        string lineContent,
        RuleResult result,
        List<string> applied,
        Action<string>? onOutput)
    {
        var fullPath = ResolvePath(localPath, relativePath);
        if (!File.Exists(fullPath))
        {
            onOutput?.Invoke($"⏭️ [{result.RuleId}] {relativePath} does not exist — cannot prepend.");
            return;
        }

        var content = File.ReadAllText(fullPath);
        if (content.Contains(lineContent, StringComparison.OrdinalIgnoreCase))
        {
            onOutput?.Invoke($"⏭️ [{result.RuleId}] {relativePath} already contains the line — skipping.");
            return;
        }

        content = lineContent + Environment.NewLine + Environment.NewLine + content;
        File.WriteAllText(fullPath, content);
        applied.Add(relativePath);
        onOutput?.Invoke($"✅ [{result.RuleId}] Prepended badge to {relativePath}");
    }

    /// <summary>
    /// Replaces the entire content of a file.
    /// </summary>
    public static void ReplaceFileContent(
        string localPath,
        string relativePath,
        string newContent,
        RuleResult result,
        List<string> applied,
        Action<string>? onOutput)
    {
        var fullPath = ResolvePath(localPath, relativePath);
        EnsureDirectory(fullPath);
        File.WriteAllText(fullPath, newContent);
        applied.Add(relativePath);
        onOutput?.Invoke($"✅ [{result.RuleId}] Replaced content of {relativePath}");
    }

    /// <summary>
    /// Replaces a text string in a file.
    /// </summary>
    public static void ReplaceInFile(
        string localPath,
        string relativePath,
        string oldText,
        string newText,
        RuleResult result,
        List<string> applied,
        Action<string>? onOutput)
    {
        var fullPath = ResolvePath(localPath, relativePath);
        if (!File.Exists(fullPath))
        {
            onOutput?.Invoke($"⏭️ [{result.RuleId}] {relativePath} does not exist — cannot replace.");
            return;
        }

        var content = File.ReadAllText(fullPath);
        if (!content.Contains(oldText, StringComparison.OrdinalIgnoreCase))
        {
            onOutput?.Invoke($"⏭️ [{result.RuleId}] {relativePath} does not contain '{oldText}' — skipping.");
            return;
        }

        content = content.Replace(oldText, newText, StringComparison.OrdinalIgnoreCase);
        File.WriteAllText(fullPath, content);
        applied.Add(relativePath);
        onOutput?.Invoke($"✅ [{result.RuleId}] Replaced '{oldText}' with '{newText}' in {relativePath}");
    }

    /// <summary>
    /// Adds a PackageVersion entry to Directory.Packages.props.
    /// </summary>
    public static void AddPackageVersion(
        string localPath,
        string packageName,
        RuleResult result,
        List<string> applied,
        Action<string>? onOutput)
    {
        const string relativePath = "Directory.Packages.props";
        var fullPath = ResolvePath(localPath, relativePath);
        if (!File.Exists(fullPath))
        {
            onOutput?.Invoke($"⏭️ [{result.RuleId}] {relativePath} does not exist — cannot add package version.");
            return;
        }

        var xml = File.ReadAllText(fullPath);
        if (xml.Contains(packageName, StringComparison.OrdinalIgnoreCase))
        {
            onOutput?.Invoke($"⏭️ [{result.RuleId}] {relativePath} already references {packageName} — skipping.");
            return;
        }

        try
        {
            var doc = XDocument.Parse(xml);
            var itemGroup = doc.Root?.Elements("ItemGroup")
                .FirstOrDefault(ig => ig.Elements("PackageVersion").Any());

            if (itemGroup is null)
            {
                itemGroup = new XElement("ItemGroup");
                doc.Root?.Add(itemGroup);
            }

            itemGroup.Add(new XElement("PackageVersion",
                new XAttribute("Include", packageName),
                new XAttribute("Version", "*")));

            doc.Save(fullPath);
            applied.Add(relativePath);
            onOutput?.Invoke($"✅ [{result.RuleId}] Added <PackageVersion Include=\"{packageName}\" Version=\"*\" /> to {relativePath}");
        }
        catch (Exception ex)
        {
            onOutput?.Invoke($"❌ [{result.RuleId}] Failed to modify {relativePath}: {ex.Message}");
        }
    }

    /// <summary>
    /// Removes Version attributes from PackageReference elements in .csproj files.
    /// </summary>
    public static void RemovePackageReferenceVersions(
        string localPath,
        string[] projectPaths,
        RuleResult result,
        List<string> applied,
        Action<string>? onOutput)
    {
        foreach (var projectPath in projectPaths)
        {
            var fullPath = ResolvePath(localPath, projectPath);
            if (!File.Exists(fullPath))
            {
                continue;
            }

            try
            {
                var doc = XDocument.Load(fullPath);
                var changed = false;
                foreach (var pkgRef in doc.Descendants("PackageReference"))
                {
                    var versionAttr = pkgRef.Attribute("Version");
                    if (versionAttr is not null)
                    {
                        versionAttr.Remove();
                        changed = true;
                    }

                    // Also remove <Version> child element
                    var versionElement = pkgRef.Element("Version");
                    if (versionElement is not null)
                    {
                        versionElement.Remove();
                        changed = true;
                    }
                }

                if (changed)
                {
                    doc.Save(fullPath);
                    applied.Add(projectPath);
                    onOutput?.Invoke($"✅ [{result.RuleId}] Removed Version attributes from PackageReference in {projectPath}");
                }
            }
            catch (Exception ex)
            {
                onOutput?.Invoke($"❌ [{result.RuleId}] Failed to modify {projectPath}: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// Adds file entries to the Solution Items folder in a .slnx file.
    /// </summary>
    public static void AddSlnxFileEntries(
        string localPath,
        string relativePath,
        string[] missingFiles,
        RuleResult result,
        List<string> applied,
        Action<string>? onOutput)
    {
        var fullPath = ResolvePath(localPath, relativePath);
        if (!File.Exists(fullPath))
        {
            onOutput?.Invoke($"⏭️ [{result.RuleId}] {relativePath} does not exist — cannot add entries.");
            return;
        }

        try
        {
            var doc = XDocument.Load(fullPath);
            var solutionItemsFolder = doc.Root?
                .Elements("Folder")
                .FirstOrDefault(f =>
                {
                    var name = f.Attribute("Name")?.Value;
                    return name is not null &&
                        name.Contains("Solution Items", StringComparison.OrdinalIgnoreCase);
                });

            if (solutionItemsFolder is null)
            {
                solutionItemsFolder = new XElement("Folder", new XAttribute("Name", "/Solution Items/"));
                doc.Root?.Add(solutionItemsFolder);
            }

            var addedCount = 0;
            foreach (var missingFile in missingFiles)
            {
                var alreadyExists = solutionItemsFolder.Elements("File")
                    .Any(el => string.Equals(el.Attribute("Path")?.Value, missingFile, StringComparison.OrdinalIgnoreCase));
                if (alreadyExists)
                {
                    continue;
                }

                solutionItemsFolder.Add(new XElement("File", new XAttribute("Path", missingFile)));
                addedCount++;
            }

            if (addedCount > 0)
            {
                doc.Save(fullPath);
                applied.Add(relativePath);
                onOutput?.Invoke($"✅ [{result.RuleId}] Added {addedCount} file entries to Solution Items in {relativePath}");
            }
            else
            {
                onOutput?.Invoke($"⏭️ [{result.RuleId}] All files already in Solution Items — skipping.");
            }
        }
        catch (Exception ex)
        {
            onOutput?.Invoke($"❌ [{result.RuleId}] Failed to modify {relativePath}: {ex.Message}");
        }
    }

    /// <summary>
    /// Resolves a relative path to a full path under the local repo root.
    /// </summary>
    public static string ResolvePath(string localPath, string relativePath)
        => Path.Combine(localPath, relativePath.Replace('/', Path.DirectorySeparatorChar));

    /// <summary>
    /// Ensures the directory for a file path exists.
    /// </summary>
    public static void EnsureDirectory(string fullPath)
    {
        var dir = Path.GetDirectoryName(fullPath);
        if (dir is not null && !Directory.Exists(dir))
        {
            Directory.CreateDirectory(dir);
        }
    }
}
