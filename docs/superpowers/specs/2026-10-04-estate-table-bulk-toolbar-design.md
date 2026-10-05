# A repository table with multi-select, and a toolbar that acts on the ticked rows

Date: 2026-10-04

## Problem

The tool is meant to be the way repositories are brought to green, but it cannot yet be used for the
last stretch of that. Once the estate is nearly clean, the work is "these three are still red; sync,
fix, build, test and push *those three*", and nothing in the app lets you choose them.

Today the toolbar acts on either one selected repository or, on the Repositories node, on **every**
governed and cloned repository at once. There is no way to act on a chosen subset, no table that shows
*which* repositories are red and in what way, and one of the buttons, Fix with AI, only works on a single
repository.

The immediate consequence, which prompted this: the sync icons for ~45 clones had to be cleared by
hand-running git, because the app offered no bulk route that could be pointed at just those.

## Goals

- One table of every repository in an organisation: a column per class of issue, plus git state, build
  status and test status.
- Multi-select on its rows.
- The toolbar acts on **the ticked rows**: Sync, Fix, Fix with AI, Build, Test, Commit & Push, and
  Re-assess.
- Available at three nodes: the organisation, **Issues** and **Repositories**.

## Non-goals

- **Publish stays one repository at a time.** Unchanged from `ToolbarScope`: a bad publish burns a version
  number that can never be reused.
- No column per *rule*. There are far too many; a column per assessment *category* is the unit.
- No change to the per-category and per-rule drill-downs under Issues (`IssueCategoryDetail`,
  `IssueRuleDetail`) or their own bulk actions.
- Selection is not persisted across restarts.
- No export, saved views or custom column sets.

## What already exists

Found by reading the code rather than assumed, because a good part of this is already built:

- **The toolbar is already estate-aware** on the Repositories node. `ToolbarScope.IsEstateWide` is true
  for `NavView.Repositories`; `ToolbarScope.Targets` picks the governed, cloned, non-excluded rows; and
  `Home.QueueAcrossEstate` queues one work item per target. It has no notion of a *subset*.
- **`EstateView`** (124 lines) is the table on that node: icon, repository, a single issue count, a git
  text summary and build status. No selection.
- **`Fix` is already scoped** through `FixScope`, which holds the rule that Fix is the only button that
  fixes things. `NavView.Issues` is not mapped there, so Fix is hidden at the Issues node.
- **Fix with AI is the exception**: `IsVisible="@(_selectedRow is not null && AiFixCandidateCount() > 0)"`
  and `FixWithAiAsync` both read the single `_selectedRow`.
- **The organisation node shares `NavView.Home`** with the landing page. `NavView.Repositories` was given
  its own view for precisely that reason, so a bulk action could tell the two apart.
- **Each `RepositoryDashboardRow` already carries** `CategorySummaries` (per `AssessmentCategory`:
  criticals, errors, warnings, infos, passed, waived), `CurrentBranch`, `IsWorkingTreeClean`,
  `HasUnpushedCommits`, `IsSyncedWithOrigin`, `LastBuildState` and `LastBuiltAtUtc`.
- **There is no test status.** A test run sets `row.Status` to `TestsPassed` / `TestsFailed`, but nothing
  remembers it the way `RememberBuildResult` remembers a build.
- **There is a single Re-assess button** whose label depends on selection (`Re-assess` for a repository,
  `Re-assess Org` for an organisation, `Re-assess Orgs` otherwise). At Issues and Repositories
  `_orgSelected` is false, so it would read "Re-assess Orgs" there.
- **`ConfirmAsync`** already exists as an in-app modal (the browser's `confirm()` is suppressed in some
  hosts, which is why a modal was built).

## Decisions

Confirmed with the requester:

1. **Placement.** One shared component. It is the landing page of the **Issues** node, **replaces the
   getting-started guidance at the organisation node** (the guidance remains where no organisation is
   selected), and replaces `EstateView` at **Repositories**, so the three can never diverge.
2. **No ticks means nothing**, with one deliberate exception: **Re-assess**, which is read-only and falls
   back to the whole organisation. **Rediscover Org** is always organisation-wide and never needs ticks.
3. **Confirmation** before **Commit & Push** and **Fix with AI** when more than one repository is ticked.
   Sync, Build, Test, Fix and Re-assess run straight away.
4. **Re-assess is one button**, not two: "Re-assess *N*" when rows are ticked, "Re-assess Org" when none
   are. This also fixes the "Re-assess Orgs" mislabel at Issues and Repositories.

## Design

### 1. `RepositorySelection` (new, pure)

The set of ticked repositories, keyed by full name, case-insensitively. Operations: `Toggle`, `Select`,
`Deselect`, `SelectAll(IEnumerable<string>)` (used for "all visible"), `Clear`, `Contains`, `Count`, and
`Prune(IEnumerable<string> stillPresent)`, which drops names that no longer exist after a re-assess,
exclusion or removal.

