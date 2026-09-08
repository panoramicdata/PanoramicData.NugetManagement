using PanoramicData.NugetManagement.Models;
using PanoramicData.NugetManagement.Services;

namespace PanoramicData.NugetManagement.Test;

/// <summary>
/// Tests for <see cref="DependencyMentionScanner"/>: whether a repository refers to a dependency
/// anywhere it could be declaring one.
/// </summary>
/// <remarks>
/// The question this answers is deliberately weaker than the one
/// <see cref="PackageReferenceScanner"/> answers, and the difference is the whole point: a triage
/// verdict that closes a pull request on the strength of "we could not find it" is only safe if
/// "found" is as generous as it can be made.
/// </remarks>
public class DependencyMentionScannerTests(ITestOutputHelper output) : TestWithOutput(output)
{
	private static RepositoryContext Ctx(params (string Path, string Content)[] files) => new()
	{
		FullName = "panoramicdata/Athonet.Api",
		Name = "Athonet.Api",
		DefaultBranch = "main",
		CurrentBranch = "main",
		Options = new RepoOptions(),
		FilePaths = [.. files.Select(f => f.Path)],
		FileContents = files.ToDictionary(f => f.Path, f => f.Content, StringComparer.OrdinalIgnoreCase)
	};

	private static bool Mentions(RepositoryContext context, string name)
		=> DependencyMentionScanner.Mentions(
			context, new DependencyRef(DependencyEcosystem.NuGet, name));

	private static bool MentionsAction(RepositoryContext context, string name)
		=> DependencyMentionScanner.Mentions(
			context, new DependencyRef(DependencyEcosystem.GitHubActions, name));

	[Fact]
	public void APackageVersionInTheCentralPropsFile_IsMentioned()
		=> Mentions(
				Ctx(("Directory.Packages.props",
					"""<Project><ItemGroup><PackageVersion Include="refit" Version="7.2.22" /></ItemGroup></Project>""")),
				"refit")
			.Should().BeTrue();

	[Fact]
	public void APackageReferenceCarryingNoVersion_IsMentioned()
		=> Mentions(
				Ctx(("src/Sample.csproj",
					"""<Project><ItemGroup><PackageReference Include="refit" /></ItemGroup></Project>""")),
				"refit")
			.Should().BeTrue(
				"central package management leaves the version off the reference, and a package the "
				+ "version scanner cannot read is still one the repository uses");

	[Fact]
	public void AToolInTheDotnetToolsManifest_IsMentioned()
		=> Mentions(
				Ctx((".config/dotnet-tools.json",
					"""{ "tools": { "nbgv": { "version": "3.10.94" } } }""")),
				"nbgv")
			.Should().BeTrue(
				"no rule reads the tool manifest, but Dependabot does — so a pull request about nbgv is "
				+ "about something the repository genuinely has");

	[Fact]
	public void APackageInAProjectTheAssessmentSkips_IsMentioned()
		=> Mentions(
				Ctx(("samples/Sample/Sample.csproj",
					"""<Project><ItemGroup><PackageReference Include="refit" Version="7.2.22" /></ItemGroup></Project>""")),
				"refit")
			.Should().BeTrue(
				"the scan reads every path, not the filtered set the rules assess — a sample project is "
				+ "still somewhere a dependency lives");

	[Fact]
	public void APackageInPackagesConfig_IsMentioned()
		=> Mentions(
				Ctx(("src/Legacy/packages.config",
					"""<packages><package id="refit" version="7.2.22" /></packages>""")),
				"refit")
			.Should().BeTrue();

	[Fact]
	public void APackageNamedNowhere_IsNotMentioned()
		=> Mentions(
				Ctx(("Directory.Packages.props",
					"""<Project><ItemGroup><PackageVersion Include="refit" Version="7.2.22" /></ItemGroup></Project>""")),
				"coverlet.collector")
			.Should().BeFalse();

	[Fact]
	public void APackageNamedOnlyInProse_IsNotMentioned()
		=> Mentions(
				Ctx(
					("Directory.Packages.props",
						"""<Project><ItemGroup><PackageVersion Include="refit" Version="7.2.22" /></ItemGroup></Project>"""),
					("README.md", "We used to use coverlet.collector for coverage."),
					(".gitignore", "# Coverlet is a free, cross platform Code Coverage Tool")),
				"coverlet.collector")
			.Should().BeFalse(
				"prose is not a declaration site, and treating it as one would keep a stale pull "
				+ "request open forever on the strength of a line in a README");

