# Work Queue Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** One analysis at a time across the whole application, with a visible queue of running and pending work, and a stop that reverts anything half-applied.

**Architecture:** A new singleton `WorkQueueService` owns the pending list, the single-flight lock and the running item's `CancellationTokenSource`, and raises a `Changed` event. It does not execute work: each item carries a delegate supplied by the component that enqueued it, and that circuit runs it when the item reaches the head. Home renders the queue in the left sidebar under the navigation tree. Cancellation is atomic per repository — a run stopped before its commit-and-push discards the working-tree changes.

**Tech Stack:** .NET 10, Blazor Server, xUnit v3, AwesomeAssertions, PanoramicData.Blazor.

**Spec:** [`DESIGN.md`](../../../DESIGN.md) — sections 2 (stage 1) and 4 (decisions).

## Global Constraints

- `TreatWarningsAsErrors` is on: the build must be clean, zero warnings.
- All public members carry XML documentation comments.
- File-scoped namespaces; tabs for indentation; CRLF line endings.
- xUnit v3 with AwesomeAssertions (`result.Should().Be(...)`), never `Assert.*` fluent mixing.
- One entry per user action, not per repository (DESIGN.md §2.2).
- Duplicate folding applies to **pending** items only, never the running one (DESIGN.md §2.2).
- No parallelism: exactly one item runs at a time, application-wide (DESIGN.md §5).

---

### Task 1: `WorkQueueService` — the coordinator

**Files:**
- Create: `PanoramicData.NugetManagement.Web/Models/WorkItem.cs`
- Create: `PanoramicData.NugetManagement.Web/Services/WorkQueueService.cs`
- Modify: `PanoramicData.NugetManagement.Test/PanoramicData.NugetManagement.Test.csproj` (add a `ProjectReference` to the Web project so the service is testable)
- Test: `PanoramicData.NugetManagement.Test/WorkQueueServiceTests.cs`

**Interfaces:**
- Consumes: nothing.
- Produces:
  - `enum WorkItemState { Pending, Running, Cancelling, Completed, Failed, Cancelled }`
  - `sealed class WorkItem` with `string Id`, `string Title`, `string? Organization`, `string DedupKey`, `object OwnerId`, `Func<IProgress<string>, CancellationToken, Task> Run`, `WorkItemState State`, `string? Progress`, `string? Error`
  - `WorkQueueService.Enqueue(string title, string? organization, string dedupKey, object ownerId, Func<IProgress<string>, CancellationToken, Task> run) → WorkItem?` (null when folded into an existing pending item)
  - `WorkQueueService.Items → IReadOnlyList<WorkItem>` (running first, then pending in order)
  - `WorkQueueService.Running → WorkItem?`
  - `WorkQueueService.TryDequeueForExecution(object ownerId, out WorkItem item) → bool`
  - `WorkQueueService.CompleteAsync(WorkItem item, Exception? error)`
  - `WorkQueueService.Cancel(string id)`, `WorkQueueService.Remove(string id)`, `WorkQueueService.RemoveOwnedBy(object ownerId)`
  - `WorkQueueService.ReportProgress(WorkItem item, string progress)`
  - `event Action? Changed`

- [ ] **Step 1: Write the failing tests**

