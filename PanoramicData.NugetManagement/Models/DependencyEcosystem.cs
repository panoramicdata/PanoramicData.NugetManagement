namespace PanoramicData.NugetManagement.Models;

/// <summary>
/// The kind of thing a dependency is, which decides where its declared version is read from.
/// </summary>
public enum DependencyEcosystem
{
	/// <summary>Not a kind this application can reason about.</summary>
	Unknown,

	/// <summary>A NuGet package, declared in a project file or <c>Directory.Packages.props</c>.</summary>
	NuGet,

	/// <summary>A GitHub Action, declared by a <c>uses:</c> step in a workflow.</summary>
	GitHubActions
}
