# Per-repository work queues — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Replace the single application-wide work queue with one queue per repository, so work on one repository never blocks work on another, moving the queue UI into the navigation tree and taking execution off the Blazor circuit so it survives both the tab and the process.

**Architecture:** A `WorkLaneService` holds FIFO lanes keyed by repository (`repo:owner/name`) or organisation (`org:name`); one item runs per lane, at most `MaxConcurrentLanes` lanes run at once. A hosted `WorkRunnerService` pumps the lanes, executing named `WorkDescriptor`s through a `WorkExecutors` service holding the work bodies lifted out of `Home.razor`. Because the work is named rather than a closure, pending work is written to disk and restored at startup.

**Tech Stack:** .NET 10, Blazor Server (interactive server render mode), PanoramicData.Blazor (`PDTree`, `PDSplitter`), xunit v3, AwesomeAssertions (`.Should()`), NSubstitute.

**Spec:** [`docs/superpowers/specs/2026-08-30-per-repo-work-queues-design.md`](../specs/2026-08-30-per-repo-work-queues-design.md)

## Global Constraints

- **Build before trusting any test run.** `dotnet build PanoramicData.NugetManagement.Test/PanoramicData.NugetManagement.Test.csproj` must print `Build succeeded` first.
- **Never use `dotnet test`** in this repo — it reports `Zero tests ran` and exits 5 even on a healthy suite. Run the binary directly:
  `./PanoramicData.NugetManagement.Test/bin/Debug/net10.0/PanoramicData.NugetManagement.Test.exe`
  Filter with `--filter-class "*ClassName"` or `--filter-method "*MethodName*"`. The bare `-class` / `-method` flags print help and run nothing.
- **Baseline is 555 tests, 0 failures.** Any task that ends with fewer passing than it started with has broken something.
- **Tabs, not spaces**, for indentation. File-scoped namespaces. Nullable enabled. Warnings are errors.
- **XML doc comments on every public type and member** — this codebase documents *why*, not *what*. Match the surrounding prose style.
- **British spelling in user-facing text and comments** ("organisation", "serialise"), matching the existing code.
- **Concurrency tests must not use `Task.Delay` to sequence anything.** Use `TaskCompletionSource` gates, or they will be flaky.
- `MaxConcurrentLanes` default is **20**.
- Persistence file: `%LOCALAPPDATA%/PanoramicData.NugetManagement/work-queue.json`.

---

## File Structure

**Created:**

| File | Responsibility |
|---|---|
| `Web/Models/WorkKind.cs` | The closed enum of nameable work. |
| `Web/Models/WorkDescriptor.cs` | Serialisable "what to run": kind, scope, parameters. |
| `Web/Models/WorkLane.cs` | One lane: its key, its items, whether it is running. |
| `Web/Services/WorkLaneService.cs` | Lane ownership, dedup, cancellation, the concurrency cap. |
| `Web/Services/WorkQueueStore.cs` | Reading and writing pending work to disk. |
| `Web/Services/WorkExecutors.cs` | The work bodies, lifted out of `Home.razor`. |
| `Web/Services/WorkRunnerService.cs` | The hosted service that pumps lanes. |
| `Web/Services/WorkFanOut.cs` | Decomposing org-scoped work into per-repository descriptors. |
| `Test/WorkLaneServiceTests.cs`, `WorkDescriptorTests.cs`, `WorkPersistenceTests.cs`, `WorkFanOutTests.cs`, `NavTreeWorkNodeTests.cs` | Tests per the spec. |

**Modified:**

| File | Change |
|---|---|
| `Web/Models/WorkItem.cs` | `Run`/`OwnerId` out, `Descriptor`/`ConsoleNodeKey`/`GeneratedPrompt`/`WasInterrupted` in. |
| `Web/Models/NavItem.cs` | `WorkItemId`, `WorkItemState`, `WorkItemProgress`, `LaneKey`. |
| `Web/Services/NavTreeDataProvider.cs` | Work nodes under repository and organisation nodes. |
| `Web/Services/WorkflowGate.cs` | Input becomes a lane's items. |
| `Web/Components/Pages/Home.razor` | Queue pane removed, work bodies removed, per-repo gating, tree buttons. |
| `Web/Components/IssuesView.razor` | `OnEnqueue` carries a descriptor list, not a closure. |
| `Web/Program.cs` | DI for the new services. |
| `Web/Services/RuntimeSettingsService.cs` | `MaxConcurrentLanes` setting. |

**Deleted:** `Web/Services/WorkQueueService.cs`, `Web/Models/QueuedWork.cs`.

**Task order rationale:** models → lane service → persistence → executors → runner → fan-out → tree → component. Each layer is testable before the one above it exists, and the `Home.razor` surgery (Task 8, the riskiest) happens last, against services that are already proven.

---

### Task 1: Work descriptors

The closed catalogue of nameable work. Everything else keys off this, so it comes first.

**Files:**
- Create: `PanoramicData.NugetManagement.Web/Models/WorkKind.cs`
- Create: `PanoramicData.NugetManagement.Web/Models/WorkDescriptor.cs`
- Test: `PanoramicData.NugetManagement.Test/WorkDescriptorTests.cs`

**Interfaces:**
- Consumes: nothing.
- Produces: `WorkKind` (enum), `WorkDescriptor` record with `Kind`, `Organization`, `RepositoryFullName`, `Parameters`, plus `LaneKey`, `Parameter(string)`, and static factories `ForRepository(...)` / `ForOrganization(...)`.

- [ ] **Step 1: Write the failing test**

`PanoramicData.NugetManagement.Test/WorkDescriptorTests.cs`:

```csharp
using System.Text.Json;
using PanoramicData.NugetManagement.Web.Models;

namespace PanoramicData.NugetManagement.Test;

/// <summary>
/// Tests for the serialisable description of a unit of work. The catalogue is closed so that
/// queued work can be written to disk and picked up again after a restart; these tests are what
/// stop a kind being added that cannot survive that round trip.
/// </summary>
public class WorkDescriptorTests(ITestOutputHelper output) : TestWithOutput(output)
{
	[Fact]
	public void LaneKey_RepositoryScoped_IsTheRepositoryLane()
		=> WorkDescriptor.ForRepository(WorkKind.Build, "panoramicdata", "panoramicdata/Athonet.Api")
			.LaneKey.Should().Be("repo:panoramicdata/Athonet.Api");

	[Fact]
	public void LaneKey_OrganizationScoped_IsTheOrganizationLane()
		=> WorkDescriptor.ForOrganization(WorkKind.RediscoverOrganization, "panoramicdata")
			.LaneKey.Should().Be("org:panoramicdata");

	[Fact]
	public void LaneKey_RepositoryCasing_IsNormalised()
		=> WorkDescriptor.ForRepository(WorkKind.Build, "panoramicdata", "PanoramicData/Athonet.Api")
			.LaneKey.Should().Be("repo:panoramicdata/athonet.api");

	[Fact]
	public void Parameter_Absent_IsNull()
		=> WorkDescriptor.ForRepository(WorkKind.Build, "org", "org/repo")
			.Parameter("ruleId").Should().BeNull();

	[Fact]
	public void Parameter_Present_IsTheValue()
		=> WorkDescriptor.ForRepository(WorkKind.FixRule, "org", "org/repo", ("ruleId", "TST-06"))
			.Parameter("ruleId").Should().Be("TST-06");

	[Theory]
	[MemberData(nameof(AllKinds))]
	public void RoundTrip_EveryKind_SurvivesJson(WorkKind kind)
	{
		var original = new WorkDescriptor(kind, "panoramicdata", "panoramicdata/Athonet.Api",
			new Dictionary<string, string> { ["ruleId"] = "TST-06", ["category"] = "NuGetHygiene" });

		var restored = JsonSerializer.Deserialize<WorkDescriptor>(JsonSerializer.Serialize(original));

		restored.Should().NotBeNull();
		restored!.Kind.Should().Be(kind);
		restored.Organization.Should().Be("panoramicdata");
		restored.RepositoryFullName.Should().Be("panoramicdata/Athonet.Api");
		restored.Parameter("ruleId").Should().Be("TST-06");
	}

	public static TheoryData<WorkKind> AllKinds()
	{
		var data = new TheoryData<WorkKind>();
		foreach (var kind in Enum.GetValues<WorkKind>())
		{
			data.Add(kind);
		}

		return data;
	}
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet build PanoramicData.NugetManagement.Test/PanoramicData.NugetManagement.Test.csproj`
Expected: FAIL to compile — `WorkKind` and `WorkDescriptor` do not exist.

- [ ] **Step 3: Write the implementation**

`PanoramicData.NugetManagement.Web/Models/WorkKind.cs`:

```csharp
namespace PanoramicData.NugetManagement.Web.Models;

/// <summary>
/// The closed catalogue of work the application can queue.
/// </summary>
/// <remarks>
/// Closed deliberately. A queue of arbitrary delegates cannot be written to disk and picked up
/// again after a restart; a queue of named work can. Adding a member here without adding it to
/// <see cref="Services.WorkExecutors"/> fails a test rather than failing at run time.
/// </remarks>
public enum WorkKind
{
	/// <summary>Clone one repository locally.</summary>
	Clone,

	/// <summary>Re-assess one repository against every rule.</summary>
	Reassess,

	/// <summary>Apply every available auto-remediation to one repository.</summary>
	FixAll,

	/// <summary>Apply the auto-remediations of one assessment category. Parameter: <c>category</c>.</summary>
	FixCategory,

	/// <summary>Apply the auto-remediation of one rule. Parameter: <c>ruleId</c>.</summary>
	FixRule,

	/// <summary>Build one repository.</summary>
	Build,

	/// <summary>Run one repository's tests.</summary>
	Test,

	/// <summary>Pull and push one repository.</summary>
	GitSync,

	/// <summary>Commit and push one repository's working tree.</summary>
	CommitAndPush,

	/// <summary>Publish one repository's packages.</summary>
	Publish,

	/// <summary>
	/// Read one organisation's package list from NuGet, then fan out re-assessment across the
	/// repositories it names. Organisation-scoped: there is no one repository it belongs to.
	/// </summary>
	RediscoverOrganization,

	/// <summary>Work out which repositories an organisation-wide re-assessment covers, then fan out.</summary>
	DiscoverReassessTargets,

	/// <summary>Work out which repositories are available to clone, then fan out.</summary>
	DiscoverCloneTargets,

	/// <summary>Rediscover and re-assess every organisation.</summary>
	RefreshAll
}
```

`PanoramicData.NugetManagement.Web/Models/WorkDescriptor.cs`:

```csharp
namespace PanoramicData.NugetManagement.Web.Models;

/// <summary>
/// What a queued item will do, in a form that can be written to disk and read back.
/// </summary>
/// <param name="Kind">Which work this is.</param>
/// <param name="Organization">The organisation it belongs to, or null when it spans all of them.</param>
/// <param name="RepositoryFullName">The repository it acts on as "owner/name", or null for organisation-scoped work.</param>
/// <param name="Parameters">The few extras a kind needs, such as <c>ruleId</c> or <c>category</c>.</param>
public sealed record WorkDescriptor(
	WorkKind Kind,
	string? Organization,
	string? RepositoryFullName,
	IReadOnlyDictionary<string, string> Parameters)
{
	/// <summary>
	/// The lane this work runs on: its repository's, or its organisation's when it acts on no single
	/// repository. Lower-cased, because a repository named two ways is still one working tree and
	/// must not end up with two lanes running against it at once.
	/// </summary>
	public string LaneKey => RepositoryFullName is { Length: > 0 } repository
		? $"repo:{repository.ToLowerInvariant()}"
		: $"org:{(Organization ?? "*").ToLowerInvariant()}";

	/// <summary>The named parameter's value, or null when this kind does not carry it.</summary>
	/// <param name="name">The parameter name, e.g. <c>ruleId</c>.</param>
	public string? Parameter(string name)
		=> Parameters.TryGetValue(name, out var value) ? value : null;

	/// <summary>Describes work acting on one repository.</summary>
	public static WorkDescriptor ForRepository(
		WorkKind kind,
		string? organization,
		string repositoryFullName,
		params (string Name, string Value)[] parameters)
		=> new(kind, organization, repositoryFullName, ToDictionary(parameters));

	/// <summary>Describes work acting on an organisation rather than any one repository.</summary>
	public static WorkDescriptor ForOrganization(
		WorkKind kind,
		string? organization,
		params (string Name, string Value)[] parameters)
		=> new(kind, organization, null, ToDictionary(parameters));

	private static Dictionary<string, string> ToDictionary((string Name, string Value)[] parameters)
		=> parameters.ToDictionary(p => p.Name, p => p.Value, StringComparer.Ordinal);
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet build PanoramicData.NugetManagement.Test/PanoramicData.NugetManagement.Test.csproj` then
`./PanoramicData.NugetManagement.Test/bin/Debug/net10.0/PanoramicData.NugetManagement.Test.exe --filter-class "*WorkDescriptorTests"`
Expected: PASS — 19 tests (5 + 14 kinds).