```csharp
public class WorkQueueServiceTests
{
	private static WorkQueueService CreateService() => new();

	[Fact]
	public void Enqueue_ShouldQueueSecondItemBehindTheFirst()
	{
		var queue = CreateService();
		var owner = new object();

		queue.Enqueue("First", "org", "first", owner, (_, _) => Task.CompletedTask);
		queue.Enqueue("Second", "org", "second", owner, (_, _) => Task.CompletedTask);

		queue.TryDequeueForExecution(owner, out var running).Should().BeTrue();
		running.Title.Should().Be("First");
		queue.TryDequeueForExecution(owner, out _).Should().BeFalse("only one item runs at a time");
		queue.Items.Should().HaveCount(2);
	}

	[Fact]
	public void Enqueue_ShouldFoldADuplicateOfAPendingItem()
	{
		var queue = CreateService();
		var owner = new object();

		queue.Enqueue("Re-assess org", "org", "reassess:org", owner, (_, _) => Task.CompletedTask).Should().NotBeNull();
		queue.Enqueue("Re-assess org", "org", "reassess:org", owner, (_, _) => Task.CompletedTask).Should().BeNull();

		queue.Items.Should().HaveCount(1);
	}

	[Fact]
	public void Enqueue_ShouldNotFoldADuplicateOfTheRunningItem()
	{
		var queue = CreateService();
		var owner = new object();
		queue.Enqueue("Re-assess org", "org", "reassess:org", owner, (_, _) => Task.CompletedTask);
		queue.TryDequeueForExecution(owner, out _);

		queue.Enqueue("Re-assess org", "org", "reassess:org", owner, (_, _) => Task.CompletedTask)
			.Should().NotBeNull("the running pass may already be stale");

		queue.Items.Should().HaveCount(2);
	}

	[Fact]
	public async Task CompleteAsync_ShouldLetTheNextItemRun()
	{
		var queue = CreateService();
		var owner = new object();
		queue.Enqueue("First", null, "first", owner, (_, _) => Task.CompletedTask);
		queue.Enqueue("Second", null, "second", owner, (_, _) => Task.CompletedTask);
		queue.TryDequeueForExecution(owner, out var first);

		await queue.CompleteAsync(first, null);

		queue.TryDequeueForExecution(owner, out var second).Should().BeTrue();
		second.Title.Should().Be("Second");
		queue.Items.Should().ContainSingle();
	}

	[Fact]
	public void Cancel_ShouldSignalTheRunningItemsToken()
	{
		var queue = CreateService();
		var owner = new object();
		queue.Enqueue("Long run", null, "long", owner, (_, _) => Task.CompletedTask);
		queue.TryDequeueForExecution(owner, out var running);

		queue.Cancel(running.Id);

		queue.Token(running.Id)!.Value.IsCancellationRequested.Should().BeTrue();
		running.State.Should().Be(WorkItemState.Cancelling);
	}

	[Fact]
	public void Remove_ShouldDropAPendingItemButNotTheRunningOne()
	{
		var queue = CreateService();
		var owner = new object();
		queue.Enqueue("First", null, "first", owner, (_, _) => Task.CompletedTask);
		var pending = queue.Enqueue("Second", null, "second", owner, (_, _) => Task.CompletedTask);
		queue.TryDequeueForExecution(owner, out var running);

		queue.Remove(pending!.Id);
		queue.Remove(running.Id);

		queue.Items.Should().ContainSingle().Which.Id.Should().Be(running.Id);
	}

	[Fact]
	public void RemoveOwnedBy_ShouldDropPendingWorkAndCancelRunningWork()
	{
		var queue = CreateService();
		var leaving = new object();
		var staying = new object();
		queue.Enqueue("Leaving runs", null, "a", leaving, (_, _) => Task.CompletedTask);
		queue.Enqueue("Leaving pending", null, "b", leaving, (_, _) => Task.CompletedTask);
		queue.Enqueue("Staying pending", null, "c", staying, (_, _) => Task.CompletedTask);
		queue.TryDequeueForExecution(leaving, out var running);

		queue.RemoveOwnedBy(leaving);

		running.State.Should().Be(WorkItemState.Cancelling);
		queue.Items.Should().HaveCount(2, "the running item stays until its revert finishes");
		queue.Items.Should().NotContain(i => i.Title == "Leaving pending");
	}

	[Fact]
	public void TryDequeueForExecution_ShouldOnlyOfferItemsOwnedByTheCaller()
	{
		var queue = CreateService();
		var owner = new object();
		var other = new object();
		queue.Enqueue("Theirs", null, "theirs", other, (_, _) => Task.CompletedTask);

		queue.TryDequeueForExecution(owner, out _).Should().BeFalse();
		queue.TryDequeueForExecution(other, out var item).Should().BeTrue();
		item.Title.Should().Be("Theirs");
	}

	[Fact]
	public void Changed_ShouldFireOnEnqueueAndCompletion()
	{
		var queue = CreateService();
		var owner = new object();
		var fired = 0;
		queue.Changed += () => fired++;

		queue.Enqueue("One", null, "one", owner, (_, _) => Task.CompletedTask);

		fired.Should().BeGreaterThan(0);
	}
}
```