Owned by `Home` as a field, one per organisation, and cleared when the organisation changes. Passed to
the table as a parameter; the table mutates it through a callback that lets `Home` re-render. It lives
on the page rather than in a DI service because the toolbar's enable/disable logic is already on the
page, and a second owner of the same truth is the thing to avoid.

### 2. `ToolbarScope` (changed)

- `IsEstateWide(NavView)` is true for `Organisation` (new view, below), `Issues` and `Repositories`.
- `Targets(rows, step, isExcluded, selection)` intersects the existing governed / cloned / not-excluded
  filter with the selection. An **empty selection yields no targets**, except where
  `FallsBackToWholeOrganisation(step)` is true, which is `Reassess` only.
- `Describe` reports what a press will do and what it leaves out, now including the selection:
  "Build will run on 3 repositories. 1 ticked repository skipped: not cloned locally." The existing
  principle stands: a bulk action must not quietly act on fewer repositories than it names.
- `AllowsEstateWide` is unchanged: everything but Publish.
- `RequiresConfirmation(step, targetCount)` is new: true for `CommitAndPush` when `targetCount > 1`;
  `Fix with AI` is not a `WorkflowStep`, so it is decided by its own check at the call site using the same
  threshold, kept in one constant so the two cannot disagree.

### 3. A view for the organisation node (new `NavView.Organisation`)

The organisation node currently uses `NavView.Home`, shared with the landing page. It gets its own view,
for the reason `Repositories` did: `ToolbarScope` and `FixScope` take only a `NavView`, so with the node
sharing `Home` they cannot tell "an organisation" from "nothing selected".

`NavTreeDataProvider` assigns it; the `RenderCurrentView` switch renders the table for it. **Every existing
use of `NavView.Home` must be audited**, because some of them mean "the organisation node" and must move
(`ShowBulkRunButtons` is one: `_currentView == NavView.Home && _orgSelected`). A node left on `Home`
renders the placeholder, and a node left on `None` renders nothing; a memory note records that the
coverage test does not catch the second.

`FixScope.For` maps `Organisation` and `Issues` to `new(true, true)`, matching what it already says for
`Repositories`: the whole estate contains everything each repository does. With selection semantics,
"everything beneath the node" means the ticked rows.

### 4. `EstateTable` and `EstateTableModel`

A shared component, `EstateTable`, replacing `EstateView`. Its logic lives in a pure `EstateTableModel`
so it can be unit tested without rendering.

**Columns, in order:** checkbox | Repository | Issues (the total, carried over from EstateView) | one column per issue class | Branch | Uncommitted | Unpushed | Sync | Build | Test.

- **Issue-class columns** are the `AssessmentCategory` values with at least one failing rule among the
  rows in view, in enum order (so the layout is stable and empty classes do not take space). A cell shows
  the count of failing rules (critical + error + warning + info; waived and passed excluded) coloured by
  the worst severity present: red for critical or error, amber for warning, blue for info, which are the
  `badge-fail` / `badge-warn` / `badge-info` classes `Home` already draws. A clean cell is "—", following
  `EstateView`. Clicking a cell navigates to that repository's node for that category.
- **Git columns** read the existing nullable flags. A `null` is "?", never "clean": an unknown state must
  not look like a good one. Branch shows `CurrentBranch`; the others are short badges.
- **Build** and **Test** show Passes / Fails / Not known, with the age of the result in the tooltip.
- **Not-cloned repositories** are shown, greyed, with the checkbox disabled and a "not cloned" note. They
  stay visible because hiding them would make the estate look smaller than it is.
- **Default order:** failed build, failed test, then most issues, then name. The reason to open the page is
  to find what is wrong, so what is wrong is at the top. Headers sort; a text box filters by name.
- **Selection:** a checkbox per row; the header checkbox is tri-state and ticks **all visible rows** (after
  filtering), which is what makes "filter to what is red, tick all" a two-step action. A line above the
  table states the selection ("3 of 47 selected") and, with none, says to tick repositories to act on them.

### 5. Test status (new data)

- `RepositoryTestState { Passed, Failed }`, with `LastTestState` and `LastTestedAtUtc` on
  `RepositoryDashboardRow`, beside the build equivalents.
- `WorkExecutors.TestAsync` records the result through a new `RememberTestResult`, in the same place and
  the same way `RememberBuildResult` does, and persists through `cache.UpsertRow(row)`.
- **Lifetime is the build's.** A test result describes a working tree exactly as a build result does, so
  it is thrown away by the same work kinds. `ForgetBuildStatusIfInvalidated` clears both together, and
  `BuildStatusLifetime`'s remarks gain a line saying so. A test pins that a kind which invalidates the
  build also invalidates the test result.