- [ ] **Step 5: Commit**

```bash
git add PanoramicData.NugetManagement.Web/Models/WorkKind.cs PanoramicData.NugetManagement.Web/Models/WorkDescriptor.cs PanoramicData.NugetManagement.Test/WorkDescriptorTests.cs
git commit -m "Name the work so it can outlive the process

A queue of closures cannot be written down. Give every queueable
operation a WorkKind and a serialisable descriptor, and derive the lane
it belongs to from its scope."
```

---

### Task 2: WorkItem and WorkLane

Reshape the item now that work is named, and give lanes a type of their own.

**Files:**
- Modify: `PanoramicData.NugetManagement.Web/Models/WorkItem.cs`
- Create: `PanoramicData.NugetManagement.Web/Models/WorkLane.cs`
- Test: covered by Task 3's tests; this task is compiled against existing callers.

**Interfaces:**
- Consumes: `WorkDescriptor`, `WorkKind` (Task 1).
- Produces: `WorkItem` with `Id`, `Title`, `Descriptor`, `DedupKey`, `Step`, `ConsoleNodeKey`, `State`, `Progress`, `GeneratedPrompt`, `WasInterrupted`, and pass-through `Organization` / `RepositoryFullName` / `LaneKey`. `WorkLane` with `Key`, `Items`, `IsRunning`.

- [ ] **Step 1: Rewrite `WorkItem`**

Replace the body of `PanoramicData.NugetManagement.Web/Models/WorkItem.cs`, keeping the existing `WorkItemState` enum unchanged except for the `Running` comment:

```csharp
using PanoramicData.NugetManagement.Web.Services;

namespace PanoramicData.NugetManagement.Web.Models;

/// <summary>
/// Where a queued unit of work has got to.
/// </summary>
public enum WorkItemState
{
	/// <summary>Waiting for its turn. Nothing has happened yet, so it can be removed freely.</summary>
	Pending,

	/// <summary>Executing. Exactly one item per lane is in this state at a time.</summary>
	Running,

	/// <summary>Stop has been asked for; the item is unwinding and reverting anything half-applied.</summary>
	Cancelling,

	/// <summary>Finished without error.</summary>
	Completed,

	/// <summary>Finished by throwing.</summary>
	Failed,

	/// <summary>Stopped before it finished. Anything it had half-applied has been reverted.</summary>
	Cancelled
}

/// <summary>
/// One unit of queued work: a single action the user asked for, acting on one repository or on one
/// organisation.
/// </summary>
/// <remarks>
/// A class rather than a record because <see cref="State"/>, <see cref="Progress"/> and
/// <see cref="GeneratedPrompt"/> change while the UI holds a reference to the item.
/// <para>
/// The item no longer carries a delegate or an owning component. Work is named
/// (<see cref="Descriptor"/>) and executed by <see cref="WorkRunnerService"/>, so it belongs to the
/// application rather than to the browser tab that asked for it.
/// </para>
/// </remarks>
public sealed class WorkItem
{
	/// <summary>Identifies the item within the queue.</summary>
	public required string Id { get; init; }

	/// <summary>What the user sees in the tree, e.g. "Fix panoramicdata/Athonet.Api".</summary>
	public required string Title { get; init; }

	/// <summary>What this item will do.</summary>
	public required WorkDescriptor Descriptor { get; init; }

	/// <summary>
	/// Identifies work that would repeat what is already queued in this lane. A second enqueue with
	/// the same key is folded into the pending item rather than queued again.
	/// </summary>
	public required string DedupKey { get; init; }

	/// <summary>
	/// The workflow step this work performs, or null for work that is not a step on the toolbar.
	/// Queueing a step closes it and everything downstream — see <see cref="WorkflowGate"/>.
	/// </summary>
	public WorkflowStep? Step { get; init; }

	/// <summary>
	/// The console this item's output belongs to, recorded when it was queued rather than read when it
	/// runs: the lane may not reach it for minutes, by which time the selection has moved and the
	/// output would land in an unrelated console.
	/// </summary>
	public string? ConsoleNodeKey { get; init; }

	/// <summary>The lane this item runs on.</summary>
	public string LaneKey => Descriptor.LaneKey;

	/// <summary>The organisation this work is scoped to, or null when it spans every organisation.</summary>
	public string? Organization => Descriptor.Organization;

	/// <summary>
	/// The repository this work acts on, or null for organisation-scoped work. What the toolbar gates
	/// against: work on one repository never closes another repository's buttons.
	/// </summary>
	public string? RepositoryFullName => Descriptor.RepositoryFullName;

	/// <summary>Where the item has got to.</summary>
	public WorkItemState State { get; set; } = WorkItemState.Pending;

	/// <summary>Progress within the item, e.g. "repo 8 of 47". Null until the work reports some.</summary>
	public string? Progress { get; set; }

	/// <summary>
	/// The AI prompt the work produced for issues it could not fix, or null when it produced none.
	/// </summary>
	/// <remarks>
	/// Held rather than pushed. The work used to write this straight to the browser clipboard and open
	/// an IDE, which cannot be done from a runner with no browser attached — and twenty lanes finishing
	/// together would have raced twenty of each. The user claims it from the prompt UI instead.
	/// </remarks>
	public string? GeneratedPrompt { get; set; }

	/// <summary>
	/// Whether this item was running when the process last stopped. Such an item is restored as
	/// pending, and its working tree is cleaned before it is run again.
	/// </summary>
	public bool WasInterrupted { get; init; }
}
```

- [ ] **Step 2: Create `WorkLane`**

`PanoramicData.NugetManagement.Web/Models/WorkLane.cs`:

```csharp
namespace PanoramicData.NugetManagement.Web.Models;

/// <summary>
/// One queue of work: everything outstanding for a single repository, or for a single organisation.
/// </summary>
/// <remarks>
/// The lane is the unit of serialisation. One item runs per lane at a time — the invariant that
/// stops two tabs, or a fix and a build, driving the same working tree at once — while different
/// lanes run concurrently, because different repositories share nothing.
/// </remarks>
public sealed class WorkLane
{
	/// <summary>The lane's key, as built by <see cref="WorkDescriptor.LaneKey"/>.</summary>
	public required string Key { get; init; }

	/// <summary>The repository this lane belongs to, or null for an organisation lane.</summary>
	public string? RepositoryFullName { get; init; }

	/// <summary>The organisation this lane belongs to.</summary>
	public string? Organization { get; init; }

	/// <summary>Outstanding work, running item first. Finished items are removed.</summary>
	public List<WorkItem> Items { get; } = [];

	/// <summary>Whether the scheduler has promoted this lane and an item is executing on it.</summary>
	public bool IsRunning { get; set; }
}
```

- [ ] **Step 3: Verify it compiles as far as the old callers**

Run: `dotnet build PanoramicData.NugetManagement.Web/PanoramicData.NugetManagement.Web.csproj -t:Compile`
Expected: FAIL, with errors only in `WorkQueueService.cs`, `Home.razor` and `WorkflowGateTests.cs` — the callers Tasks 3–8 replace. Confirm no *other* file is named. If one is, it is a consumer this plan has not accounted for: stop and report it.

- [ ] **Step 4: Commit**

```bash
git add PanoramicData.NugetManagement.Web/Models/WorkItem.cs PanoramicData.NugetManagement.Web/Models/WorkLane.cs
git commit -m "Reshape WorkItem around named work and lanes

The item carried a delegate and the component that would run it. Both
go: work is now a descriptor, and the lane it belongs to follows from
its scope rather than from who asked for it."
```

*(The build is expected to be red between here and Task 8. That is the cost of replacing the queue in one change, which is what "big bang" chose.)*

---

### Task 3: WorkLaneService

The heart of the feature: lanes, dedup, cancellation, and the concurrency cap.

**Files:**
- Create: `PanoramicData.NugetManagement.Web/Services/WorkLaneService.cs`
- Delete: `PanoramicData.NugetManagement.Web/Services/WorkQueueService.cs`
- Delete: `PanoramicData.NugetManagement.Web/Models/QueuedWork.cs`
- Test: `PanoramicData.NugetManagement.Test/WorkLaneServiceTests.cs`

**Interfaces:**
- Consumes: `WorkItem`, `WorkLane`, `WorkDescriptor` (Tasks 1–2).
- Produces: `WorkLaneService` with `Changed` event; `Enqueue(title, descriptor, dedupKey, step, consoleNodeKey) → WorkItem?`; `Lanes`; `ItemsFor(laneKey)`; `TryStartNext(out WorkItem)`; `Token(id)`; `ReportProgress`; `Complete(item, error)`; `Cancel(id)`; `Remove(id)`; `CancelLane(laneKey)`; `CancelUnder(organization)`; `MaxConcurrentLanes` (settable); `RunningLaneCount`.

- [ ] **Step 1: Write the failing tests**

`PanoramicData.NugetManagement.Test/WorkLaneServiceTests.cs`:

```csharp
using PanoramicData.NugetManagement.Web.Models;
using PanoramicData.NugetManagement.Web.Services;

namespace PanoramicData.NugetManagement.Test;

/// <summary>
/// Tests for the lane queue. What matters here is the pair of invariants the design turns on: one
/// item at a time within a lane, and many lanes at a time across the estate, bounded by a cap.
/// </summary>
public class WorkLaneServiceTests(ITestOutputHelper output) : TestWithOutput(output)
{
	private const string RepoA = "panoramicdata/Athonet.Api";
	private const string RepoB = "panoramicdata/Auvik.Api";

	private static WorkLaneService NewService(int maxConcurrentLanes = 20)
		=> new() { MaxConcurrentLanes = maxConcurrentLanes };

	private static WorkItem Enqueue(WorkLaneService service, string repository, WorkKind kind = WorkKind.Build)
		=> service.Enqueue(
			$"{kind} {repository}",
			WorkDescriptor.ForRepository(kind, "panoramicdata", repository),
			$"{kind}:{repository}",
			step: null,
			consoleNodeKey: null)!;

	[Fact]
	public void Enqueue_TwoRepositories_MakesTwoLanes()
	{
		var service = NewService();

		Enqueue(service, RepoA);
		Enqueue(service, RepoB);

		service.Lanes.Should().HaveCount(2);
	}

	[Fact]
	public void TryStartNext_TwoLanes_StartsBoth()
	{
		var service = NewService();
		Enqueue(service, RepoA);
		Enqueue(service, RepoB);

		service.TryStartNext(out var first).Should().BeTrue();
		service.TryStartNext(out var second).Should().BeTrue();

		first.RepositoryFullName.Should().NotBe(second.RepositoryFullName);
		service.RunningLaneCount.Should().Be(2);
	}

	[Fact]
	public void TryStartNext_SameLaneTwice_StartsOnlyTheFirst()
	{
		var service = NewService();
		Enqueue(service, RepoA, WorkKind.Build);
		Enqueue(service, RepoA, WorkKind.Test);

		service.TryStartNext(out var first).Should().BeTrue();
		service.TryStartNext(out _).Should().BeFalse();

		first.Descriptor.Kind.Should().Be(WorkKind.Build);
	}

	[Fact]
	public void TryStartNext_LaneFinishes_NextItemInThatLaneStarts()
	{
		var service = NewService();
		Enqueue(service, RepoA, WorkKind.Build);
		Enqueue(service, RepoA, WorkKind.Test);
		service.TryStartNext(out var first);

		service.Complete(first, error: null);

		service.TryStartNext(out var second).Should().BeTrue();
		second.Descriptor.Kind.Should().Be(WorkKind.Test);
	}

	[Fact]
	public void TryStartNext_AtTheCap_StartsNoFurtherLane()
	{
		var service = NewService(maxConcurrentLanes: 1);
		Enqueue(service, RepoA);
		Enqueue(service, RepoB);

		service.TryStartNext(out _).Should().BeTrue();
		service.TryStartNext(out _).Should().BeFalse();
		service.RunningLaneCount.Should().Be(1);
	}

	[Fact]
	public void TryStartNext_CapRaised_PromotesTheWaitingLane()
	{
		var service = NewService(maxConcurrentLanes: 1);
		Enqueue(service, RepoA);
		Enqueue(service, RepoB);
		service.TryStartNext(out _);

		service.MaxConcurrentLanes = 2;

		service.TryStartNext(out _).Should().BeTrue();
	}

	[Fact]
	public void Enqueue_IdenticalPendingItemInSameLane_IsFoldedIn()
	{
		var service = NewService();
		Enqueue(service, RepoA);

		var second = service.Enqueue(
			"Build again",
			WorkDescriptor.ForRepository(WorkKind.Build, "panoramicdata", RepoA),
			$"{WorkKind.Build}:{RepoA}",
			step: null,
			consoleNodeKey: null);

		second.Should().BeNull();
		service.ItemsFor($"repo:{RepoA.ToLowerInvariant()}").Should().HaveCount(1);
	}

	[Fact]
	public void Enqueue_SameDedupKeyInAnotherLane_IsNotFoldedIn()
	{
		var service = NewService();
		service.Enqueue("Build A", WorkDescriptor.ForRepository(WorkKind.Build, "o", RepoA), "build", null, null);

		var second = service.Enqueue("Build B", WorkDescriptor.ForRepository(WorkKind.Build, "o", RepoB), "build", null, null);

		second.Should().NotBeNull();
	}

	[Fact]
	public void Enqueue_MatchingTheRunningItem_IsQueuedRatherThanFolded()
	{
		var service = NewService();
		Enqueue(service, RepoA);
		service.TryStartNext(out _);

		var second = service.Enqueue(
			"Build again",
			WorkDescriptor.ForRepository(WorkKind.Build, "panoramicdata", RepoA),
			$"{WorkKind.Build}:{RepoA}",
			step: null,
			consoleNodeKey: null);

		second.Should().NotBeNull("the running item may already be returning a stale picture");
	}

	[Fact]
	public void Cancel_PendingItem_RemovesIt()
	{
		var service = NewService();
		Enqueue(service, RepoA, WorkKind.Build);
		var pending = Enqueue(service, RepoA, WorkKind.Test);

		service.Cancel(pending.Id);

		service.ItemsFor(pending.LaneKey).Should().ContainSingle();
	}

	[Fact]
	public void Cancel_RunningItem_SignalsItRatherThanDroppingIt()
	{
		var service = NewService();
		var item = Enqueue(service, RepoA);
		service.TryStartNext(out _);

		service.Cancel(item.Id);

		item.State.Should().Be(WorkItemState.Cancelling);
		service.Token(item.Id)!.Value.IsCancellationRequested.Should().BeTrue();
	}

	[Fact]
	public void CancelUnder_Organization_StopsEveryLaneBeneathIt()
	{
		var service = NewService();
		Enqueue(service, RepoA);
		Enqueue(service, RepoB);
		service.Enqueue("Other org", WorkDescriptor.ForRepository(WorkKind.Build, "other", "other/Thing"), "k", null, null);

		service.CancelUnder("panoramicdata");

		service.ItemsFor($"repo:{RepoA.ToLowerInvariant()}").Should().BeEmpty();
		service.ItemsFor($"repo:{RepoB.ToLowerInvariant()}").Should().BeEmpty();
		service.ItemsFor("repo:other/thing").Should().ContainSingle();
	}

	[Fact]
	public void Complete_LastItemInLane_RemovesTheLane()
	{
		var service = NewService();
		var item = Enqueue(service, RepoA);
		service.TryStartNext(out _);

		service.Complete(item, error: null);

		service.Lanes.Should().BeEmpty("an empty lane is not a lane, and would show as an empty node");
	}

	[Fact]
	public void Changed_OnEnqueue_IsRaised()
	{
		var service = NewService();
		var raised = 0;
		service.Changed += () => raised++;

		Enqueue(service, RepoA);

		raised.Should().Be(1);
	}
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet build PanoramicData.NugetManagement.Test/PanoramicData.NugetManagement.Test.csproj`
Expected: FAIL to compile — `WorkLaneService` does not exist.