- [ ] **Step 2: Run the tests and watch them fail**

Run: `dotnet build && PanoramicData.NugetManagement.Test/bin/Debug/net10.0/PanoramicData.NugetManagement.Test.exe -method "*WorkQueue*"`
Expected: compile failure — `WorkQueueService` does not exist.

- [ ] **Step 3: Add the project reference**

```xml
<ProjectReference Include="..\PanoramicData.NugetManagement.Web\PanoramicData.NugetManagement.Web.csproj" />
```

- [ ] **Step 4: Write `WorkItem`**

```csharp
/// <summary>Where a queued unit of work has got to.</summary>
public enum WorkItemState { Pending, Running, Cancelling, Completed, Failed, Cancelled }
```

`WorkItem` is a class, not a record: `State` and `Progress` are mutated in place while the UI holds
a reference to it.

- [ ] **Step 5: Write `WorkQueueService`**

Single `Lock` around a `List<WorkItem>`. `Enqueue` folds when a *pending* item shares the dedup key.
`TryDequeueForExecution` returns false unless nothing is running and the head item belongs to the
caller — the head is never skipped, so a disconnected owner's item does not let a later one jump the
queue (`RemoveOwnedBy` clears those). `CompleteAsync` disposes the CTS, sets the final state, and
raises `Changed`.

- [ ] **Step 6: Run the tests and watch them pass**

Run: `dotnet build && PanoramicData.NugetManagement.Test/bin/Debug/net10.0/PanoramicData.NugetManagement.Test.exe -method "*WorkQueue*"`
Expected: PASS, and the whole suite still at its pre-existing 4 failures.

- [ ] **Step 7: Commit**

```bash
git add PanoramicData.NugetManagement.Web/Models/WorkItem.cs PanoramicData.NugetManagement.Web/Services/WorkQueueService.cs PanoramicData.NugetManagement.Test/
git commit -m "Add the work queue coordinator"
```

---

### Task 2: Register the service and run the queue from Home

**Files:**
- Modify: `PanoramicData.NugetManagement.Web/Program.cs` (register `WorkQueueService` as a singleton)
- Modify: `PanoramicData.NugetManagement.Web/Components/Pages/Home.razor` (inject; pump; subscribe to `Changed`; unsubscribe and `RemoveOwnedBy` in `Dispose`)

**Interfaces:**
- Consumes: `WorkQueueService` from Task 1.
- Produces: `Home.EnqueueWork(string title, string? organization, string dedupKey, Func<IProgress<string>, CancellationToken, Task> run)` and `Home.PumpQueueAsync()` — the loop that drains items this circuit owns.

- [ ] **Step 1: Register the singleton**

```csharp
builder.Services.AddSingleton<WorkQueueService>();
```

- [ ] **Step 2: Add the pump to Home**

```csharp
private async Task PumpQueueAsync()
{
	while (WorkQueue.TryDequeueForExecution(this, out var item))
	{
		Exception? error = null;
		try
		{
			var progress = new Progress<string>(line => WorkQueue.ReportProgress(item, line));
			await item.Run(progress, WorkQueue.Token(item.Id) ?? CancellationToken.None);
		}
		catch (Exception ex)
		{
			error = ex;
			AppendConsole($"⛔ {item.Title}: {ex.Message}");
		}

		await WorkQueue.CompleteAsync(item, error);
		await InvokeAsync(StateHasChanged);
	}
}
```

`EnqueueWork` calls `Enqueue` then `_ = PumpQueueAsync()` — the pump is a no-op when another item is
already running, which is what makes the queue single-flight.