	[Fact]
	public void APackageWhoseNamePrefixesAnotherPackage_IsMentioned()
		=> Mentions(
				Ctx(("Directory.Packages.props",
					"""<Project><ItemGroup><PackageVersion Include="Serilog.Sinks.Console" Version="6.0.0" /></ItemGroup></Project>""")),
				"Serilog")
			.Should().BeTrue(
				"a prefix match is a false sighting, and a false sighting only ever leaves a pull "
				+ "request open — the direction this scan is allowed to be wrong in");

	[Fact]
	public void AnActionUsedByAWorkflow_IsMentioned()
		=> MentionsAction(
				Ctx((".github/workflows/ci.yml", "jobs:\n  build:\n    steps:\n    - uses: actions/checkout@v5\n")),
				"actions/checkout")
			.Should().BeTrue();

	[Fact]
	public void AnActionNoWorkflowUses_IsNotMentioned()
		=> MentionsAction(
				Ctx((".github/workflows/ci.yml", "jobs:\n  build:\n    steps:\n    - uses: actions/checkout@v5\n")),
				"github/codeql-action")
			.Should().BeFalse();

	[Fact]
	public void AnActionUsedByACompositeAction_IsMentioned()
		=> MentionsAction(
				Ctx((".github/actions/setup/action.yml", "runs:\n  steps:\n    - uses: actions/setup-dotnet@v5\n")),
				"actions/setup-dotnet")
			.Should().BeTrue(
				"a composite action is a workflow's worth of uses, and Dependabot updates it too");

	[Fact]
	public void AContextListingNoFilesAtAll_CannotProveAbsence()
		=> Mentions(Ctx(), "refit").Should().BeTrue(
			"a context that lists not one file is a failed read of the repository rather than an empty "
			+ "repository, and answering 'absent' to it would close every open Dependabot pull request "
			+ "in the estate in a single pass");

	[Fact]
	public void AnActionInARepositoryThatStillHasFiles_ButNoWorkflows_IsNotMentioned()
		=> MentionsAction(
				Ctx(("Directory.Packages.props", "<Project />")),
				"github/codeql-action")
			.Should().BeFalse(
				"the repository was read and has no workflows left, which is exactly what deleting the "
				+ "last workflow that used an action looks like");

	/// <summary>
	/// A repository where a declaration site is listed but its content was never fetched — the
	/// ordinary state of any file the context builders do not read.
	/// </summary>
	private static RepositoryContext CtxWithUnreadPath(
		string unreadPath,
		params (string Path, string Content)[] files)
		=> new()
		{
			FullName = "panoramicdata/Athonet.Api",
			Name = "Athonet.Api",
			DefaultBranch = "main",
			CurrentBranch = "main",
			Options = new RepoOptions(),
			FilePaths = [.. files.Select(f => f.Path), unreadPath],
			FileContents = files.ToDictionary(f => f.Path, f => f.Content, StringComparer.OrdinalIgnoreCase)
		};

	[Fact]
	public void ADeclarationSiteWhoseContentWasNotRead_CountsAsAMention()
		=> Mentions(
				CtxWithUnreadPath(
					".config/dotnet-tools.json",
					("Directory.Packages.props",
						"""<Project><ItemGroup><PackageVersion Include="refit" Version="7.2.22" /></ItemGroup></Project>""")),
				"nbgv")
			.Should().BeTrue(
				"an unread file is not an empty one, and a verdict that closes pull requests must never "
				+ "rest on a file nobody looked in");

	[Fact]
	public void AFileThatIsNotADeclarationSite_BeingUnread_ChangesNothing()
		=> Mentions(
				CtxWithUnreadPath(
					"docs/architecture.md",
					("Directory.Packages.props",
						"""<Project><ItemGroup><PackageVersion Include="refit" Version="7.2.22" /></ItemGroup></Project>""")),
				"nbgv")
			.Should().BeFalse(
				"only the files a dependency could be declared in need to have been read");
}
