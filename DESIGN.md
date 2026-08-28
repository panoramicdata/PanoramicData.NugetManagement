# Design

How work is started, serialised, shown and stopped in the dashboard — what the code does today,
what we are building next, and where it is meant to end up.

This document covers the **execution model** only: the machinery that runs long operations against
repositories. It does not describe the rules engine, which lives in `PanoramicData.NugetManagement`
and is documented by its own rule classes.

---

## 1. The current design

### 1.1 Where work lives

| Piece | Lifetime | Responsibility |
|---|---|---|
| `Home.razor` | Circuit (component) | Owns the whole dashboard shell: navigation tree, toolbar, console, and **the implementations of nearly every long-running operation** (`RefreshAsync`, `ReassessOrganizationAsync`, `FixAllAsync`, `BuildAsync`, `RunTestsAsync`, `CommitAndPushAsync`, `CloneSelectedRepositoriesAsync`, …). ~5,800 lines. |
| `IssuesView.razor` | Circuit (component) | The issue-centric view and its bulk actions: *Fix everything*, *Apply & push to N repos*, *Apply here*. Delegates execution to `DashboardService`. |
| `DashboardService` | **Scoped** (per circuit) | The operations themselves: assess, git sync, apply remediations, commit and push, and the `ApplyAcrossReposAsync` bulk loop. Every method already accepts a `CancellationToken`. |
| `DashboardCacheService` | Singleton | The shared row cache, persisted to JSON on disk. How results reach other circuits at all. |
| `LocalRepoService` | Singleton | Git and process execution: clone, sync, commit, push, `DiscardLocalChangesAsync`, `RevertRangeAndPushAsync`. |
| `RegressionGuardService` | Singleton `BackgroundService` | Builds each repository we pushed to, and reverts our commit if we broke it. Channel + worker + `StatusesChanged` event. |

### 1.2 How work starts today

Every entry point is *start it now, or refuse*. There is no queue, and nothing that represents "work
about to be undertaken" — a click either begins executing immediately or is turned away.

Three independent booleans decide whether a click is allowed, and none of them can see the others:

```
Home.razor        _isLoading | _isAssessing | _isCloningAll  →  IsBulkOperationInFlight
                                                                 guards org Refresh / Re-assess /
                                                                 Rediscover / Clone

Home.razor        _isRunning                                  →  guards per-repo Sync / Fix / Build /
                                                                 Test / Commit & Push / Publish

IssuesView.razor  _busy                                       →  guards Fix everything /
                                                                 Apply & push / Apply here
```

The gaps this leaves are not hypothetical:

- **Per-repo work collides with org work.** The org-level buttons test `IsBulkOperationInFlight`,
  which does not include `_isRunning`. A *Fix* on one repository and a *Re-assess* across the
  organisation can therefore run at the same time.
- **IssuesView collides with everything.** `_busy` is private to that component, and Home's buttons
  never consult it. *Apply & push to 12 repos* can start while an assessment is mid-flight, and
  both walk the same working trees.
- **Two tabs collide with each other.** `DashboardService` is scoped, so a second browser tab has an
  entirely separate set of flags. Nothing in the process knows that two circuits are driving the
  same clone.

`BlockIfBulkOperationInFlightAsync` exists and does the polite thing — names what is in the way and
refuses — but it is only consulted in five places, and only for the flags Home itself owns.

### 1.3 Cancellation today

Cancellation is largely **scaffolding that was never wired up**. `Home.razor` defines
`BeginCancelableOperation`, `CancelCurrentOperationAsync`, `EndCancelableOperation` and
`RestoreStatusAfterCancellation`, along with an `_operationCts` field. None of them has a call site.

What actually exists:

- **Clone-all** can be stopped (`_cloneAllCts`), checked between repositories, and reports
  "stopped early".
- **Everything else** cannot. `IssuesView` passes `CancellationToken.None` into
  `ApplyRuleAcrossReposAsync`, so a confirmed 12-repository commit-and-push runs to completion or
  to its first failure, whichever comes first.

The plumbing below the UI is fine — `DashboardService` and `LocalRepoService` take tokens
throughout. Only the UI never supplies one.

### 1.4 What the current design gets right

Worth keeping, because the target design builds on it:

- **`ApplyAcrossReposAsync` already treats a repository as the unit of work**: verify the clone is
  writable → sync → re-assess the fresh tree → apply → commit and push → hand to the regression
  guard. It checks the token between repositories and stops the whole run on the first failure.
- **`RegressionGuardService` is the right shape for background work** in this app: a singleton, a
  channel, a worker, a status dictionary and a change event that components subscribe to.
  `IssuesView` already renders from it. The work queue follows the same idiom.
- **Revert already exists.** `DiscardLocalChangesAsync` throws away uncommitted work
  (`reset --hard` *and* `clean -fd`, because remediations add files as well as edit them), and
  `RevertRangeAndPushAsync` reverses commits that have already been pushed.

---

## 2. The target design (stage 1 — what we are building now)

**One analysis at a time, application-wide, with a visible queue and a stop that leaves nothing
half-done.**

### 2.1 Shape

A new singleton, `WorkQueueService`, owns the queue, the single-flight lock and the running item's
`CancellationTokenSource`. **It does not execute the work.** Each queued item carries a delegate
supplied by the component that enqueued it, and that component's circuit runs it when the item
reaches the head of the queue.

```
IssuesView ─┐                    ┌─ pending: [ Apply TST-06 (12 repos), Fix everything (5) ]
            ├─► WorkQueueService ┤
Home ───────┘   (singleton)      └─ running: Re-assess panoramicdata, 8/47, CTS
                     │
                     └─ Changed event ──► every connected circuit re-renders its queue panel
```

Why the coordinator does not run the work: the operations live inside `Home.razor` and are woven
into component state — the selected row, the console, the navigation tree. Lifting them into a
service that takes no component state is the stage-2 migration described below. Stage 1 gets the
guarantees without that migration, and is written so stage 2 is a change of executor rather than a
change of model.

### 2.2 The queue item

```csharp
sealed record WorkItem
{
    string Id;                                        // assigned by the queue
    string Title;                                     // "Apply TST-06 & push — 12 repos"
    string? Organization;                             // scope, for display and dedup
    string DedupKey;                                  // identical keys are folded together
    Func<IProgress<string>, CancellationToken, Task> Run;
    WorkItemState State;                              // Pending | Running | Cancelling | Done | Failed | Cancelled
    string? Progress;                                 // "repo 8 of 47"
}
```

One entry per **action the user clicked**, not per repository. A 47-repository re-assessment is one
row reporting `repo 8 of 47`; the per-repository detail continues to go to the console. This keeps
the panel readable and makes each entry exactly one cancellable unit.

**Duplicate folding.** An enqueue whose `DedupKey` matches a *pending* item is dropped, so clicking
*Re-assess org* three times does not buy three passes over the estate. It never folds into the
*running* item: that one has already started and its result may be stale.

### 2.3 Interaction model

- Run buttons **stay enabled** while work is in flight. A click appends to the queue rather than
  being refused. This replaces the `_isLoading` / `_isAssessing` / `_isRunning` / `_busy` guards, and
  `BlockIfBulkOperationInFlightAsync` goes with them.
- The queue is rendered **in the left sidebar, beneath the navigation tree**: the running entry with
  its progress and a Stop button, then the pending entries, each with a ✕ to drop it.
- Removing a pending entry is unconditional — nothing has happened yet.
- Stopping the running entry is described below.
- Output continues to go to the console at the bottom of the screen. The queue says *what* is
  happening; the console says *how it is going*.

### 2.4 Cancellation and atomicity

**A change is atomic per repository.** Stop is honoured immediately, and anything half-applied is
reverted, so a repository is left either fully changed or exactly as it was found.

Within `ApplyAcrossReposAsync`, one repository passes through:

```
verify writable → sync → re-assess → apply edits → commit & push → hand to regression guard
└──────────────────── revertible ─────────────────┘└─ commit point ─┘
```

- **Cancelled before the commit point:** `DiscardLocalChangesAsync` returns the clone to `HEAD`,
  discarding edits and any files the remediation added. The repository counts as untouched. This is
  safe precisely because these are the app's own clones, which is why they live apart from the
  user's own checkouts.
- **Cancelled after the push has succeeded:** that repository's change is *done*, not half-done. The
  run stops before the next repository. Reversing a pushed commit is the regression guard's job, and
  only when the build proves the change was wrong.