- [ ] **Step 3: Subscribe and clean up**

`OnInitialized`: `WorkQueue.Changed += OnWorkQueueChanged;`
`Dispose`: `WorkQueue.Changed -= OnWorkQueueChanged; WorkQueue.RemoveOwnedBy(this);`
`OnWorkQueueChanged` is `() => _ = InvokeAsync(StateHasChanged);`

- [ ] **Step 4: Build**

Run: `dotnet build -c Debug --nologo`
Expected: 0 errors, 0 new warnings.

- [ ] **Step 5: Commit**

```bash
git commit -am "Run queued work from the dashboard circuit"
```

---

### Task 3: The sidebar queue panel

**Files:**
- Modify: `PanoramicData.NugetManagement.Web/Components/Pages/Home.razor` (markup between `.nav-tree-scroll` and `.nav-footer`)
- Modify: `PanoramicData.NugetManagement.Web/wwwroot/app.css`

**Interfaces:**
- Consumes: `WorkQueueService.Items`, `.Cancel(id)`, `.Remove(id)` from Task 1.
- Produces: nothing other tasks depend on.

- [ ] **Step 1: Render the panel**

Hidden entirely when the queue is empty, so the tree keeps its full height:

```razor
@if (WorkQueue.Items.Count > 0)
{
	<div class="nav-queue">
		<div class="nav-queue-head">
			<i class="fas fa-list-check"></i> Work queue
			<span class="nav-queue-count">@WorkQueue.Items.Count</span>
		</div>
		@foreach (var item in WorkQueue.Items)
		{
			<div class="nav-queue-item @(item.State == WorkItemState.Running ? "running" : null)">
				<i class="fas @QueueItemIcon(item)"></i>
				<span class="nav-queue-title" title="@item.Title">@item.Title</span>
				@if (item.Progress is { Length: > 0 } progress)
				{
					<span class="nav-queue-progress">@progress</span>
				}
				@if (item.State == WorkItemState.Running)
				{
					<button class="nav-queue-btn" title="Stop, reverting anything half-applied" @onclick="() => WorkQueue.Cancel(item.Id)"><i class="fas fa-stop"></i></button>
				}
				else if (item.State == WorkItemState.Pending)
				{
					<button class="nav-queue-btn" title="Remove from the queue" @onclick="() => WorkQueue.Remove(item.Id)"><i class="fas fa-xmark"></i></button>
				}
			</div>
		}
	</div>
}
```

- [ ] **Step 2: Style it** — borrow the existing `--bs-border-color` / `--bs-tertiary-bg` variables used by `.nav-footer`, cap the list with `max-height: 30vh; overflow-y: auto`, and ellipsise long titles.

- [ ] **Step 3: Build and eyeball it**

Run: `dotnet build -c Debug --nologo`, then start the app and enqueue two runs.
Expected: running entry first with a stop button, pending entries below with ✕.

- [ ] **Step 4: Commit**

```bash
git commit -am "Show the work queue under the navigation tree"
```

---

### Task 4: Atomic revert on cancellation

**Files:**
- Modify: `PanoramicData.NugetManagement.Web/Services/DashboardService.cs:1243-1333` (`ApplyAcrossReposAsync`)
- Test: `PanoramicData.NugetManagement.Test/BulkApplyCancellationTests.cs`

**Interfaces:**
- Consumes: `LocalRepoService.DiscardLocalChangesAsync` (exists).
- Produces: `RepoApplyStatus.Reverted` added to the existing enum.

- [ ] **Step 1: Write the failing test** — cancel between the apply and the commit, and assert the
  clone is discarded and the repository reported as reverted, not pushed.

- [ ] **Step 2: Run it and watch it fail.**

- [ ] **Step 3: Wrap each repository's apply-to-push span**

