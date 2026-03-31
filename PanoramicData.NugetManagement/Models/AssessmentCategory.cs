namespace PanoramicData.NugetManagement.Models;

/// <summary>
/// Categories of assessment rules.
/// </summary>
public enum AssessmentCategory
{
	/// <summary>
	/// CI/CD pipeline configuration.
	/// </summary>
	CiCd,

	/// <summary>
	/// Version management (Nerdbank.GitVersioning etc.).
	/// </summary>
	Versioning,

	/// <summary>
	/// Centralized Package Management.
	/// </summary>
	CentralPackageManagement,

	/// <summary>
	/// NuGet package hygiene (up-to-date, snupkg, etc.).
	/// </summary>
	NuGetHygiene,

	/// <summary>
	/// Target framework version.
	/// </summary>
	TargetFramework,

	/// <summary>
	/// Build quality settings (warnings as errors, nullable, etc.).
	/// </summary>
	BuildQuality,

	/// <summary>
	/// Code quality and analysis tools (Codacy, editorconfig).
	/// </summary>
	CodeQuality,

	/// <summary>
	/// Testing framework and practices.
	/// </summary>
	Testing,

	/// <summary>
	/// Serialization library choices.
	/// </summary>
	Serialization,

	/// <summary>
	/// Licensing and legal compliance.
	/// </summary>
	Licensing,

	/// <summary>
	/// README content and badges.
	/// </summary>
	ReadmeBadges,

	/// <summary>
	/// Repository hygiene (.gitignore, secrets, etc.).
	/// </summary>
	RepositoryHygiene,

	/// <summary>
	/// NuGet package project metadata.
	/// </summary>
	ProjectMetadata,

	/// <summary>
	/// HTTP client library choices.
	/// </summary>
	HttpClient,

	/// <summary>
	/// XML documentation comments.
	/// </summary>
	Documentation,

	/// <summary>
	/// Dependency update automation (Dependabot/Renovate).
	/// </summary>
	DependencyAutomation,

	/// <summary>
	/// Community health files (SECURITY.md, CONTRIBUTING.md).
	/// </summary>
	CommunityHealth
}