- [ ] **Step 3: Write the implementation**

Delete `Web/Services/WorkQueueService.cs` and `Web/Models/QueuedWork.cs`, then create
`PanoramicData.NugetManagement.Web/Services/WorkLaneService.cs`:

```csharp
using System.Globalization;
using PanoramicData.NugetManagement.Web.Models;

namespace PanoramicData.NugetManagement.Web.Services;

/// <summary>
/// The application's work queues: one lane per repository, one per organisation, running
/// concurrently up to a cap.
/// </summary>
/// <remarks>
/// This replaces a single application-wide queue. That queue serialised the whole estate in order to
/// protect one working tree at a time, which meant fixing one repository blocked building another
/// that shared nothing with it. The invariant is kept but narrowed: one item at a time
/// <em>within a lane</em>, many lanes at once across the estate.
/// <para>
/// The service coordinates but does not execute. <see cref="WorkRunnerService"/> pumps it.
/// </para>
/// </remarks>
public sealed class WorkLaneService
{
	private readonly Lock _lock = new();
	private readonly Dictionary<string, WorkLane> _lanes = new(StringComparer.Ordinal);
	private readonly Dictionary<string, CancellationTokenSource> _tokenSources = new(StringComparer.Ordinal);
	private int _nextId;
	private int _maxConcurrentLanes = 20;

	/// <summary>
	/// Raised whenever any lane changes: an item is added, starts, reports progress, or finishes.
	/// </summary>
	/// <remarks>
	/// Raised from whatever thread the change happened on, and — with twenty lanes reporting progress
	/// — often. Subscribers rendering from it must debounce; see the navigation tree.
	/// </remarks>
	public event Action? Changed;

	/// <summary>
	/// How many lanes may execute at once. Lowering it does not stop lanes already running; it takes
	/// effect as they drain.
	/// </summary>
	public int MaxConcurrentLanes
	{
		get { lock (_lock) { return _maxConcurrentLanes; } }
		set { lock (_lock) { _maxConcurrentLanes = Math.Max(1, value); } }
	}

	/// <summary>Every lane with outstanding work.</summary>
	public IReadOnlyList<WorkLane> Lanes
	{
		get { lock (_lock) { return [.. _lanes.Values]; } }
	}

	/// <summary>How many lanes are executing.</summary>
	public int RunningLaneCount
	{
		get { lock (_lock) { return _lanes.Values.Count(l => l.IsRunning); } }
	}

	/// <summary>The outstanding work in one lane, running item first.</summary>
	/// <param name="laneKey">The lane, as built by <see cref="WorkDescriptor.LaneKey"/>.</param>
	public IReadOnlyList<WorkItem> ItemsFor(string laneKey)
	{
		lock (_lock)
		{
			return _lanes.TryGetValue(laneKey, out var lane) ? [.. lane.Items] : [];
		}
	}

	/// <summary>
	/// Adds work to its lane, returning the queued item — or null when an identical item is already
	/// waiting in that lane and this request was folded into it.
	/// </summary>
	/// <param name="title">What the user sees in the tree.</param>
	/// <param name="descriptor">What the work will do.</param>
	/// <param name="dedupKey">Identifies work that would repeat what is already pending in this lane.</param>
	/// <param name="step">The workflow step this work performs, or null when it is not one.</param>
	/// <param name="consoleNodeKey">The console its output belongs to.</param>
	/// <param name="wasInterrupted">Whether this item is being restored after the process stopped mid-run.</param>
	public WorkItem? Enqueue(
		string title,
		WorkDescriptor descriptor,
		string dedupKey,
		WorkflowStep? step,
		string? consoleNodeKey,
		bool wasInterrupted = false)
	{
		WorkItem item;

		lock (_lock)
		{
			var lane = GetOrAddLane(descriptor);

			// Folded against pending items only, and only within this lane: the running item may
			// already be returning a stale picture, so asking again earns a fresh pass rather than
			// being swallowed. Across lanes a shared key means two repositories, not one repeat.
			if (lane.Items.Any(i => i.State == WorkItemState.Pending
				&& string.Equals(i.DedupKey, dedupKey, StringComparison.Ordinal)))
			{
				return null;
			}

			item = new WorkItem
			{
				Id = (++_nextId).ToString(CultureInfo.InvariantCulture),
				Title = title,
				Descriptor = descriptor,
				DedupKey = dedupKey,
				Step = step,
				ConsoleNodeKey = consoleNodeKey,
				WasInterrupted = wasInterrupted
			};

			lane.Items.Add(item);
		}

		Changed?.Invoke();
		return item;
	}

	/// <summary>
	/// Claims the next item that may start: the head of a lane that is idle, where starting it would
	/// not exceed <see cref="MaxConcurrentLanes"/>. Returns false when nothing may start.
	/// </summary>
	/// <param name="item">The claimed item, when this returns true.</param>
	public bool TryStartNext(out WorkItem item)
	{
		lock (_lock)
		{
			item = null!;

			if (_lanes.Values.Count(l => l.IsRunning) >= _maxConcurrentLanes)
			{
				return false;
			}

			// Insertion-ordered: Dictionary preserves it here because lanes are only removed when empty,
			// and a lane that empties has no claim on its old position.
			var lane = _lanes.Values.FirstOrDefault(l =>
				!l.IsRunning && l.Items.Any(i => i.State == WorkItemState.Pending));

			if (lane is null)
			{
				return false;
			}

			var head = lane.Items.Find(i => i.State == WorkItemState.Pending)!;
			head.State = WorkItemState.Running;
			lane.IsRunning = true;
			_tokenSources[head.Id] = new CancellationTokenSource();
			item = head;
		}

		Changed?.Invoke();
		return true;
	}

	/// <summary>The token for a running item, or null when it is not running.</summary>
	/// <param name="id">The item's identifier.</param>
	public CancellationToken? Token(string id)
	{
		lock (_lock)
		{
			return _tokenSources.TryGetValue(id, out var source) ? source.Token : null;
		}
	}

	/// <summary>Records progress within an item, e.g. "repo 8 of 47".</summary>
	/// <param name="item">The running item.</param>
	/// <param name="progress">What to show.</param>
	public void ReportProgress(WorkItem item, string progress)
	{
		item.Progress = progress;
		Changed?.Invoke();
	}

	/// <summary>
	/// Marks an item finished and frees its lane.
	/// </summary>
	/// <param name="item">The item that has stopped executing.</param>
	/// <param name="error">The exception it failed with, or null if it did not.</param>
	public void Complete(WorkItem item, Exception? error)
	{
		lock (_lock)
		{
			item.State = item.State == WorkItemState.Cancelling || error is OperationCanceledException
				? WorkItemState.Cancelled
				: error is null ? WorkItemState.Completed : WorkItemState.Failed;

			if (_tokenSources.Remove(item.Id, out var source))
			{
				source.Dispose();
			}

			if (_lanes.TryGetValue(item.LaneKey, out var lane))
			{
				lane.Items.Remove(item);
				lane.IsRunning = false;

				// An empty lane is not a lane. Kept, it would render as a work node with nothing under it.
				if (lane.Items.Count == 0)
				{
					_lanes.Remove(item.LaneKey);
				}
			}
		}

		Changed?.Invoke();
	}

	/// <summary>
	/// Stops an item: a pending one is removed, and a running one is signalled to unwind, reverting
	/// anything it has half-applied.
	/// </summary>
	/// <param name="id">The item's identifier.</param>
	public void Cancel(string id)
	{
		lock (_lock)
		{
			CancelLocked(id);
		}

		Changed?.Invoke();
	}

	/// <summary>Removes a pending item. A running item is left to <see cref="Cancel"/>, which unwinds it.</summary>
	/// <param name="id">The item's identifier.</param>
	public void Remove(string id)
	{
		lock (_lock)
		{
			foreach (var lane in _lanes.Values.ToList())
			{
				var pending = lane.Items.Find(i => i.Id == id && i.State == WorkItemState.Pending);
				if (pending is null)
				{
					continue;
				}

				pending.State = WorkItemState.Cancelled;
				lane.Items.Remove(pending);
				RemoveLaneIfEmptyLocked(lane);
				break;
			}
		}

		Changed?.Invoke();
	}

	/// <summary>Stops everything in one lane.</summary>
	/// <param name="laneKey">The lane to clear.</param>
	public void CancelLane(string laneKey)
	{
		lock (_lock)
		{
			if (_lanes.TryGetValue(laneKey, out var lane))
			{
				foreach (var item in lane.Items.ToList())
				{
					CancelLocked(item.Id);
				}
			}
		}

		Changed?.Invoke();
	}

	/// <summary>
	/// Stops everything in every lane belonging to an organisation — its own lane and its
	/// repositories'. What the organisation node's "stop all" offers, now that a bulk action is many
	/// items rather than one.
	/// </summary>
	/// <param name="organization">The organisation to clear.</param>
	public void CancelUnder(string organization)
	{
		lock (_lock)
		{
			foreach (var lane in _lanes.Values
				.Where(l => string.Equals(l.Organization, organization, StringComparison.OrdinalIgnoreCase))
				.ToList())
			{
				foreach (var item in lane.Items.ToList())
				{
					CancelLocked(item.Id);
				}
			}
		}

		Changed?.Invoke();
	}

	private void CancelLocked(string id)
	{
		foreach (var lane in _lanes.Values.ToList())
		{
			var item = lane.Items.Find(i => i.Id == id);
			if (item is null)
			{
				continue;
			}

			if (item.State == WorkItemState.Running)
			{
				item.State = WorkItemState.Cancelling;
				if (_tokenSources.TryGetValue(id, out var source))
				{
					source.Cancel();
				}
			}
			else if (item.State == WorkItemState.Pending)
			{
				item.State = WorkItemState.Cancelled;
				lane.Items.Remove(item);
				RemoveLaneIfEmptyLocked(lane);
			}

			return;
		}
	}

	private void RemoveLaneIfEmptyLocked(WorkLane lane)
	{
		if (lane.Items.Count == 0 && !lane.IsRunning)
		{
			_lanes.Remove(lane.Key);
		}
	}

	private WorkLane GetOrAddLane(WorkDescriptor descriptor)
	{
		var key = descriptor.LaneKey;
		if (_lanes.TryGetValue(key, out var lane))
		{
			return lane;
		}

		lane = new WorkLane
		{
			Key = key,
			Organization = descriptor.Organization,
			RepositoryFullName = descriptor.RepositoryFullName
		};

		_lanes[key] = lane;
		return lane;
	}
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet build PanoramicData.NugetManagement.Test/PanoramicData.NugetManagement.Test.csproj` — expect errors still in `Home.razor` and `WorkflowGateTests.cs`. To test this task in isolation before Task 8 lands, temporarily exclude `Home.razor` is *not* an option; instead run the full build after Task 8. **For this task, verify by inspection that the only remaining compile errors name `Home.razor`, `IssuesView.razor` and `WorkflowGateTests.cs`**, then proceed. Task 8 is where the suite goes green again.

