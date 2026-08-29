# Repository layer in the navigation tree

Date: 2026-08-29
Status: approved, not yet implemented

## Problem

The navigation tree presents one node per **NuGet package** and treats it as if it were a
repository. That identification is false in both directions.

A repository can publish many packages. `panoramicdata/PanoramicData.ECharts` publishes four —
`PanoramicData.ECharts`, `.BindingGenerator`, `.Samples` and `.Sandbox`. Each becomes its own node,
each is cloned, assessed and remediated separately, and each reports the same findings against the
same repository. The estate looks larger than it is and the same work is done four times.

A package need not resolve to a repository at all. Discovery derives the repository from the
package's nuspec, and when that lookup fails the package is recorded as declaring no repository.
The two outcomes — *declared nothing* and *we could not find out* — are indistinguishable, so a
transient failure reads as a permanent fact about the nuspec.

Both faults share a root: `PackageDashboardRow` is keyed on `PackageId`, while everything the
application actually does — cloning, assessing, branching, committing, remediating — is keyed on a
repository. `RepoAssessment` already carries `RepositoryFullName`; the row around it does not.

### Observed

Of 106 discovered packages, 15 carried no repository. Eight of those declare a perfectly good
GitHub repository in their nuspec:

| Package | Declared in nuspec |
|---|---|
| ConnectWise.Manage.Api | panoramicdata/ConnectWise.Manage.Api |
| MagicSuite.Api | panoramicdata/magicsuite |
| Ollama.Api | panoramicdata/Ollama.Api |
| PanoramicData.Blazor.Demo | panoramicdata/PanoramicData.Blazor |
| PanoramicData.ECharts.BindingGenerator | panoramicdata/PanoramicData.ECharts |
| PanoramicData.ECharts.Samples | panoramicdata/PanoramicData.ECharts |
| PanoramicData.ECharts.Sandbox | panoramicdata/PanoramicData.ECharts |
| PanoramicData.SheetMagic.Benchmarks | panoramicdata/PanoramicData.SheetMagic |

The remaining seven genuinely declare nothing usable, `Harvest` naming Bitbucket rather than GitHub.

Commit `5568d8f` fixed one cause of this — a nuspec declaring the SCP-style
`git@github.com:owner/repo.git` threw out of the discovery loop. It did not address the fetch
itself, which still builds an `HttpClient` per package, runs 106 requests sequentially, makes one
attempt each, and returns null on any failure.

## Goals

1. A repository appears once in the tree, however many packages it publishes.
2. A repository is cloned, assessed and remediated once.
3. A failed nuspec lookup can never be mistaken for a nuspec that declares nothing, and can never
   silently remove a repository from governance.

## Non-goals

- Manual package-to-repository mapping. The fix for a wrong nuspec belongs in the nuspec.
- Governing repositories that publish no NuGet package. Discovery remains package-driven.
- Refactoring `Home.razor` beyond the mechanical changes this requires.

## Design

### 1. Discovery resolves repository URLs reliably

`ResolveRepositoryUrlAsync` returns a tri-state rather than `string?`:

| Outcome | Meaning |
|---|---|
| `Resolved(url)` | the nuspec, or failing that the project URL, names a GitHub repository |
| `NotDeclared` | the nuspec was read successfully and names no GitHub repository |
| `LookupFailed(error)` | the nuspec could not be read after three attempts |

Changes to `NuGetDiscoveryService`:

- Take `IHttpClientFactory` and use a named client, replacing the per-package
  `using var client = new HttpClient`.
- Three attempts with exponential backoff on `HttpRequestException` and `TaskCanceledException`.
  A 404 is not retried: it is a definitive `NotDeclared`.
- Resolve the batch with `Parallel.ForEachAsync` at a degree of 8, rather than 106 sequential
  awaits.

`NuGetPackageInfo` carries the outcome alongside `RepositoryUrl`.

URL parsing is unchanged: `GitHubRepositoryUrl.Normalize/Owner/Name` from `5568d8f` already handles
every form we have seen.

