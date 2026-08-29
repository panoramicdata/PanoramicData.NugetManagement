# NuGet version floor and upstream version cache

Date: 2026-08-30
Status: approved, not yet implemented

## Problem

`PKG-05`, `PKG-06` and `PKG-07` ask whether each explicitly pinned NuGet package is on the newest
version published to nuget.org. Three faults follow from that question.

**The bar is set by strangers.** `NuGetPackageUpdateRuleBase.EvaluateAsync` resolves "latest" live
from `https://api.nuget.org/v3/index.json` on every run. A repository that changed nothing goes from
green to red because somebody published a patch. `SelfAssessmentTests.SelfAssessment_AllRulesShouldPass`
and `GitHubIntegrationTests.GitHubAssessment_ThisRepository_ShouldBeCompliant` both assert that every
rule passes, so the suite is red whenever the estate is one release behind. This was observed on
2026-08-29 with `Codacy.Api 3.0.42 → 3.0.43`: two tests failed, no code had changed, and the fix was
to bump a dependency.

**The question is unanswerable.** "Be on the newest version in the world" is a state no repository
can durably hold. A rule that every repository fails most of the time trains people to ignore it.

**Every rule re-asks the same question.** The evaluation loop awaits one round trip per package
reference, serially, and builds a fresh `SourceCacheContext` per call, so nothing is reused between
packages or between the three rules. An assessment's wall-clock time is dominated by repeated
questions with identical answers.

### What governance actually wants

Two different things, currently conflated into one:

- **Consistency** — no repository should lag behind a version the estate has already proven works.
  This is the question the tool exists to ask, and nuget.org cannot answer it.
- **Freshness** — the estate as a whole should not drift indefinitely behind upstream. This one
  needs nuget.org, but it does not need an immediate verdict.

## Design

### Which pattern applies

The repository already contains two distinct catalog patterns, chosen deliberately:

- **Ratchet on observation**, where no authority exists: `ActionVersionCatalog` and
  `CoverageBaselineCatalog` learn a floor from the estate's own repositories and never lower it.
- **Read the authority**, where one exists and carries meaning beyond "newest":
  `DotNetReleaseCatalog` reads Microsoft's release index and keys off `support-phase`.

The test is whether upstream tells you what you *should* be on or merely what *exists*. nuget.org
publishes the highest version and has no opinion on whether you should be there, so package freshness
belongs to the ratchet pattern. Package *deprecation* and *vulnerability* data is authoritative and
stays an authority-read — `PKG-11` and `PKG-12` are unaffected by this design.

### Components

**`NuGetVersionCache`** — `nuget-versions.json` at the scanner repository root, committed.

```
packageId → { latestVersion, published, refreshedAtUtc }
```

Loaded once at construction and frozen for the process. Written only by the refresher. Exposes
`TryGet(packageId, out NuGetVersionSnapshot)`.

`published` comes from `IPackageSearchMetadata.Published` (confirmed present in NuGet.Protocol
7.9.0), so the cache stays a pure snapshot with no history of its own. Any machine computes the same
verdict from it, and a wiped or freshly cloned cache changes nothing.

**`NuGetFloorCatalog`** — `nuget-floors.json` at the scanner repository root, committed.

```
packageId → floor version
```

Modelled directly on `ActionVersionCatalog`: a `_frozenBaseline` used for pass/fail so a run's
verdicts cannot shift underneath it, a `_learned` set raised by `Observe(packageId, version,
repository)` and persisted for subsequent runs, and a `RecentBumps` queue so the UI can show what
moved and which repository moved it. A single repository ahead of the pack is enough — it is the
canary.

**`NuGetVersionRefresher`** — the only component that contacts nuget.org.

Runs as a `BackgroundService` in the Web app, and as a one-shot command so CI or a developer can
refresh deliberately without the server running. Walks the distinct package ids in the estate,
refreshes on an interval, and is bounded by both a requests-per-second limit and a maximum
concurrency.

**The sweep persists only when a version actually changed.** `refreshedAtUtc` alone must not dirty
the file: the cache is committed, so a refresher rewriting a timestamp every interval would leave the
working tree permanently modified and bury real version changes in noisy diffs. `refreshedAtUtc` is
therefore recorded per package and updated only alongside a version change, which also keeps the
committed file a record of what changed rather than of when we last looked.

Both stores expose a settable static `Default`, because `RuleRegistry` constructs rules via
`Activator.CreateInstance` with the parameterless constructor and cannot inject them. This is the
seam `ActionVersionCatalog.Default` already establishes, and it is what tests substitute.

### How a rule decides

`NuGetPackageUpdateRuleBase` stops resolving over the network. For each package reference:

1. `Observe` the declared version into the floor catalog. This affects subsequent runs only.
2. If the declared version is **below the frozen floor** → **fail**. The estate has already proven
   that version elsewhere, so this is a consistency failure and is immediate.