- **Cancelled while the push is in flight:** the push is allowed to resolve rather than being killed
  mid-ref-update; the outcome is then reported as it lands.
- **Read-only work** (assess, discover, rate-limit checks) writes nothing, so it aborts at the first
  token check with no revert needed.

Every revert is announced in the console. A silent rollback is worse than none.

### 2.5 Circuit ownership

Because a circuit executes its own items, the coordinator must react to circuits disappearing:

- On disconnect, that circuit's **pending** items are removed from the queue.
- Its **running** item is cancelled, which triggers the revert above, and the queue moves on.

The practical consequence: closing the tab mid-run stops the run cleanly rather than continuing
server-side. For a local operator tool driven by someone watching the console, stopping is the
honest behaviour — but it is the one thing stage 2 changes.

### 2.6 Testing

- `WorkQueueService` is a plain singleton with no UI dependency, so it is unit-testable directly:
  serialisation (a second enqueue does not start until the first completes), duplicate folding,
  cancellation transitions, and removal of a disconnected circuit's items.
- The revert-on-cancel path is tested against `ApplyAcrossReposAsync` with a fake apply delegate and
  a token cancelled mid-run, asserting `DiscardLocalChangesAsync` is called and the repository is
  reported as untouched.
- The existing `RuleEvaluationTests` and self-assessment tests are unaffected; this is Web-project
  work.

---

## 3. The long-term design (stage 2)

**Work belongs to the application, not to a browser tab.**

`WorkQueueService` becomes a `BackgroundService` — the same shape as `RegressionGuardService` — with
a channel and a single worker loop. Each item resolves its own DI scope via `IServiceScopeFactory`,
gets a fresh `DashboardService`, and runs independently of any circuit. Circuits become pure
observers: they render the queue, they enqueue, they stop things, and closing one changes nothing
about what is running.

What that buys:

- A long bulk run survives a browser refresh, a reconnect, or a closed tab.
- The queue is a genuine record of what the *application* is doing, not what a particular tab
  started.
- Work can be scheduled without a browser present at all — the natural home for a future
  "assess everything overnight".

What it costs, and why it is not stage 1:

- Every operation must become expressible without component state. Today they mutate
  `_selectedRow`, append to the console, refresh the tree and re-render inline. They would instead
  write results through `DashboardCacheService` and raise events, with the UI re-rendering from the
  cache — the pattern `RegressionGuardService` already uses.
- That means lifting the bulk of `Home.razor`'s operation methods out into services, which is most
  of a 5,800-line file and where the regression risk in this codebase lives.

The stage-1 interface is chosen so this is a change of executor, not a change of model: components
already enqueue a described unit of work and observe state through the coordinator. Migration is
per-operation — each one moves from "delegate closing over component state" to "delegate resolved
from a scope" — so it can be done a few operations at a time, with both kinds of item in the queue
during the transition.

---

## 4. Decisions

| Decision | Choice | Why |
|---|---|---|
| Scope of the single-flight guarantee | Application-wide | The working trees on disk are shared. A per-tab guarantee would still let two tabs fight over one clone. |
| Queue unit | One entry per action, with progress | Matches what the user clicked, keeps the panel readable, and gives one cancellable unit per entry. Per-repository detail belongs in the console. |
| Stop semantics | Immediate, with revert; atomic per repository | A repository is never left half-fixed with a dirty tree. Pushed work is done work and stays. |
| Click while busy | Enqueue, folding duplicates | Lining up the next job is the point of a queue; folding stops an impatient double-click costing a second pass over the estate. |
| Queue placement | Left sidebar, under the tree | Stays put while navigating, and does not compete with console output for the bottom panel. |
| Executor | The enqueuing circuit (stage 1) | Delivers the guarantees without lifting ~5,800 lines of operations out of `Home.razor` first. |

## 5. Non-goals

- **Parallelism.** One item at a time is the requirement, not a limitation to be tuned away later.
  `RegressionGuardService` keeps its own bounded-concurrency build queue; it verifies pushed work
  and does not touch working trees the queue is using.
- **Persisting the queue across restarts.** A queue that survives a process restart would resume
  work nobody is watching. Restart starts empty.
- **Priorities or reordering.** Enqueue order is run order. If that becomes painful, drag-to-reorder
  is a small addition to a list that already exists.
