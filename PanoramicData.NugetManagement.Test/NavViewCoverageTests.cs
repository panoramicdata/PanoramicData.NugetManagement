using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using PanoramicData.NugetManagement.Models;
using PanoramicData.NugetManagement.Web.Models;
using PanoramicData.NugetManagement.Web.Services;

namespace PanoramicData.NugetManagement.Test;

/// <summary>
/// Checks that every view the navigation tree can select has somewhere to render.
/// </summary>
/// <remarks>
/// RenderCurrentView ends in a default that falls back to the getting-started placeholder, so a
/// NavView with no case of its own does not fail loudly — the node selects, the breadcrumb and the
/// toolbar update, and the panel silently shows "Select an item from the navigation tree" as though
/// nothing had been selected. RepositoryDetail was in exactly that state: assigned to every
/// repository node, handled in the tooltip switch, and missing from the render switch.
///
/// The Web project has no bUnit reference, so the switch cannot be exercised by rendering it. This
/// reads the source instead, which is enough to catch a missing case.
/// </remarks>
public class NavViewCoverageTests(ITestOutputHelper output) : TestWithOutput(output), IDisposable
{
	private readonly string _temporaryDirectory = Path.Combine(
		Path.GetTempPath(),
		"nugetmanagement-tests",
		Guid.NewGuid().ToString("n"));

	[Fact]
	public void EveryViewTheTreeCanSelectShouldBeRenderedSomewhere()
	{
		var switchBody = ReadRenderCurrentViewSwitch();

		var unhandled = BuildTree()
			.Select(item => item.View)
			.Distinct()
			.Where(view => !switchBody.Contains($"case NavView.{view}:", StringComparison.Ordinal))
			.ToList();

		unhandled.Should().BeEmpty(
			"a view with no case falls through to the getting-started placeholder, which looks like nothing was selected");
	}

	[Fact]
	public void SelectingARepositoryShouldNotFallThroughToThePlaceholder()
		=> ReadRenderCurrentViewSwitch()
			.Should().Contain("case NavView.RepositoryDetail:",
				"selecting a repository must show its failing rules, not the getting-started text");

	/// <summary>
	/// The repository view is where the failing rules are read, so it is where Fix is pressed. The
	/// toolbar button lists the views it appears in, and RepositoryDetail was added to the tree
	/// without being added to that list — the button simply vanished, taking the queued, downstream-
	/// blocking Fix run with it and leaving only the per-category buttons.
	/// </summary>
	/// <remarks>
	/// Fix is no longer in this list. It used to name its views literally and was the button that went
	/// missing; it now derives them from <see cref="FixScope"/>, so the same guarantee is made by
	/// <c>FixScopeTests</c> asserting what Fix does on a repository — a real assertion about behaviour
	/// rather than a search for a string in markup. The two tests below hold that line.
	/// </remarks>
	[Theory]
	[InlineData("reassess")]
	public void RepositoryDetailShouldOfferTheWorkflowToolbarButton(string key)
		=> ReadToolbarButton(key)
			.Should().Contain("NavView.RepositoryDetail",
				"a step hidden on the repository view cannot be run from where its issues are read");

	/// <summary>
	/// The repository's own Issues branch is where its open pull requests are read, so it is where
	/// Dependabot triage has to be startable from. It was built with NavView.None, which selects
	/// cleanly and renders nothing — so the triage action existed but had nowhere to be pressed.
	/// </summary>
	[Fact]
	public void TheRepositorysIssuesBranchShouldHaveAViewOfItsOwn()
		=> BuildTree()
			.Single(item => item.Key == NavTreeDataProvider.RepoIssuesKey("panoramicdata/Governed.Api"))
			.View
			.Should().Be(NavView.RepositoryIssuesDetail,
				"selecting a repository's Issues branch must show its inbox, not nothing at all");

	/// <summary>
	/// The organisation node shared <c>NavView.Home</c> with the landing page, so nothing that takes only
	/// a view could tell "an organisation" from "nothing selected", which a bulk toolbar needs to.
	/// </summary>
	[Fact]
	public void TheOrganisationNodeShouldHaveAViewOfItsOwn()
		=> BuildTree()
			.Single(item => item.Key == NavTreeDataProvider.OrgKey("panoramicdata"))
			.View
			.Should().Be(NavView.Organisation,
				"the organisation node must be distinguishable from the landing page by view alone");

	/// <summary>
	/// The Repositories node was given a view but rejected by the selectability rule, so the table never
	/// showed there. Every node whose view shows the estate table must be selectable.
	/// </summary>
	[Fact]
	public void EveryNodeThatShowsTheEstateTableShouldBeSelectable()
	{
		var estateNodes = BuildTree()
			.Where(item => ToolbarScope.IsEstateWide(item.View))
			.ToList();

		estateNodes.Select(item => item.View).Distinct()
			.Should().BeEquivalentTo([NavView.Organisation, NavView.Issues, NavView.Repositories]);
		estateNodes.Should().OnlyContain(item => NavTreeDataProvider.IsSelectable(item));
		NavTreeDataProvider.IsSelectable(
			new NavItem { Key = "repos-loading:panoramicdata", Text = "Loading", View = NavView.None })
			.Should().BeFalse();
	}

	/// <summary>
	/// Fix is the only button that fixes things. Its visibility is driven by
	/// <see cref="FixScope"/> rather than a hand-maintained list of views, so that a view where Fix
	/// has work can never be one where the button is absent — the bug this whole area keeps producing.
	/// </summary>
	[Fact]
	public void TheFixButtonShouldDeriveItsVisibilityFromTheFixScope()
		=> ReadToolbarButton("fix")
			.Should().Contain("FixScope.For(_currentView).HasAnything",
				"a second hand-maintained list of views is how the button goes missing again");