```csharp
catch (OperationCanceledException)
{
	// Atomic per repository: a change that never reached its commit is undone, so the clone is left
	// exactly as it was found rather than half-remediated.
	await RevertUncommittedAsync(row, onOutput).ConfigureAwait(false);
	outcome.Results.Add(new RepoApplyResult
	{
		RepositoryFullName = name,
		Status = RepoApplyStatus.Reverted,
		Message = "Stopped before commit; local changes were reverted."
	});
	throw;
}
```

`RevertUncommittedAsync` calls `DiscardLocalChangesAsync` with `CancellationToken.None` — the revert
must not itself be cancelled — and reports each discarded path to the console.

- [ ] **Step 4: Run the test and watch it pass.**

- [ ] **Step 5: Commit**

```bash
git commit -am "Revert a repository stopped before its commit"
```

---

### Task 5: Route IssuesView's bulk runs through the queue

**Files:**
- Modify: `PanoramicData.NugetManagement.Web/Components/IssuesView.razor` (`RunConfirmedAsync`, `_busy`)
- Modify: `PanoramicData.NugetManagement.Web/Components/Pages/Home.razor` (pass an enqueue callback down)

**Interfaces:**
- Consumes: `Home.EnqueueWork` from Task 2.
- Produces: `IssuesView.OnEnqueue` — `EventCallback<QueuedWorkRequest>` carrying title, dedup key and the delegate.

- [ ] **Step 1: Replace the direct run.** `RunConfirmedAsync` stops awaiting the work and instead
  enqueues it; `CancellationToken.None` is replaced by the token the queue supplies.

- [ ] **Step 2:** Drop `_busy` from the bulk-action buttons: enqueuing is always allowed, and
  duplicate folding — not a disabled button — is what stops a double-click costing two passes.

- [ ] **Step 3: Build, then confirm a bulk apply appears in the sidebar and can be stopped.**

- [ ] **Step 4: Commit**

```bash
git commit -am "Queue the issue view's bulk runs"
```

---

### Task 6: Route Home's own operations through the queue

**Files:**
- Modify: `PanoramicData.NugetManagement.Web/Components/Pages/Home.razor`

**Interfaces:**
- Consumes: `Home.EnqueueWork` from Task 2.
- Produces: nothing.

- [ ] **Step 1: Convert the org-level operations** — `RefreshAsync`, `RediscoverOrganizationAsync`,
  `ReassessOrganizationAsync`, `ReassessAllAsync`, `CloneSelectedRepositoriesAsync` — each becoming
  an `EnqueueWork` call whose delegate is the existing body, taking the queue's token.

- [ ] **Step 2: Convert the per-repository steps** — `GitSyncAsync`, `FixAllAsync`,
  `FixCategoryAsync`, `FixSingleRuleAsync`, `BuildAsync`, `RunTestsAsync`, `CommitAndPushAsync`,
  `RunPublishAsync`.

- [ ] **Step 3: Delete what the queue replaces** — `_isRunning`, `_isAssessing`, `_isLoading`,
  `_isCloningAll`, `IsBulkOperationInFlight`, `BlockIfBulkOperationInFlightAsync`, and the dead
  `BeginCancelableOperation` / `CancelCurrentOperationAsync` / `EndCancelableOperation` /
  `RestoreStatusAfterCancellation` / `_operationCts` scaffolding. Buttons that consulted those flags
  become unconditionally enabled.

- [ ] **Step 4: Build and exercise every converted button.**

- [ ] **Step 5: Commit**

```bash
git commit -am "Queue the dashboard's own operations"
```

---

## Self-review notes

- **Spec coverage:** §2.1 coordinator → Task 1; §2.2 item shape and folding → Task 1; §2.3
  interaction model → Tasks 3, 5, 6; §2.4 cancellation and atomicity → Tasks 1 (token) and 4
  (revert); §2.5 circuit ownership → Task 1 (`RemoveOwnedBy`) and Task 2 (`Dispose`); §2.6 testing →
  Tasks 1 and 4.
- **Deliberately deferred:** the queue does not yet drive `RegressionGuardService`, which keeps its
  own bounded-concurrency build queue by design (DESIGN.md §5).