- [ ] **Step 5: Commit**

```bash
git add -A PanoramicData.NugetManagement.Web/Services PanoramicData.NugetManagement.Web/Models PanoramicData.NugetManagement.Test/WorkLaneServiceTests.cs
git commit -m "Split the global queue into per-repository lanes

One item at a time within a lane, many lanes at once across the estate,
bounded by a configurable cap. Fixing one repository no longer blocks
building another that shares nothing with it."
```

---

### Task 4: WorkQueueStore — persistence

**Files:**
- Create: `PanoramicData.NugetManagement.Web/Services/WorkQueueStore.cs`
- Modify: `PanoramicData.NugetManagement.Web/Services/WorkLaneService.cs` (add `Snapshot()` / `Restore(...)`)
- Test: `PanoramicData.NugetManagement.Test/WorkPersistenceTests.cs`

**Interfaces:**
- Consumes: `WorkLaneService`, `WorkItem`, `WorkDescriptor`.
- Produces: `PersistedWorkItem` record (`Title`, `Descriptor`, `DedupKey`, `Step`, `ConsoleNodeKey`, `WasRunning`); `WorkQueueStore` with `Save(IReadOnlyList<PersistedWorkItem>)`, `Load() → IReadOnlyList<PersistedWorkItem>`, `static DefaultPath()`; `WorkLaneService.Snapshot()` and `WorkLaneService.Restore(items)`.

- [ ] **Step 1: Write the failing tests**

`PanoramicData.NugetManagement.Test/WorkPersistenceTests.cs`:

```csharp
using PanoramicData.NugetManagement.Web.Models;
using PanoramicData.NugetManagement.Web.Services;

namespace PanoramicData.NugetManagement.Test;

/// <summary>
/// Tests that queued work survives a restart, and that work interrupted mid-run comes back marked
/// so its half-applied changes can be cleaned up before it runs again.
/// </summary>
public class WorkPersistenceTests(ITestOutputHelper output) : TestWithOutput(output), IDisposable
{
	private readonly string _path = Path.Combine(
		Path.GetTempPath(),
		$"work-queue-test-{Guid.NewGuid():N}.json");

	private const string Repo = "panoramicdata/Athonet.Api";

	public void Dispose()
	{
		if (File.Exists(_path))
		{
			File.Delete(_path);
		}

		GC.SuppressFinalize(this);
	}

	private WorkLaneService ServiceWithBuildAndTestQueued()
	{
		var service = new WorkLaneService();
		service.Enqueue("Build", WorkDescriptor.ForRepository(WorkKind.Build, "panoramicdata", Repo), "build", WorkflowStep.Build, "node");
		service.Enqueue("Test", WorkDescriptor.ForRepository(WorkKind.Test, "panoramicdata", Repo), "test", WorkflowStep.Test, "node");
		return service;
	}

	[Fact]
	public void Load_NothingSaved_IsEmpty()
		=> new WorkQueueStore(_path, NullLogger<WorkQueueStore>.Instance).Load().Should().BeEmpty();

	[Fact]
	public void SaveThenLoad_PendingItems_ComeBack()
	{
		var store = new WorkQueueStore(_path, NullLogger<WorkQueueStore>.Instance);
		store.Save(ServiceWithBuildAndTestQueued().Snapshot());

		var loaded = store.Load();

		loaded.Should().HaveCount(2);
		loaded[0].Descriptor.Kind.Should().Be(WorkKind.Build);
		loaded[0].Step.Should().Be(WorkflowStep.Build);
		loaded[0].ConsoleNodeKey.Should().Be("node");
		loaded[1].Descriptor.Kind.Should().Be(WorkKind.Test);
	}

	[Fact]
	public void Snapshot_RunningItem_IsRecordedAsHavingBeenRunning()
	{
		var service = ServiceWithBuildAndTestQueued();
		service.TryStartNext(out _);

		var snapshot = service.Snapshot();

		snapshot.Should().HaveCount(2);
		snapshot[0].WasRunning.Should().BeTrue();
		snapshot[1].WasRunning.Should().BeFalse();
	}

	[Fact]
	public void Restore_ItemThatWasRunning_ComesBackPendingAndInterrupted()
	{
		var store = new WorkQueueStore(_path, NullLogger<WorkQueueStore>.Instance);
		var service = ServiceWithBuildAndTestQueued();
		service.TryStartNext(out _);
		store.Save(service.Snapshot());

		var restored = new WorkLaneService();
		restored.Restore(store.Load());

		var items = restored.ItemsFor($"repo:{Repo.ToLowerInvariant()}");
		items.Should().HaveCount(2);
		items[0].State.Should().Be(WorkItemState.Pending, "nothing resumes mid-run");
		items[0].WasInterrupted.Should().BeTrue();
		items[1].WasInterrupted.Should().BeFalse();
	}

	[Fact]
	public void Restore_RebuildsTheLanes()
	{
		var store = new WorkQueueStore(_path, NullLogger<WorkQueueStore>.Instance);
		var service = new WorkLaneService();
		service.Enqueue("A", WorkDescriptor.ForRepository(WorkKind.Build, "o", "o/A"), "a", null, null);
		service.Enqueue("B", WorkDescriptor.ForRepository(WorkKind.Build, "o", "o/B"), "b", null, null);
		store.Save(service.Snapshot());

		var restored = new WorkLaneService();
		restored.Restore(store.Load());

		restored.Lanes.Should().HaveCount(2);
	}

	[Fact]
	public void Load_CorruptFile_IsEmptyRatherThanThrowing()
	{
		File.WriteAllText(_path, "{ this is not json");

		new WorkQueueStore(_path, NullLogger<WorkQueueStore>.Instance).Load().Should().BeEmpty(
			"a queue file that cannot be read must not stop the application starting");
	}
}
```

Add `using Microsoft.Extensions.Logging.Abstractions;` if `NullLogger<T>` is not already in the test project's global usings — check `PanoramicData.NugetManagement.Test/GlobalUsings.cs` first and follow whatever the neighbouring test files do.

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet build PanoramicData.NugetManagement.Test/PanoramicData.NugetManagement.Test.csproj`
Expected: FAIL to compile — `WorkQueueStore`, `Snapshot`, `Restore` do not exist.

- [ ] **Step 3: Add `Snapshot` and `Restore` to `WorkLaneService`**

```csharp
	/// <summary>
	/// Everything outstanding, in a form that can be written to disk. A running item is recorded as
	/// having been running so that it can be cleaned up rather than resumed.
	/// </summary>
	public IReadOnlyList<PersistedWorkItem> Snapshot()
	{
		lock (_lock)
		{
			return
			[
				.. _lanes.Values
					.SelectMany(lane => lane.Items)
					.Where(i => i.State is WorkItemState.Pending or WorkItemState.Running or WorkItemState.Cancelling)
					.Select(i => new PersistedWorkItem(
						i.Title,
						i.Descriptor,
						i.DedupKey,
						i.Step,
						i.ConsoleNodeKey,
						i.State is WorkItemState.Running or WorkItemState.Cancelling))
			];
		}
	}

	/// <summary>
	/// Puts saved work back into its lanes at startup. Nothing is resumed mid-run: an item that was
	/// executing comes back pending and flagged, so the runner cleans its working tree first.
	/// </summary>
	/// <param name="items">What was saved.</param>
	public void Restore(IReadOnlyList<PersistedWorkItem> items)
	{
		foreach (var saved in items)
		{
			Enqueue(
				saved.Title,
				saved.Descriptor,
				saved.DedupKey,
				saved.Step,
				saved.ConsoleNodeKey,
				wasInterrupted: saved.WasRunning);
		}
	}
```

- [ ] **Step 4: Write `WorkQueueStore`**

`PanoramicData.NugetManagement.Web/Services/WorkQueueStore.cs`:

```csharp
using System.Text.Json;
using System.Text.Json.Serialization;
using PanoramicData.NugetManagement.Web.Models;

namespace PanoramicData.NugetManagement.Web.Services;

/// <summary>One outstanding item as it is written to disk.</summary>
/// <param name="Title">What the user saw in the tree.</param>
/// <param name="Descriptor">What the work will do.</param>
/// <param name="DedupKey">Identifies a repeat of this work.</param>
/// <param name="Step">The workflow step it performs, or null.</param>
/// <param name="ConsoleNodeKey">The console its output belongs to.</param>
/// <param name="WasRunning">Whether it was executing when the process stopped.</param>
public sealed record PersistedWorkItem(
	string Title,
	WorkDescriptor Descriptor,
	string DedupKey,
	WorkflowStep? Step,
	string? ConsoleNodeKey,
	bool WasRunning);

/// <summary>
/// Reads and writes the outstanding work queue, so that closing the application does not throw away
/// what the user asked for.
/// </summary>
/// <remarks>
/// Beside the runtime settings, and for the same reason: it is state about this machine's session
/// rather than about any repository, and it must not end up committed to one.
/// </remarks>
public sealed class WorkQueueStore(string path, ILogger<WorkQueueStore> logger)
{
	private readonly Lock _lock = new();

	private static readonly JsonSerializerOptions _options = new()
	{
		WriteIndented = true,
		Converters = { new JsonStringEnumConverter() }
	};

	/// <summary>The queue file's location under the user's local application data.</summary>
	public static string DefaultPath() => Path.Combine(
		Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
		"PanoramicData.NugetManagement",
		"work-queue.json");

	/// <summary>Writes the outstanding work, replacing whatever was there.</summary>
	/// <param name="items">What is outstanding.</param>
	public void Save(IReadOnlyList<PersistedWorkItem> items)
	{
		lock (_lock)
		{
			try
			{
				var directory = Path.GetDirectoryName(path)!;
				Directory.CreateDirectory(directory);
				File.WriteAllText(path, JsonSerializer.Serialize(items, _options));
			}
			catch (Exception ex)
			{
				// Losing the queue file costs the user their pending work on the next restart. Failing
				// the operation that triggered the save would cost them the work they are doing now.
				logger.LogWarning(ex, "Failed to save the work queue to {Path}", path);
			}
		}
	}

	/// <summary>Reads the outstanding work saved by a previous run, or nothing when there is none.</summary>
	public IReadOnlyList<PersistedWorkItem> Load()
	{
		lock (_lock)
		{
			try
			{
				if (!File.Exists(path))
				{
					return [];
				}

				return JsonSerializer.Deserialize<List<PersistedWorkItem>>(File.ReadAllText(path), _options) ?? [];
			}
			catch (Exception ex)
			{
				// A queue file that cannot be read must not stop the application starting. The cost of
				// ignoring it is the pending work; the cost of throwing is the application.
				logger.LogWarning(ex, "Failed to load the work queue from {Path}; starting with an empty queue", path);
				return [];
			}
		}
	}
}
```

- [ ] **Step 5: Run tests to verify they pass**

Run: `./PanoramicData.NugetManagement.Test/bin/Debug/net10.0/PanoramicData.NugetManagement.Test.exe --filter-class "*WorkPersistenceTests"`
Expected: PASS — 6 tests. (The test project will only build once `Home.razor` compiles; if it does not yet, verify by inspection and re-run this filter at the end of Task 8.)

- [ ] **Step 6: Commit**

```bash
git add PanoramicData.NugetManagement.Web/Services/WorkQueueStore.cs PanoramicData.NugetManagement.Web/Services/WorkLaneService.cs PanoramicData.NugetManagement.Test/WorkPersistenceTests.cs
git commit -m "Persist outstanding work across a restart

