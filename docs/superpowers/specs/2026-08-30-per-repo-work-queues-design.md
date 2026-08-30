# Per-repository work queues

Date: 2026-08-30

## Problem

One work item runs at a time, application-wide. `WorkQueueService` serialises everything so
that two tabs — or the dashboard and the issues view — cannot drive the same git working tree
at once. That invariant is correct, but its scope is far wider than it needs to be: fixing
`Athonet.Api` blocks building `Auvik.Api`, though the two share nothing. On an estate of
several dozen repositories the queue is the bottleneck, and a bulk apply-and-push across
twelve repositories is twelve serial round trips.

The queue is also shown in a splitter pane below the navigation tree, which is a second place
to look for state that the tree already models per repository.

## Goals

1. Work on one repository never blocks work on another.
2. The queue moves into the navigation tree, as work nodes under the repository (and
   organisation) it belongs to. The left-hand pane goes away.
3. Work survives the tab that started it, and survives a restart of the application.

## Non-goals

- Distributed or multi-process execution. One process owns the lanes.
- Reordering or prioritising items by hand. Lanes stay FIFO.
- Retrying failed work automatically.

## Design

### 1. Lanes

`WorkQueueService` is replaced by `WorkLaneService`, which holds lanes keyed by scope:

| Lane kind | Key | Holds |
|---|---|---|
| Repository | `repo:owner/name` | Everything that acts on one clone: clone, re-assess, fix, build, test, git sync, commit and push, publish. |
| Organisation | `org:name` | Work with no single repository to belong to: rediscovering an organisation's packages from NuGet, and discovering which repositories a bulk action will target. |

**Within a lane, one item runs at a time.** This is the existing invariant, narrowed from the
estate to the clone. It is what still stops two tabs writing to the same working tree.

**Across lanes, a scheduler runs at most `MaxConcurrentLanes` lanes at once** (default 20,
settable in runtime settings). A lane with queued work is *ready*; the scheduler promotes ready
lanes to *running* up to the cap, in the order they became ready. Items in a lane that is ready
but not running show as pending in the tree, exactly as an item waiting behind another does —
the user does not need to distinguish the two.

Lowering the cap does not stop running lanes; it takes effect as lanes drain. Raising it
promotes ready lanes on the next scheduling pass.

The cap exists because a lane can run `dotnet build`, `dotnet test`, `git clone` and GitHub API
calls. Twenty is a working default for a developer machine, not a measured optimum.

### 2. Fan-out

An action that spans repositories no longer exists as a single queued item. It is decomposed
into one item per repository, each landing in that repository's own lane:

- *Clone N repositories* → N `Clone` items.
- *Re-assess organisation* → one `Reassess` item per assessable repository.
- *Apply rule / apply and push* from the issues view → one item per affected repository.

Two consequences the user sees, and they are deliberate:

- The bulk action is no longer a single thing to stop. Stopping it means stopping the items
  still pending, which the organisation node's work node offers as a single "stop all" button
  over every lane beneath it (see section 5). Items already running unwind individually, as they do today.
- Failures are per repository. One repository failing no longer ends the run for the rest.
  The console still reports each failure; the difference is that the others carry on.

Where the target list must be *discovered* before it can be fanned out — rediscovering an
organisation's packages from NuGet, or listing what is available to clone — that discovery is
itself an item on the organisation lane. It is visible and cancellable on the organisation node,
and on completion it enqueues the per-repository items.

### 3. Execution off the circuit

Today the queue coordinates and the Blazor circuit executes: `WorkItem.Run` is a closure over
`Home.razor`, and `RemoveOwnedBy` cancels a circuit's work when it disconnects. With twenty
lanes in flight, tying execution to a browser tab is wrong — closing the tab would cancel
twenty long-running jobs.

Execution moves to `WorkRunnerService`, a singleton `IHostedService` that pumps the lanes.
`WorkItem.OwnerId` and `RemoveOwnedBy` are removed. Any circuit may watch any lane, and any
circuit may cancel any item; no circuit owns work.

This is possible because almost everything the work needs is already a singleton
(`LocalRepoService`, `DashboardCacheService`, `RemediationRegistry`, `RuntimeSettingsService`,
`NuGetDiscoveryService`, `RegressionGuardService`). `DashboardService` is the one scoped
service; the runner resolves it from a per-item `IServiceScope`, which also gives each item a
clean lifetime for anything scoped it acquires.

**Console output** needs no new mechanism. `UiConsoleLogSink` is already a singleton raising
lines to whichever circuits are listening, and `UiConsoleScope.NodeKey` is an `AsyncLocal`
stamped on the run's asynchronous flow. The runner stamps it from the item's own
`ConsoleNodeKey` instead of the enqueueing component's current selection. Lines therefore
reach the right console whether or not the tab that started the work is still open.

The work bodies themselves — `FixAllCoreAsync`, `BuildCoreAsync`, `GitSyncCoreAsync` and the
rest, currently private methods on a 6,665-line `Home.razor` — move to a `WorkExecutors`
service. They already take `(row, params, IProgress<string>, CancellationToken)`, so the move
is mechanical for the bodies themselves. What has to be untangled is their tail work: several
of them mutate `_rows` and reload the tree when they finish. That becomes an event the runner
raises (`ItemCompleted`), which each circuit handles by refreshing its own tree. A component
that is not open simply does not handle it.

