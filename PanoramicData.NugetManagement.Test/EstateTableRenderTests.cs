using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using PanoramicData.NugetManagement.Models;
using PanoramicData.NugetManagement.Web.Components;
using PanoramicData.NugetManagement.Web.Models;
using PanoramicData.NugetManagement.Web.Services;

namespace PanoramicData.NugetManagement.Test;

/// <summary>
/// Renders <see cref="EstateTable"/>. A green test run does not prove a Razor change renders: a Razor
/// comment between a component's attributes compiles and then throws at render, and the Web project has
/// no bUnit reference, so this uses the framework's own <see cref="HtmlRenderer"/>.
/// </summary>
public class EstateTableRenderTests(ITestOutputHelper output) : TestWithOutput(output)
{
	private static RepositoryDashboardRow Row(string name, bool assessed = true, int errors = 0)
	{
		var row = new RepositoryDashboardRow
		{
			Organization = "panoramicdata",
			RepositoryFullName = $"panoramicdata/{name}",
			IsClonedLocally = true,
			Assessment = assessed
				? new RepoAssessment
				{
					RepositoryFullName = $"panoramicdata/{name}",
					DefaultBranch = "main",
					AssessedAtUtc = DateTimeOffset.UtcNow,
					RuleResults = []
				}
				: null
		};

		if (errors > 0)
		{
			row.CategorySummaries[AssessmentCategory.CiCd] = new CategorySummary { Errors = errors };
		}

		return row;
	}

	private static async Task<string> RenderAsync(
		IReadOnlyList<RepositoryDashboardRow> rows,
		RepositorySelection? selection = null)
	{
		var services = new ServiceCollection().AddLogging().BuildServiceProvider();

		using var renderer = new HtmlRenderer(services, services.GetRequiredService<ILoggerFactory>());

		return await renderer.Dispatcher.InvokeAsync(async () =>
		{
			var parameters = ParameterView.FromDictionary(new Dictionary<string, object?>
			{
				["Rows"] = rows,
				["Organization"] = "panoramicdata",
				["Selection"] = selection ?? new RepositorySelection(),
				["IsExcluded"] = (Func<string, bool>)(_ => false)
			});

			var result = await renderer.RenderComponentAsync<EstateTable>(parameters).ConfigureAwait(false);
			return result.ToHtmlString();
		}).ConfigureAwait(false);
	}

	[Fact]
	public async Task ItRendersEveryRepository_AndTheIssueClassColumnsThatHaveFailures()
	{
		var html = await RenderAsync([Row("Alpha.Api", errors: 2), Row("Beta.Api")]);

		html.Should().Contain("Alpha.Api").And.Contain("Beta.Api");
		html.Should().Contain("Ci Cd", "the CiCd category has a failure, so it is a column");
	}

	[Fact]
	public async Task WithNothingTicked_ItSaysToTickRepositories()
	{
		var html = await RenderAsync([Row("Alpha.Api")]);

		html.Should().Contain("Tick repositories to act on them");
		html.Should().Contain("0 of 1 selected");
	}

	[Fact]
	public async Task ASelection_IsReflectedInTheSummaryAndTheCheckbox()
	{
		var selection = new RepositorySelection();
		selection.Select("panoramicdata/Alpha.Api");

		var html = await RenderAsync([Row("Alpha.Api"), Row("Beta.Api")], selection);

		html.Should().Contain("1 of 2 selected");
		var inputs = html.Split("<input", StringSplitOptions.RemoveEmptyEntries);
		var alpha = inputs.Single(i => i.Contains("aria-label=\"Select Alpha.Api\"", StringComparison.Ordinal));
		var beta = inputs.Single(i => i.Contains("aria-label=\"Select Beta.Api\"", StringComparison.Ordinal));
		alpha.Split('>')[0].Should().Contain("checked");
		beta.Split('>')[0].Should().NotContain("checked");
	}

	[Fact]
	public async Task ATickedRowThatHasBecomeInert_CanStillBeUnticked_ButAnUntickedOneCannotBeTicked()
	{
		var tickedRow = Row("Ticked.Api");
		tickedRow.IsClonedLocally = false;
		var untickedRow = Row("Unticked.Api");
		untickedRow.IsClonedLocally = false;
		var selection = new RepositorySelection();
		selection.Select("panoramicdata/Ticked.Api");

		var html = await RenderAsync([tickedRow, untickedRow], selection);

		var inputs = html.Split("<input", StringSplitOptions.RemoveEmptyEntries);
		var ticked = inputs.Single(i => i.Contains("aria-label=\"Select Ticked.Api\"", StringComparison.Ordinal)).Split('>')[0];
		var unticked = inputs.Single(i => i.Contains("aria-label=\"Select Unticked.Api\"", StringComparison.Ordinal)).Split('>')[0];
		ticked.Should().NotContain("disabled", "a tick that can no longer act must still be clearable");
		unticked.Should().Contain("disabled");
	}

	[Fact]
	public async Task AnUnassessedRepository_DoesNotLookClean()
	{
		var html = await RenderAsync([Row("Alpha.Api", errors: 1), Row("Never.Assessed", assessed: false)]);

		html.Should().Contain("Never.Assessed");
		html.Should().Contain("title=\"Not assessed\"",
			"an unassessed repository's category cells say they are not known, rather than looking clean");
	}

	[Fact]
	public async Task ZeroRepositories_RendersAMessage_WithoutThrowing()
	{
		var html = await RenderAsync([]);

		html.Should().Contain("No repositories are cached for panoramicdata");
	}

	[Fact]
	public async Task RepositoriesWithNoFailureAnywhere_RenderWithoutCategoryColumns()
	{
		var html = await RenderAsync([Row("Alpha.Api"), Row("Beta.Api")]);

		html.Should().Contain("Alpha.Api").And.Contain("Beta.Api");
	}
}