Pending items are written beside the runtime settings and restored at
startup. An item that was executing comes back pending and flagged
rather than resumed: a half-applied fix must be cleaned up, not built
on."
```

---

### Task 5: WorkExecutors — lift the work bodies out of Home.razor

The largest and riskiest task. Thirteen `*CoreAsync` methods move from a 6,665-line component into a service, losing their component coupling on the way.

**Files:**
- Create: `PanoramicData.NugetManagement.Web/Services/WorkExecutors.cs`
- Modify: `PanoramicData.NugetManagement.Web/Components/Pages/Home.razor` (remove the moved bodies)
- Test: `PanoramicData.NugetManagement.Test/WorkExecutorsTests.cs`

**Interfaces:**
- Consumes: `WorkDescriptor`, `WorkKind`, `WorkItem`.
- Produces: `WorkExecutors` with `Task ExecuteAsync(WorkItem item, IProgress<string> progress, CancellationToken cancellationToken)` and `static IReadOnlySet<WorkKind> SupportedKinds { get; }`.

**What to strip from each body as it moves.** Every `*CoreAsync` method currently touches the component. Replace each of these, mechanically:

| In the component | In the executor |
|---|---|
| `AppendConsole(line)` | `logger.LogInformation("{Line}", line)` — the `UiConsoleLoggerProvider` already mirrors this category into the console. |
| `ClearConsole()` | Delete. A lane cannot clear a console it does not own, and twenty lanes clearing one console is nonsense. |
| `StateHasChanged()` / `InvokeAsync(StateHasChanged)` | Delete. The runner raises `Changed`; circuits re-render themselves. |
| `_isRunning`, `_isAssessing`, `_progressMessage` | Delete. Item state now lives on `WorkItem.State` and `WorkItem.Progress`; report via `progress.Report(...)`. |
| `PersistSelectedRowUpdateAsync()` | `cache.PersistRowAsync(row)` — check `DashboardCacheService` for the existing method this component method wraps and call it directly. |
| `OpenInIde()` | Delete — see spec §3a. |
| `AutoCopyAiPromptAsync(prompt, step, reason)` | `item.GeneratedPrompt = prompt;` — see spec §3a. |
| `RevertPartAppliedFixAsync(row)` | Keep the behaviour: `await localRepo.DiscardLocalChangesAsync(row.RepositoryFullName, CancellationToken.None)` then `await dashboard.RefreshGitStatusAsync(row, CancellationToken.None)`. |
| `_selectedRow` | Never. The row comes from `cache.GetCachedRows()` matched on `item.RepositoryFullName`. |

- [ ] **Step 1: Write the failing test**

`PanoramicData.NugetManagement.Test/WorkExecutorsTests.cs`:

```csharp
using PanoramicData.NugetManagement.Web.Models;
using PanoramicData.NugetManagement.Web.Services;

namespace PanoramicData.NugetManagement.Test;

/// <summary>
/// Tests that the catalogue of work and the code that runs it cannot drift apart.
/// </summary>
/// <remarks>
/// The bodies themselves are covered by the service tests they call into. What is not covered
/// anywhere else — and what a closed catalogue makes checkable at all — is that every kind has
/// somewhere to go.
/// </remarks>
public class WorkExecutorsTests(ITestOutputHelper output) : TestWithOutput(output)
{
	[Fact]
	public void SupportedKinds_CoversEveryWorkKind()
	{
		var missing = Enum.GetValues<WorkKind>()
			.Where(kind => !WorkExecutors.SupportedKinds.Contains(kind))
			.ToList();

		missing.Should().BeEmpty(
			"a WorkKind with no executor would queue work that can never run");
	}

	[Fact]
	public void SupportedKinds_InventsNothing()
		=> WorkExecutors.SupportedKinds.Should().OnlyContain(kind => Enum.IsDefined(kind));
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet build PanoramicData.NugetManagement.Test/PanoramicData.NugetManagement.Test.csproj`
Expected: FAIL to compile — `WorkExecutors` does not exist.

- [ ] **Step 3: Create the executor shell**

`PanoramicData.NugetManagement.Web/Services/WorkExecutors.cs`. Write the class with a constructor taking `DashboardService dashboard, DashboardCacheService cache, LocalRepoService localRepo, NuGetDiscoveryService discovery, RuntimeSettingsService runtimeSettings, ILogger<WorkExecutors> logger`, then:

```csharp
	/// <summary>Every kind this service knows how to run.</summary>
	/// <remarks>
	/// Exposed so a test can assert it covers <see cref="WorkKind"/> in full. A kind that can be
	/// queued but not run would sit in a lane for ever, blocking everything behind it.
	/// </remarks>
	public static IReadOnlySet<WorkKind> SupportedKinds { get; } = new HashSet<WorkKind>
	{
		WorkKind.Clone, WorkKind.Reassess, WorkKind.FixAll, WorkKind.FixCategory, WorkKind.FixRule,
		WorkKind.Build, WorkKind.Test, WorkKind.GitSync, WorkKind.CommitAndPush, WorkKind.Publish,
		WorkKind.RediscoverOrganization, WorkKind.DiscoverReassessTargets,
		WorkKind.DiscoverCloneTargets, WorkKind.RefreshAll
	};

	/// <summary>Runs one queued item.</summary>
	/// <param name="item">The item to run; its <see cref="WorkItem.Descriptor"/> selects the body.</param>
	/// <param name="progress">Reports progress lines into the item's tree node.</param>
	/// <param name="cancellationToken">Signalled when the user stops the item.</param>
	public Task ExecuteAsync(WorkItem item, IProgress<string> progress, CancellationToken cancellationToken)
		=> item.Descriptor.Kind switch
		{
			WorkKind.Clone => CloneAsync(item, progress, cancellationToken),
			WorkKind.Reassess => ReassessAsync(item, progress, cancellationToken),
			WorkKind.FixAll => FixAllAsync(item, progress, cancellationToken),
			WorkKind.FixCategory => FixCategoryAsync(item, progress, cancellationToken),
			WorkKind.FixRule => FixRuleAsync(item, progress, cancellationToken),
			WorkKind.Build => BuildAsync(item, progress, cancellationToken),
			WorkKind.Test => TestAsync(item, progress, cancellationToken),
			WorkKind.GitSync => GitSyncAsync(item, progress, cancellationToken),
			WorkKind.CommitAndPush => CommitAndPushAsync(item, progress, cancellationToken),
			WorkKind.Publish => PublishAsync(item, progress, cancellationToken),
			WorkKind.RediscoverOrganization => RediscoverOrganizationAsync(item, progress, cancellationToken),
			WorkKind.DiscoverReassessTargets => DiscoverReassessTargetsAsync(item, progress, cancellationToken),
			WorkKind.DiscoverCloneTargets => DiscoverCloneTargetsAsync(item, progress, cancellationToken),
			WorkKind.RefreshAll => RefreshAllAsync(item, progress, cancellationToken),
			_ => throw new NotSupportedException($"No executor for {item.Descriptor.Kind}.")
		};

	/// <summary>
	/// The cached row for an item's repository, or null when the repository is no longer known —
	/// which happens when a restored item names a repository since removed from the estate.
	/// </summary>
	/// <param name="item">The item whose repository is wanted.</param>
	private RepositoryDashboardRow? RowFor(WorkItem item)
		=> cache.GetCachedRows()?.FirstOrDefault(r => string.Equals(
			r.RepositoryFullName, item.RepositoryFullName, StringComparison.OrdinalIgnoreCase));
```

- [ ] **Step 4: Move the bodies, one at a time**

For each of the thirteen methods, in this order — `BuildCoreAsync`, `RunTestsCoreAsync`, `GitSyncCoreAsync`, `CommitAndPushCoreAsync`, `RunPublishCoreAsync`, `ReassessAllCoreAsync`, `FixAllCoreAsync`, `FixCategoryCoreAsync`, `FixSingleRuleCoreAsync`, `CloneSelectedRepositoriesCoreAsync`, `RediscoverOrganizationCoreAsync`, `ReassessOrganizationCoreAsync`, `RefreshCoreAsync` — do this:

1. Cut the method from `Home.razor`.
2. Paste it into `WorkExecutors` and rename it per the switch above (`BuildCoreAsync` → `BuildAsync`).
3. Change the signature to `(WorkItem item, IProgress<string> progress, CancellationToken cancellationToken)`, and open with:
   ```csharp
   var row = RowFor(item);
   if (row is null)
   {
       logger.LogWarning("Skipping {Title}: {Repository} is no longer in the estate.", item.Title, item.RepositoryFullName);
       return;
   }
   ```
   (Organisation-scoped kinds take their organisation from `item.Organization` instead and skip the row lookup.)
4. Apply every substitution in the table above.
5. Extract parameters from the descriptor where the old method took them:
   `FixCategoryAsync` → `Enum.Parse<AssessmentCategory>(item.Descriptor.Parameter("category")!)`;
   `FixRuleAsync` → find the `RuleResult` on `row.Assessment` by `item.Descriptor.Parameter("ruleId")`, and skip with a warning if it is no longer failing.
6. Build the Web project with `-t:Compile` after each method. Do not move the next one until the previous compiles.

The four discovery kinds (`RediscoverOrganization`, `DiscoverReassessTargets`, `DiscoverCloneTargets`, `RefreshAll`) keep only their *discovery* half here; their fan-out half is Task 6. For now, end each with a `TODO`-free explicit call to `fanOut.EnqueueFor(...)`, which Task 6 defines — so write them last, after reading Task 6's interface block.

- [ ] **Step 5: Verify the Web project compiles**

Run: `dotnet build PanoramicData.NugetManagement.Web/PanoramicData.NugetManagement.Web.csproj -t:Compile`
Expected: errors only where `Home.razor` still calls `EnqueueWork` (Task 8) and where `WorkFanOut` is not yet written (Task 6).

- [ ] **Step 6: Commit**

```bash
git add PanoramicData.NugetManagement.Web/Services/WorkExecutors.cs PanoramicData.NugetManagement.Web/Components/Pages/Home.razor PanoramicData.NugetManagement.Test/WorkExecutorsTests.cs
git commit -m "Lift the work bodies out of the page and into a service

Thirteen methods that ran repository work from inside a 6,665-line
component, reaching into its fields and its JS interop. Work that runs
without a browser attached cannot do either, so they move out and lose
their console clears, their render calls, and their clipboard writes."
```

---

### Task 6: WorkFanOut

**Files:**
- Create: `PanoramicData.NugetManagement.Web/Services/WorkFanOut.cs`
- Test: `PanoramicData.NugetManagement.Test/WorkFanOutTests.cs`

**Interfaces:**
- Consumes: `WorkLaneService`, `WorkDescriptor`, `WorkKind`.
- Produces: `WorkFanOut` with
  `int EnqueueReassess(string? organization, IReadOnlyList<RepositoryDashboardRow> rows, string? consoleNodeKey)`,
  `int EnqueueClone(string organization, IReadOnlyList<RepositoryCloneCandidate> targets, string? consoleNodeKey)`,
  `int EnqueueRule(string organization, string ruleId, IReadOnlyList<string> repositoryFullNames, bool push, string? consoleNodeKey)`,
  each returning how many items it queued.

- [ ] **Step 1: Write the failing tests**

`PanoramicData.NugetManagement.Test/WorkFanOutTests.cs`:

```csharp
using PanoramicData.NugetManagement.Web.Models;
using PanoramicData.NugetManagement.Web.Services;

namespace PanoramicData.NugetManagement.Test;

/// <summary>
/// Tests that work spanning many repositories is decomposed into one item per repository, each in
/// its own lane. This is what lets a bulk apply-and-push across twelve repositories run twelve
/// abreast instead of twelve in a row.
/// </summary>
public class WorkFanOutTests(ITestOutputHelper output) : TestWithOutput(output)
{
	private static RepositoryDashboardRow Row(string fullName) => new()
	{
		RepositoryFullName = fullName,
		RepositoryName = fullName.Split('/')[1],
		Organization = fullName.Split('/')[0]
	};

	[Fact]
	public void EnqueueReassess_ThreeRepositories_MakesThreeLanes()
	{
		var lanes = new WorkLaneService();
		var fanOut = new WorkFanOut(lanes);

		var queued = fanOut.EnqueueReassess(
			"panoramicdata",
			[Row("panoramicdata/A"), Row("panoramicdata/B"), Row("panoramicdata/C")],
			consoleNodeKey: null);

		queued.Should().Be(3);
		lanes.Lanes.Should().HaveCount(3);
		lanes.Lanes.Should().OnlyContain(l => l.Items.Count == 1);
	}

	[Fact]
	public void EnqueueReassess_EveryItem_IsRepositoryScopedReassess()
	{
		var lanes = new WorkLaneService();

		new WorkFanOut(lanes).EnqueueReassess("panoramicdata", [Row("panoramicdata/A")], null);

		var item = lanes.ItemsFor("repo:panoramicdata/a").Single();
		item.Descriptor.Kind.Should().Be(WorkKind.Reassess);
		item.Descriptor.RepositoryFullName.Should().Be("panoramicdata/A");
		item.Step.Should().Be(WorkflowStep.Reassess);
	}