### 4. Persistence

Pending work survives a restart of the application.

`WorkItem.Run` — a closure — is replaced by a serialisable descriptor:

```csharp
public sealed record WorkDescriptor(
    WorkKind Kind,
    string? Organization,
    string? RepositoryFullName,
    IReadOnlyDictionary<string, string> Parameters);
```

`WorkKind` is a closed enum covering every existing enqueue site: `Clone`, `Reassess`,
`RediscoverOrganization`, `RefreshAll`, `FixAll`, `FixCategory`, `FixRule`, `Build`, `Test`,
`GitSync`, `CommitAndPush`, `Publish`, `BulkApplyRule`, `BulkApplyAndPushRule`,
`DiscoverCloneTargets`, `DiscoverReassessTargets`. `WorkExecutors` maps each kind to its
method; `Parameters` carries the few extras a kind needs (`category`, `ruleId`).

Making the catalogue closed is the point: it is what turns a queue of arbitrary delegates into
a queue of *named work*, which is the only form that can be written down and picked up again.
It also makes every queueable operation enumerable, which the tests exercise.

**On disk:** one JSON file beside the runtime settings
(`%LOCALAPPDATA%/PanoramicData.NugetManagement/work-queue.json`), written on every queue
change, holding lane keys and their pending descriptors.

**On startup:** pending items are restored to their lanes and the scheduler starts them.

**Items that were running when the process died** are *not* resumed. They are restored as
pending with a `WasInterrupted` flag, and before such an item runs, its repository's working
tree is checked: if dirty, the changes are discarded first, via the existing
`LocalRepoService.DiscardLocalChangesAsync`. A fix that was half-applied when the power went
out must not be built on. This mirrors what cancellation already does.

### 5. Tree UI

The `nav-queue-pane` splitter panel and its `PDSplitter` wrapper are removed from
`Home.razor`; the navigation tree takes the full column.

`NavTreeDataProvider` gains work nodes:

- Under each **repository** node, a `Work` node (`work:owner/name`), shown only when that
  repository's lane has items. Its children are the items themselves — one node per item,
  showing title, state icon and progress text, with stop (running) or remove (pending) buttons
  in the node template, as the pane does today.
- Under each **organisation** node, a `Work` node (`work-org:name`), shown when the
  organisation lane has items **or** any repository beneath it does. Its children are the
  organisation lane's own discovery items; its header carries the "stop everything below here"
  button described in section 2, which cancels the pending items in every descendant repository
  lane and signals the running ones. It does not list those descendant items — they belong to
  their own repositories''' work nodes.

New `NavItem` fields: `WorkItemId`, `WorkItemState`, `WorkItemProgress`. `NavView.WorkItem` is
not needed — work nodes are not selectable views; they carry buttons, like the organisation
node's action glyphs.

There is deliberately no estate-wide roll-up node. Activity is found by the spinner on the
repository node, as agreed. Repository nodes already render `IsBusy`; that now reflects the
repository's own lane.

The tree is rebuilt on `WorkLaneService.Changed`, debounced. Twenty lanes reporting progress
lines will otherwise rebuild the tree far more often than a person can read it. A 250 ms
trailing debounce is the starting value.

### 6. Gating

`WorkflowGate.FirstBlockedStep` is already per repository and needs no change to its logic —
only its input, which becomes the repository's lane rather than the global item list.

`IsQueueBusy`, which currently disables actions whenever *anything* is queued anywhere,
becomes `IsRepositoryBusy(repositoryFullName)`. This is the change that makes the feature
visible: the toolbar for a repository is governed by that repository alone.

## Testing

The existing 555 tests pass unchanged except where they name `WorkQueueService` directly.

New tests, all against services rather than the UI:

- **`WorkLaneServiceTests`** — items in different lanes run concurrently; items in one lane do
  not; the concurrency cap is honoured; a lane over the cap is ready but not running; dedup is
  per lane and pending-only; cancelling a pending item removes it while cancelling a running
  one signals it.
- **`WorkFanOutTests`** — each org-scoped kind decomposes into the expected per-repository
  descriptors; a discovery item enqueues the items its result implies.
- **`WorkDescriptorTests`** — every `WorkKind` round-trips through JSON, and every `WorkKind`
  resolves to an executor. The second is what stops a new kind being added without a way to run
  it.
- **`WorkPersistenceTests`** — pending items are written and restored; an item recorded as
  running is restored as pending and interrupted; an interrupted item over a dirty tree
  discards before running.
- **`NavTreeWorkNodeTests`** — work nodes appear under the repository and organisation whose
  lanes hold items, and are absent when the lanes are empty.

Concurrency tests use a controllable clock/gate rather than sleeps, so they are deterministic.

## Risks

- **Twenty concurrent `dotnet build` invocations** will saturate a laptop. The cap is settable,
  and the default is a starting point to tune against the real estate.
- **GitHub API rate limiting** becomes reachable where serial execution never approached it.
  Out of scope to solve here; worth watching once the change is in use.
- **The `Home.razor` extraction is the bulk of the work** and touches every enqueue site in a
  6,665-line file. It is mechanical but wide, and it is where regressions will come from if
  they come. The existing suite covers the services these bodies call, not the bodies
  themselves, so the extraction is the part to review most carefully.
