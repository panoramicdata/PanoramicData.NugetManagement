namespace PanoramicData.NugetManagement.Models;

/// <summary>
/// Structured advisory data emitted by a governance rule when it fails.
/// Designed for consumption by AI agents performing automated remediation.
/// </summary>
public sealed class RuleAdvisory
{
    /// <summary>
    /// One-line summary suitable for a bullet point in an AI prompt.
    /// Example: "Add SECURITY.md with the standard security policy content"
    /// </summary>
    public required string Summary { get; init; }

    /// <summary>
    /// Detailed multi-line advisory in markdown, suitable for an AI agent
    /// to understand the full context and perform the remediation.
    /// May include code snippets, file paths, expected content, etc.
    /// </summary>
    public required string Detail { get; init; }

    /// <summary>
    /// Structured key/value data providing machine-readable context.
    /// Keys use snake_case naming (e.g. "missing_files", "outdated_packages").
    /// Values may be strings, string arrays, or dictionaries for complex data.
    /// Consumers can use these to build targeted prompts or filter/group advisories.
    /// </summary>
    public Dictionary<string, object> Data { get; init; } = [];
}