	/// <summary>
	/// Rediscover Org is organisation-wide whatever is ticked, so it is offered at every node the table
	/// is on. Its visibility used to name the dashboard and the organisation's Home view only.
	/// </summary>
	[Fact]
	public void RediscoverOrgShouldBeOfferedAtEveryEstateWideNode()
		=> ReadToolbarButton("refresh")
			.Should().Contain("IsEstateWide",
				"the table is on three nodes, and the organisation-wide buttons belong on all of them");

	/// <summary>
	/// There is one Re-assess button, so it has to be visible wherever the table is, or one of the three
	/// nodes has no way to re-assess.
	/// </summary>
	[Fact]
	public void ReassessShouldBeOfferedAtEveryEstateWideNode()
		=> ReadToolbarButton("reassess")
			.Should().Contain("IsEstateWide");

	/// <summary>
	/// Fix with AI used to read only the single selected repository, so on the estate it was never
	/// offered at all.
	/// </summary>
	[Fact]
	public void FixWithAiShouldBeOfferedForTheTickedRepositories()
		=> ReadToolbarButton("fix-with-ai")
			.Should().Contain("IsEstateWide");

	[Fact]
	public void PublishShouldNeverBeOfferedForTheEstate()
		=> ReadToolbarButton("publish")
			.Should().NotContain("IsEstateWide",
				"a package pushed to nuget.org cannot be taken back, so it stays one repository at a time");

	/// <summary>
	/// The markup of one PDToolbarButton, from its Key to the end of the element.
	/// </summary>
	private static string ReadToolbarButton(string key)
	{
		var razor = File.ReadAllText(ResolveHomeRazorPath());

		var start = razor.IndexOf($"<PDToolbarButton Key=\"{key}\"", StringComparison.Ordinal);
		start.Should().BeGreaterThan(-1, $"the '{key}' toolbar button must exist for this test to mean anything");

		var end = razor.IndexOf("/>", start, StringComparison.Ordinal);
		end.Should().BeGreaterThan(start, "the button element must be closed");

		return razor[start..end];
	}

	/// <summary>
	/// The body of RenderCurrentView, from its declaration to the start of the next member.
	/// </summary>
	private static string ReadRenderCurrentViewSwitch()
	{
		var razor = File.ReadAllText(ResolveHomeRazorPath());

		var start = razor.IndexOf("private RenderFragment RenderCurrentView()", StringComparison.Ordinal);
		start.Should().BeGreaterThan(-1, "RenderCurrentView must exist for this test to mean anything");

		var end = razor.IndexOf("private RenderFragment RenderConsolePanel()", start, StringComparison.Ordinal);
		end.Should().BeGreaterThan(start, "the member after RenderCurrentView bounds the switch being read");

		return razor[start..end];
	}

	private static string ResolveHomeRazorPath()
	{
		var directory = AppContext.BaseDirectory;
		while (directory is not null)
		{
			var candidate = Path.Combine(
				directory,
				"PanoramicData.NugetManagement.Web",
				"Components",
				"Pages",
				"Home.razor");

			if (File.Exists(candidate))
			{
				return candidate;
			}

			directory = Directory.GetParent(directory)?.FullName;
		}

		throw new FileNotFoundException("Could not find Home.razor by walking up from the test assembly.");
	}

	private List<NavItem> BuildTree()
	{
		var rows = new List<RepositoryDashboardRow>
		{
			new()
			{
				Organization = "panoramicdata",
				RepositoryFullName = "panoramicdata/Governed.Api",
				Packages = [new() { PackageId = "Governed.Api" }],
				OpenIssues =
				[
					new()
					{
						Number = 1,
						Title = "Sample issue",
						HtmlUrl = "https://github.com/panoramicdata/Governed.Api/issues/1",
						AuthorLogin = "reporter",
						CreatedAtUtc = DateTimeOffset.UtcNow - TimeSpan.FromDays(400),
						LastMaintainerReplyUtc = DateTimeOffset.UtcNow - TimeSpan.FromDays(1)
					}
				],
				Assessment = new RepoAssessment
				{
					RepositoryFullName = "panoramicdata/Governed.Api",
					DefaultBranch = "main",
					AssessedAtUtc = DateTimeOffset.UtcNow,
					RuleResults =
					[
						new()
						{
							RuleId = "CQ-03",
							RuleName = "Codacy configured",
							Category = AssessmentCategory.CodeQuality,
							Severity = AssessmentSeverity.Error,
							Passed = false,
							Message = "Something is wrong."
						}
					]
				}
			}
		};

		Directory.CreateDirectory(_temporaryDirectory);
		var cache = new DashboardCacheService(
			NullLogger<DashboardCacheService>.Instance,
			Path.Combine(_temporaryDirectory, "dashboard-cache.json"));
		cache.SetRows(rows);

		var settings = Options.Create(new AppSettings { NuGetOrganization = "panoramicdata" });

		return new NavTreeDataProvider(
			cache,
			new RuntimeSettingsService(
				settings,
				NullLogger<RuntimeSettingsService>.Instance,
				Path.Combine(_temporaryDirectory, "runtime-settings.json")),
			settings).BuildNavItems();
	}

	/// <inheritdoc />
	public void Dispose()
	{
		try
		{
			if (Directory.Exists(_temporaryDirectory))
			{
				Directory.Delete(_temporaryDirectory, recursive: true);
			}
		}
		catch (IOException)
		{
			// A leftover temp directory is not worth failing a test over.
		}

		GC.SuppressFinalize(this);
	}
}
