namespace PanoramicData.NugetManagement.Models;

/// <summary>
/// Publishing-related per-repository options.
/// </summary>
public class PublishingOptions
{
	/// <summary>
	/// Ref patterns that should be considered public releases by Nerdbank.GitVersioning.
	/// Defaults to main branch and numeric x.y.z tags.
	/// </summary>
	public List<string> PublicReleaseRefSpec { get; set; } =
	[
		"^refs/heads/main$",
		"^refs/tags/\\d+\\.\\d+\\.\\d+$"
	];
}