- **Stale means not known**, as for builds: a green that is no longer true is worse than none.
- The cache must tolerate rows written before these fields existed (they read back as `null`). This is a
  requirement on the implementation to verify, not an assumption.

### 6. Toolbar wiring in `Home`

- **Visibility:** the Sync, Fix, Build, Test and Commit & Push buttons are shown at the three nodes.
  `HasToolbarTarget` already reads `IsEstateWide`.
- **Enablement:** a step is enabled when `Targets(...).Count > 0` and its queue is not blocked. With no
  ticks the tooltip says why ("Tick repositories to act on them") rather than just greying out.
- **`QueueAcrossEstate`** takes the selection through `ToolbarScope.Targets`. It remains the single choke
  point, so the lane key shape that folds a bulk press into an existing single press is unchanged.
- **Fix with AI** is generalised from one repository to a list of target rows. `FixWithAiAsync` loops over
  them, and `UnanalysedHumanIssues()` takes a row instead of reading `_selectedRow`. A repository with
  nothing only a model can do is skipped and named, not silently omitted. It queues through the existing
  `FanOut.EnqueueAiFix` and per-repository lanes, so it adds **no new concurrency**. The button is shown
  when any ticked row has candidates, and its tooltip counts them.
- **Confirmation:** `ConfirmAsync` before Commit & Push and Fix with AI when the target count exceeds one,
  naming the count and the repositories. Declining queues nothing.
- **Re-assess:** one button. With ticks, label "Re-assess *N*", queueing `WorkKind.Reassess` per ticked
  repository through `QueueAcrossEstate`. Without, label "Re-assess Org", doing what it does today.
  `GetReassessButtonText` and its tooltip are rewritten to this rule. Visible at all three nodes, which
  adds Issues, where it is currently absent.
- **Rediscover Org:** shown at all three nodes. Its visibility today is `ShowBulkRunButtons`.

## Error handling

- A ticked repository that disappears (excluded, removed, or gone after a re-assess) is pruned from the
  selection, and the next press names how many were dropped rather than acting on fewer silently.
- A ticked repository that cannot be acted on (not cloned, excluded) is skipped and counted in the
  `Describe` message, as for the whole-estate press today.
- Unknown git state is shown as "?", and a step that needs a clean state still checks it per item when it
  runs, which is how the estate-wide path already behaves.

## Testing

Plain, unit-tested types (the page itself has no bUnit reference and cannot be):

- `RepositorySelection`: toggle, select-all-visible, clear, case-insensitivity, prune.
- `ToolbarScope`: empty selection gives no targets; ticked subset; Re-assess falls back to the org; Publish
  gives none even when ticked; excluded and not-cloned ticks are skipped and counted; the `Describe` text;
  the confirmation threshold.
- `FixScope`: `Organisation` and `Issues` are mapped.
- `EstateTableModel`: column set (only classes with failures, enum order); cell counts and worst severity;
  waived excluded; null git flags render as unknown; default ordering; sorting; filtering; "select all"
  covers only visible rows.
- Test status: recorded on pass and on fail; cleared by every kind that clears the build; `IsKnown` still
  covers the whole `WorkKind` enum.

**Rendering.** A green test run does not prove a `.razor` change renders; a recorded failure mode here is a
Razor comment between a component's attributes, which compiles and then throws at render. `EstateTable`
gets a render test using the framework's `HtmlRenderer`, which needs no new package. `Home.razor` itself
can only be build-verified.

**What I will not do:** start the app to look at it. Starting it restores the shared queue and runs AI
fixes against the real clones, so the toolbar wiring in `Home` will be first seen live by the requester,
and the implementation notes will say so.

## Risks

- **`Home.razor` is ~7,000 lines**, and every change to it risks the file-level traps the memory notes
  record (Razor comments inside tags; a node whose `NavView` renders nothing; reloading the tree on a
  progress path causes flicker). Mitigated by keeping logic out of it and adding only wiring.
- **`NavView.Organisation` touches shared behaviour.** The audit of `NavView.Home` is the riskiest step;
  it is done first, with a test that every node the provider creates has a view the render switch handles.
- **Per-render cost.** Candidate counts for Fix with AI over ~80 rows are computed once per render in the
  model, not per cell.
- **Dev-server lock.** If the app is running, the build must go to a redirected output directory, or it
  compiles nothing.

## Order of work

1. `RepositorySelection` and `ToolbarScope` / `FixScope` changes, with tests.
2. `EstateTableModel` and its tests.
3. Test status: model, recording, lifetime, tests.
4. `NavView.Organisation`: audit, add, and the provider test.
5. `EstateTable` component with its render test; swap it in for `EstateView` and show it at Issues and the
   organisation.
6. Toolbar wiring in `Home`: visibility, enablement, `QueueAcrossEstate`, Re-assess, Rediscover, Fix with
   AI, confirmation.