	[Fact]
	public void EnqueueRule_WithPush_QueuesFixThenCommitAndPushInEachLane()
	{
		var lanes = new WorkLaneService();

		new WorkFanOut(lanes).EnqueueRule(
			"panoramicdata", "TST-06", ["panoramicdata/A", "panoramicdata/B"], push: true, consoleNodeKey: null);

		foreach (var laneKey in new[] { "repo:panoramicdata/a", "repo:panoramicdata/b" })
		{
			var items = lanes.ItemsFor(laneKey);
			items.Should().HaveCount(2);
			items[0].Descriptor.Kind.Should().Be(WorkKind.FixRule);
			items[0].Descriptor.Parameter("ruleId").Should().Be("TST-06");
			items[1].Descriptor.Kind.Should().Be(WorkKind.CommitAndPush);
		}
	}

	[Fact]
	public void EnqueueRule_WithoutPush_QueuesOnlyTheFix()
	{
		var lanes = new WorkLaneService();

		new WorkFanOut(lanes).EnqueueRule("panoramicdata", "TST-06", ["panoramicdata/A"], push: false, null);

		lanes.ItemsFor("repo:panoramicdata/a").Should().ContainSingle()
			.Which.Descriptor.Kind.Should().Be(WorkKind.FixRule);
	}

	[Fact]
	public void EnqueueReassess_TheSameRepositoryTwice_IsFoldedIntoOne()
	{
		var lanes = new WorkLaneService();

		new WorkFanOut(lanes).EnqueueReassess("panoramicdata", [Row("panoramicdata/A"), Row("panoramicdata/A")], null);

		lanes.ItemsFor("repo:panoramicdata/a").Should().ContainSingle();
	}

	[Fact]
	public void EnqueueReassess_NoRepositories_QueuesNothing()
	{
		var lanes = new WorkLaneService();

		new WorkFanOut(lanes).EnqueueReassess("panoramicdata", [], null).Should().Be(0);
		lanes.Lanes.Should().BeEmpty();
	}
}
```

If `RepositoryDashboardRow` requires more members than the test's `Row` helper sets, read the type and set whatever is `required`; do not add a test-only constructor to production code.

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet build PanoramicData.NugetManagement.Test/PanoramicData.NugetManagement.Test.csproj`
Expected: FAIL to compile — `WorkFanOut` does not exist.

- [ ] **Step 3: Write the implementation**

`PanoramicData.NugetManagement.Web/Services/WorkFanOut.cs`:

```csharp
using PanoramicData.NugetManagement.Web.Models;

namespace PanoramicData.NugetManagement.Web.Services;

/// <summary>
/// Decomposes work that spans repositories into one item per repository, each in its own lane.
/// </summary>
/// <remarks>
/// A bulk action is no longer a single queued item. It cannot be: a single item runs on a single
/// lane, and the whole point of lanes is that twelve repositories are twelve independent pieces of
/// work. What the user loses is one thing to stop; what they gain is twelve running at once, and one
/// repository's failure no longer ending the run for the other eleven. Stopping the lot is the
/// organisation node's "stop all", which is <see cref="WorkLaneService.CancelUnder"/>.
/// </remarks>
public sealed class WorkFanOut(WorkLaneService lanes)
{
	/// <summary>Queues a re-assessment of every given repository. Returns how many were queued.</summary>
	/// <param name="organization">The organisation they belong to.</param>
	/// <param name="rows">The repositories to re-assess.</param>
	/// <param name="consoleNodeKey">The console the output belongs to.</param>
	public int EnqueueReassess(
		string? organization,
		IReadOnlyList<RepositoryDashboardRow> rows,
		string? consoleNodeKey)
		=> rows.Count(row => lanes.Enqueue(
			$"Re-assess {row.RepositoryName}",
			WorkDescriptor.ForRepository(WorkKind.Reassess, organization, row.RepositoryFullName),
			$"reassess:{row.RepositoryFullName}",
			WorkflowStep.Reassess,
			consoleNodeKey) is not null);

	/// <summary>Queues a clone of every given candidate. Returns how many were queued.</summary>
	/// <param name="organization">The organisation they belong to.</param>
	/// <param name="targets">The repositories to clone.</param>
	/// <param name="consoleNodeKey">The console the output belongs to.</param>
	public int EnqueueClone(
		string organization,
		IReadOnlyList<RepositoryCloneCandidate> targets,
		string? consoleNodeKey)
		=> targets.Count(target => lanes.Enqueue(
			$"Clone {target.FullName}",
			WorkDescriptor.ForRepository(WorkKind.Clone, organization, target.FullName),
			$"clone:{target.FullName}",
			step: null,
			consoleNodeKey) is not null);

	/// <summary>
	/// Queues one rule's auto-fix against every affected repository, optionally following each fix
	/// with a commit and push in the same lane. Returns how many repositories were queued.
	/// </summary>
	/// <param name="organization">The organisation the repositories belong to.</param>
	/// <param name="ruleId">The rule to apply.</param>
	/// <param name="repositoryFullNames">The repositories it affects.</param>
	/// <param name="push">Whether to commit and push each repository after fixing it.</param>
	/// <param name="consoleNodeKey">The console the output belongs to.</param>
	public int EnqueueRule(
		string organization,
		string ruleId,
		IReadOnlyList<string> repositoryFullNames,
		bool push,
		string? consoleNodeKey)
	{
		var queued = 0;

		foreach (var repositoryFullName in repositoryFullNames)
		{
			var fix = lanes.Enqueue(
				$"Fix {ruleId} — {repositoryFullName}",
				WorkDescriptor.ForRepository(WorkKind.FixRule, organization, repositoryFullName, ("ruleId", ruleId)),
				$"fix-rule:{repositoryFullName}:{ruleId}",
				step: null,
				consoleNodeKey);

			if (fix is null)
			{
				continue;
			}

			queued++;

			// Queued behind the fix in the same lane, which is what makes "apply and push" atomic per
			// repository without any coordination: the lane is the ordering.
			if (push)
			{
				lanes.Enqueue(
					$"Commit & push {repositoryFullName}",
					WorkDescriptor.ForRepository(WorkKind.CommitAndPush, organization, repositoryFullName),
					$"commit-push:{repositoryFullName}:{ruleId}",
					WorkflowStep.CommitAndPush,
					consoleNodeKey);
			}
		}

		return queued;
	}
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `./PanoramicData.NugetManagement.Test/bin/Debug/net10.0/PanoramicData.NugetManagement.Test.exe --filter-class "*WorkFanOutTests"`
Expected: PASS — 6 tests.

- [ ] **Step 5: Commit**

```bash
git add PanoramicData.NugetManagement.Web/Services/WorkFanOut.cs PanoramicData.NugetManagement.Test/WorkFanOutTests.cs
git commit -m "Decompose estate-wide work into per-repository items

A bulk action across twelve repositories was one queued item and twelve
serial round trips. It becomes twelve items in twelve lanes, running
abreast, where one repository failing no longer ends the run for the
rest."
```

---

### Task 7: WorkRunnerService

**Files:**
- Create: `PanoramicData.NugetManagement.Web/Services/WorkRunnerService.cs`
- Test: exercised through `WorkLaneServiceTests`; no new test file — the runner is thin glue and its parts are covered.

**Interfaces:**
- Consumes: `WorkLaneService`, `WorkExecutors`, `WorkQueueStore`, `LocalRepoService`, `UiConsoleScope`.
- Produces: `WorkRunnerService : BackgroundService` with `event Action<WorkItem>? ItemCompleted`.

- [ ] **Step 1: Write the implementation**

`PanoramicData.NugetManagement.Web/Services/WorkRunnerService.cs`:

```csharp
using PanoramicData.NugetManagement.Web.Models;

namespace PanoramicData.NugetManagement.Web.Services;

/// <summary>
/// Runs the lanes. Owned by the application rather than by any browser tab, so work outlives the
/// tab that asked for it and, through <see cref="WorkQueueStore"/>, the process itself.
/// </summary>
/// <remarks>
/// The queue used to be pumped by the Blazor circuit that enqueued it, which meant closing the tab
/// cancelled the work. That was tolerable when one item ran at a time; with twenty lanes in flight
/// it is not.
/// </remarks>
public sealed class WorkRunnerService(
	WorkLaneService lanes,
	WorkQueueStore store,
	IServiceScopeFactory scopeFactory,
	ILogger<WorkRunnerService> logger) : BackgroundService
{
	private readonly SemaphoreSlim _wake = new(0);

	/// <summary>Raised when an item finishes, so open circuits can refresh what it changed.</summary>
	public event Action<WorkItem>? ItemCompleted;

	/// <inheritdoc />
	public override Task StartAsync(CancellationToken cancellationToken)
	{
		// Restored before the pump starts, so work saved by the last run is in its lanes by the time
		// anything can claim it.
		lanes.Restore(store.Load());
		lanes.Changed += OnLanesChanged;
		return base.StartAsync(cancellationToken);
	}

	/// <inheritdoc />
	public override async Task StopAsync(CancellationToken cancellationToken)
	{
		lanes.Changed -= OnLanesChanged;
		store.Save(lanes.Snapshot());
		await base.StopAsync(cancellationToken);
	}

	/// <inheritdoc />
	protected override async Task ExecuteAsync(CancellationToken stoppingToken)
	{
		while (!stoppingToken.IsCancellationRequested)
		{
			// Claims as many as the cap allows, then sleeps until something changes. Each claimed item
			// runs on its own task: that is the concurrency, and TryStartNext is what bounds it.
			while (lanes.TryStartNext(out var item))
			{
				_ = RunAsync(item);
			}

			try
			{
				await _wake.WaitAsync(stoppingToken);
			}
			catch (OperationCanceledException)
			{
				return;
			}
		}
	}

	private void OnLanesChanged()
	{
		store.Save(lanes.Snapshot());

		// Released rather than set: a change while the pump is mid-claim must not be lost, or a lane
		// would sit ready with nothing to wake it.
		if (_wake.CurrentCount == 0)
		{
			_wake.Release();
		}
	}

	private async Task RunAsync(WorkItem item)
	{
		Exception? error = null;

		// Stamped on this method's asynchronous flow, which the work and everything it logs runs
		// inside. That, not any current selection, is what decides where its output appears — and it
		// is why output still reaches the right console when no tab started it.
		UiConsoleScope.NodeKey = item.ConsoleNodeKey;

		using var scope = scopeFactory.CreateScope();
		var executors = scope.ServiceProvider.GetRequiredService<WorkExecutors>();
		var localRepo = scope.ServiceProvider.GetRequiredService<LocalRepoService>();

		try
		{
			// An item that was executing when the process stopped may have left the clone half-written.
			// Cleaned before it runs again, for the same reason cancellation cleans up: a half-applied
			// fix must not be built on.
			if (item.WasInterrupted && item.RepositoryFullName is { Length: > 0 } repository)
			{
				var (success, discarded) = await localRepo.DiscardLocalChangesAsync(repository, CancellationToken.None);
				if (success && discarded.Count > 0)
				{
					logger.LogInformation(
						"↩️ Reverted {Count} change(s) left by {Title} when the application last stopped.",
						discarded.Count,
						item.Title);
				}
			}

			var progress = new Progress<string>(line => lanes.ReportProgress(item, line));
			await executors.ExecuteAsync(item, progress, lanes.Token(item.Id) ?? CancellationToken.None);
		}
		catch (OperationCanceledException ex)
		{
			error = ex;
			logger.LogInformation("⏹️ Stopped: {Title}", item.Title);
		}
		catch (Exception ex)
		{
			error = ex;
			logger.LogError(ex, "⛔ {Title}: {Message}", item.Title, ex.Message);
		}
		finally
		{
			lanes.Complete(item, error);
			ItemCompleted?.Invoke(item);
		}
	}

	/// <inheritdoc />
	public override void Dispose()
	{
		_wake.Dispose();
		base.Dispose();
	}
}
```

- [ ] **Step 2: Verify it compiles**

Run: `dotnet build PanoramicData.NugetManagement.Web/PanoramicData.NugetManagement.Web.csproj -t:Compile`
Expected: errors only in `Home.razor` and `IssuesView.razor` (Task 8).

- [ ] **Step 3: Commit**

```bash
git add PanoramicData.NugetManagement.Web/Services/WorkRunnerService.cs
git commit -m "Run the lanes from a hosted service, not a browser tab

Work belonged to the circuit that enqueued it, so closing the tab
cancelled it. Tolerable for one item at a time; not for twenty. The
runner claims what the cap allows, restores what the last run left, and
cleans up after anything the process interrupted."
```

---

### Task 8: Tree work nodes

**Files:**
- Modify: `PanoramicData.NugetManagement.Web/Models/NavItem.cs`
- Modify: `PanoramicData.NugetManagement.Web/Services/NavTreeDataProvider.cs`
- Test: `PanoramicData.NugetManagement.Test/NavTreeWorkNodeTests.cs`

**Interfaces:**
- Consumes: `WorkLaneService`, `WorkItem`.
- Produces: `NavTreeDataProvider.WorkKey(repositoryFullName)`, `.OrgWorkKey(organization)`, `.WorkItemKey(itemId)`; `NavItem.WorkItemId`, `.WorkItemState`, `.WorkItemProgress`, `.LaneKey`.

- [ ] **Step 1: Write the failing tests**

`PanoramicData.NugetManagement.Test/NavTreeWorkNodeTests.cs` — model on the existing nav-tree tests in this project (read one first for how `NavTreeDataProvider` is constructed there and reuse that setup verbatim):

```csharp
using PanoramicData.NugetManagement.Web.Models;
using PanoramicData.NugetManagement.Web.Services;

namespace PanoramicData.NugetManagement.Test;

/// <summary>
/// Tests that queued work appears in the navigation tree under the repository it belongs to. The
/// queue used to be a pane of its own below the tree, which was a second place to look for state
/// the tree already models per repository.
/// </summary>
public class NavTreeWorkNodeTests(ITestOutputHelper output) : TestWithOutput(output)
{
	private const string Repo = "panoramicdata/Athonet.Api";