3. Otherwise consult the cache:
   - **Miss** → upstream unknown. No advisory, no grace check; the floor verdict stands. Reported as
     unknown, never guessed.
   - **Hit**, `latest > declared`, and the semantic gap equals this rule's `TargetUpdateLevel` →
     **fail only if `now - published > grace`**. Inside grace it passes and reports the available
     update.

Ahead-of-upstream needs no special case: a repository pinned above the latest published version
simply has no gap.

### Grace periods

Each rule already overrides `TargetUpdateLevel`; grace slots in beside it as a second override.

| Rule | Level | Grace |
|---|---|---|
| PKG-05 | build/patch | 30 days |
| PKG-06 | minor | 90 days |
| PKG-07 | major | 365 days |

**Assumption, easily changed:** these three numbers were proposed rather than specified. A single
shared value is wrong at one end — short enough to chase patches makes majors fail while the
migration is still being planned; long enough for majors lets trivial patches rot for a year.

`now` comes from an injected `TimeProvider` so grace is testable without waiting.

### Error handling

| Condition | Behaviour |
|---|---|
| Cache file absent or unreadable | Upstream unknown for every package; floor still gates. Never guessed. |
| Package missing from cache | Upstream unknown for that package only. |
| Cache stale | Used regardless. Staleness is surfaced, never fails a repository — the refresher being down is not the assessed repository's fault. |
| Refresher error | Logged; the last good snapshot is left intact. |
| Floor persist failure | Swallowed, as `ActionVersionCatalog.Persist` already does for read-only environments. |

The last row has a consequence worth stating plainly: **a floor learned in CI evaporates**, because
nothing commits the file there. In practice the floor moves only on machines that commit. This is
inherited from the existing pattern rather than introduced here, but it means CI cannot be relied on
to raise the bar.

### Self-assessment tests

`SelfAssessment_AllRulesShouldPass` and `GitHubAssessment_ThisRepository_ShouldBeCompliant` stop
asserting that the grace-dependent rules pass, and report their results without failing.

A grace period is a clock. Even with a committed cache, a version that sits un-adopted long enough
will eventually breach its grace and turn the suite red with no code change. That is the rule working
as designed, but "all rules pass" would then be an assertion about the calendar rather than about
this repository.

### Rollout

Until both files are seeded every package is "upstream unknown", and the floor is whatever the estate
currently declares — so nothing fails spuriously on the first run. Seeding is one refresher sweep
plus one assessment, then commit both files.

## Consequences

An assessment becomes a pure function of the repository plus two committed files. Runs are
reproducible, work offline, need no network stubbing in tests, and the bar moves only when a refresh
or a learned floor is committed — which is reviewable in a diff.

The cost is that upgrade pressure now depends on someone committing refreshed data. If no repository
ever adopts a new version and nobody commits a cache refresh, the estate sits green on old packages.
The advisory line is therefore kept visible even when nothing fails, so drift is always reported even
when it is not yet an error.

## Out of scope

- `PKG-11` and `PKG-12` (deprecation, vulnerabilities) stay authority-reads against live data.
- Continuous re-assessment of the estate. The floor moves on observation, and observation is the
  assessment run; making that continuous is a change to the work queue, not to this design.
- Unifying the existing catalogs. They are two patterns for two different situations and should stay
  that way.

## Files

**New**

- `PanoramicData.NugetManagement/Services/NuGetVersionCache.cs`
- `PanoramicData.NugetManagement/Services/NuGetFloorCatalog.cs`
- `PanoramicData.NugetManagement.Web/Services/NuGetVersionRefresher.cs`
- `nuget-versions.json`, `nuget-floors.json` (seeded, committed)

**Changed**

- `PanoramicData.NugetManagement/Rules/NuGetHygiene/NuGetPackageUpdateRuleBase.cs` — cache and floor
  instead of a live resolver; adds a grace override.
- `NuGetBuildLevelUpdatesRule.cs`, `NuGetMinorLevelUpdatesRule.cs`, `NuGetMajorLevelUpdatesRule.cs` —
  declare their grace.
- `PanoramicData.NugetManagement/Services/NuGetVersionChecker.cs` — retained, but called by the
  refresher rather than by rules; gains the `Published` date.
- `PanoramicData.NugetManagement.Test/SelfAssessmentTests.cs`,
  `PanoramicData.NugetManagement.Test/GitHubIntegrationTests.cs` — stop asserting grace-dependent
  verdicts.

## Testing

- **Stores** — load/save round trip, absent file, corrupt file; floor ratchets up, never down, and is
  case-insensitive.
- **Rule** — table-driven against a fake cache and a fixed `TimeProvider`: below floor fails; above
  floor within grace passes; beyond grace fails; unknown upstream passes with the floor still gating;
  ahead of latest passes.
- **Refresher** — the rate limiter is honoured; no test touches the network.