### 2. A failed lookup never shrinks the estate

In `DashboardService.DiscoverPackagesAsync`, when a package's resolution is `LookupFailed`:

1. If the previous cache held a repository for that package id, carry it forward and keep the
   repository governed.
2. Otherwise mark it ungoverned with a reason distinct from the declared-nothing case:
   *"Could not read the nuspec (network) — retry rediscovery"*, never *"declares no repository"*.

Either way the failures are logged at Warning and surfaced in the UI as an error banner naming the
affected packages.

### 3. Repository-primary data model

`PackageDashboardRow` becomes `RepositoryDashboardRow`, keyed on `RepositoryFullName` compared
case-insensitively:

```
RepositoryDashboardRow
  RepositoryFullName          "panoramicdata/PanoramicData.ECharts"
  Organization, RepositoryUrl
  Packages: [ { PackageId, LatestVersion, NuGetVersionMatchesTag } ]
  LatestTag
  IsClonedLocally, LocalPath, SlnxPath
  CurrentBranch, IsWorkingTreeClean, IsSyncedWithOrigin, SyncStatusCheckedAtUtc
  Assessment, CategorySummaries, Status, StatusMessage, HealthStatus
```

Per-package facts stay per-package. A repository's packages version independently, so
`LatestVersion` and `NuGetVersionMatchesTag` belong to the nested record; `LatestTag` is a fact
about the repository and stays on the row. The repository rolls up "any package out of step with
its tag".

Ungoverned packages have no repository and are not forced into a repository-shaped row. They move
to their own `UngovernedPackage` list — `{ PackageId, Organization, DeclaredRepository, Reason }` —
held beside the rows in the cache envelope.

Once discovery is fixed this turns 106 package rows into roughly 98 repositories and 7 ungoverned
packages.

### 4. Tree

```
Repositories
 └ PanoramicData.ECharts                      eye · RAG · clone/sync/dirty markers
    ├ Packages (4)
    │  ├ PanoramicData.ECharts                    1.4.2
    │  ├ PanoramicData.ECharts.BindingGenerator   1.4.2
    │  ├ PanoramicData.ECharts.Samples            1.4.0  ⚠ tag mismatch
    │  └ PanoramicData.ECharts.Sandbox            1.4.2
    ├ Licensing (2)
    │  ├ LIC-01
    │  └ LIC-03
    └ Packaging (1)
       └ PKG-04
```

Assessment categories hang off the **repository**, because that is what the rules evaluate. The
packages sit in their own `Packages` branch, so no finding is ever shown twice.

Node keys:

| Node | Key |
|---|---|
| repository | `repo:{org}/{name}` |
| packages container | `pkgs:{org}/{name}` |
| package | `pkg:{org}/{name}:{packageId}` |
| category | `cat:{org}/{name}:{category}` |
| rule | `rule:{org}/{name}:{ruleId}` |

The repository node is labelled with the repository name alone; the owner is already the
organisation above it. The eye toggle and the build-guard marker move from the package node to the
repository node, which is what they always acted on.

The `Packages` branch is unconditional. A repository publishing one package still shows it, so the
shape of the tree never changes underneath the reader.

`NavView` gains `RepositoryDetail` for the repository node. `PackageDetail` narrows to the
per-package view: version, tag match, listing state.

The existing `Not governed` branch keeps its behaviour and every assertion its tests make; only
the type it reads changes, from rows to the `UngovernedPackage` list. Once discovery is fixed it
shrinks from 15 entries to 7, each naming the nuspec that needs fixing.

### 5. Downstream

| Component | Change |
|---|---|
| `GovernanceScope` | operates on `RepositoryDashboardRow` |
| `NavHealthRollup.ForRepositories` | takes repository rows; rollup semantics unchanged |
| `IssueTreeDataProvider` | affected-repository counts become exact rather than package counts |
| `PackageDashboardDataProvider` | searches repository name and any package id |
| `IssuesView.razor`, `Home.razor` | mechanical row-type change at the `_rows` sites |
| assessment loop, work queue, bulk apply | iterate repositories |