	[Fact]
	public void WorkNode_LaneWithItems_AppearsUnderTheRepository()
	{
		var lanes = new WorkLaneService();
		lanes.Enqueue("Build", WorkDescriptor.ForRepository(WorkKind.Build, "panoramicdata", Repo), "build", null, null);
		var provider = NewProvider(lanes, withRepository: Repo);

		var items = provider.BuildNavItems();

		items.Should().ContainSingle(i => i.Key == NavTreeDataProvider.WorkKey(Repo))
			.Which.ParentKey.Should().Be(NavTreeDataProvider.RepoKey(Repo));
	}

	[Fact]
	public void WorkNode_EmptyLane_IsAbsent()
	{
		var provider = NewProvider(new WorkLaneService(), withRepository: Repo);

		provider.BuildNavItems().Should().NotContain(i => i.Key == NavTreeDataProvider.WorkKey(Repo));
	}

	[Fact]
	public void WorkItemNodes_AreChildrenOfTheWorkNode()
	{
		var lanes = new WorkLaneService();
		var item = lanes.Enqueue("Build", WorkDescriptor.ForRepository(WorkKind.Build, "panoramicdata", Repo), "build", null, null)!;
		var provider = NewProvider(lanes, withRepository: Repo);

		var node = provider.BuildNavItems()
			.Should().ContainSingle(i => i.WorkItemId == item.Id).Subject;

		node.ParentKey.Should().Be(NavTreeDataProvider.WorkKey(Repo));
		node.Text.Should().Be("Build");
		node.WorkItemState.Should().Be(WorkItemState.Pending);
		node.IsLeaf.Should().BeTrue();
	}

	[Fact]
	public void WorkItemNode_RunningWithProgress_CarriesBoth()
	{
		var lanes = new WorkLaneService();
		var item = lanes.Enqueue("Build", WorkDescriptor.ForRepository(WorkKind.Build, "panoramicdata", Repo), "build", null, null)!;
		lanes.TryStartNext(out _);
		lanes.ReportProgress(item, "repo 8 of 47");
		var provider = NewProvider(lanes, withRepository: Repo);

		var node = provider.BuildNavItems().Single(i => i.WorkItemId == item.Id);

		node.WorkItemState.Should().Be(WorkItemState.Running);
		node.WorkItemProgress.Should().Be("repo 8 of 47");
	}

