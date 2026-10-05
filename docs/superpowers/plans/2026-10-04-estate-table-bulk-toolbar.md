# Repository Table With Multi-Select And A Toolbar That Acts On The Ticked Rows: Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** One shared table of every repository, with a column per issue class, git, build and test state and multi-select rows, shown at the organisation, Issues and Repositories nodes, with a toolbar whose steps act on the ticked rows.

**Architecture:** The page (`Home.razor`) owns one `RepositorySelection` per organisation and passes it to a shared `EstateTable` component. All decisions live in small pure, unit-tested types (`RepositorySelection`, `ToolbarScope`, `FixScope`, `EstateTableModel`, `AiFixTargets`), because the page itself cannot be unit tested. `Home.QueueAcrossEstate` stays the single choke point through which every step is queued, now intersected with the selection.

**Tech Stack:** .NET 10, Blazor Server (Razor components), xUnit v3 on Microsoft.Testing.Platform, AwesomeAssertions, `HtmlRenderer` (already in the ASP.NET Core framework, no new package).

**Spec:** [docs/superpowers/specs/2026-10-04-estate-table-bulk-toolbar-design.md](../specs/2026-10-04-estate-table-bulk-toolbar-design.md)

## Global Constraints

- Tabs for indentation in C#, Razor and JSON; file-scoped namespaces. The repository's own rules enforce both.
- Every public type and member in the library and Web projects carries an XML doc comment, or the build fails on CS1591.
- Tests use xUnit v3 with AwesomeAssertions (`result.Should().Be(...)`), the `public class X(ITestOutputHelper output) : TestWithOutput(output)` shape, and never `Assert.*`.
- `.ConfigureAwait(false)` on awaits in non-test async helpers (CA2007) but **never** inside a `[Fact]`/`[Theory]` method (xUnit1030).
- **Publish stays one repository at a time.** It is never estate-wide, whatever is ticked (`ToolbarScope.AllowsEstateWide`).
- **No ticks means nothing**, except **Re-assess** (read-only; falls back to the whole organisation) and **Rediscover Org** (always organisation-wide).
- **Confirmation** before **Commit & Push** and **Fix with AI** when more than one repository is a target. Sync, Fix, Build, Test and Re-assess run immediately.
- **Re-assess is one button**: "Re-assess *N*" with ticks, "Re-assess Org" without. There is no second button.
- No new packages. No new settings (so `RuntimeSettingsService`'s hand-written save snapshot is not touched).
- **Razor:** never put a Razor comment (`@* *@`) between a component's or element's attributes. It compiles and then throws at render as an unknown parameter name. Comments go between elements, or in C#.
- **Do not start the app.** Starting it restores the shared work queue and runs AI fixes against the real clones. Verify with `dotnet build` and `dotnet test` only.
- Build and test from the worktree directory, with plain single-step commands. Use `dotnet test` and read the exit code: 5 is a Microsoft.Testing.Platform configuration problem, 1 is usually a file lock, 2 is real failures.
- A filtered test run can pass without having run a new test. After each new test class, run it on its own and confirm the total is not zero.
- Do not weaken `TreatWarningsAsErrors`, and do not delete or skip a test to make a build pass.

## Review Focus

Inputs and conditions the spec implies but does not spell out, most likely to bite first. Each has a test in the task that owns the code.

1. **A repository ticked and later excluded, ungoverned or un-cloned** must be skipped *and counted* in the message, not silently dropped. (Task 3, `ToolbarScopeTests`.)
2. **Name-case mismatch** between selection keys and row names (GitHub reports canonical casing, caches may not). Matching must be case-insensitive. (Task 1, `RepositorySelectionTests`.)
3. **An unassessed repository, or an unknown git, build or test state, must never render as clean or good.** Unknown is its own state. (Task 5, `EstateTableModelTests`.)
4. **Ticked rows hidden by the filter.** A press acts on every ticked row, so the selection line must count the hidden ones, and "select all visible" must leave them alone. (Task 5, `EstateTableModelTests`.)
5. **Zero repositories, or no category with any failure.** The table renders (no empty-sequence or divide exceptions) and the toolbar stays disabled. (Task 5 model, Task 6 render test.)

---

## File Structure

**Create**
- `PanoramicData.NugetManagement.Web/Services/RepositorySelection.cs`: the ticked set; pure.
- `PanoramicData.NugetManagement.Web/Models/RepositoryTestState.cs`: `Passed` / `Failed`.
- `PanoramicData.NugetManagement.Web/Services/EstateTableModel.cs`: columns, cells, ordering, filtering; pure.
- `PanoramicData.NugetManagement.Web/Services/AiFixTargets.cs`: the Fix-with-AI logic lifted out of `Home`; pure.
- `PanoramicData.NugetManagement.Web/Components/EstateTable.razor`: the shared table.
- Tests: `RepositorySelectionTests.cs`, `RepositoryTestStatusTests.cs`, `EstateTableModelTests.cs`, `EstateTableRenderTests.cs`, `AiFixTargetsTests.cs`.

**Delete**
- `PanoramicData.NugetManagement.Web/Components/EstateView.razor`: replaced by `EstateTable`.

**Modify**
- `Models/NavItem.cs`: add `NavView.Organisation`.
- `Services/NavTreeDataProvider.cs`: the organisation node's view.
- `Services/ToolbarScope.cs`, `Services/FixScope.cs`: selection-aware scoping.
- `Models/RepositoryDashboardRow.cs`: test state fields and two helper methods.
- `Services/WorkExecutors.cs`, `Services/BuildStatusLifetime.cs`: record and forget the test result.
- `Components/Pages/Home.razor`: wiring only.
- Tests: `ToolbarScopeTests.cs`, `FixScopeTests.cs`, `NavViewCoverageTests.cs`.

All paths are under `PanoramicData.NugetManagement.Web/` or `PanoramicData.NugetManagement.Test/` in the worktree.

---

### Task 1: `RepositorySelection`

**Files:**
- Create: `PanoramicData.NugetManagement.Web/Services/RepositorySelection.cs`
- Test: `PanoramicData.NugetManagement.Test/RepositorySelectionTests.cs`

**Interfaces:**
- Consumes: nothing.
- Produces (later tasks rely on these exact names):
  - `enum SelectionState { None, Some, All }`
  - `sealed class RepositorySelection` with `int Count`, `IReadOnlyCollection<string> Names`, `bool Contains(string)`, `bool Toggle(string)` (returns the new ticked state), `void Select(string)`, `void Deselect(string)`, `void SelectAll(IEnumerable<string>)`, `void DeselectAll(IEnumerable<string>)`, `void Clear()`, `int Prune(IEnumerable<string> stillPresent)` (returns how many were dropped), `SelectionState StateOf(IReadOnlyCollection<string> visible)`.

- [ ] **Step 1: Write the failing tests**

Create `PanoramicData.NugetManagement.Test/RepositorySelectionTests.cs`:

```csharp
using PanoramicData.NugetManagement.Web.Services;

namespace PanoramicData.NugetManagement.Test;

/// <summary>
/// Tests for <see cref="RepositorySelection"/>: the set of repositories the toolbar will act on.
/// </summary>
public class RepositorySelectionTests(ITestOutputHelper output) : TestWithOutput(output)
{
	[Fact]
	public void Toggle_TicksThenUnticks()
	{
		var selection = new RepositorySelection();

		selection.Toggle("panoramicdata/A").Should().BeTrue("the first toggle ticks it");
		selection.Contains("panoramicdata/A").Should().BeTrue();

		selection.Toggle("panoramicdata/A").Should().BeFalse("the second toggle unticks it");
		selection.Count.Should().Be(0);
	}

	[Fact]
	public void Names_AreMatchedWithoutRegardToCase()
	{
		// GitHub reports a repository's canonical casing, and a cached row may carry another. A
		// selection that treated them as different repositories would silently act on fewer than the
		// user ticked.
		var selection = new RepositorySelection();
		selection.Select("panoramicdata/Cisco.Iq.Api");

		selection.Contains("PanoramicData/cisco.iq.api").Should().BeTrue();

		selection.Toggle("PANORAMICDATA/CISCO.IQ.API").Should().BeFalse("it is the same repository");
		selection.Count.Should().Be(0);
	}

	[Fact]
	public void SelectAll_AndDeselectAll_ActOnlyOnTheNamesGiven()
	{
		var selection = new RepositorySelection();
		selection.Select("panoramicdata/Hidden");

		selection.SelectAll(["panoramicdata/A", "panoramicdata/B"]);
		selection.Count.Should().Be(3);

		selection.DeselectAll(["panoramicdata/A", "panoramicdata/B"]);

		selection.Names.Should().ContainSingle().Which.Should().Be("panoramicdata/Hidden",
			"unticking what is visible must leave a ticked row the filter is hiding alone");
	}

	[Fact]
	public void Prune_DropsNamesThatNoLongerExist_AndSaysHowMany()
	{
		var selection = new RepositorySelection();
		selection.SelectAll(["panoramicdata/A", "panoramicdata/B", "panoramicdata/C"]);

		var dropped = selection.Prune(["panoramicdata/A", "panoramicdata/c"]);

		dropped.Should().Be(1, "B has gone, and c is C in another case");
		selection.Count.Should().Be(2);
	}

	[Fact]
	public void Clear_EmptiesTheSelection()
	{
		var selection = new RepositorySelection();
		selection.Select("panoramicdata/A");

		selection.Clear();

		selection.Count.Should().Be(0);
	}

	[Theory]
	[InlineData(0, SelectionState.None)]
	[InlineData(1, SelectionState.Some)]
	[InlineData(3, SelectionState.All)]
	public void StateOf_ReportsHowMuchOfWhatIsVisibleIsTicked(int ticked, SelectionState expected)
	{
		var visible = new[] { "panoramicdata/A", "panoramicdata/B", "panoramicdata/C" };
		var selection = new RepositorySelection();
		selection.SelectAll(visible.Take(ticked));

		selection.StateOf(visible).Should().Be(expected);
	}

	[Fact]
	public void StateOf_NothingVisible_IsNone()
	{
		var selection = new RepositorySelection();
		selection.Select("panoramicdata/A");

		selection.StateOf([]).Should().Be(SelectionState.None,
			"with no visible rows there is nothing for a header checkbox to be ticked about");
	}
}
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test --filter "FullyQualifiedName~RepositorySelectionTests"`
Expected: build failure, `RepositorySelection` and `SelectionState` do not exist.

- [ ] **Step 3: Write the implementation**

Create `PanoramicData.NugetManagement.Web/Services/RepositorySelection.cs`:

```csharp
namespace PanoramicData.NugetManagement.Web.Services;

/// <summary>
/// How much of what is visible is ticked, for a header checkbox that has three faces.
/// </summary>
public enum SelectionState
{
	/// <summary>Nothing visible is ticked, or nothing is visible.</summary>
	None,

	/// <summary>Some, but not all, of what is visible is ticked.</summary>
	Some,

	/// <summary>Everything visible is ticked.</summary>
	All
}

/// <summary>
/// The repositories the toolbar will act on: the ones ticked in the table.
/// </summary>
/// <remarks>
/// Names are compared without regard to case, because GitHub reports a repository's canonical casing
/// and a cached row may carry another; treating them as different repositories would quietly act on
/// fewer than were ticked. A separate type rather than state on the page, for the reason
/// <see cref="ToolbarScope"/> gives: the page cannot be unit tested, and this is the part worth being
/// sure of.
/// </remarks>
public sealed class RepositorySelection
{
	private readonly HashSet<string> _names = new(StringComparer.OrdinalIgnoreCase);

	/// <summary>How many repositories are ticked.</summary>
	public int Count => _names.Count;

	/// <summary>The ticked repositories' full names.</summary>
	public IReadOnlyCollection<string> Names => _names;

	/// <summary>Whether a repository is ticked.</summary>
	/// <param name="repositoryFullName">The repository, as "owner/name".</param>
	public bool Contains(string repositoryFullName) => _names.Contains(repositoryFullName);

	/// <summary>Ticks a repository that is not ticked, and unticks one that is.</summary>
	/// <param name="repositoryFullName">The repository, as "owner/name".</param>
	/// <returns>Whether it is ticked afterwards.</returns>
	public bool Toggle(string repositoryFullName)
	{
		if (_names.Remove(repositoryFullName))
		{
			return false;
		}

		_names.Add(repositoryFullName);
		return true;
	}

	/// <summary>Ticks a repository.</summary>
	/// <param name="repositoryFullName">The repository, as "owner/name".</param>
	public void Select(string repositoryFullName) => _names.Add(repositoryFullName);

	/// <summary>Unticks a repository.</summary>
	/// <param name="repositoryFullName">The repository, as "owner/name".</param>
	public void Deselect(string repositoryFullName) => _names.Remove(repositoryFullName);

	/// <summary>Ticks every named repository, leaving any other ticks as they are.</summary>
	/// <param name="repositoryFullNames">The repositories to tick.</param>
	public void SelectAll(IEnumerable<string> repositoryFullNames)
	{
		foreach (var name in repositoryFullNames)
		{
			_names.Add(name);
		}
	}

	/// <summary>Unticks every named repository, leaving any other ticks as they are.</summary>
	/// <param name="repositoryFullNames">The repositories to untick.</param>
	public void DeselectAll(IEnumerable<string> repositoryFullNames)
	{
		foreach (var name in repositoryFullNames)
		{
			_names.Remove(name);
		}
	}

	/// <summary>Unticks everything.</summary>
	public void Clear() => _names.Clear();

	/// <summary>
	/// Drops ticks for repositories that no longer exist, so a re-assess or a removal cannot leave the
	/// toolbar naming something that is not there.
	/// </summary>
	/// <param name="stillPresent">Every repository that exists now.</param>
	/// <returns>How many ticks were dropped.</returns>
	public int Prune(IEnumerable<string> stillPresent)
	{
		var keep = new HashSet<string>(stillPresent, StringComparer.OrdinalIgnoreCase);
		return _names.RemoveWhere(name => !keep.Contains(name));
	}

	/// <summary>How much of <paramref name="visible"/> is ticked.</summary>
	/// <param name="visible">The repositories currently on screen.</param>
	public SelectionState StateOf(IReadOnlyCollection<string> visible)
	{
		if (visible.Count == 0)
		{
			return SelectionState.None;
		}

		var ticked = visible.Count(Contains);

		return ticked == 0
			? SelectionState.None
			: ticked == visible.Count ? SelectionState.All : SelectionState.Some;
	}
}
```

- [ ] **Step 4: Run to verify it passes**

Run: `dotnet test --filter "FullyQualifiedName~RepositorySelectionTests"`
Expected: PASS. Confirm the total is **9** (1 + 1 + 1 + 1 + 1 + 3 + 1) and not zero.

- [ ] **Step 5: Commit**

```bash
git add PanoramicData.NugetManagement.Web/Services/RepositorySelection.cs PanoramicData.NugetManagement.Test/RepositorySelectionTests.cs
git commit -m "feat: a selection of repositories for the toolbar to act on"
```

---

### Task 2: `NavView.Organisation`

The organisation node currently uses `NavView.Home`, shared with the landing page, so `ToolbarScope` and `FixScope`, which take only a `NavView`, cannot tell "an organisation" from "nothing selected". This task gives it a view of its own and moves every use that meant the organisation node. It touches shared behaviour, so it comes before anything depends on it.

**Files:**
- Modify: `PanoramicData.NugetManagement.Web/Models/NavItem.cs`
- Modify: `PanoramicData.NugetManagement.Web/Services/NavTreeDataProvider.cs` (line ~326)
- Modify: `PanoramicData.NugetManagement.Web/Components/Pages/Home.razor`
- Test: `PanoramicData.NugetManagement.Test/NavViewCoverageTests.cs`

**Interfaces:**
- Consumes: nothing.
- Produces: `NavView.Organisation`, assigned to the organisation root node; rendered (for now) like the landing guidance, replaced by the table in Task 6.

The audit result, from reading every `NavView.Home` use in the solution. **Moves to `Organisation`:** the provider's org node, `ShowBulkRunButtons`, the fallback in `NavigateToOrgAsync`, and the tooltip case. **Stays `Home`** (all mean the landing page): the field initialiser (`_currentView = NavView.Home` at declaration), the breadcrumb root test, the two resets in `OnAfterRender`-style init and `NavigateToDashboard`, and the render case.

- [ ] **Step 1: Write the failing test**

Add to `NavViewCoverageTests.cs`, after `TheRepositorysIssuesBranchShouldHaveAViewOfItsOwn`:

```csharp
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
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test --filter "FullyQualifiedName~NavViewCoverageTests"`
Expected: build failure, `NavView.Organisation` does not exist.

- [ ] **Step 3: Add the enum value**

In `NavItem.cs`, immediately before the `Repositories,` member of `NavView` (after the `Repositories` doc comment block ends, add this *new* member after `Repositories,`):

```csharp
	/// <summary>
	/// One organisation: its repositories in a table, with a toolbar that acts on the ones ticked.
	/// </summary>
	/// <remarks>
	/// A view of its own rather than <see cref="Home"/>, for the reason <see cref="Repositories"/> has
	/// one: <see cref="Home"/> is also the landing page, and a toolbar scope that takes only a view cannot
	/// tell "an organisation" from "nothing selected".
	/// </remarks>
	Organisation,
```

- [ ] **Step 4: Move the provider's organisation node**

In `NavTreeDataProvider.cs`, in the `items.Add(new NavItem { Key = orgKey, ...` block, change:

```csharp
			View = NavView.Home,
			Organization = organization,
```
to:
```csharp
			View = NavView.Organisation,
			Organization = organization,
```

- [ ] **Step 5: Move the uses in `Home.razor` that meant the organisation node**

Use Grep to locate each, view the lines, and edit with the exact whitespace shown.

1. `ShowBulkRunButtons`. Replace
```csharp
		=> _currentView == NavView.Dashboard || (_currentView == NavView.Home && _orgSelected);
```
with
```csharp
		=> _currentView is NavView.Dashboard or NavView.Organisation;
```

2. The fallback in `NavigateToOrgAsync` (the `else` branch that runs when the org node is not in the tree). Replace
```csharp
				_currentView = NavView.Home;
				_selectedRow = null;
				_selectedNodeKey = orgKey;
```
with
```csharp
				_currentView = NavView.Organisation;
				_selectedRow = null;
				_selectedNodeKey = orgKey;
```

3. The tooltip switch in `GetNavItemTooltip`. Replace
```csharp
			case NavView.Home:
				// Only what is governed is counted: the rest are accounted for in their own branch, and
```
with
```csharp
			case NavView.Home:
			case NavView.Organisation:
				// Only what is governed is counted: the rest are accounted for in their own branch, and
```

4. The render switch in `RenderCurrentView`. Replace
```csharp
			case NavView.Home:
			case NavView.None:
				@RenderGettingStarted()
				break;
```
with
```csharp
			case NavView.Home:
			case NavView.None:
			case NavView.Organisation:
				@RenderGettingStarted()
				break;
```
(The organisation case shows the existing guidance until Task 6 replaces it with the table, so the coverage test passes in between.)

- [ ] **Step 6: Build and fix any non-exhaustive switch**

Run: `dotnet build PanoramicData.NugetManagement.Web/PanoramicData.NugetManagement.Web.csproj`
Expected: succeeds. If a `switch` expression over `NavView` with no default now warns (CS8509, which is an error here), add `NavView.Organisation` to the arm that handles `NavView.Home` or `NavView.Repositories` as the surrounding comment dictates, and say which in the commit message.

- [ ] **Step 7: Run the tests**

Run: `dotnet test --filter "FullyQualifiedName~NavViewCoverageTests"`
Expected: PASS, including the new test and `EveryViewTheTreeCanSelectShouldBeRenderedSomewhere` (which proves the new view has a render case).

- [ ] **Step 8: Commit**

```bash
git add PanoramicData.NugetManagement.Web/Models/NavItem.cs PanoramicData.NugetManagement.Web/Services/NavTreeDataProvider.cs PanoramicData.NugetManagement.Web/Components/Pages/Home.razor PanoramicData.NugetManagement.Test/NavViewCoverageTests.cs
git commit -m "refactor: give the organisation node a view of its own, apart from the landing page"
```

---

### Task 3: `ToolbarScope`, `FixScope` and the breadcrumb

**Files:**
- Modify: `PanoramicData.NugetManagement.Web/Services/ToolbarScope.cs` (replace whole file)
- Modify: `PanoramicData.NugetManagement.Web/Services/FixScope.cs`
- Modify: `PanoramicData.NugetManagement.Web/Components/Pages/Home.razor` (minimal, to keep it compiling)
- Test: `PanoramicData.NugetManagement.Test/ToolbarScopeTests.cs`, `PanoramicData.NugetManagement.Test/FixScopeTests.cs`

**Interfaces:**
- Consumes: `RepositorySelection` (Task 1), `NavView.Organisation` (Task 2).
- Produces:
  - `ToolbarScope.IsEstateWide(NavView)`: true for `Repositories`, `Issues`, `Organisation`.
  - `ToolbarScope.AllowsEstateWide(WorkflowStep)` (unchanged), `ToolbarScope.FallsBackToWholeOrganisation(WorkflowStep)`, `ToolbarScope.RequiresClone(WorkflowStep)`.
  - `ToolbarScope.Targets(IEnumerable<RepositoryDashboardRow> rows, WorkflowStep step, Func<string,bool> isExcluded, RepositorySelection selection)` returning `List<RepositoryDashboardRow>`.
  - `ToolbarScope.DescribeSelection(string stepName, int targetCount, int selectedCount)` returning `string`. **Replaces** `Describe`.
  - `ToolbarScope.ConfirmAboveRepositoryCount` (const `int`, 1), `ToolbarScope.RequiresConfirmation(WorkflowStep, int)`, `ToolbarScope.RequiresAiFixConfirmation(int)`, `ToolbarScope.ConfirmationMessage(string stepName, IReadOnlyList<string> repositoryNames)`.
  - `FixScope.For` maps `Organisation` and `Issues` to `(true, true)`.

- [ ] **Step 1: Rewrite `ToolbarScopeTests.cs`**

Replace the whole file:

```csharp
using PanoramicData.NugetManagement.Web.Models;
using PanoramicData.NugetManagement.Web.Services;

namespace PanoramicData.NugetManagement.Test;

/// <summary>
/// Tests for <see cref="ToolbarScope"/>: which selections act on many repositories, which steps are
/// willing to be run that way, and which of the ticked repositories are left out when they are.
/// </summary>
public class ToolbarScopeTests(ITestOutputHelper output) : TestWithOutput(output)
{
	private static RepositoryDashboardRow Row(
		string name,
		bool cloned = true,
		bool governed = true) => new()
		{
			RepositoryFullName = $"panoramicdata/{name}",
			Organization = "panoramicdata",
			IsClonedLocally = cloned,
			IsGoverned = governed
		};

	private static RepositorySelection Ticked(params RepositoryDashboardRow[] rows)
	{
		var selection = new RepositorySelection();
		selection.SelectAll(rows.Select(row => row.RepositoryFullName));
		return selection;
	}

	private static List<RepositoryDashboardRow> Targets(
		WorkflowStep step,
		IEnumerable<RepositoryDashboardRow> rows,
		RepositorySelection selection,
		params string[] excluded)
		=> ToolbarScope.Targets(
			rows,
			step,
			name => excluded.Contains(name, StringComparer.OrdinalIgnoreCase),
			selection);

	[Theory]
	[InlineData(NavView.Repositories)]
	[InlineData(NavView.Issues)]
	[InlineData(NavView.Organisation)]
	public void TheEstateNodes_ActOnTheTickedRepositories(NavView view)
		=> ToolbarScope.IsEstateWide(view).Should().BeTrue();

	[Theory]
	[InlineData(NavView.RepositoryDetail)]
	[InlineData(NavView.PackageDetail)]
	[InlineData(NavView.Home)]
	[InlineData(NavView.Settings)]
	[InlineData(NavView.IssueCategoryDetail)]
	[InlineData(NavView.IssueRuleDetail)]
	public void EveryOtherSelection_ActsOnWhateverIsSelected(NavView view)
		=> ToolbarScope.IsEstateWide(view).Should().BeFalse(
			"the landing page selects nothing, a repository is one repository, and the per-category and "
			+ "per-rule issue views keep the bulk actions they already have");

	[Theory]
	[InlineData(WorkflowStep.GitSync)]
	[InlineData(WorkflowStep.Reassess)]
	[InlineData(WorkflowStep.Fix)]
	[InlineData(WorkflowStep.Build)]
	[InlineData(WorkflowStep.Test)]
	[InlineData(WorkflowStep.CommitAndPush)]
	public void EveryStepButPublish_MayBeRunAcrossTheEstate(WorkflowStep step)
		=> ToolbarScope.AllowsEstateWide(step).Should().BeTrue();

	[Fact]
	public void Publish_IsNeverRunAcrossTheEstate_EvenWhenTicked()
	{
		ToolbarScope.AllowsEstateWide(WorkflowStep.Publish).Should().BeFalse(
			"a package pushed to nuget.org cannot be taken back, and its version number can never be "
			+ "reused, so it is not a mistake one button press should be able to make forty times");

		var rows = new[] { Row("A"), Row("B") };

		Targets(WorkflowStep.Publish, rows, Ticked(rows)).Should().BeEmpty();
	}

	[Fact]
	public void WithNothingTicked_ABulkStepActsOnNothing()
	{
		var rows = new[] { Row("A"), Row("B") };

		Targets(WorkflowStep.Build, rows, new RepositorySelection()).Should().BeEmpty(
			"no ticks means nothing: one stray click on a forty-repository estate must not touch it");
	}

	[Fact]
	public void OnlyTheTickedRepositories_AreTargeted()
	{
		var rows = new[] { Row("A"), Row("B"), Row("C") };

		Targets(WorkflowStep.Build, rows, Ticked(rows[0], rows[2]))
			.Select(row => row.RepositoryName).Should().BeEquivalentTo(["A", "C"]);
	}

	[Fact]
	public void Reassess_WithNothingTicked_FallsBackToTheWholeOrganisation()
	{
		var rows = new[] { Row("A"), Row("B", cloned: false) };

		ToolbarScope.FallsBackToWholeOrganisation(WorkflowStep.Reassess).Should().BeTrue();

		Targets(WorkflowStep.Reassess, rows, new RepositorySelection())
			.Select(row => row.RepositoryName).Should().BeEquivalentTo(["A", "B"],
				"re-assessing is read-only and works remotely, so it needs no clone and no ticks");
	}

	[Theory]
	[InlineData(WorkflowStep.GitSync)]
	[InlineData(WorkflowStep.Fix)]
	[InlineData(WorkflowStep.Build)]
	[InlineData(WorkflowStep.Test)]
	[InlineData(WorkflowStep.CommitAndPush)]
	public void NoOtherStepFallsBackToTheWholeOrganisation(WorkflowStep step)
		=> ToolbarScope.FallsBackToWholeOrganisation(step).Should().BeFalse();

	[Fact]
	public void Reassess_NeedsNoClone_ButEveryOtherStepDoes()
	{
		ToolbarScope.RequiresClone(WorkflowStep.Reassess).Should().BeFalse();
		ToolbarScope.RequiresClone(WorkflowStep.Build).Should().BeTrue();
		ToolbarScope.RequiresClone(WorkflowStep.CommitAndPush).Should().BeTrue();
	}

	[Fact]
	public void ATickedRepositoryThatIsNowExcluded_TakesNoPart()
	{
		var rows = new[] { Row("A"), Row("B") };

		Targets(WorkflowStep.Build, rows, Ticked(rows), "panoramicdata/B")
			.Should().ContainSingle()
			.Which.RepositoryFullName.Should().Be("panoramicdata/A");
	}

	[Fact]
	public void ATickedRepositoryWithNoClone_IsSkipped()
	{
		var rows = new[] { Row("A"), Row("B", cloned: false) };

		Targets(WorkflowStep.Build, rows, Ticked(rows))
			.Should().ContainSingle()
			.Which.RepositoryFullName.Should().Be("panoramicdata/A", "there is nothing on disk to build");
	}

	[Fact]
	public void ATickedUngovernedRepository_IsSkipped()
	{
		var rows = new[] { Row("A"), Row("B", governed: false) };

		Targets(WorkflowStep.Build, rows, Ticked(rows))
			.Should().ContainSingle()
			.Which.RepositoryFullName.Should().Be("panoramicdata/A");
	}

	[Fact]
	public void ASelectionNamedInAnotherCase_StillMatches()
	{
		var rows = new[] { Row("Cisco.Iq.Api") };
		var selection = new RepositorySelection();
		selection.Select("PANORAMICDATA/CISCO.IQ.API");

		Targets(WorkflowStep.Build, rows, selection).Should().ContainSingle();
	}

	[Fact]
	public void WhatItWillDo_SaysHowManyAndWhatItLeftOut()
		=> ToolbarScope.DescribeSelection("Build", targetCount: 12, selectedCount: 47)
			.Should().Contain("12 repositories")
			.And.Contain("35 ticked repositories skipped",
				"acting on twelve of forty-seven ticked without saying so is how a bulk action lies");

	[Fact]
	public void WhatItWillDo_StaysQuietWhenNothingWasLeftOut()
		=> ToolbarScope.DescribeSelection("Build", targetCount: 47, selectedCount: 47)
			.Should().Contain("47 repositories")
			.And.NotContain("skipped");

	[Fact]
	public void WhatItWillDo_WithNothingTicked_SaysToTickSome()
		=> ToolbarScope.DescribeSelection("Build", targetCount: 0, selectedCount: 0)
			.Should().Contain("tick repositories");

	[Fact]
	public void WhatItWillDo_WhenNoneOfTheTickedCanBeActedOn_SaysSo()
		=> ToolbarScope.DescribeSelection("Build", targetCount: 0, selectedCount: 3)
			.Should().Contain("nothing to act on").And.Contain("3 ticked repositories");

	[Fact]
	public void WhatItWillDo_UsesTheSingularForOne()
		=> ToolbarScope.DescribeSelection("Build", targetCount: 1, selectedCount: 1)
			.Should().Contain("1 repository.");

	[Theory]
	[InlineData(WorkflowStep.CommitAndPush, 2, true)]
	[InlineData(WorkflowStep.CommitAndPush, 1, false)]
	[InlineData(WorkflowStep.CommitAndPush, 0, false)]
	[InlineData(WorkflowStep.Build, 40, false)]
	[InlineData(WorkflowStep.GitSync, 40, false)]
	[InlineData(WorkflowStep.Test, 40, false)]
	[InlineData(WorkflowStep.Fix, 40, false)]
	public void OnlyCommitAndPush_AsksFirst_AndOnlyForMoreThanOneRepository(
		WorkflowStep step, int targets, bool expected)
		=> ToolbarScope.RequiresConfirmation(step, targets).Should().Be(expected);

	[Theory]
	[InlineData(2, true)]
	[InlineData(1, false)]
	[InlineData(0, false)]
	public void FixWithAi_AsksFirst_ForMoreThanOneRepository(int targets, bool expected)
		=> ToolbarScope.RequiresAiFixConfirmation(targets).Should().Be(expected,
			"one threshold for both, so the two guards cannot disagree");

	[Fact]
	public void TheConfirmationNamesTheCountAndTheRepositories()
		=> ToolbarScope.ConfirmationMessage("Commit & push", ["A.Api", "B.Api", "C.Api"])
			.Should().Be("Commit & push 3 repositories? A.Api, B.Api, C.Api.");

	[Fact]
	public void TheConfirmationTruncatesAVeryLongList()
	{
		var names = Enumerable.Range(1, 12).Select(i => $"Repo{i}").ToList();

		var message = ToolbarScope.ConfirmationMessage("Commit & push", names);

		message.Should().Contain("12 repositories").And.Contain("Repo8").And.NotContain("Repo9")
			.And.Contain("and 4 more");
	}
}
```

- [ ] **Step 2: Update `FixScopeTests.cs`**

Replace the `WhereThereIsNothingToFix_FixDoesNothing` theory and add the estate theory. Change:

```csharp
	[Theory]
	[InlineData(NavView.None)]
	[InlineData(NavView.Settings)]
	[InlineData(NavView.Issues)]
	public void WhereThereIsNothingToFix_FixDoesNothing(NavView view)
		=> FixScope.For(view).HasAnything.Should().BeFalse();
```
to:
```csharp
	[Theory]
	[InlineData(NavView.None)]
	[InlineData(NavView.Settings)]
	[InlineData(NavView.Home)]
	public void WhereThereIsNothingToFix_FixDoesNothing(NavView view)
		=> FixScope.For(view).HasAnything.Should().BeFalse();

	[Theory]
	[InlineData(NavView.Repositories)]
	[InlineData(NavView.Organisation)]
	[InlineData(NavView.Issues)]
	public void OnTheEstate_FixDoesEverythingBeneathIt(NavView view)
	{
		var scope = FixScope.For(view);

		scope.ApplyRemediations.Should().BeTrue(
			"the whole estate contains every failing rule, and with a selection that means the ticked rows");
		scope.TriageDependabot.Should().BeTrue("and every Dependabot inbox");
	}
```

- [ ] **Step 3: Run to verify they fail**

Run: `dotnet test --filter "FullyQualifiedName~ToolbarScopeTests|FullyQualifiedName~FixScopeTests"`
Expected: build failure (`Targets` overload, `DescribeSelection`, `RequiresConfirmation` and the rest do not exist).

- [ ] **Step 4: Replace `ToolbarScope.cs`**

```csharp
using PanoramicData.NugetManagement.Web.Models;

namespace PanoramicData.NugetManagement.Web.Services;

/// <summary>
/// Maps what is selected to which repositories the toolbar acts on.
/// </summary>
/// <remarks>
/// The toolbar was written around one selected repository. The organisation, Issues and Repositories
/// nodes stand for many, so there a button acts on the repositories <em>ticked in the table</em>: no
/// ticks means nothing, because one stray click on a forty-repository estate must not touch it. Fix
/// already establishes the rule this follows: a button acts on everything beneath the selected node.
/// This says which repositories that is, and which steps are willing to be run that way.
/// <para>
/// A separate type rather than a method on the page, for the reason <see cref="FixScope"/> gives:
/// the page cannot be unit tested, and this mapping is the part worth being sure of.
/// </para>
/// </remarks>
public static class ToolbarScope
{
	/// <summary>
	/// A bulk step that would act on more repositories than this asks first. One threshold for every
	/// guarded step, so the guards cannot disagree about what counts as "many".
	/// </summary>
	public const int ConfirmAboveRepositoryCount = 1;

	/// <summary>
	/// Whether this selection acts on many repositories rather than on the one that is selected.
	/// </summary>
	/// <param name="view">The selected node's view.</param>
	public static bool IsEstateWide(NavView view)
		=> view is NavView.Repositories or NavView.Issues or NavView.Organisation;

	/// <summary>
	/// Whether a step may be run across many repositories at once.
	/// </summary>
	/// <remarks>
	/// Everything but Publish. Publishing pushes packages to nuget.org, which no revert undoes and
	/// which burns version numbers that can never be reused — the one step here whose mistake cannot
	/// be taken back, so it stays a decision made one repository at a time.
	/// </remarks>
	/// <param name="step">The step in question.</param>
	public static bool AllowsEstateWide(WorkflowStep step) => step is not WorkflowStep.Publish;

	/// <summary>
	/// Whether a step with nothing ticked acts on the whole organisation instead of on nothing.
	/// </summary>
	/// <remarks>
	/// Only re-assessing. It reads and writes nothing in any working tree, so the worst a stray click
	/// can do is spend an API budget the user is warned about first; every step that changes a clone or
	/// a remote needs an explicit choice of repositories.
	/// </remarks>
	/// <param name="step">The step in question.</param>
	public static bool FallsBackToWholeOrganisation(WorkflowStep step) => step is WorkflowStep.Reassess;

	/// <summary>
	/// Whether a step works on a clone on disk, and so skips a repository that has none.
	/// </summary>
	/// <remarks>
	/// Re-assessing does not: it reads the repository from GitHub, so it can describe one that was
	/// never cloned.
	/// </remarks>
	/// <param name="step">The step in question.</param>
	public static bool RequiresClone(WorkflowStep step) => step is not WorkflowStep.Reassess;

	/// <summary>
	/// The repositories a press of <paramref name="step"/> should act on.
	/// </summary>
	/// <param name="rows">The repositories in view.</param>
	/// <param name="step">The step being run.</param>
	/// <param name="isExcluded">Whether a repository has been excluded from governance.</param>
	/// <param name="selection">The repositories ticked in the table.</param>
	/// <remarks>
	/// Ticked, governed and not excluded — and cloned, for a step that works on a clone. An excluded
	/// repository takes no part in any figure or action, and a repository with no clone has nothing on
	/// disk to build, test, fix or push. The count that comes back can be smaller than the number
	/// ticked, which is why <see cref="DescribeSelection"/> exists: quietly acting on twelve of
	/// forty-seven is how a bulk action lies about what it did.
	/// </remarks>
	public static List<RepositoryDashboardRow> Targets(
		IEnumerable<RepositoryDashboardRow> rows,
		WorkflowStep step,
		Func<string, bool> isExcluded,
		RepositorySelection selection)
	{
		if (!AllowsEstateWide(step))
		{
			return [];
		}

		var eligible = rows.Where(row =>
			row.IsGoverned
			&& !isExcluded(row.RepositoryFullName)
			&& (!RequiresClone(step) || row.IsClonedLocally));

		if (selection.Count == 0)
		{
			return FallsBackToWholeOrganisation(step) ? [.. eligible] : [];
		}

		return [.. eligible.Where(row => selection.Contains(row.RepositoryFullName))];
	}

	/// <summary>
	/// What a press is about to do, in a sentence, including what it is leaving out.
	/// </summary>
	/// <param name="stepName">The step's label, as the button shows it.</param>
	/// <param name="targetCount">How many repositories it will act on.</param>
	/// <param name="selectedCount">How many repositories are ticked.</param>
	public static string DescribeSelection(string stepName, int targetCount, int selectedCount)
	{
		if (selectedCount == 0)
		{
			return $"{stepName}: tick repositories to act on them.";
		}

		if (targetCount == 0)
		{
			return $"{stepName} has nothing to act on: none of the {selectedCount} ticked "
				+ $"{Repositories(selectedCount)} can be acted on (excluded, not governed, or not cloned locally).";
		}

		var skipped = selectedCount - targetCount;
		var sentence = $"{stepName} will run on {targetCount} {Repositories(targetCount)}.";

		return skipped > 0
			? $"{sentence} {skipped} ticked {Repositories(skipped)} skipped: excluded, or not cloned locally."
			: sentence;
	}

	/// <summary>
	/// Whether pressing <paramref name="step"/> should ask first.
	/// </summary>
	/// <param name="step">The step being run.</param>
	/// <param name="targetCount">How many repositories it would act on.</param>
	/// <remarks>
	/// Only Commit &amp; Push: it changes remote state. Sync, Build and Test change nothing a revert
	/// cannot undo, Fix edits local clones, and Re-assess is read-only.
	/// </remarks>
	public static bool RequiresConfirmation(WorkflowStep step, int targetCount)
		=> step is WorkflowStep.CommitAndPush && targetCount > ConfirmAboveRepositoryCount;

	/// <summary>
	/// Whether pressing Fix with AI should ask first.
	/// </summary>
	/// <remarks>
	/// Fix with AI is not a <see cref="WorkflowStep"/>, so it has its own method over the same
	/// threshold. It starts model sessions, one per repository, which is worth saying before it does.
	/// </remarks>
	/// <param name="targetCount">How many repositories it would start work in.</param>
	public static bool RequiresAiFixConfirmation(int targetCount) => targetCount > ConfirmAboveRepositoryCount;

	/// <summary>
	/// The question put to the user before a guarded press, naming how many repositories and which.
	/// </summary>
	/// <param name="stepName">What is about to happen, as a verb phrase: "Commit &amp; push".</param>
	/// <param name="repositoryNames">The repositories' short names.</param>
	public static string ConfirmationMessage(string stepName, IReadOnlyList<string> repositoryNames)
	{
		const int shown = 8;

		var listed = string.Join(", ", repositoryNames.Take(shown));
		var more = repositoryNames.Count - shown;

		return more > 0
			? $"{stepName} {repositoryNames.Count} repositories? {listed} and {more} more."
			: $"{stepName} {repositoryNames.Count} {Repositories(repositoryNames.Count)}? {listed}.";
	}

	private static string Repositories(int count) => count == 1 ? "repository" : "repositories";
}
```

- [ ] **Step 5: Map the estate views in `FixScope.cs`**

Replace
```csharp
		// The whole estate is every repository at once, and so contains everything each of them does.
		// Fix acting on everything beneath the selected node is the rule; a container that declined to
		// would be the exception that makes the rule not worth stating.
		NavView.Repositories => new(true, true),
```
with
```csharp
		// The estate, whichever node stands for it, is every repository at once, and so contains
		// everything each of them does. Fix acting on everything beneath the selected node is the rule;
		// a container that declined to would be the exception that makes the rule not worth stating.
		// With a selection, "everything beneath" means the ticked rows.
		NavView.Repositories or NavView.Organisation or NavView.Issues => new(true, true),
```

- [ ] **Step 6: Keep `Home.razor` compiling and the breadcrumb correct**

Three edits in `Home.razor`:

1. Add the selection field. Directly under `private bool _orgSelected;` add:
```csharp
	/// <summary>
	/// The repositories ticked in the table, which every estate-wide toolbar step acts on.
	/// </summary>
	private readonly RepositorySelection _selection = new();
```

2. `EstateTargets`. Replace
```csharp
		=> ToolbarScope.Targets(EstateCandidates, step, RuntimeSettings.IsRepositoryExcluded);
```
with
```csharp
		=> ToolbarScope.Targets(EstateCandidates, step, RuntimeSettings.IsRepositoryExcluded, _selection);
```

3. `QueueAcrossEstate`. Replace
```csharp
		var candidates = EstateCandidates;
		var targets = ToolbarScope.Targets(candidates, step, RuntimeSettings.IsRepositoryExcluded);

		AppendConsole(ToolbarScope.Describe(stepName, targets.Count, candidates.Count));
```
with
```csharp
		var targets = EstateTargets(step);

		AppendConsole(ToolbarScope.DescribeSelection(stepName, targets.Count, _selection.Count));
```

4. The breadcrumb. `IsEstateWide` is now true at Issues and the organisation node as well, so the "Repositories" crumb must follow the *view*, not the toolbar scope. Replace
```razor
							@if (IsEstateWide)
							{
								<span class="breadcrumb-sep">›</span>
								<span class="breadcrumb-current">Repositories</span>
							}
```
with
```razor
							@if (_currentView == NavView.Repositories)
							{
								<span class="breadcrumb-sep">›</span>
								<span class="breadcrumb-current">Repositories</span>
							}
```
(Locate it with Grep for `@if (IsEstateWide)`; there is exactly one match, in the breadcrumb.)

- [ ] **Step 7: Run the tests**

Run: `dotnet test --filter "FullyQualifiedName~ToolbarScopeTests|FullyQualifiedName~FixScopeTests|FullyQualifiedName~NavViewCoverageTests"`
Expected: PASS. Confirm the `ToolbarScopeTests` total is **46** (not zero): 3 + 6 + 6 + 1 + 1 + 1 + 1 + 5 + 1 + 1 + 1 + 1 + 1 + 5 + 7 + 3 + 1 + 1. If the build fails in `Home.razor`, a `Describe(` or three-argument `Targets(` call remains: Grep for `ToolbarScope.Describe(` and `ToolbarScope.Targets(` and update it.

- [ ] **Step 8: Commit**

```bash
git add PanoramicData.NugetManagement.Web/Services/ToolbarScope.cs PanoramicData.NugetManagement.Web/Services/FixScope.cs PanoramicData.NugetManagement.Web/Components/Pages/Home.razor PanoramicData.NugetManagement.Test/ToolbarScopeTests.cs PanoramicData.NugetManagement.Test/FixScopeTests.cs
git commit -m "feat: scope the toolbar to the ticked repositories at the organisation, Issues and Repositories nodes"
```

(Between this task and Task 6 the estate toolbar acts on nothing, because nothing can tick a row yet. That is the intended intermediate state on the branch.)

---

### Task 4: Test status

Rows remember a build result but not a test result. Record one, and discard it under exactly the rule a build result is discarded under.

**Files:**
- Create: `PanoramicData.NugetManagement.Web/Models/RepositoryTestState.cs`
- Modify: `PanoramicData.NugetManagement.Web/Models/RepositoryDashboardRow.cs`
- Modify: `PanoramicData.NugetManagement.Web/Services/WorkExecutors.cs`
- Modify: `PanoramicData.NugetManagement.Web/Services/BuildStatusLifetime.cs` (doc only)
- Test: `PanoramicData.NugetManagement.Test/RepositoryTestStatusTests.cs`

**Interfaces:**
- Consumes: nothing.
- Produces:
  - `enum RepositoryTestState { Passed, Failed }`
  - `RepositoryDashboardRow.LastTestState` (`RepositoryTestState?`), `.LastTestedAtUtc` (`DateTimeOffset?`)
  - `RepositoryDashboardRow.RememberTestResult(RepositoryTestState state, DateTimeOffset at)`
  - `RepositoryDashboardRow.ForgetVerificationResults()` (clears build **and** test), `bool HasVerificationResults`

- [ ] **Step 1: Write the failing tests**

Create `PanoramicData.NugetManagement.Test/RepositoryTestStatusTests.cs`:

```csharp
using Microsoft.Extensions.Logging.Abstractions;
using PanoramicData.NugetManagement.Web.Models;
using PanoramicData.NugetManagement.Web.Services;

namespace PanoramicData.NugetManagement.Test;

/// <summary>
/// Tests the per-repository test status: that it is remembered, that it is thrown away with the build
/// result rather than outliving it, and that it survives the cache.
/// </summary>
public class RepositoryTestStatusTests(ITestOutputHelper output) : TestWithOutput(output), IDisposable
{
	private readonly string _directory = Path.Combine(
		Path.GetTempPath(),
		"nugetmanagement-tests",
		Guid.NewGuid().ToString("n"));

	/// <inheritdoc />
	public void Dispose()
	{
		try
		{
			Directory.Delete(_directory, recursive: true);
		}
		catch (IOException)
		{
			// A locked temp file must not fail the test that produced it.
		}

		GC.SuppressFinalize(this);
	}

	private static RepositoryDashboardRow Row() => new()
	{
		Organization = "panoramicdata",
		RepositoryFullName = "panoramicdata/Sample"
	};

	[Fact]
	public void ANewRow_HasNeverBeenTested()
	{
		var row = Row();

		row.LastTestState.Should().BeNull("not known is not the same as passed");
		row.LastTestedAtUtc.Should().BeNull();
		row.HasVerificationResults.Should().BeFalse();
	}

	[Theory]
	[InlineData(RepositoryTestState.Passed)]
	[InlineData(RepositoryTestState.Failed)]
	public void RememberTestResult_RecordsTheOutcomeAndWhen(RepositoryTestState state)
	{
		var row = Row();
		var at = new DateTimeOffset(2026, 10, 4, 12, 0, 0, TimeSpan.Zero);

		row.RememberTestResult(state, at);

		row.LastTestState.Should().Be(state);
		row.LastTestedAtUtc.Should().Be(at);
		row.HasVerificationResults.Should().BeTrue();
	}

	[Fact]
	public void ForgetVerificationResults_ThrowsAwayTheBuildAndTheTestResultTogether()
	{
		var row = Row();
		row.LastBuildState = RepositoryBuildState.Succeeded;
		row.LastBuiltAtUtc = DateTimeOffset.UtcNow;
		row.RememberTestResult(RepositoryTestState.Passed, DateTimeOffset.UtcNow);

		row.ForgetVerificationResults();

		row.LastBuildState.Should().BeNull();
		row.LastBuiltAtUtc.Should().BeNull();
		row.LastTestState.Should().BeNull("a test result describes a working tree exactly as a build does");
		row.LastTestedAtUtc.Should().BeNull();
		row.HasVerificationResults.Should().BeFalse();
	}

	[Fact]
	public void AnUnbuiltRowThatHasBeenTested_StillCountsAsHavingResultsToForget()
	{
		// The executor skips the cache write when there is nothing to forget. A row with only a test
		// result must not be mistaken for one with nothing, or its stale result would survive a fix.
		var row = Row();
		row.RememberTestResult(RepositoryTestState.Failed, DateTimeOffset.UtcNow);

		row.HasVerificationResults.Should().BeTrue();
	}

	[Fact]
	public void TheTestResult_SurvivesTheCache()
	{
		Directory.CreateDirectory(_directory);
		var path = Path.Combine(_directory, "dashboard-cache.json");
		var at = new DateTimeOffset(2026, 10, 4, 12, 30, 0, TimeSpan.Zero);

		var row = Row();
		row.RememberTestResult(RepositoryTestState.Failed, at);

		new DashboardCacheService(NullLogger<DashboardCacheService>.Instance, path).Update([row]);

		var reloaded = new DashboardCacheService(NullLogger<DashboardCacheService>.Instance, path)
			.GetCachedRows();

		reloaded.Should().NotBeNull().And.ContainSingle();
		reloaded![0].LastTestState.Should().Be(RepositoryTestState.Failed);
		reloaded[0].LastTestedAtUtc.Should().Be(at);
	}

	[Fact]
	public void ARowWrittenBeforeTheseFieldsExisted_ReadsBackAsNotKnown()
	{
		// A cache file from an earlier build has no test fields at all. It must load, with the result
		// not known, rather than failing or inventing a pass.
		Directory.CreateDirectory(_directory);
		var path = Path.Combine(_directory, "dashboard-cache.json");

		var old = Row();
		old.LastBuildState = RepositoryBuildState.Succeeded;
		old.LastBuiltAtUtc = DateTimeOffset.UtcNow;

		new DashboardCacheService(NullLogger<DashboardCacheService>.Instance, path).Update([old]);

		var json = File.ReadAllText(path);
		json.Should().NotContainEquivalentOf("TestState",
			"nulls are not written, so a row never tested looks exactly like a file from an earlier build");

		var reloaded = new DashboardCacheService(NullLogger<DashboardCacheService>.Instance, path)
			.GetCachedRows();

		reloaded![0].LastTestState.Should().BeNull();
		reloaded[0].LastBuildState.Should().Be(RepositoryBuildState.Succeeded);
	}
}
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test --filter "FullyQualifiedName~RepositoryTestStatusTests"`
Expected: build failure, `RepositoryTestState`, `LastTestState` etc. do not exist.

- [ ] **Step 3: Add the enum**

Create `PanoramicData.NugetManagement.Web/Models/RepositoryTestState.cs`:

```csharp
namespace PanoramicData.NugetManagement.Web.Models;

/// <summary>
/// What the last test run in a repository's working tree did.
/// </summary>
/// <remarks>
/// Mirrors <see cref="RepositoryBuildState"/>, and for the same reason has no member for "not known":
/// that is the null on the row. A repository that has never been tested here, and one whose tree has
/// changed since, are the same thing to a reader, and neither is a pass.
/// </remarks>
public enum RepositoryTestState
{
	/// <summary>The tests passed.</summary>
	Passed,

	/// <summary>The tests failed, or could not be run.</summary>
	Failed
}
```

- [ ] **Step 4: Add the fields and helpers to the row**

In `RepositoryDashboardRow.cs`, directly after the `LastBuiltAtUtc` property, add:

```csharp
	/// <summary>
	/// What the last test run in this working tree did, or null when it never has been or is no longer
	/// believed.
	/// </summary>
	public RepositoryTestState? LastTestState { get; set; }

	/// <summary>
	/// When <see cref="LastTestState"/> was established, or null when it never has been.
	/// </summary>
	public DateTimeOffset? LastTestedAtUtc { get; set; }

	/// <summary>
	/// Whether the row holds a remembered build or test result that could go stale.
	/// </summary>
	public bool HasVerificationResults => LastBuildState is not null || LastTestState is not null;

	/// <summary>
	/// Records what a test run did.
	/// </summary>
	/// <param name="state">What the run did.</param>
	/// <param name="at">When it finished.</param>
	public void RememberTestResult(RepositoryTestState state, DateTimeOffset at)
	{
		LastTestState = state;
		LastTestedAtUtc = at;
	}

	/// <summary>
	/// Throws away the remembered build and test results, because something rewrote the working tree
	/// and neither describes what is on disk any more.
	/// </summary>
	/// <remarks>
	/// Both together, by the same rule: a test result claims that this exact tree passed, exactly as a
	/// build result claims that it built, and a stale green of either is worse than none.
	/// </remarks>
	public void ForgetVerificationResults()
	{
		LastBuildState = null;
		LastBuiltAtUtc = null;
		LastTestState = null;
		LastTestedAtUtc = null;
	}
```

- [ ] **Step 5: Record and forget in `WorkExecutors.cs`**

1. Replace the body of `ForgetBuildStatusIfInvalidated`:
```csharp
		var row = RowFor(item);
		if (row is null || row.LastBuildState is null)
		{
			return;
		}

		row.LastBuildState = null;
		row.LastBuiltAtUtc = null;
		cache.UpsertRow(row);
```
with:
```csharp
		var row = RowFor(item);
		if (row is null || !row.HasVerificationResults)
		{
			return;
		}

		row.ForgetVerificationResults();
		cache.UpsertRow(row);
```
and update its summary line to "Throws away the repository's remembered build and test results when the work that just ran could have changed what is on disk."

2. In `TestAsync`, replace
```csharp
			if (row.Status == PackageStatus.TestsPassed)
			{
				Say("✅ Tests passed");
				item.Succeeded = true;
			}
			else
			{
				Say("❌ Tests failed");
				item.GeneratedPrompt = DashboardService.GenerateConciseWorkflowFailurePrompt(row, "test", output);
				item.Succeeded = false;
			}
		}
```
with
```csharp
			var passed = row.Status == PackageStatus.TestsPassed;

			if (passed)
			{
				Say("✅ Tests passed");
				item.Succeeded = true;
			}
			else
			{
				Say("❌ Tests failed");
				item.GeneratedPrompt = DashboardService.GenerateConciseWorkflowFailurePrompt(row, "test", output);
				item.Succeeded = false;
			}

			RememberTestResult(row, passed ? RepositoryTestState.Passed : RepositoryTestState.Failed);
		}
```
and in the same method's `catch (Exception ex)` block, after `item.Succeeded = false;` add:
```csharp
			// Tests that could not be run are a repository whose tests do not pass here, which is what
			// the badge is asked. Leaving it as not-known would hide it among the never-tested.
			RememberTestResult(row, RepositoryTestState.Failed);
```

3. Beside `RememberBuildResult`, add:
```csharp
	/// <summary>
	/// Records what a test run did, so the estate can be read at a glance rather than one repository at
	/// a time.
	/// </summary>
	/// <param name="row">The repository that was tested.</param>
	/// <param name="state">What the run did.</param>
	private void RememberTestResult(RepositoryDashboardRow row, RepositoryTestState state)
	{
		row.RememberTestResult(state, DateTimeOffset.UtcNow);
		cache.UpsertRow(row);
	}
```

- [ ] **Step 6: Say so in `BuildStatusLifetime`**

In the `<remarks>` of `BuildStatusLifetime`, after the first paragraph, add:
```csharp
/// <para>
/// A remembered <em>test</em> result is held to the same rule and discarded by the same kinds, because
/// it makes the same claim about the same tree. <see cref="RepositoryDashboardRow.ForgetVerificationResults"/>
/// clears both together, so there is one list of kinds to keep right, not two.
/// </para>
```

- [ ] **Step 7: Run the tests**

Run: `dotnet test --filter "FullyQualifiedName~RepositoryTestStatusTests|FullyQualifiedName~RepositoryBuildStatusTests"`
Expected: PASS. Confirm `RepositoryTestStatusTests` ran **7** tests (1 + 2 + 1 + 1 + 1 + 1) and not zero.

- [ ] **Step 8: Commit**

```bash
git add PanoramicData.NugetManagement.Web/Models/RepositoryTestState.cs PanoramicData.NugetManagement.Web/Models/RepositoryDashboardRow.cs PanoramicData.NugetManagement.Web/Services/WorkExecutors.cs PanoramicData.NugetManagement.Web/Services/BuildStatusLifetime.cs PanoramicData.NugetManagement.Test/RepositoryTestStatusTests.cs
git commit -m "feat: remember a repository's last test result, and forget it when the tree changes"
```

---

### Task 5: `EstateTableModel`

The table's logic as a pure type: which columns, what each cell holds, what order, what the filter hides. No Razor.

**Files:**
- Create: `PanoramicData.NugetManagement.Web/Services/EstateTableModel.cs`
- Test: `PanoramicData.NugetManagement.Test/EstateTableModelTests.cs`
- Modify: `docs/superpowers/specs/2026-10-04-estate-table-bulk-toolbar-design.md` (one line, see Step 6)

**Interfaces:**
- Consumes: `RepositorySelection` (Task 1), `RepositoryTestState` and the row fields (Task 4).
- Produces:
  - `enum EstateHealth { Unknown, Good, Bad }`
  - `enum EstateSeverity { Unknown, Clean, Info, Warning, Error }`
  - `enum EstateSortColumn { Default, Repository, Issues, Branch, Uncommitted, Unpushed, Sync, Build, Test, Category }`
  - `record EstateCategoryCell(AssessmentCategory Category, int Count, EstateSeverity Severity)`
  - `record EstateTableRow(RepositoryDashboardRow Source, string Name, string FullName, string? InertReason, int Issues, IReadOnlyDictionary<AssessmentCategory, EstateCategoryCell> Cells, string? Branch, EstateHealth Uncommitted, EstateHealth Unpushed, EstateHealth Sync, EstateHealth Build, EstateHealth Test)` with `bool CanSelect`
  - `sealed class EstateTableModel` with `IReadOnlyList<AssessmentCategory> Categories`, `IReadOnlyList<EstateTableRow> Rows` (filtered and sorted), `int TotalCount`, `IReadOnlyList<string> SelectableNames`, `int HiddenSelectedCount(RepositorySelection)`, and `static EstateTableModel Build(IEnumerable<RepositoryDashboardRow> rows, Func<string,bool> isExcluded, string? filter = null, EstateSortColumn sort = EstateSortColumn.Default, bool descending = false, AssessmentCategory? sortCategory = null)`

- [ ] **Step 1: Write the failing tests**

Create `PanoramicData.NugetManagement.Test/EstateTableModelTests.cs`:

```csharp
using PanoramicData.NugetManagement.Models;
using PanoramicData.NugetManagement.Web.Models;
using PanoramicData.NugetManagement.Web.Services;

namespace PanoramicData.NugetManagement.Test;

/// <summary>
/// Tests for <see cref="EstateTableModel"/>: which columns the estate table has, what each cell says,
/// and what order and filter do to the rows.
/// </summary>
public class EstateTableModelTests(ITestOutputHelper output) : TestWithOutput(output)
{
	private static RepositoryDashboardRow Row(
		string name,
		bool assessed = true,
		bool cloned = true,
		bool governed = true,
		params (AssessmentCategory Category, int Critical, int Error, int Warning, int Info)[] findings)
	{
		// Rule results and category summaries are built from the same findings, as they are in real data:
		// the total issue count comes from the results and the cells from the summaries, and a test row
		// that set only one would pass or fail for a reason that has nothing to do with the model.
		var results = new List<RuleResult>();

		foreach (var finding in findings)
		{
			AddFailures(results, finding.Category, AssessmentSeverity.Critical, finding.Critical);
			AddFailures(results, finding.Category, AssessmentSeverity.Error, finding.Error);
			AddFailures(results, finding.Category, AssessmentSeverity.Warning, finding.Warning);
			AddFailures(results, finding.Category, AssessmentSeverity.Info, finding.Info);
		}

		var row = new RepositoryDashboardRow
		{
			Organization = "panoramicdata",
			RepositoryFullName = $"panoramicdata/{name}",
			IsClonedLocally = cloned,
			IsGoverned = governed,
			Assessment = assessed
				? new RepoAssessment
				{
					RepositoryFullName = $"panoramicdata/{name}",
					DefaultBranch = "main",
					AssessedAtUtc = DateTimeOffset.UtcNow,
					RuleResults = results
				}
				: null
		};

		foreach (var finding in findings)
		{
			row.CategorySummaries[finding.Category] = new CategorySummary
			{
				Criticals = finding.Critical,
				Errors = finding.Error,
				Warnings = finding.Warning,
				Infos = finding.Info
			};
		}

		return row;
	}

	private static void AddFailures(
		List<RuleResult> results,
		AssessmentCategory category,
		AssessmentSeverity severity,
		int count)
	{
		for (var i = 0; i < count; i++)
		{
			results.Add(new RuleResult
			{
				RuleId = $"{category}-{severity}-{i}",
				RuleName = "A failing rule",
				Category = category,
				Severity = severity,
				Passed = false,
				Message = "wrong"
			});
		}
	}

	private static EstateTableModel Build(
		IEnumerable<RepositoryDashboardRow> rows,
		string? filter = null,
		EstateSortColumn sort = EstateSortColumn.Default,
		bool descending = false,
		AssessmentCategory? category = null,
		params string[] excluded)
		=> EstateTableModel.Build(
			rows,
			name => excluded.Contains(name, StringComparer.OrdinalIgnoreCase),
			filter,
			sort,
			descending,
			category);

	[Fact]
	public void Categories_AreOnlyThoseWithAFailure_InEnumOrder()
	{
		var model = Build(
		[
			Row("A", findings: [(AssessmentCategory.CodeQuality, 0, 1, 0, 0)]),
			Row("B", findings: [(AssessmentCategory.CiCd, 0, 0, 2, 0), (AssessmentCategory.Versioning, 0, 0, 0, 0)])
		]);

		model.Categories.Should().Equal(
			[AssessmentCategory.CiCd, AssessmentCategory.CodeQuality],
			"Versioning has no failing rule anywhere so takes no space, and the order is the enum's so the layout is stable");
	}

	[Theory]
	[InlineData(1, 0, 0, 0, 1, EstateSeverity.Error)]
	[InlineData(0, 2, 0, 0, 2, EstateSeverity.Error)]
	[InlineData(0, 0, 3, 0, 3, EstateSeverity.Warning)]
	[InlineData(0, 0, 0, 4, 4, EstateSeverity.Info)]
	[InlineData(1, 1, 1, 1, 4, EstateSeverity.Error)]
	[InlineData(0, 0, 0, 0, 0, EstateSeverity.Clean)]
	public void ACell_CountsTheFailuresAndShowsTheWorstSeverity(
		int critical, int error, int warning, int info, int count, EstateSeverity severity)
	{
		var model = Build(
		[
			Row("A", findings: [(AssessmentCategory.CiCd, critical, error, warning, info)]),
			Row("B", findings: [(AssessmentCategory.CiCd, 0, 1, 0, 0)])
		]);

		var cell = model.Rows.Single(r => r.Name == "A").Cells[AssessmentCategory.CiCd];

		cell.Count.Should().Be(count);
		cell.Severity.Should().Be(severity);
	}

	[Fact]
	public void AWaivedRule_IsNotAFailure()
	{
		var row = Row("A", findings: [(AssessmentCategory.CiCd, 0, 1, 0, 0)]);
		row.CategorySummaries[AssessmentCategory.CiCd].Waived = 5;

		Build([row]).Rows[0].Cells[AssessmentCategory.CiCd].Count.Should().Be(1,
			"a waived rule is one the estate has excused, not one the repository fails");
	}

	[Fact]
	public void AnUnassessedRepository_ShowsUnknown_NotClean()
	{
		var model = Build(
		[
			Row("Assessed", findings: [(AssessmentCategory.CiCd, 0, 1, 0, 0)]),
			Row("Never", assessed: false)
		]);

		model.Rows.Single(r => r.Name == "Never").Cells[AssessmentCategory.CiCd].Severity
			.Should().Be(EstateSeverity.Unknown,
				"nothing has been checked, and a blank that looks like a clean bill of health is the worst answer");
	}

	[Fact]
	public void AnAssessedRepositoryWithNoFindingInACategory_IsClean()
	{
		var model = Build(
		[
			Row("A", findings: [(AssessmentCategory.CiCd, 0, 1, 0, 0)]),
			Row("B")
		]);

		model.Rows.Single(r => r.Name == "B").Cells[AssessmentCategory.CiCd].Severity
			.Should().Be(EstateSeverity.Clean);
	}

	[Fact]
	public void UnknownGitBuildAndTestState_IsUnknown_NotGood()
	{
		var row = Build([Row("A")]).Rows[0];

		row.Uncommitted.Should().Be(EstateHealth.Unknown);
		row.Unpushed.Should().Be(EstateHealth.Unknown);
		row.Sync.Should().Be(EstateHealth.Unknown);
		row.Build.Should().Be(EstateHealth.Unknown);
		row.Test.Should().Be(EstateHealth.Unknown);
	}

	[Fact]
	public void TheGitFlags_MapToGoodAndBad()
	{
		var good = Row("Good");
		good.IsWorkingTreeClean = true;
		good.HasUnpushedCommits = false;
		good.IsSyncedWithOrigin = true;
		good.CurrentBranch = "main";

		var bad = Row("Bad");
		bad.IsWorkingTreeClean = false;
		bad.HasUnpushedCommits = true;
		bad.IsSyncedWithOrigin = false;

		var model = Build([good, bad]);

		var goodRow = model.Rows.Single(r => r.Name == "Good");
		goodRow.Uncommitted.Should().Be(EstateHealth.Good);
		goodRow.Unpushed.Should().Be(EstateHealth.Good);
		goodRow.Sync.Should().Be(EstateHealth.Good);
		goodRow.Branch.Should().Be("main");

		var badRow = model.Rows.Single(r => r.Name == "Bad");
		badRow.Uncommitted.Should().Be(EstateHealth.Bad);
		badRow.Unpushed.Should().Be(EstateHealth.Bad);
		badRow.Sync.Should().Be(EstateHealth.Bad);
	}

	[Fact]
	public void BuildAndTestResults_MapToGoodAndBad()
	{
		var passes = Row("Passes");
		passes.LastBuildState = RepositoryBuildState.Succeeded;
		passes.RememberTestResult(RepositoryTestState.Passed, DateTimeOffset.UtcNow);

		var fails = Row("Fails");
		fails.LastBuildState = RepositoryBuildState.Failed;
		fails.RememberTestResult(RepositoryTestState.Failed, DateTimeOffset.UtcNow);

		var model = Build([passes, fails]);

		model.Rows.Single(r => r.Name == "Passes").Build.Should().Be(EstateHealth.Good);
		model.Rows.Single(r => r.Name == "Passes").Test.Should().Be(EstateHealth.Good);
		model.Rows.Single(r => r.Name == "Fails").Build.Should().Be(EstateHealth.Bad);
		model.Rows.Single(r => r.Name == "Fails").Test.Should().Be(EstateHealth.Bad);
	}

	[Fact]
	public void ARepositoryThatCannotBeActedOn_IsShownButInert_AndSaysWhy()
	{
		var model = Build(
		[
			Row("Fine"),
			Row("NoClone", cloned: false),
			Row("Dropped"),
			Row("Foreign", governed: false)
		],
		excluded: ["panoramicdata/Dropped"]);

		model.Rows.Should().HaveCount(4, "hiding them would make the estate look smaller than it is");
		model.Rows.Single(r => r.Name == "Fine").CanSelect.Should().BeTrue();

		model.Rows.Single(r => r.Name == "NoClone").InertReason.Should().Be("not cloned");
		model.Rows.Single(r => r.Name == "Dropped").InertReason.Should().Be("excluded");
		model.Rows.Single(r => r.Name == "Foreign").InertReason.Should().Be("not governed");
		model.Rows.Where(r => !r.CanSelect).Should().HaveCount(3);
	}

	[Fact]
	public void TheDefaultOrder_PutsWhatIsWrongFirst()
	{
		var broken = Row("Broken");
		broken.LastBuildState = RepositoryBuildState.Failed;

		var untested = Row("Untested");

		var green = Row("Green");
		green.LastBuildState = RepositoryBuildState.Succeeded;
		green.RememberTestResult(RepositoryTestState.Passed, DateTimeOffset.UtcNow);

		var failingTests = Row("FailingTests");
		failingTests.LastBuildState = RepositoryBuildState.Succeeded;
		failingTests.RememberTestResult(RepositoryTestState.Failed, DateTimeOffset.UtcNow);

		var model = Build([green, untested, failingTests, broken]);

		model.Rows.Select(r => r.Name).Should().Equal(
			["Broken", "FailingTests", "Untested", "Green"],
			"failed builds, then failed tests, then anything not known, then what both builds and passes");
	}

	[Fact]
	public void WithinTheSameState_MoreIssuesComeFirst_ThenByName()
	{
		var model = Build(
		[
			Row("B", findings: [(AssessmentCategory.CiCd, 0, 1, 0, 0)]),
			Row("A", findings: [(AssessmentCategory.CiCd, 0, 1, 0, 0)]),
			Row("C", findings: [(AssessmentCategory.CiCd, 0, 3, 0, 0)])
		]);

		model.Rows.Select(r => r.Name).Should().Equal(["C", "A", "B"]);
	}

	[Fact]
	public void Filter_MatchesTheNameWithoutRegardToCase_AndKeepsTheColumns()
	{
		var model = Build(
		[
			Row("Cisco.Api", findings: [(AssessmentCategory.CiCd, 0, 1, 0, 0)]),
			Row("Toggl.Api", findings: [(AssessmentCategory.CodeQuality, 0, 1, 0, 0)])
		],
		filter: "CISCO");

		model.Rows.Should().ContainSingle().Which.Name.Should().Be("Cisco.Api");
		model.TotalCount.Should().Be(2, "the total is what the selection line compares against");
		model.Categories.Should().HaveCount(2, "the layout must not shift as the filter is typed");
	}

	[Fact]
	public void SortByACategory_PutsTheMostFailuresFirst_AndDescendingReversesIt()
	{
		var rows = new[]
		{
			Row("Few", findings: [(AssessmentCategory.CiCd, 0, 1, 0, 0)]),
			Row("Many", findings: [(AssessmentCategory.CiCd, 0, 5, 0, 0)]),
			Row("None", findings: [(AssessmentCategory.CiCd, 0, 0, 0, 0)])
		};

		Build(rows, sort: EstateSortColumn.Category, category: AssessmentCategory.CiCd)
			.Rows.Select(r => r.Name).Should().Equal(["Many", "Few", "None"]);

		Build(rows, sort: EstateSortColumn.Category, descending: true, category: AssessmentCategory.CiCd)
			.Rows.Select(r => r.Name).Should().Equal(["None", "Few", "Many"]);
	}

	[Fact]
	public void SelectableNames_AreTheVisibleRowsThatCanBeActedOn()
	{
		var model = Build(
		[
			Row("Alpha.Api"),
			Row("Alpha.Cli", cloned: false),
			Row("Beta.Api")
		],
		filter: "alpha");

		model.SelectableNames.Should().Equal(["panoramicdata/Alpha.Api"],
			"a header checkbox ticks what is on screen and can be acted on, nothing else");
	}

	[Fact]
	public void HiddenSelectedCount_CountsTickedRowsTheFilterHides()
	{
		var model = Build([Row("Alpha.Api"), Row("Beta.Api"), Row("Gamma.Api")], filter: "alpha");

		var selection = new RepositorySelection();
		selection.SelectAll(["panoramicdata/Alpha.Api", "panoramicdata/Beta.Api", "panoramicdata/Gamma.Api"]);

		model.HiddenSelectedCount(selection).Should().Be(2,
			"a press acts on every ticked row, so the two the filter hides must still be counted");
	}

	[Fact]
	public void ZeroRepositories_ProducesAnEmptyModel_WithoutThrowing()
	{
		var model = Build([]);

		model.Rows.Should().BeEmpty();
		model.Categories.Should().BeEmpty();
		model.SelectableNames.Should().BeEmpty();
		model.TotalCount.Should().Be(0);
		model.HiddenSelectedCount(new RepositorySelection()).Should().Be(0);
	}

	[Fact]
	public void NoCategoryWithAFailure_ProducesNoCategoryColumns()
	{
		var model = Build([Row("A"), Row("B")]);

		model.Categories.Should().BeEmpty();
		model.Rows.Should().HaveCount(2);
	}
}
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test --filter "FullyQualifiedName~EstateTableModelTests"`
Expected: build failure, `EstateTableModel` and its types do not exist.

- [ ] **Step 3: Write the implementation**

Create `PanoramicData.NugetManagement.Web/Services/EstateTableModel.cs`:

```csharp
using PanoramicData.NugetManagement.Models;
using PanoramicData.NugetManagement.Web.Models;

namespace PanoramicData.NugetManagement.Web.Services;

/// <summary>
/// How one column is doing for one repository.
/// </summary>
/// <remarks>
/// Unknown is a state of its own and never reads as good. Nothing about an unchecked repository is
/// reassuring, and a cell that looked clean because it was empty is the most expensive way for the
/// estate to mislead.
/// </remarks>
public enum EstateHealth
{
	/// <summary>Not established: never checked here, or changed since.</summary>
	Unknown,

	/// <summary>Fine.</summary>
	Good,

	/// <summary>Not fine.</summary>
	Bad
}

/// <summary>
/// The worst severity among a repository's failures in one category.
/// </summary>
public enum EstateSeverity
{
	/// <summary>The repository has not been assessed, so nothing is known.</summary>
	Unknown,

	/// <summary>Assessed, with no failing rule in this category.</summary>
	Clean,

	/// <summary>Only informational findings.</summary>
	Info,

	/// <summary>At worst a warning.</summary>
	Warning,

	/// <summary>At least one error or critical finding.</summary>
	Error
}

/// <summary>
/// A column the table can be sorted by.
/// </summary>
public enum EstateSortColumn
{
	/// <summary>What is wrong first: failed build, failed tests, not known, then most issues.</summary>
	Default,

	/// <summary>The repository's name.</summary>
	Repository,

	/// <summary>The total issue count.</summary>
	Issues,

	/// <summary>The current branch.</summary>
	Branch,

	/// <summary>Uncommitted changes.</summary>
	Uncommitted,

	/// <summary>Commits not pushed.</summary>
	Unpushed,

	/// <summary>Sync with origin.</summary>
	Sync,

	/// <summary>The last build.</summary>
	Build,

	/// <summary>The last test run.</summary>
	Test,

	/// <summary>One issue category; the category is passed separately.</summary>
	Category
}

/// <summary>
/// One cell of an issue-class column.
/// </summary>
/// <param name="Category">The class of issue.</param>
/// <param name="Count">How many failing rules, waived and passing ones excluded.</param>
/// <param name="Severity">The worst severity present.</param>
public sealed record EstateCategoryCell(AssessmentCategory Category, int Count, EstateSeverity Severity);

/// <summary>
/// One row of the estate table.
/// </summary>
/// <param name="Source">The row it was built from.</param>
/// <param name="Name">The repository's short name.</param>
/// <param name="FullName">The repository, as "owner/name".</param>
/// <param name="InertReason">Why the row cannot be ticked, or null when it can.</param>
/// <param name="Issues">The total issue count, including stale GitHub issues.</param>
/// <param name="Cells">One cell per category column.</param>
/// <param name="Branch">The current branch, or null when it is not known.</param>
/// <param name="Uncommitted">Whether the working tree is free of uncommitted changes.</param>
/// <param name="Unpushed">Whether there are no commits waiting to be pushed.</param>
/// <param name="Sync">Whether the clone matches origin.</param>
/// <param name="Build">The last build.</param>
/// <param name="Test">The last test run.</param>
public sealed record EstateTableRow(
	RepositoryDashboardRow Source,
	string Name,
	string FullName,
	string? InertReason,
	int Issues,
	IReadOnlyDictionary<AssessmentCategory, EstateCategoryCell> Cells,
	string? Branch,
	EstateHealth Uncommitted,
	EstateHealth Unpushed,
	EstateHealth Sync,
	EstateHealth Build,
	EstateHealth Test)
{
	/// <summary>Whether the row can be ticked.</summary>
	public bool CanSelect => InertReason is null;
}

/// <summary>
/// The estate table, as data: which columns, what each cell holds, what order, and what a filter hides.
/// </summary>
/// <remarks>
/// A separate type rather than logic in the component, for the reason <see cref="ToolbarScope"/> gives:
/// a Razor component cannot be unit tested here, and this is the part worth being sure of.
/// <para>
/// "Ascending" means worst first, for every column where there is a worst: a repository's name sorts
/// A to Z, but a state column puts what is wrong at the top, which is why anyone opens the table.
/// </para>
/// </remarks>
public sealed class EstateTableModel
{
	/// <summary>The issue categories that are columns, in enum order.</summary>
	public required IReadOnlyList<AssessmentCategory> Categories { get; init; }

	/// <summary>The rows to show: filtered, then sorted.</summary>
	public required IReadOnlyList<EstateTableRow> Rows { get; init; }

	/// <summary>How many repositories there are before the filter.</summary>
	public required int TotalCount { get; init; }

	/// <summary>The visible rows that can be ticked, which is what a header checkbox acts on.</summary>
	public IReadOnlyList<string> SelectableNames
		=> [.. Rows.Where(row => row.CanSelect).Select(row => row.FullName)];

	/// <summary>
	/// How many ticked repositories the filter is hiding.
	/// </summary>
	/// <remarks>
	/// A press acts on every ticked row, hidden or not, so the selection line has to say how many it
	/// cannot see rather than let someone believe a press only touches what is on screen.
	/// </remarks>
	/// <param name="selection">What is ticked.</param>
	public int HiddenSelectedCount(RepositorySelection selection)
	{
		var visible = new HashSet<string>(Rows.Select(row => row.FullName), StringComparer.OrdinalIgnoreCase);
		return selection.Names.Count(name => !visible.Contains(name));
	}

	/// <summary>
	/// Builds the table for a set of repositories.
	/// </summary>
	/// <param name="rows">Every repository in the organisation.</param>
	/// <param name="isExcluded">Whether a repository has been excluded from governance.</param>
	/// <param name="filter">Text the name must contain, without regard to case; null or empty shows all.</param>
	/// <param name="sort">The column to sort by.</param>
	/// <param name="descending">Whether to reverse the order.</param>
	/// <param name="sortCategory">The category to sort by when <paramref name="sort"/> is <see cref="EstateSortColumn.Category"/>.</param>
	public static EstateTableModel Build(
		IEnumerable<RepositoryDashboardRow> rows,
		Func<string, bool> isExcluded,
		string? filter = null,
		EstateSortColumn sort = EstateSortColumn.Default,
		bool descending = false,
		AssessmentCategory? sortCategory = null)
	{
		var all = rows.ToList();

		// The columns come from every repository the estate counts, not the filtered ones, so the
		// layout does not shift as a filter is typed.
		var categories = all
			.Where(row => row.IsGoverned && !isExcluded(row.RepositoryFullName) && row.Assessment is not null)
			.SelectMany(row => row.CategorySummaries
				.Where(pair => pair.Value.TotalFailures > 0)
				.Select(pair => pair.Key))
			.Distinct()
			.OrderBy(category => category)
			.ToList();

		var built = all.Select(row => ToRow(row, categories, isExcluded)).ToList();

		IEnumerable<EstateTableRow> visible = built;

		if (!string.IsNullOrWhiteSpace(filter))
		{
			visible = visible.Where(row => row.FullName.Contains(filter.Trim(), StringComparison.OrdinalIgnoreCase));
		}

		var ordered = Order(visible, sort, sortCategory).ToList();

		if (descending)
		{
			ordered.Reverse();
		}

		return new EstateTableModel
		{
			Categories = categories,
			Rows = ordered,
			TotalCount = all.Count
		};
	}

	private static EstateTableRow ToRow(
		RepositoryDashboardRow row,
		List<AssessmentCategory> categories,
		Func<string, bool> isExcluded)
	{
		var cells = categories.ToDictionary(category => category, category => Cell(row, category));

		return new EstateTableRow(
			row,
			row.RepositoryName,
			row.RepositoryFullName,
			InertReason(row, isExcluded),
			row.TotalFailures,
			cells,
			row.CurrentBranch,
			Flag(row.IsWorkingTreeClean, goodWhen: true),
			Flag(row.HasUnpushedCommits, goodWhen: false),
			Flag(row.IsSyncedWithOrigin, goodWhen: true),
			row.LastBuildState switch
			{
				RepositoryBuildState.Succeeded => EstateHealth.Good,
				RepositoryBuildState.Failed => EstateHealth.Bad,
				_ => EstateHealth.Unknown
			},
			row.LastTestState switch
			{
				RepositoryTestState.Passed => EstateHealth.Good,
				RepositoryTestState.Failed => EstateHealth.Bad,
				_ => EstateHealth.Unknown
			});
	}

	private static EstateCategoryCell Cell(RepositoryDashboardRow row, AssessmentCategory category)
	{
		if (row.Assessment is null)
		{
			return new EstateCategoryCell(category, 0, EstateSeverity.Unknown);
		}

		if (!row.CategorySummaries.TryGetValue(category, out var summary))
		{
			return new EstateCategoryCell(category, 0, EstateSeverity.Clean);
		}

		var severity = summary.Criticals + summary.Errors > 0
			? EstateSeverity.Error
			: summary.Warnings > 0
				? EstateSeverity.Warning
				: summary.Infos > 0 ? EstateSeverity.Info : EstateSeverity.Clean;

		return new EstateCategoryCell(category, summary.TotalFailures, severity);
	}

	private static string? InertReason(RepositoryDashboardRow row, Func<string, bool> isExcluded)
		=> isExcluded(row.RepositoryFullName)
			? "excluded"
			: !row.IsGoverned
				? "not governed"
				: !row.IsClonedLocally ? "not cloned" : null;

	private static EstateHealth Flag(bool? value, bool goodWhen)
		=> value is null ? EstateHealth.Unknown : value == goodWhen ? EstateHealth.Good : EstateHealth.Bad;

	private static IEnumerable<EstateTableRow> Order(
		IEnumerable<EstateTableRow> rows,
		EstateSortColumn sort,
		AssessmentCategory? category)
	{
		static int WorstFirst(EstateHealth health) => health switch
		{
			EstateHealth.Bad => 0,
			EstateHealth.Unknown => 1,
			_ => 2
		};

		// The default order is one rule, stated once: a failed build, then a failed test, then anything
		// not known, then what is good. Two keys in sequence cannot express it, because a repository
		// whose build is good but whose tests fail must rank above one whose build is merely unknown.
		static int Tier(EstateTableRow row)
			=> row.Build == EstateHealth.Bad
				? 0
				: row.Test == EstateHealth.Bad
					? 1
					: row.Build == EstateHealth.Unknown || row.Test == EstateHealth.Unknown ? 2 : 3;

		var byName = StringComparer.OrdinalIgnoreCase;

		return sort switch
		{
			EstateSortColumn.Repository => rows.OrderBy(row => row.Name, byName),
			EstateSortColumn.Issues => rows.OrderByDescending(row => row.Issues).ThenBy(row => row.Name, byName),
			EstateSortColumn.Branch => rows.OrderBy(row => row.Branch ?? string.Empty, byName).ThenBy(row => row.Name, byName),
			EstateSortColumn.Uncommitted => rows.OrderBy(row => WorstFirst(row.Uncommitted)).ThenBy(row => row.Name, byName),
			EstateSortColumn.Unpushed => rows.OrderBy(row => WorstFirst(row.Unpushed)).ThenBy(row => row.Name, byName),
			EstateSortColumn.Sync => rows.OrderBy(row => WorstFirst(row.Sync)).ThenBy(row => row.Name, byName),
			EstateSortColumn.Build => rows.OrderBy(row => WorstFirst(row.Build)).ThenBy(row => row.Name, byName),
			EstateSortColumn.Test => rows.OrderBy(row => WorstFirst(row.Test)).ThenBy(row => row.Name, byName),
			EstateSortColumn.Category when category is { } chosen => rows
				.OrderByDescending(row => row.Cells.TryGetValue(chosen, out var cell) ? cell.Count : 0)
				.ThenBy(row => row.Name, byName),
			_ => rows
				.OrderBy(Tier)
				.ThenByDescending(row => row.Issues)
				.ThenBy(row => row.Name, byName)
		};
	}
}
```

- [ ] **Step 4: Run to verify it passes**

Run: `dotnet test --filter "FullyQualifiedName~EstateTableModelTests"`
Expected: PASS. Confirm the total is **22** (the severity theory contributes 6) and not zero.

`TheDefaultOrder_PutsWhatIsWrongFirst` pins the `Tier` rule in the model: a failed build, then a failed test, then anything not known, then what both builds and passes. The spec states that rule, so if the test and the model ever disagree, the spec is the authority and the other is wrong.

- [ ] **Step 5: Commit**

```bash
git add PanoramicData.NugetManagement.Web/Services/EstateTableModel.cs PanoramicData.NugetManagement.Test/EstateTableModelTests.cs
git commit -m "feat: the estate table as data - columns, cells, order and filter"
```

- [ ] **Step 6: Amend the spec for the one deviation**

The existing `EstateView` shows a total "Issues" count (rule failures plus stale GitHub issues), which a column per category does not carry. Dropping it would lose information the table has today, so the model keeps it. In the spec's *4. `EstateTable` and `EstateTableModel`* section, change the column list line to:

`**Columns, in order:** checkbox | Repository | Issues (the total, carried over from EstateView) | one column per issue class | Branch | Uncommitted | Unpushed | Sync | Build | Test.`

Then:
```bash
git add docs/superpowers/specs/2026-10-04-estate-table-bulk-toolbar-design.md
git commit -m "docs: keep EstateView's total issue count in the estate table spec"
```

---

### Task 6: `EstateTable` component, and putting it at three nodes

**Files:**
- Create: `PanoramicData.NugetManagement.Web/Components/EstateTable.razor`
- Delete: `PanoramicData.NugetManagement.Web/Components/EstateView.razor`
- Modify: `PanoramicData.NugetManagement.Web/Components/Pages/Home.razor`
- Test: `PanoramicData.NugetManagement.Test/EstateTableRenderTests.cs`

**Interfaces:**
- Consumes: `EstateTableModel`, `RepositorySelection`, `ToolbarScope`, `NavTreeDataProvider.CategoryKey` / `RepoKey` / `BuildRepositoryIconCss`.
- Produces: component `EstateTable` with parameters `Rows` (`IReadOnlyList<RepositoryDashboardRow>`), `Organization` (`string`), `Heading` (`string`, default "Repositories"), `Selection` (`RepositorySelection`), `IsExcluded` (`Func<string,bool>`), `OnSelectionChanged` (`EventCallback`), `OnNavigate` (`EventCallback<(string RepositoryFullName, AssessmentCategory? Category)>`).

- [ ] **Step 1: Write the failing render tests**

Create `PanoramicData.NugetManagement.Test/EstateTableRenderTests.cs`:

```csharp
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

		await using var renderer = new HtmlRenderer(services, services.GetRequiredService<ILoggerFactory>());

		return await renderer.Dispatcher.InvokeAsync(async () =>
		{
			var parameters = ParameterView.FromDictionary(new Dictionary<string, object?>
			{
				["Rows"] = rows,
				["Organization"] = "panoramicdata",
				["Selection"] = selection ?? new RepositorySelection(),
				["IsExcluded"] = (Func<string, bool>)(_ => false)
			});

			var output = await renderer.RenderComponentAsync<EstateTable>(parameters);
			return output.ToHtmlString();
		});
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
		html.Should().Contain("checked");
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
```

If the test project does not compile because `HtmlRenderer` is unavailable, add `<FrameworkReference Include="Microsoft.AspNetCore.App" />` inside an `<ItemGroup>` in `PanoramicData.NugetManagement.Test.csproj` and say so in the commit message. Add nothing else.

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test --filter "FullyQualifiedName~EstateTableRenderTests"`
Expected: build failure, `EstateTable` does not exist.

- [ ] **Step 3: Write the component**

Create `PanoramicData.NugetManagement.Web/Components/EstateTable.razor`. **No Razor comment may sit inside any tag.**

```razor
@using PanoramicData.NugetManagement.Models
@using PanoramicData.NugetManagement.Web.Models
@using PanoramicData.NugetManagement.Web.Services

@{
	var model = Model;
	var visibleNames = model.SelectableNames;
	var headerState = Selection.StateOf(visibleNames);
	var hidden = model.HiddenSelectedCount(Selection);
}

<div class="mb-3">
	<h4><i class="fas fa-cubes"></i> @Heading</h4>
	<div class="text-muted">@Organization</div>
</div>

@if (model.TotalCount == 0)
{
	<div class="alert alert-info">
		No repositories are cached for @Organization. Discover them from the organisation node first.
	</div>
}
else
{
	<p class="fixes-intro">
		Tick the repositories to act on, then use the toolbar. <strong>Publish</strong> stays a decision made one
		repository at a time. Repositories that are excluded or not cloned locally are shown but cannot be ticked.
	</p>

	<div class="d-flex align-items-center gap-3 mb-2">
		<input type="search" class="form-control form-control-sm" style="max-width: 18rem" placeholder="Filter repositories..." value="@_filter" @oninput="OnFilterInput" />
		<span class="text-muted small">@SummaryText(model, hidden)</span>
	</div>

	<div class="table-responsive">
		<table class="table table-sm align-middle">
			<thead>
				<tr>
					<th>
						<button type="button" class="btn btn-link p-0" title="Tick or untick every visible repository" @onclick="() => ToggleAllVisible(visibleNames, headerState)">
							<i class="@HeaderIcon(headerState)"></i>
						</button>
					</th>
					<th></th>
					<th class="sortable" @onclick="() => SortBy(EstateSortColumn.Repository)">Repository</th>
					<th class="sortable text-center" @onclick="() => SortBy(EstateSortColumn.Issues)">Issues</th>
					@foreach (var category in model.Categories)
					{
						<th class="sortable text-center" style="max-width: 6rem; white-space: normal" title="@category" @onclick="() => SortByCategory(category)">@CategoryLabel(category)</th>
					}
					<th class="sortable" @onclick="() => SortBy(EstateSortColumn.Branch)">Branch</th>
					<th class="sortable" @onclick="() => SortBy(EstateSortColumn.Uncommitted)">Uncommitted</th>
					<th class="sortable" @onclick="() => SortBy(EstateSortColumn.Unpushed)">Unpushed</th>
					<th class="sortable" @onclick="() => SortBy(EstateSortColumn.Sync)">Sync</th>
					<th class="sortable" @onclick="() => SortBy(EstateSortColumn.Build)">Build</th>
					<th class="sortable" @onclick="() => SortBy(EstateSortColumn.Test)">Test</th>
				</tr>
			</thead>
			<tbody>
				@foreach (var row in model.Rows)
				{
					<tr class="@(row.CanSelect ? null : "text-muted")">
						<td>
							<input type="checkbox" checked="@Selection.Contains(row.FullName)" disabled="@(!row.CanSelect)" @onchange="() => ToggleRow(row.FullName)" />
						</td>
						<td><i class="@NavTreeDataProvider.BuildRepositoryIconCss(row.Source)"></i></td>
						<td>
							<a href="#" @onclick="() => Navigate(row.FullName, null)" @onclick:preventDefault>@row.Name</a>
							@if (row.InertReason is not null)
							{
								<span class="badge bg-secondary ms-1">@row.InertReason</span>
							}
						</td>
						<td class="text-center">@(row.Issues == 0 ? "—" : row.Issues.ToString())</td>
						@foreach (var category in model.Categories)
						{
							var cell = row.Cells[category];
							<td class="text-center">
								@if (cell.Severity == EstateSeverity.Unknown)
								{
									<span class="text-muted" title="Not assessed">?</span>
								}
								else if (cell.Count == 0)
								{
									<span class="text-muted">—</span>
								}
								else
								{
									<a href="#" class="@SeverityBadge(cell.Severity)" title="@category" @onclick="() => Navigate(row.FullName, category)" @onclick:preventDefault>@cell.Count</a>
								}
							</td>
						}
						<td class="small">@(row.Branch ?? "?")</td>
						<td>@FlagText(row.Uncommitted, "clean", "uncommitted")</td>
						<td>@FlagText(row.Unpushed, "none", "unpushed")</td>
						<td>@FlagText(row.Sync, "synced", "out of sync")</td>
						<td title="@Age(row.Source.LastBuiltAtUtc)">@FlagText(row.Build, "builds", "fails")</td>
						<td title="@Age(row.Source.LastTestedAtUtc)">@FlagText(row.Test, "passes", "fails")</td>
					</tr>
				}
			</tbody>
		</table>
	</div>
}

@code {
	/// <summary>The repositories to show.</summary>
	[Parameter]
	public IReadOnlyList<RepositoryDashboardRow> Rows { get; set; } = [];

	/// <summary>The organisation they belong to, for the subheading.</summary>
	[Parameter]
	public string Organization { get; set; } = string.Empty;

	/// <summary>The title shown above the table.</summary>
	[Parameter]
	public string Heading { get; set; } = "Repositories";

	/// <summary>The ticked repositories. Owned by the page, so ticks survive moving between nodes.</summary>
	[Parameter]
	public RepositorySelection Selection { get; set; } = new();

	/// <summary>Whether a repository has been excluded from governance.</summary>
	[Parameter]
	public Func<string, bool> IsExcluded { get; set; } = _ => false;

	/// <summary>Raised after the selection changes, so the page can re-render its toolbar.</summary>
	[Parameter]
	public EventCallback OnSelectionChanged { get; set; }

	/// <summary>Raised to open a repository, or one of its issue categories.</summary>
	[Parameter]
	public EventCallback<(string RepositoryFullName, AssessmentCategory? Category)> OnNavigate { get; set; }

	private string _filter = string.Empty;
	private EstateSortColumn _sort = EstateSortColumn.Default;
	private bool _descending;
	private AssessmentCategory? _sortCategory;

	private EstateTableModel Model
		=> EstateTableModel.Build(Rows, IsExcluded, _filter, _sort, _descending, _sortCategory);

	private void OnFilterInput(ChangeEventArgs args) => _filter = args.Value?.ToString() ?? string.Empty;

	private void SortBy(EstateSortColumn column)
	{
		_descending = _sort == column && !_descending;
		_sort = column;
		_sortCategory = null;
	}

	private void SortByCategory(AssessmentCategory category)
	{
		_descending = _sort == EstateSortColumn.Category && _sortCategory == category && !_descending;
		_sort = EstateSortColumn.Category;
		_sortCategory = category;
	}

	private async Task ToggleRow(string repositoryFullName)
	{
		Selection.Toggle(repositoryFullName);
		await OnSelectionChanged.InvokeAsync();
	}

	private async Task ToggleAllVisible(IReadOnlyList<string> visible, SelectionState state)
	{
		if (state == SelectionState.All)
		{
			Selection.DeselectAll(visible);
		}
		else
		{
			Selection.SelectAll(visible);
		}

		await OnSelectionChanged.InvokeAsync();
	}

	private Task Navigate(string repositoryFullName, AssessmentCategory? category)
		=> OnNavigate.InvokeAsync((repositoryFullName, category));

	private string SummaryText(EstateTableModel model, int hidden)
	{
		var text = $"{Selection.Count} of {model.TotalCount} selected";

		if (hidden > 0)
		{
			text += $" ({hidden} hidden by the filter, and still acted on)";
		}

		return Selection.Count == 0 ? $"{text}. Tick repositories to act on them." : text;
	}

	private static string HeaderIcon(SelectionState state) => state switch
	{
		SelectionState.All => "fas fa-square-check",
		SelectionState.Some => "fas fa-square-minus",
		_ => "far fa-square"
	};

	private static string CategoryLabel(AssessmentCategory category)
		=> System.Text.RegularExpressions.Regex.Replace(category.ToString(), "(?<=[a-z])(?=[A-Z])", " ");

	private static string SeverityBadge(EstateSeverity severity) => severity switch
	{
		EstateSeverity.Error => "badge bg-danger text-decoration-none",
		EstateSeverity.Warning => "badge bg-warning text-dark text-decoration-none",
		_ => "badge bg-info text-dark text-decoration-none"
	};

	private static RenderFragment FlagText(EstateHealth health, string good, string bad) => __builder =>
	{
		switch (health)
		{
			case EstateHealth.Good:
				<span class="text-success">@good</span>
				break;
			case EstateHealth.Bad:
				<span class="text-danger fw-bold">@bad</span>
				break;
			default:
				<span class="text-muted" title="Not known">?</span>
				break;
		}
	};

	private static string Age(DateTimeOffset? at)
		=> at is { } when ? $"As at {when.ToLocalTime():yyyy-MM-dd HH:mm}" : "Never run here, or changed since";
}
```

If the build reports an unknown-parameter or markup error, the likeliest cause is a `@* *@` comment inside a tag. There should be none in this file.

- [ ] **Step 4: Run to verify it passes**

Run: `dotnet test --filter "FullyQualifiedName~EstateTableRenderTests"`
Expected: PASS. Confirm the total is **6** and not zero.

- [ ] **Step 5: Show it at the three nodes in `Home.razor`**

1. Selection upkeep. Beside `_selection` (added in Task 3) add:
```csharp
	private string _selectionOrganization = string.Empty;
	private int _selectionDropped;

	/// <summary>
	/// Keeps the selection to the current organisation, and drops ticks for repositories that have since
	/// gone. Called from the places that read the selection, never from anything that must not have side
	/// effects, and it only ever shrinks the selection.
	/// </summary>
	private void EnsureSelectionIsCurrent()
	{
		if (!string.Equals(_selectionOrganization, CurrentOrganization, StringComparison.OrdinalIgnoreCase))
		{
			_selection.Clear();
			_selectionOrganization = CurrentOrganization;
			_selectionDropped = 0;
		}

		_selectionDropped += _selection.Prune(EstateCandidates.Select(row => row.RepositoryFullName));
	}
```
2. Call it first in `EstateTargets`:
```csharp
	private List<RepositoryDashboardRow> EstateTargets(WorkflowStep step)
	{
		EnsureSelectionIsCurrent();
		return ToolbarScope.Targets(EstateCandidates, step, RuntimeSettings.IsRepositoryExcluded, _selection);
	}
```
(Replace the existing expression-bodied `EstateTargets`.)
3. Report dropped ticks in `QueueAcrossEstate`, directly before `AppendConsole(ToolbarScope.DescribeSelection(...))`:
```csharp
		if (_selectionDropped > 0)
		{
			AppendConsole($"{_selectionDropped} ticked "
				+ $"{(_selectionDropped == 1 ? "repository no longer exists and was" : "repositories no longer exist and were")} "
				+ "dropped from the selection.");
			_selectionDropped = 0;
		}
```
4. The render helper and handlers, near `RenderCurrentView`:
```csharp
	private RenderFragment RenderEstateTable(string heading) => __builder =>
	{
		EnsureSelectionIsCurrent();

		<EstateTable Rows="@EstateCandidates"
					 Organization="@CurrentOrganization"
					 Heading="@heading"
					 Selection="@_selection"
					 IsExcluded="@(name => RuntimeSettings.IsRepositoryExcluded(name))"
					 OnSelectionChanged="@OnTableSelectionChanged"
					 OnNavigate="@NavigateFromTableAsync" />
	};

	private void OnTableSelectionChanged() => StateHasChanged();

	private async Task NavigateFromTableAsync((string RepositoryFullName, AssessmentCategory? Category) target)
	{
		if (target.Category is { } category)
		{
			await SelectNavNodeAsync(
				NavTreeDataProvider.CategoryKey(target.RepositoryFullName, category),
				NavView.CategoryDetail);
		}
		else
		{
			await SelectNavNodeAsync(
				NavTreeDataProvider.RepoKey(target.RepositoryFullName),
				NavView.RepositoryDetail);
		}
	}
```
5. The render switch in `RenderCurrentView`:
   - Take `NavView.Organisation` back out of the Home/None group (undo the Task 2 step 5.4 addition), so that group is `NavView.Home` / `NavView.None` only.
   - Replace the `NavView.Repositories` case body (the Razor comment and `<EstateView ... />`) with:
```csharp
			case NavView.Repositories:
				@RenderEstateTable("Repositories")
				break;
			case NavView.Organisation:
				@RenderEstateTable("Repositories")
				break;
```
   - Replace the `NavView.Issues` case's `<IssuesView Embedded="true" Organization="@CurrentOrganization" OnOutput="AppendIssuesOutput" OnEnqueue="EnqueueBulkRule" />` (the one **without** `FocusCategory`) with `@RenderEstateTable("Issues")`. Leave `IssueCategoryDetail` and `IssueRuleDetail` exactly as they are.
6. Delete the old component: `git rm PanoramicData.NugetManagement.Web/Components/EstateView.razor`, then Grep for `EstateView` across the solution; the only remaining match should be prose in comments. Fix any code reference.

- [ ] **Step 6: Build and run the view-coverage tests**

Run: `dotnet build PanoramicData.NugetManagement.Web/PanoramicData.NugetManagement.Web.csproj`
Expected: succeeds.

Run: `dotnet test --filter "FullyQualifiedName~NavViewCoverageTests|FullyQualifiedName~EstateTableRenderTests"`
Expected: PASS. `EveryViewTheTreeCanSelectShouldBeRenderedSomewhere` proves `Organisation` still has a case.

- [ ] **Step 7: Commit**

```bash
git add PanoramicData.NugetManagement.Web/Components/EstateTable.razor PanoramicData.NugetManagement.Web/Components/Pages/Home.razor PanoramicData.NugetManagement.Test/EstateTableRenderTests.cs
git commit -m "feat: one repository table with multi-select at the organisation, Issues and Repositories nodes"
```
(`git rm` already staged the `EstateView.razor` deletion.)

---

### Task 7: Toolbar wiring: visibility, hints, Re-assess and Rediscover

The steps already route through `EstateTargets` and `QueueAcrossEstate`, so Sync, Fix, Build, Test and Commit & Push act on the ticked rows as of Task 3. This task finishes what the user sees: the tooltip that says why a button is greyed, one Re-assess button that reads correctly, and Rediscover Org at all three nodes.

**Files:**
- Modify: `PanoramicData.NugetManagement.Web/Components/Pages/Home.razor`
- Test: `PanoramicData.NugetManagement.Test/NavViewCoverageTests.cs`

**Interfaces:**
- Consumes: `_selection`, `EstateTargets`, `EnsureSelectionIsCurrent`, `ToolbarScope` (Tasks 3 and 6).
- Produces: `Home.TickedCount` (`int`, private), a Re-assess button whose label, tooltip, visibility and click follow the rule in the spec.

- [ ] **Step 1: Write the failing source-reading tests**

`NavViewCoverageTests` already reads `Home.razor` for toolbar markup (`ReadToolbarButton`). That is this repository's accepted way to hold page markup to a rule. Add to it:

```csharp
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
```
(`FixWithAiShouldBeOfferedForTheTickedRepositories` is made to pass in Task 8; it is added here so the rule exists from the start and fails until then. If you prefer green at every commit, add that one test in Task 8 instead.)

- [ ] **Step 2: Run to verify they fail**

Run: `dotnet test --filter "FullyQualifiedName~NavViewCoverageTests"`
Expected: `RediscoverOrg...`, `Reassess...` and `FixWithAi...` fail; `Publish...` passes.

- [ ] **Step 3: Rediscover Org at all three nodes**

In `Home.razor`, the `Key="refresh"` toolbar button:
- `Text="@(_orgSelected ? "Rediscover Org" : "Rediscover Orgs")"` becomes `Text="@(_orgSelected || IsEstateWide ? "Rediscover Org" : "Rediscover Orgs")"`.
- `IsVisible="@ShowBulkRunButtons"` (on that button only; confirm with Grep and view the lines) becomes `IsVisible="@(ShowBulkRunButtons || IsEstateWide)"`.
- Its `ToolTip="@(_orgSelected` expression becomes `ToolTip="@(_orgSelected || IsEstateWide`.

In `OnToolbarClick`, `case "refresh":`, change `if (_orgSelected)` to `if (_orgSelected || IsEstateWide)`.

- [ ] **Step 4: One Re-assess button**

1. The `Key="reassess"` button: change `IsVisible="@(ShowBulkRunButtons || _currentView is NavView.Repositories or NavView.RepositoryDetail or NavView.PackageDetail or NavView.CategoryDetail or NavView.RuleDetail)"` to `IsVisible="@(ShowBulkRunButtons || IsEstateWide || _currentView is NavView.Repositories or NavView.RepositoryDetail or NavView.PackageDetail or NavView.CategoryDetail or NavView.RuleDetail)"`. Keep the existing `NavView.RepositoryDetail` text; an existing test pins it.

2. Add:
```csharp
	/// <summary>
	/// How many repositories are ticked, at a node where that means anything.
	/// </summary>
	private int TickedCount
	{
		get
		{
			if (!IsEstateWide)
			{
				return 0;
			}

			EnsureSelectionIsCurrent();
			return _selection.Count;
		}
	}
```

3. Replace `GetReassessButtonText`:
```csharp
	private string GetReassessButtonText()
		=> _selectedRow is not null ? "Re-assess"
			: TickedCount > 0 ? $"Re-assess {TickedCount}"
			: _orgSelected || IsEstateWide ? "Re-assess Org"
			: "Re-assess Orgs";
```

4. In `GetReassessButtonTooltip`, replace the `scope` initialiser with:
```csharp
		var scope = _selectedRow is not null
			? "Re-assess this package"
			: TickedCount > 0
				? $"Assess the {TickedCount} ticked {(TickedCount == 1 ? "repository" : "repositories")} again"
				: _orgSelected || IsEstateWide
					? $"Assess every {CurrentOrganization} repository again, without re-reading the package list from NuGet"
					: "Assess every known repository again, without re-reading the package list from NuGet";
```

5. In `OnToolbarClick`, replace the `case "reassess":` body with (copy the surrounding whitespace from the file):
```csharp
			case "reassess":
				// Ticked rows win; otherwise package, then organisation, then everything.
				if (TickedCount > 0)
				{
					QueueAcrossEstate(WorkKind.Reassess, WorkflowStep.Reassess, "Re-assess");
				}
				else if (_selectedRow is not null)
				{
					await ReassessAllAsync();
				}
				else if (_orgSelected || IsEstateWide)
				{
					await ReassessOrganizationAsync(CurrentOrganization);
				}
				else
				{
					await ReassessAllReposAsync();
				}

				break;
```

- [ ] **Step 5: The hint, in the one place every step button's tooltip passes through**

At the top of `GatedTooltip`, before the existing `if (FirstQueuedStep ...)`:
```csharp
		// At the estate nodes a button acts on the ticked rows, so the useful answer to "why is this
		// greyed" is what it would do, or that nothing is ticked. Re-assess with nothing ticked keeps its
		// own tooltip: it falls back to the whole organisation.
		if (IsEstateWide && !(ToolbarScope.FallsBackToWholeOrganisation(step) && TickedCount == 0))
		{
			return ToolbarScope.DescribeSelection(
				GetWorkflowStepDisplayName(step),
				EstateTargets(step).Count,
				TickedCount);
		}

```

And in `GetNavItemTooltip`, replace the `case NavView.Repositories:` body (the text that says every step runs on all N repositories) with:
```csharp
			case NavView.Repositories:
				return "Repositories — tick rows in the table, then use the toolbar to act on them. "
					+ "Publish stays one repository at a time"
					+ GetRolledUpHealthSuffix(item);
```
(That text described the old whole-estate behaviour, which no longer exists.)

- [ ] **Step 6: Build and run**

Run: `dotnet build PanoramicData.NugetManagement.Web/PanoramicData.NugetManagement.Web.csproj`
Expected: succeeds.

Run: `dotnet test --filter "FullyQualifiedName~NavViewCoverageTests"`
Expected: PASS except `FixWithAiShouldBeOfferedForTheTickedRepositories`, which Task 8 satisfies.

- [ ] **Step 7: Commit**

```bash
git add PanoramicData.NugetManagement.Web/Components/Pages/Home.razor PanoramicData.NugetManagement.Test/NavViewCoverageTests.cs
git commit -m "feat: one Re-assess and a Rediscover Org button at every estate node, and a tooltip that says what a press will do"
```

---

### Task 8: Fix with AI on the ticked rows, and the confirmations

**Files:**
- Create: `PanoramicData.NugetManagement.Web/Services/AiFixTargets.cs`
- Modify: `PanoramicData.NugetManagement.Web/Components/Pages/Home.razor`
- Test: `PanoramicData.NugetManagement.Test/AiFixTargetsTests.cs`

**Interfaces:**
- Consumes: `AiFixCandidates.For(row, RemediationRegistry)`, `HumanIssueFilter.IsHumanRaised`, `ToolbarScope.RequiresConfirmation` / `RequiresAiFixConfirmation` / `ConfirmationMessage` / `DescribeSelection`, `Home.ConfirmAsync`.
- Produces: `AiFixTargets.UnanalysedHumanIssues(RepositoryDashboardRow)`, `AiFixTargets.IsVerdictStale(RepositoryIssue)`, `AiFixTargets.WorkCount(RepositoryDashboardRow, RemediationRegistry)`.

- [ ] **Step 1: Write the failing tests**

Create `PanoramicData.NugetManagement.Test/AiFixTargetsTests.cs`:

```csharp
using PanoramicData.NugetManagement.Models;
using PanoramicData.NugetManagement.Web.Models;
using PanoramicData.NugetManagement.Web.Remediations;
using PanoramicData.NugetManagement.Web.Services;

namespace PanoramicData.NugetManagement.Test;

/// <summary>
/// Tests for <see cref="AiFixTargets"/>: which repositories have work only a local model can do. Lifted
/// out of the page so that deciding it for many repositories at once is testable.
/// </summary>
public class AiFixTargetsTests(ITestOutputHelper output) : TestWithOutput(output)
{
	private static RepositoryIssue Issue(string author, int number = 1, bool isPullRequest = false) => new()
	{
		Number = number,
		Title = "Missing methods",
		IsPullRequest = isPullRequest,
		HtmlUrl = $"https://github.com/panoramicdata/Sample/issues/{number}",
		AuthorLogin = author,
		CreatedAtUtc = new DateTimeOffset(2026, 3, 1, 0, 0, 0, TimeSpan.Zero)
	};

	private static RepositoryDashboardRow Row(params RepositoryIssue[] issues) => new()
	{
		Organization = "panoramicdata",
		RepositoryFullName = "panoramicdata/Sample",
		OpenIssues = [.. issues]
	};

	[Fact]
	public void AHumanIssueNobodyHasAnalysed_IsWork()
		=> AiFixTargets.UnanalysedHumanIssues(Row(Issue("a-person")))
			.Should().ContainSingle();

	[Fact]
	public void ABotsItem_IsNotWork()
		=> AiFixTargets.UnanalysedHumanIssues(Row(Issue("dependabot[bot]")))
			.Should().BeEmpty("Dependabot's belong to triage, which does not need a model");

	[Fact]
	public void APullRequest_IsNotWork()
		=> AiFixTargets.UnanalysedHumanIssues(Row(Issue("a-person", isPullRequest: true)))
			.Should().BeEmpty();

	[Fact]
	public void AVerdictWhoseConversationMovedOn_IsStale()
	{
		var issue = Issue("a-person");
		issue.AnalysedAtIssueUpdatedUtc = new DateTimeOffset(2026, 3, 2, 0, 0, 0, TimeSpan.Zero);
		issue.LastMaintainerReplyUtc = new DateTimeOffset(2026, 3, 3, 0, 0, 0, TimeSpan.Zero);

		AiFixTargets.IsVerdictStale(issue).Should().BeTrue(
			"a comment after the verdict may be the reporter answering the very question that made it escalate");
	}

	[Fact]
	public void AVerdictNoOneHasAnsweredSince_IsNotStale()
	{
		var issue = Issue("a-person");
		issue.AnalysedAtIssueUpdatedUtc = new DateTimeOffset(2026, 3, 3, 0, 0, 0, TimeSpan.Zero);
		issue.LastMaintainerReplyUtc = new DateTimeOffset(2026, 3, 2, 0, 0, 0, TimeSpan.Zero);

		AiFixTargets.IsVerdictStale(issue).Should().BeFalse();
	}

	[Fact]
	public void ARepositoryWithNothingForTheModel_HasNoWork()
		=> AiFixTargets.WorkCount(Row(), new RemediationRegistry())
			.Should().Be(0, "no assessment and no issues: there is nothing here the model could do");

	[Fact]
	public void WorkCount_AddsUnanalysedIssuesToRulesOnlyTheModelCanFix()
	{
		var row = Row(Issue("a-person"), Issue("another-person", number: 2));
		row.IsClonedLocally = true;
		row.LocalPath = Path.GetTempPath();
		row.Assessment = new RepoAssessment
		{
			RepositoryFullName = row.RepositoryFullName,
			DefaultBranch = "main",
			AssessedAtUtc = DateTimeOffset.UtcNow,
			RuleResults =
			[
				new RuleResult
				{
					RuleId = "ZZ-99",
					RuleName = "A rule with no deterministic remediation",
					Category = AssessmentCategory.CodeQuality,
					Severity = AssessmentSeverity.Error,
					Passed = false,
					Message = "wrong"
				}
			]
		};

		AiFixTargets.WorkCount(row, new RemediationRegistry())
			.Should().BeGreaterThan(2, "two issues, plus at least one candidate for the unremediable rule");
	}
}
```

If `RepositoryIssue.AnalysedAtIssueUpdatedUtc` or `LastMaintainerReplyUtc` has no public setter, set it with an object initialiser in `Issue(...)` instead, matching how `NavViewCoverageTests` builds an issue.

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test --filter "FullyQualifiedName~AiFixTargetsTests"`
Expected: build failure, `AiFixTargets` does not exist.

- [ ] **Step 3: Write the helper**

Create `PanoramicData.NugetManagement.Web/Services/AiFixTargets.cs`:

```csharp
using PanoramicData.NugetManagement.Models;
using PanoramicData.NugetManagement.Web.Models;
using PanoramicData.NugetManagement.Web.Remediations;

namespace PanoramicData.NugetManagement.Web.Services;

/// <summary>
/// What a local model has to do in a repository: the failing rules no deterministic remediation covers,
/// and the human-raised issues nobody has analysed yet.
/// </summary>
/// <remarks>
/// Lifted out of the page. It used to read the one selected repository, which is why Fix with AI could
/// not act on many; as pure functions of a row it can be asked about every ticked repository, and
/// tested.
/// </remarks>
public static class AiFixTargets
{
	/// <summary>
	/// The human-raised issues in a repository that no analysis has judged yet.
	/// </summary>
	/// <remarks>
	/// A verdict is re-taken when the conversation has moved on since it was reached: a comment added
	/// after the fact may be the reporter answering the very question that made the model escalate,
	/// and a stale verdict shown as current is worse than none.
	/// <para>
	/// Only human-raised issues. Dependabot's belong to triage, which runs on Fix and does not need a
	/// model.
	/// </para>
	/// </remarks>
	/// <param name="row">The repository.</param>
	public static IReadOnlyList<RepositoryIssue> UnanalysedHumanIssues(RepositoryDashboardRow row)
		=>
		[
			.. row.OpenIssues
				.Where(HumanIssueFilter.IsHumanRaised)
				.Where(issue => issue.Analysis is null || IsVerdictStale(issue))
		];

	/// <summary>Whether the conversation has moved on since the verdict was reached.</summary>
	/// <param name="issue">The issue.</param>
	public static bool IsVerdictStale(RepositoryIssue issue)
		=> issue.AnalysedAtIssueUpdatedUtc is { } analysed
			&& issue.LastMaintainerReplyUtc is { } replied
			&& replied > analysed;

	/// <summary>
	/// How much a local model has to do in a repository.
	/// </summary>
	/// <param name="row">The repository.</param>
	/// <param name="remediations">The deterministic remediations, whose coverage is excluded.</param>
	public static int WorkCount(RepositoryDashboardRow row, RemediationRegistry remediations)
		=> AiFixCandidates.For(row, remediations).Count + UnanalysedHumanIssues(row).Count;
}
```

- [ ] **Step 4: Run to verify it passes**

Run: `dotnet test --filter "FullyQualifiedName~AiFixTargetsTests"`
Expected: PASS. Confirm the total is **7** and not zero.

- [ ] **Step 5: Point `Home` at the helper**

1. Replace the body of `UnanalysedHumanIssues()` with a delegate, keeping its signature so its three callers need no change:
```csharp
	private IReadOnlyList<RepositoryIssue> UnanalysedHumanIssues()
		=> _selectedRow is null ? [] : AiFixTargets.UnanalysedHumanIssues(_selectedRow);
```
and replace the body of `IsVerdictStale` with `=> AiFixTargets.IsVerdictStale(issue);`. (Keep its doc comment and signature.)

2. Make the candidate count estate-aware. Replace `AiFixCandidateCount`:
```csharp
	private int AiFixCandidateCount()
		=> IsEstateWide
			? EstateAiFixRows().Count
			: _selectedRow is null
				? 0
				: AiFixCandidates.For(_selectedRow, Remediations).Count + UnanalysedHumanIssues().Count;

	/// <summary>
	/// The ticked repositories that have something only a local model can do.
	/// </summary>
	private List<RepositoryDashboardRow> EstateAiFixRows()
		=> [.. EstateTargets(WorkflowStep.Fix).Where(row => AiFixTargets.WorkCount(row, Remediations) > 0)];
```
   (At the estate the "count" is how many *repositories* have work, which is what enables the button and what its tooltip reports.)

3. The button. Change `IsVisible="@(_selectedRow is not null && AiFixCandidateCount() > 0)"` on the `Key="fix-with-ai"` button to `IsVisible="@((_selectedRow is not null || IsEstateWide) && AiFixCandidateCount() > 0)"`.

4. The tooltip. At the top of `GetFixWithAiTooltip()` add:
```csharp
		if (IsEstateWide)
		{
			var withWork = EstateAiFixRows().Count;
			return !RuntimeSettings.Ollama.IsConfigured
				? "No local model is configured — set one under Settings, Ollama Config."
				: withWork == 0
					? "Nothing ticked needs the model."
					: $"Start local-model work in {withWork} ticked "
						+ $"{(withWork == 1 ? "repository" : "repositories")}.";
		}

```

5. Replace `FixWithAiAsync` with the estate-aware version, extracting the existing per-repository body into `QueueAiFixFor`:
```csharp
	private async Task FixWithAiAsync()
	{
		if (IsEstateWide)
		{
			await FixWithAiAcrossEstateAsync();
			return;
		}

		if (_selectedRow is null)
		{
			return;
		}

		EnsureSelectedRowIsCurrent();
		QueueAiFixFor(_selectedRow, announceNothingToDo: true);
	}

	/// <summary>
	/// Starts local-model work in every ticked repository that has any, asking first when that is more
	/// than one, and saying which it left out.
	/// </summary>
	private async Task FixWithAiAcrossEstateAsync()
	{
		var targets = EstateTargets(WorkflowStep.Fix);
		var withWork = targets.Where(row => AiFixTargets.WorkCount(row, Remediations) > 0).ToList();

		AppendConsole(ToolbarScope.DescribeSelection("Fix with AI", targets.Count, _selection.Count));

		if (withWork.Count == 0)
		{
			return;
		}

		if (ToolbarScope.RequiresAiFixConfirmation(withWork.Count)
			&& !await ConfirmAsync(ToolbarScope.ConfirmationMessage(
				"Start local-model fixes in",
				[.. withWork.Select(row => row.RepositoryName)])))
		{
			AppendConsole("Fix with AI cancelled: nothing was queued.");
			return;
		}

		foreach (var row in withWork)
		{
			QueueAiFixFor(row, announceNothingToDo: false);
		}

		var without = targets.Except(withWork).Select(row => row.RepositoryName).ToList();
		if (without.Count > 0)
		{
			AppendConsole($"Nothing for the model in: {string.Join(", ", without)}.");
		}
	}

	/// <summary>
	/// Queues one repository's local-model fixes and issue analyses.
	/// </summary>
	/// <param name="row">The repository.</param>
	/// <param name="announceNothingToDo">Whether to say so when there is nothing to queue.</param>
	private void QueueAiFixFor(RepositoryDashboardRow row, bool announceNothingToDo)
	{
		var candidates = AiFixCandidates.For(row, Remediations);
		var issues = AiFixTargets.UnanalysedHumanIssues(row);

		if (candidates.Count == 0 && issues.Count == 0)
		{
			if (announceNothingToDo)
			{
				AppendConsole("Nothing here needs the model: every failing rule has an automatic "
					+ "remediation, and every human-raised issue already has a verdict.");
			}

			return;
		}

		if (candidates.Count > 0)
		{
			var queued = FanOut.EnqueueAiFix(
				row.Organization,
				row.RepositoryFullName,
				candidates,
				_selectedNodeKey);

			AppendConsole($"⏳ Queued {queued} local-model fix(es) for {row.RepositoryName}: "
				+ $"{string.Join(", ", candidates)}.");
		}

		if (issues.Count > 0)
		{
			var queued = FanOut.EnqueueIssueAnalysis(
				row.Organization,
				row.RepositoryFullName,
				[.. issues.Select(issue => issue.Number)],
				_selectedNodeKey);

			// Said plainly because analysis is not fixing. Most of these end as a verdict somebody
			// reads; only a confident, unflagged one from a known contributor turns into a change,
			// and none of them closes anything.
			AppendConsole($"⏳ Queued {queued} issue analysis(es) for {row.RepositoryName}: "
				+ $"{string.Join(", ", issues.Select(issue => $"#{issue.Number}"))}. Each produces a "
				+ "verdict; rejections always wait for you.");
		}
	}
```
   Delete the old `private Task FixWithAiAsync()` it replaces. Its `<remarks>` (the paragraph distinguishing it from the frontier-model IDE hand-off) should move onto the new `FixWithAiAsync`.

6. Commit & Push asks first. Replace `CommitAndPushAsync` (the whole method, shown in full here):
```csharp
	private async Task CommitAndPushAsync()
	{
		if (IsEstateWide)
		{
			var targets = EstateTargets(WorkflowStep.CommitAndPush);

			if (ToolbarScope.RequiresConfirmation(WorkflowStep.CommitAndPush, targets.Count)
				&& !await ConfirmAsync(ToolbarScope.ConfirmationMessage(
					"Commit & push",
					[.. targets.Select(row => row.RepositoryName)])))
			{
				AppendConsole("Commit & push cancelled: nothing was queued.");
				return;
			}

			QueueAcrossEstate(WorkKind.CommitAndPush, WorkflowStep.CommitAndPush, "Commit & push");
			return;
		}

		if (_selectedRow is null)
		{
			return;
		}

		EnsureSelectedRowIsCurrent();

		QueueForSelectedRepository(WorkKind.CommitAndPush, $"Commit & push {_selectedRow.RepositoryFullName}", WorkflowStep.CommitAndPush);
	}
```

- [ ] **Step 6: Build and run**

Run: `dotnet build PanoramicData.NugetManagement.Web/PanoramicData.NugetManagement.Web.csproj`
Expected: succeeds. If `CommitAndPushAsync` was referenced as a `Func<Task>` or via `Task.CompletedTask` somewhere, Grep for its callers (`OnToolbarClick` awaits it, which is fine).

Run: `dotnet test --filter "FullyQualifiedName~NavViewCoverageTests|FullyQualifiedName~AiFixTargetsTests|FullyQualifiedName~ToolbarScopeTests"`
Expected: PASS, now including `FixWithAiShouldBeOfferedForTheTickedRepositories`.

- [ ] **Step 7: Commit**

```bash
git add PanoramicData.NugetManagement.Web/Services/AiFixTargets.cs PanoramicData.NugetManagement.Web/Components/Pages/Home.razor PanoramicData.NugetManagement.Test/AiFixTargetsTests.cs
git commit -m "feat: Fix with AI on the ticked repositories, and ask before a multi-repository Commit & Push or Fix with AI"
```

---

### Task 9: Whole-branch verification and the guidance text

**Files:**
- Modify: `PanoramicData.NugetManagement.Web/Components/Pages/Home.razor` (one paragraph of guidance)

- [ ] **Step 1: Update the getting-started step that describes Issues**

In `RenderGettingStarted`, the fifth list item currently reads (it describes Issues as grouping failures by rule). Replace its text with:
```razor
					<strong>Or act on several at once.</strong> <em>Issues</em>, <em>Repositories</em> and an
					organisation each show every repository against each class of issue, with its git, build and
					test state. Tick the ones you want and use the toolbar; open a category or rule under
					<em>Issues</em> to apply one fix across every repository it affects.
```
(Locate it with Grep for `Or fix one problem everywhere at once.`.)

- [ ] **Step 2: Build everything**

Run: `dotnet build`
Expected: succeeds with no warnings treated as errors.

- [ ] **Step 3: Run the whole suite**

Run: `dotnet test --filter "Category!=Ollama"`
Expected: PASS. `AI02_ShouldOfferReplacement_WhenAgentsMdIsTheGeneratedLegacyTemplate` is **known** to fail on a clean checkout of `main` on Windows (a CRLF effect in its fixture). If it is the only failure, that is not this branch's; say so in the PR rather than hiding it. Any other failure is real: fix it.

- [ ] **Step 4: Check for leftovers**

Run these as three plain commands and confirm each result:
- Grep for `EstateView` in `*.cs` and `*.razor`. Expect no code references.
- Grep for `ToolbarScope.Describe(` (not `DescribeSelection`). Expect none.
- Grep for `@\*` between attributes: open `EstateTable.razor` and confirm no `@*` appears inside any tag.

- [ ] **Step 5: Confirm what could not be verified**

State plainly in the pull request body, as the spec requires: the app was **not** started, so the toolbar wiring in `Home.razor` is build-verified and source-pattern-tested only, and the first live view of it is the requester's. The `EstateTable` component is render-tested with `HtmlRenderer`.

- [ ] **Step 6: Commit, push, open the pull request**

```bash
git add PanoramicData.NugetManagement.Web/Components/Pages/Home.razor
git commit -m "docs: describe the table and ticking in the getting-started guidance"
git push -u origin worktree-estate-table-bulk-toolbar
```
Then `gh pr create` with a body that lists: what the table shows, that the toolbar acts on ticked rows (no ticks means nothing, except Re-assess), the two confirmations, that Publish is unchanged, the new test status, and the unverified-live caveat above. Do **not** merge; the requester merges.

---

## Self-Review

**Spec coverage**

| Spec requirement | Task |
|---|---|
| `RepositorySelection` (toggle, select all visible, clear, prune, case-insensitive) | 1 |
| `NavView.Organisation` and the `Home` audit | 2 |
| `ToolbarScope` estate-wide at three nodes; empty selection yields nothing; Re-assess exception; Describe with the selection; confirmation threshold | 3 |
| `FixScope` maps `Organisation` and `Issues` | 3 |
| Breadcrumb must not show "Repositories" at the new nodes (found while planning) | 3 |
| Test status: field, recording, shared lifetime, cache round trip, old-cache tolerance | 4 |
| Table logic: columns, cells, unknown never good, inert rows, order, sort, filter, hidden-selected | 5 |
| Shared component; replaces `EstateView`; at Issues, org, Repositories; select-all-visible; selection line | 6 |
| Selection pruning and organisation change; dropped ticks reported on the next press | 6 |
| Re-assess one button, label and behaviour; Rediscover Org at three nodes; "tick repositories" tooltip | 7 |
| Fix with AI in bulk; no new concurrency; confirmation for Commit & Push and Fix with AI | 8 |
| Guidance text; whole-branch verification; unverified-live caveat | 9 |

**Placeholder scan:** none. Two steps carry a conditional fix (a possible `HtmlRenderer` framework reference in Task 6, and a possible public-setter difference on `RepositoryIssue` in Task 8); each names the exact change and is not a deferral. One test expectation in Task 5 (`DefaultOrder_PutsWhatIsWrongFirst`) is flagged as written from the rule and to be corrected against the model's actual ordering if they disagree, with an instruction to correct the test only if the rule demands it.

**Type consistency:** `RepositorySelection` members (`Count`, `Names`, `Contains`, `Toggle`, `Select`, `Deselect`, `SelectAll`, `DeselectAll`, `Clear`, `Prune`, `StateOf`) are used identically in Tasks 3, 5, 6, 7 and 8. `ToolbarScope.Targets(rows, step, isExcluded, selection)` and `DescribeSelection(stepName, targetCount, selectedCount)` match between Tasks 3, 6, 7 and 8. `EstateTableModel.Build` parameters and `EstateSortColumn` / `EstateHealth` / `EstateSeverity` names match between Tasks 5 and 6. `ForgetVerificationResults` and `HasVerificationResults` match between Tasks 4 and its tests. `TickedCount` is defined in Task 7 and used in Task 7 only.

**Known limits, stated rather than hidden:** `Home.razor` wiring is verified by build plus source-pattern tests, not rendering; the app is not started; `TestAsync`'s recording is covered through the row's methods and the cache round trip, not by running the executor.