Deduplication is not a step anyone must remember. Once the row is the repository, assessing a
repository twice is unrepresentable.

### 6. Cache and migration

`DashboardCacheService.DiscoveryVersion` goes from 1 to 2, and the envelope gains `Repositories`
and `UngovernedPackages`. A cache written by version 1 is discarded on load by the mechanism that
already exists, and the first launch rediscovers. No hand-written migration.

## Testing

Test-driven: each behaviour gets a failing test first. Run the xunit v3 binary directly —
`dotnet test` reports "Zero tests ran" in this repository.

New:

- Nuspec resolution returns `Resolved` / `NotDeclared` / `LookupFailed` for the three cases.
- A lookup that fails twice and succeeds on the third attempt resolves.
- A `LookupFailed` package with a prior cached repository keeps it and stays governed.
- A `LookupFailed` package with no prior mapping is ungoverned with the network reason, not the
  declared-nothing reason.
- Four ECharts packages produce one repository node with four package children.
- A repository publishing three packages is assessed exactly once.
- A repository whose packages disagree with its tag rolls that up to the repository node.

Updated: `NotGovernedNavNodeTests`, `NavHealthRollupTests`, `GovernanceScopeTests`,
`DashboardCacheVersionTests`, `GroupedRemediationPromptTests`.

## Risks

`Home.razor` is roughly 5,500 lines and touches rows in many places. That file, not the model
change, is where the cost lies. The row change is kept mechanical there and the file is not
otherwise refactored in this pass.

`main` is under active concurrent development, including in the discovery code this touches. The
branch is rebased onto `main` before merging.

## Verified

Real discovery run against nuget.org for `panoramicdata`, 2026-08-29:

| | Estimated in this spec | Observed |
|---|---|---|
| Packages discovered | 106 | 106 |
| Repository rows | ~98 | **81** |
| Ungoverned packages | 7 | **10** |
| Nuspec lookups failed | — | 0 |

The estimate of ~98 was wrong, and wrong in the direction that matters: it was extrapolated from the
eight packages that had failed to resolve, and so counted only the multi-package repositories visible
among them. There are ten in the live estate, collapsing 25 packages into 10 rows:

```
Lifx.Api                    Lifx.Api, Lifx.Cli
LogicMonitor.Api            LogicMonitor.Api, LogicMonitor.PowerShell
magicsuite                  MagicSuite.Api, MagicSuite.Cli
PanoramicData.Blazor        PanoramicData.Blazor, .Demo
PanoramicData.ECharts       PanoramicData.ECharts, .BindingGenerator, .Samples, .Sandbox
PanoramicData.HealthChecks  .BasicAuthentication, .BasicAuthentication.HashGenerator, .Core, .Versions
PanoramicData.Maps          PanoramicData.Maps, .Abstractions, .Blazor
PanoramicData.SheetMagic    PanoramicData.SheetMagic, .Benchmarks
PanoramicData.SyslogServer  ExampleApp, PanoramicData.SyslogServer
Passbolt.Api                Passbolt.Api, Passbolt.Cli
```

`ConnectWise.Manage.Api` now resolves to `panoramicdata/ConnectWise.Manage.Api` and is a governed
repository row.

The ten ungoverned packages each state an accurate reason: eight declare no GitHub repository (Harvest
declares Bitbucket, which is correctly read as none), and `Vizor.ECharts.Net80` plus the two
`PanoramicData.OData.*.Client` packages declare repositories under owners that are not ours.

**On the resilience work:** every nuspec was read first time on this run, so the eight packages that
had failed would have resolved today with or without the retry. What the change guarantees is not
that they resolve — it is that the next failure is reported as a failure rather than recorded as a
fact about a nuspec, and that a repository governed yesterday is not dropped because of it.