	[Fact]
	public void OrgWorkNode_OrganisationLaneWithItems_AppearsUnderTheOrganisation()
	{
		var lanes = new WorkLaneService();
		lanes.Enqueue("Rediscover", WorkDescriptor.ForOrganization(WorkKind.RediscoverOrganization, "panoramicdata"), "rd", null, null);
		var provider = NewProvider(lanes, withRepository: Repo);

		provider.BuildNavItems()
			.Should().ContainSingle(i => i.Key == NavTreeDataProvider.OrgWorkKey("panoramicdata"))
			.Which.ParentKey.Should().Be(NavTreeDataProvider.OrgKey("panoramicdata"));
	}
}
```

`NewProvider` is a helper this test file defines, building a `NavTreeDataProvider` over a cache primed with one repository — copy the construction from the existing nav-tree test file rather than inventing it.

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet build PanoramicData.NugetManagement.Test/PanoramicData.NugetManagement.Test.csproj`
Expected: FAIL to compile — `WorkKey`, `OrgWorkKey`, `NavItem.WorkItemId` do not exist.

- [ ] **Step 3: Add the `NavItem` fields**

Append to `NavItem`:

```csharp
	/// <summary>
	/// The queued work item this node represents, or null for every other kind of node.
	/// </summary>
	public string? WorkItemId { get; init; }

	/// <summary>Where the work item has got to, for work-item nodes.</summary>
	public WorkItemState? WorkItemState { get; init; }

	/// <summary>The work item's progress line, e.g. "repo 8 of 47". Null until it reports some.</summary>
	public string? WorkItemProgress { get; init; }

	/// <summary>
	/// The lane a work node covers, so its "stop everything" button knows what to clear.
	/// </summary>
	public string? LaneKey { get; init; }
```

- [ ] **Step 4: Add the keys and the node building to `NavTreeDataProvider`**

Take `WorkLaneService` as a new constructor parameter (nullable with a default, matching how `RegressionGuardService` is taken, so existing tests constructing the provider keep compiling). Add beside the other key builders:

```csharp
	/// <summary>Builds the key for a repository's "Work" container.</summary>
	/// <param name="repositoryFullName">The repository, as "owner/name".</param>
	public static string WorkKey(string repositoryFullName) => $"work:{repositoryFullName}";

	/// <summary>Builds the key for an organisation's "Work" container.</summary>
	/// <param name="organization">The organisation.</param>
	public static string OrgWorkKey(string organization) => $"work-org:{organization}";

	/// <summary>Builds the key for one queued work item's node.</summary>
	/// <param name="workItemId">The item's identifier.</param>
	public static string WorkItemKey(string workItemId) => $"work-item:{workItemId}";
```

In `AddRepositoryNodes`, after the `Packages` container is added for a row, add:

```csharp
			AddWorkNodes(
				items,
				WorkKey(row.RepositoryFullName),
				repoKey,
				$"repo:{row.RepositoryFullName.ToLowerInvariant()}",
				organization,
				row.RepositoryFullName,
				// Below Packages and above the categories: work is transient, and putting it first would
				// move the nodes the user navigates by every time something is queued.
				sortOrder: 1);
```

In `AddOrganizationNodes`, after the `Repositories` container is added:

```csharp
		AddWorkNodes(
			items,
			OrgWorkKey(organization),
			orgKey,
			$"org:{organization.ToLowerInvariant()}",
			organization,
			repositoryFullName: null,
			sortOrder: 1);
```

And the shared builder:

```csharp
	/// <summary>
	/// Adds a lane's "Work" container and one node per outstanding item, or nothing when the lane is
	/// empty. An empty container would be a node to open and find nothing in.
	/// </summary>
	private void AddWorkNodes(
		List<NavItem> items,
		string workKey,
		string parentKey,
		string laneKey,
		string organization,
		string? repositoryFullName,
		int sortOrder)
	{
		var laneItems = _workLanes?.ItemsFor(laneKey) ?? [];
		if (laneItems.Count == 0)
		{
			return;
		}

		items.Add(new NavItem
		{
			Key = workKey,
			Text = $"Work ({laneItems.Count})",
			ParentKey = parentKey,
			IconCss = "fas fa-list-check",
			View = NavView.None,
			Organization = organization,
			RepositoryFullName = repositoryFullName,
			LaneKey = laneKey,
			IsLeaf = false,
			SortOrder = sortOrder
		});

		for (var index = 0; index < laneItems.Count; index++)
		{
			var workItem = laneItems[index];

			items.Add(new NavItem
			{
				Key = WorkItemKey(workItem.Id),
				Text = workItem.Title,
				ParentKey = workKey,
				IconCss = workItem.State switch
				{
					Models.WorkItemState.Running => "fas fa-circle-notch fa-spin",
					Models.WorkItemState.Cancelling => "fas fa-rotate-left fa-spin",
					_ => "fas fa-clock"
				},
				View = NavView.None,
				Organization = organization,
				RepositoryFullName = repositoryFullName,
				LaneKey = laneKey,
				WorkItemId = workItem.Id,
				WorkItemState = workItem.State,
				WorkItemProgress = workItem.Progress,
				IsLeaf = true,
				// The lane's own order, not alphabetical: the queue's order is the information.
				SortOrder = index
			});
		}
	}
```

- [ ] **Step 5: Run tests to verify they pass**

Run: `./PanoramicData.NugetManagement.Test/bin/Debug/net10.0/PanoramicData.NugetManagement.Test.exe --filter-class "*NavTreeWorkNodeTests"`
Expected: PASS — 5 tests.

- [ ] **Step 6: Commit**

```bash
git add PanoramicData.NugetManagement.Web/Models/NavItem.cs PanoramicData.NugetManagement.Web/Services/NavTreeDataProvider.cs PanoramicData.NugetManagement.Test/NavTreeWorkNodeTests.cs
git commit -m "Show queued work in the tree, under what it acts on

The queue was a pane below the tree — a second place to look for state
the tree already models per repository. Each lane with work now hangs
off the node it belongs to."
```

---

### Task 9: Home.razor and IssuesView.razor

Where the build goes green again. Nothing here is subtle, but it is wide.

**Files:**
- Modify: `PanoramicData.NugetManagement.Web/Components/Pages/Home.razor`
- Modify: `PanoramicData.NugetManagement.Web/Components/IssuesView.razor`
- Modify: `PanoramicData.NugetManagement.Web/Services/WorkflowGate.cs`
- Modify: `PanoramicData.NugetManagement.Test/WorkflowGateTests.cs`

**Interfaces:**
- Consumes: everything from Tasks 1–8.
- Produces: no new public API.

- [ ] **Step 1: Remove the queue pane**

In `Home.razor`, delete the `<PDSplitPanel Size="1" MinSize="0" CssClass="spa-panel nav-queue-pane">` element and its entire contents (the `nav-queue` div, lines ~181–213). Then unwrap the now-single-child `PDSplitter` around the tree, so the tree pane fills the column. Delete `QueueItemIcon`, `IsQueueBusy`, and the `nav-queue*` CSS rules from the component's stylesheet.

- [ ] **Step 2: Replace `EnqueueWork` with lane enqueues**

Delete `EnqueueWork`, `PumpQueueAsync`, `EnqueueRequestedWork`, `_consoleNodeKeyByWorkItemId` and the `WorkQueue.RemoveOwnedBy(this)` call in `Dispose`. Inject `WorkLaneService Lanes`, `WorkFanOut FanOut` and `WorkRunnerService Runner` instead of `WorkQueueService`.

Rewrite each of the former call sites to build a descriptor. The repository-scoped ones follow this shape:

```csharp
	private void QueueForSelectedRepository(WorkKind kind, string title, WorkflowStep? step = null, params (string Name, string Value)[] parameters)
	{
		if (_selectedRow?.RepositoryFullName is not { Length: > 0 } repositoryFullName)
		{
			return;
		}

		var queued = Lanes.Enqueue(
			title,
			WorkDescriptor.ForRepository(kind, _selectedRow.Organization, repositoryFullName, parameters),
			$"{kind}:{repositoryFullName}:{string.Join(',', parameters.Select(p => p.Value))}",
			step,
			// Recorded now rather than read when the item runs: the lane may not reach it for minutes,
			// by which time the selection has moved and the output would land in an unrelated console.
			_selectedNodeKey);

		AppendConsole(queued is null ? $"↩️ {title} is already queued." : $"⏳ Queued: {title}");
	}
```

so `BuildAsync` becomes `QueueForSelectedRepository(WorkKind.Build, $"Build {_selectedRow.RepositoryFullName}", WorkflowStep.Build)`, and `FixCategoryAsync` becomes the same with `("category", category.ToString())`. Keep every existing pre-flight guard (`EnsureSelectedRowIsCurrent`, `GetFixBlockReason`, the clone checks) exactly where it is — those run before queueing and are not the runner's business.

The organisation-scoped buttons (`RediscoverOrganizationAsync`, `ReassessOrganizationAsync`, the clone dialog's confirm) enqueue a discovery descriptor on the org lane via `Lanes.Enqueue(..., WorkDescriptor.ForOrganization(...), ...)`.

- [ ] **Step 3: Subscribe to lane changes, debounced**

Replace `WorkQueue.Changed += OnWorkQueueChanged` with a debounced handler — twenty lanes reporting progress will otherwise rebuild the tree far faster than anyone can read it:

```csharp
	private readonly System.Timers.Timer _laneChangeDebounce = new(250) { AutoReset = false };

	private void OnLanesChanged() => _laneChangeDebounce.Stop() is var _ ? _laneChangeDebounce.Start() : default;
```

Write it as a plain method rather than that expression — the point is: on every `Changed`, restart a 250 ms one-shot timer; on elapse, `await ReloadTreePreservingExpansionAsync()` and `SafeStateHasChangedAsync()`. Subscribe to `Runner.ItemCompleted` for the row refresh that `EnqueueRequestedWork` used to do in its `finally`:

```csharp
	private void OnItemCompleted(WorkItem item)
	{
		_rows = Cache.GetCachedRows() ?? _rows;
		_ = InvokeAsync(async () => await ReloadTreePreservingExpansionAsync());
	}
```

Unsubscribe both in `Dispose`, and dispose the timer.

- [ ] **Step 4: Wire the tree node buttons**

In the `NodeTemplate`, add a branch for work nodes, beside the existing `IsOrgRootNode` branch:

```razor
									@if (node.Data?.WorkItemId is { Length: > 0 } workItemId)
									{
										<span class="nav-work-item">
											@if (node.Data.WorkItemProgress is { Length: > 0 } workProgress)
											{
												<span class="nav-work-progress">@workProgress</span>
											}
											@if (node.Data.WorkItemState == WorkItemState.Running)
											{
												<button type="button" class="nav-org-action-btn" title="Stop, reverting anything half-applied" @onclick="() => Lanes.Cancel(workItemId)" @onclick:stopPropagation @onmousedown:stopPropagation><i class="fas fa-stop"></i></button>
											}
											else if (node.Data.WorkItemState == WorkItemState.Pending)
											{
												<button type="button" class="nav-org-action-btn" title="Remove from the queue" @onclick="() => Lanes.Remove(workItemId)" @onclick:stopPropagation @onmousedown:stopPropagation><i class="fas fa-xmark"></i></button>
											}
										</span>
									}
									else if (node.Data?.LaneKey is { Length: > 0 } laneKey && node.Data.WorkItemId is null)
									{
										<span class="nav-org-actions">
											@* On the organisation's work node this stops every lane beneath it, which is
											   what replaces stopping a bulk action that used to be one item. *@
											<button type="button" class="nav-org-action-btn" title="@(node.Data.RepositoryFullName is null ? "Stop all work in this organisation" : "Stop all work on this repository")" @onclick="() => StopLane(node.Data)" @onclick:stopPropagation @onmousedown:stopPropagation><i class="fas fa-stop"></i></button>
										</span>
									}
```

with:

```csharp
	/// <summary>
	/// Stops a work node's lane — or, on an organisation's work node, every lane beneath it.
	/// </summary>
	private void StopLane(NavItem node)
	{
		if (node.RepositoryFullName is { Length: > 0 })
		{
			Lanes.CancelLane(node.LaneKey!);
			return;
		}

		Lanes.CancelUnder(node.Organization);
	}
```

- [ ] **Step 5: Per-repository gating**

Replace every use of `IsQueueBusy` with `IsRepositoryBusy(_selectedRow?.RepositoryFullName)`:

```csharp
	/// <summary>
	/// Whether this repository has work outstanding, so a click now queues rather than starts. Its own
	/// work only: work on another repository never closes this one's buttons.
	/// </summary>
	private bool IsRepositoryBusy(string? repositoryFullName)
		=> repositoryFullName is { Length: > 0 } name
			&& Lanes.ItemsFor($"repo:{name.ToLowerInvariant()}").Count > 0;
```

Change `FirstBlockedStep`'s call site to pass the lane rather than the global list:

```csharp
	private WorkflowStep? BlockedStep => _selectedRow?.RepositoryFullName is { Length: > 0 } name
		? WorkflowGate.FirstBlockedStep(Lanes.ItemsFor($"repo:{name.ToLowerInvariant()}"), name)
		: null;
```

`WorkflowGate.FirstBlockedStep` itself needs no logic change — only that its `WorkItem` parameter no longer has `OwnerId` or `Run`. Update `WorkflowGateTests.Item(...)` to build the new shape:

```csharp
	private static WorkItem Item(WorkflowStep? step, string? repositoryFullName) => new()
	{
		Id = $"{step}",
		Title = $"{step} {repositoryFullName}",
		DedupKey = $"{step}:{repositoryFullName}",
		Descriptor = repositoryFullName is null
			? WorkDescriptor.ForOrganization(WorkKind.Reassess, "panoramicdata")
			: WorkDescriptor.ForRepository(WorkKind.Reassess, "panoramicdata", repositoryFullName),
		Step = step
	};
```

- [ ] **Step 6: Update `IssuesView`**

Change `OnEnqueue` from `EventCallback<QueuedWork>` to `EventCallback<BulkRuleRequest>`, where:

```csharp
/// <summary>What a bulk rule action asks the host to queue, once fanned out across its repositories.</summary>
/// <param name="Organization">The organisation the repositories belong to.</param>
/// <param name="RuleId">The rule to apply.</param>
/// <param name="RepositoryFullNames">The repositories it affects.</param>
/// <param name="Push">Whether to commit and push each repository after fixing it.</param>
/// <param name="Title">What to say in the console.</param>
public sealed record BulkRuleRequest(
	string Organization,
	string RuleId,
	IReadOnlyList<string> RepositoryFullNames,
	bool Push,
	string Title);
```

placed in `Web/Models/BulkRuleRequest.cs`. `RunConfirmedAsync` builds one from `pending` and invokes the callback; `Home.razor` handles it with `FanOut.EnqueueRule(...)` and reports the count:

```csharp
	private void EnqueueBulkRule(BulkRuleRequest request)
	{
		var queued = FanOut.EnqueueRule(
			request.Organization, request.RuleId, request.RepositoryFullNames, request.Push, _selectedNodeKey);

		AppendConsole($"⏳ Queued {request.Title} across {queued} repositor{(queued == 1 ? "y" : "ies")}.");
	}
```

- [ ] **Step 7: Build and run the whole suite**

Run:
```
dotnet build PanoramicData.NugetManagement.Test/PanoramicData.NugetManagement.Test.csproj
./PanoramicData.NugetManagement.Test/bin/Debug/net10.0/PanoramicData.NugetManagement.Test.exe
```
Expected: `Build succeeded`, then **at least 555 + 36 = 591 tests, 0 failures**. Anything less than the original 555 passing means a regression, not a rescoped suite — investigate before proceeding.

- [ ] **Step 8: Commit**

```bash
git add -A
git commit -m "Move the queue UI into the tree and gate per repository

The pane below the navigation tree is gone; work hangs off the node it
acts on. A repository's toolbar is now governed by that repository's own
lane, so a fix running on one no longer closes the buttons on another."
```

---

### Task 10: Wiring and the concurrency setting

**Files:**
- Modify: `PanoramicData.NugetManagement.Web/Program.cs`
- Modify: `PanoramicData.NugetManagement.Web/Services/RuntimeSettingsService.cs`
- Test: `PanoramicData.NugetManagement.Test/RuntimeSettingsServiceTests.cs` (extend the existing file)

- [ ] **Step 1: Write the failing test**

Append to the existing runtime-settings test class (match its existing setup for the temp settings path):

```csharp
	[Fact]
	public void MaxConcurrentLanes_Unset_DefaultsToTwenty()
		=> NewService().MaxConcurrentLanes.Should().Be(20);

	[Fact]
	public void SetMaxConcurrentLanes_Persists()
	{
		var service = NewService();

		service.SetMaxConcurrentLanes(4);

		NewService().MaxConcurrentLanes.Should().Be(4);
	}

	[Fact]
	public void SetMaxConcurrentLanes_BelowOne_IsClampedToOne()
	{
		var service = NewService();

		service.SetMaxConcurrentLanes(0);

		service.MaxConcurrentLanes.Should().Be(1, "a cap of zero would stall every lane for ever");
	}
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet build PanoramicData.NugetManagement.Test/PanoramicData.NugetManagement.Test.csproj`
Expected: FAIL to compile — `MaxConcurrentLanes` does not exist.

- [ ] **Step 3: Add the setting**

To `RuntimeSettings`:

```csharp
	/// <summary>
	/// How many repository lanes may run at once. Defaults to 20.
	/// </summary>
	/// <remarks>
	/// A lane can run <c>dotnet build</c>, <c>dotnet test</c>, a clone and GitHub API calls, so the
	/// cap is what stops a large estate saturating the machine. Twenty is a working default for a
	/// developer machine, not a measured optimum.
	/// </remarks>
	public int? MaxConcurrentLanes { get; set; }
```

To `RuntimeSettingsService`:

```csharp
	/// <summary>How many repository lanes may run at once.</summary>
	public int MaxConcurrentLanes
	{
		get
		{
			lock (_lock)
			{
				return Math.Max(1, _runtimeSettings.MaxConcurrentLanes ?? 20);
			}
		}
	}

	/// <summary>Sets the lane concurrency cap and persists it.</summary>
	/// <param name="value">The new cap; values below one are clamped.</param>
	public void SetMaxConcurrentLanes(int value)
	{
		lock (_lock)
		{
			_runtimeSettings.MaxConcurrentLanes = Math.Max(1, value);
		}

		SaveToDisk();
	}
```

- [ ] **Step 4: Register everything in `Program.cs`**

Replace `builder.Services.AddSingleton<WorkQueueService>();` with:

```csharp
builder.Services.AddSingleton<WorkLaneService>(sp =>
{
	var runtimeSettings = sp.GetRequiredService<RuntimeSettingsService>();
	return new WorkLaneService { MaxConcurrentLanes = runtimeSettings.MaxConcurrentLanes };
});
builder.Services.AddSingleton(sp => new WorkQueueStore(
	WorkQueueStore.DefaultPath(),
	sp.GetRequiredService<ILogger<WorkQueueStore>>()));
builder.Services.AddSingleton<WorkFanOut>();
builder.Services.AddScoped<WorkExecutors>();
builder.Services.AddSingleton<WorkRunnerService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<WorkRunnerService>());
```

`WorkExecutors` is scoped because it depends on `DashboardService`, which is; the runner resolves it per item from its own scope.

- [ ] **Step 5: Run the whole suite**

Run:
```
dotnet build PanoramicData.NugetManagement.Test/PanoramicData.NugetManagement.Test.csproj
./PanoramicData.NugetManagement.Test/bin/Debug/net10.0/PanoramicData.NugetManagement.Test.exe
```
Expected: `Build succeeded`; **594 tests, 0 failures**.

- [ ] **Step 6: Commit**

```bash
git add -A
git commit -m "Wire the lanes up and make the cap a setting

Twenty lanes is a working default for a developer machine, not a
measured optimum, so it is settable rather than baked in."
```

---

### Task 11: Run it

The suite cannot tell you whether twenty lanes feel right, or whether the tree is legible while eight repositories build at once. This task is manual and its result is a report, not a commit.

- [ ] **Step 1: Start the app**

Ask David to run it in his own terminal — it does not stay up when started from a tool call:

```powershell
dotnet run --project PanoramicData.NugetManagement.Web
```

- [ ] **Step 2: Check each of these, and report what actually happened**

- Queue work on two repositories; confirm both run at once, and that each repository's work node shows its own items.
- Confirm the left-hand queue pane is gone and the tree fills the column.
- With work running on repository A, confirm repository B's toolbar is *not* gated, and A's is.
- Run a bulk rule apply across several repositories from the issues view; confirm it fans out into per-repository items, and that the organisation node's stop button clears them all.
- Confirm console output from a queued item still lands on the node it was started from after navigating elsewhere.
- Stop the app with work pending; restart; confirm the pending work is back and starts.
- Confirm no clipboard write and no IDE launch happens on its own (the deliberate change from spec §3a).

- [ ] **Step 3: Report**

State what worked and what did not, with the actual behaviour observed. Do not claim the feature works without having run these.

---

## Self-Review

**Spec coverage:** §1 Lanes → Tasks 1–3. §2 Fan-out → Task 6, with the org stop-all in Task 9 step 4. §3 Off-circuit execution → Tasks 5 and 7. §3a Side effects → Task 5 step 4's substitution table. §4 Persistence → Task 4, restore wired in Task 7. §5 Tree UI → Task 8, pane removal in Task 9 step 1, debounce in Task 9 step 3. §6 Gating → Task 9 step 5. Testing section → the five test files, all present. Every section has a task.

**Known rough edge, stated rather than hidden:** the build is red from Task 2 to Task 9. That is inherent to replacing the queue in one change, which is what the "big bang" staging chose; the alternative was the phased option. Tasks 3–7 therefore verify by inspection ("the only remaining errors name these files") rather than by a green suite, and Task 9 step 7 is the first real gate. A reviewer should treat Task 9 as the point where the work is provable.

**Type consistency:** `WorkDescriptor.LaneKey` lower-cases; every hand-built lane key in Tasks 8–9 uses `.ToLowerInvariant()` to match. `WorkLaneService.Enqueue` takes `(title, descriptor, dedupKey, step, consoleNodeKey, wasInterrupted = false)` and is called with that shape in Tasks 4, 6, 8 and 9. `WorkExecutors.ExecuteAsync(item, progress, cancellationToken)` matches the runner's call in Task 7.
