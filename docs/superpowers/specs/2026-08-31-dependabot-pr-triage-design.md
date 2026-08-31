# Dependabot pull request triage

## Problem

Every governed repository accumulates open Dependabot pull requests. `Athonet.Api` has six,
and none of them needs a human:

| PR | Proposal |
|---|---|
| #1 | Bump refit from 6.3.2 to 7.2.22 in /Athonet.Api |
| #2 | Bump actions/setup-dotnet from 1 to 5 |
| #3 | Bump actions/checkout from 3 to 6 |
| #4 | Bump actions/download-artifact from 4 to 8 |
| #5 | Bump github/codeql-action from 2 to 4 |
| #6 | Bump actions/upload-artifact from 4 to 7 |

The tool already surfaces these — `RepositoryIssueService` lists open issues and pull requests and
ages them — but it knows nothing about what a pull request *proposes*, so it cannot say whether one
is still worth acting on. Meanwhile the tool holds everything needed to answer that: the declared
package versions, the workflow contents, the estate-learned action floors in `action-versions.json`,
and a registry of remediations that would make most of these pull requests moot.

Three outcomes are wanted:

1. A pull request the repository has already outgrown is closed, with a comment saying why.
2. A pull request that is still valid and that we can fix automatically is fixed by the existing
   remediation pipeline.
3. A pull request that is still valid and that we *cannot* fix automatically raises an issue against
   this repository, so the missing remediation becomes visible work.

## Scope

In scope: classifying Dependabot pull requests, closing the redundant ones, and raising one issue per
uncovered dependency. Out of scope: writing any new remediation (the issues this raises are the
backlog for that), extracting the four CI rules' inline `uses:` parsing onto the new shared scanner,
and any change to the staleness severity bands.

## Validity

A pull request is **invalid** on exactly one condition: **already satisfied** — the repository's
declared version is at or above the pull request's target, so merging it would change nothing.

Deliberately *not* invalidity conditions:

- **Target below our floor.** `#3` proposes `actions/checkout` v6 where `action-versions.json`
  records a floor of v7. The pull request is still valid: the repository declares v3 and does not
  satisfy v6. It is handled by the covered path, whose remediation pushes v7 — and the *next* triage
  pass then reads it as already satisfied and closes it. One rule, no special case, and the fix
  pipeline converts valid pull requests into invalid ones on its own.
- **Stale base version** and **package governed away** were considered and rejected: both require a
  judgement about intent, and a wrong call closes someone's pull request.

Only pull requests authored by `dependabot[bot]` are eligible. A human pull request titled "Bump X"
is never touched.

`Unrecognised` is a first-class verdict, not a failure. Grouped pull requests
(`Bump the nuget group with 3 updates`), non-version pull requests, and anything the parser does not
recognise are left strictly alone: not closed, no issue raised, and still visible in the tree exactly
as today. The parser fails silent rather than open, because failing open means closing pull requests
we did not understand. The cost is that a grouped Dependabot pull request is invisible to this
feature and only a human clears it.

## Deciding "already satisfied"

Everything needed is in `RepositoryContext.FileContents`.

**NuGet.** `PackageReferenceScanner.Scan(context)` already yields
`PackageVersionReference(PackageId, CurrentVersion, FilePath)`. Satisfied when
`CurrentVersion >= to`, compared as `NuGetVersion`, never as strings.

**GitHub Actions.** No scanner exists — the four CI rules each regex `uses:` inline. A new
`Services/ActionUsageScanner` returns `(Action, VersionSpec, WorkflowPath)` for every
`uses: owner/name@spec` across `.github/workflows/**`. Satisfied when *every* usage of that action is
at or above the target: one workflow left behind means the pull request still has work to do.
Comparison is major-only, because that is the only granularity Dependabot proposes for actions and
the only granularity `action-versions.json` records.

A SHA-pinned usage (`@abc123 # v4`) is not readable as a version. It counts as **not** satisfied, so
nothing is closed on the strength of a version we could not read.

A usage of a sub-action — `github/codeql-action/init@v2` — is a usage of the repository
`github/codeql-action`, which is what Dependabot versions. The scanner attributes it there and keeps
the sub-path alongside.

## Coverage

A valid pull request is **covered** when some *failing* rule governs its dependency and
`RemediationRegistry` has a remediation for that rule's ID.

A failing rule does not currently say which dependency it governs in machine-readable form:
`CiActionsCheckoutVersionRule`'s advisory carries `latest_version` but never the string
`actions/checkout`, and `NuGetPackageUpdateRuleBase` names packages only as formatted prose inside
`behind_estate` (`"refit 6.3.2 -> 7.2.22 (Directory.Packages.props)"`).

So rules declare it, via an opt-in interface:

```csharp
public interface IGovernsDependency
{
	bool Governs(DependencyRef dependency);
}
```

Implemented only by rules that enforce a **minimum version** of a named dependency:
`CiActionsCheckoutVersionRule`, `CiSetupDotnetVersionRule`, `CiWorkflowMatchesMerakiRule` (which
version-checks `actions/upload-artifact` against the learned floor and replaces the whole workflow,
so it also carries `actions/download-artifact` to the template's v8), the three
`NuGetPackageUpdateRuleBase` descendants, and the deprecated-package rules. Every other rule does not
implement it and is never asked.

**Presence-only rules must not implement it.** `CodeQlWorkflowRule` checks that a
`github/codeql-action` workflow *exists*; it says nothing about which version. A rule that cannot
move a version cannot cover a version bump, so implementing `Governs` there would claim coverage that
does not exist and swallow the gap we are trying to surface. This is the distinction the interface
turns on, and the coverage-guard test asserts it.

Chosen over adding a `["governs"]` key to those same rules' advisory `Data`: the same number of
edits, but compiler-checked and testable, which matters for a mechanism whose failure modes are
silently closing a pull request and silently never raising a gap.

## Types

Core library, `PanoramicData.NugetManagement`:

| Type | Purpose |
|---|---|
| `Models/DependencyRef` | `(DependencyEcosystem Ecosystem, string Name)`, case-insensitive. The single currency between a pull request, a rule, and a raised issue. |
| `Models/DependencyEcosystem` | `NuGet`, `GitHubActions`, `Unknown`. |
| `Models/DependabotProposal` | A parsed pull request: dependency, `from`, `to`, sub-directory, number, URL. |
| `Models/DependabotVerdict` | `Unrecognised`, `AlreadySatisfied`, `ValidCovered`, `ValidUncovered`. |
| `Services/DependabotTitleParser` | `RepositoryIssue` to `DependabotProposal?`. Ecosystem inferred from the name containing `/`. |
| `Services/ActionUsageScanner` | Declared action versions across the workflows. |
| `Services/DependabotTriageService` | Proposals + context + `RuleResults` + a `canRemediate(ruleId)` predicate, giving a verdict per pull request. No I/O, no GitHub, no clock. |
| `Rules/IGovernsDependency` | Above. |

`DependabotTriageService` takes `canRemediate` as a predicate rather than depending on
`RemediationRegistry`, because the registry lives in the web project. This keeps the existing
rules-in-core, remediations-in-web seam intact.

## Work item

`WorkKind.TriageDependabot`, repository-scoped, on the repository's existing lane. Lanes are
sequential, so enqueueing it after `Reassess` on the same lane gives correct ordering for free — no
new dependency mechanism. `RefreshAll` fans it out alongside the reassessment it already fans out.

Inputs, both already available: the repository's current `RepoAssessment` for the failing rules, and
a `RepositoryContext` built the way `Reassess` builds one for the declared versions. With no
assessment present, triage does nothing and reports "assess first" rather than guessing.

Adding the member without adding it to `WorkExecutors` fails an existing test.

## Writing to GitHub

This is the first code in the repository that mutates GitHub. A second, separate port keeps the read
path provably read-only, so a test double for staleness cannot accidentally gain teeth:

```csharp
public interface IGitHubWriteApi
{
	Task CommentAsync(string owner, string name, int number, string body, CancellationToken ct);
	Task ClosePullRequestAsync(string owner, string name, int number, CancellationToken ct);
	Task<int> CreateIssueAsync(string owner, string name, string title, string body,
		IReadOnlyList<string> labels, CancellationToken ct);
}
```

Finding an already-raised issue needs bodies, which `GetOpenItemsAsync` does not return. A trailing
optional `string? Body = null` is added to `GitHubOpenItem` — non-breaking for every existing caller.

Closing is **on by default**. Every intended comment and close is written to the work item's output
before it happens, so the queue UI is the audit trail.

Comments carry a marker (`<!-- nugetmgmt:closed:already-satisfied -->`) so a re-run cannot
double-comment.

**Why no ignore directive.** Closing a Dependabot pull request by API normally invites Dependabot to
recreate it, which is why `@dependabot ignore this version` exists. That cannot happen here: a pull
request we close is one whose manifest already meets or exceeds the target, so Dependabot has no
update left to propose. A plain close is stable, and we never suppress a future legitimate bump.

A token without write scope surfaces the resulting failure on the work item rather than half-running.

## Raising the gap issue

One issue per uncovered *dependency*, not per sighting: the same gap seen across eight repositories
is one issue, because the deliverable is a remediation to write, not a symptom to file.

- Target repository from `AppSettings`, defaulting to `panoramicdata/PanoramicData.NugetManagement`.
  Configurable, not hardcoded.
- Title: `No auto-remediation for github-actions: github/codeql-action`
- Marker: `<!-- nugetmgmt:uncovered:github-actions/github/codeql-action -->`
- Body: what the gap is, an evidence table (repository, pull request, from and to), and what a
  remediation would have to do.
- Dedupe: list the target repository's open issues and match the marker. Found with new evidence
  means append a comment. Found with nothing new means do nothing.

**Race guard.** Triage runs on many repository lanes at once, so two repositories hitting the same
uncovered dependency would both try to create the issue. `UncoveredDependencyIssueService` in the web
project serialises on the marker and re-checks immediately before creating.

## UI

**Fix is the only button that fixes things, and it fixes everything under the selected node.** An
earlier draft of this gave triage a button of its own, which was wrong: a second control for each kind
of fixing is how a toolbar stops being readable, and how somebody ends up hunting for the one that
applies.

`FixScope` maps the selection to what Fix does:

| Selected | Fix does |
|---|---|
| A repository, or a package in it | applies the remediations **and** triages the inbox, queued in that order on the one lane |
| Its Issues branch, or one pull request | triages only — no failing rule sits beneath a pull request |
| A category or a rule | applies remediations only, as before |
| Anything else | nothing, and the button is hidden |

The remediations are queued first deliberately: a remediation that moves a version is what turns a
still-valid pull request into one triage can close.

The two halves are gated separately. Applying remediations needs a clean clone on `main`; triaging
needs neither, because it reads the repository and writes only to GitHub. So a repository with no
auto-remediable rule failure but an open Dependabot backlog still has an enabled Fix — which the old
single gate would have greyed out.

The button's visibility derives from `FixScope`, not from a literal list of views. That list is what
made Fix vanish from the repository view once before, and it is what left the Issues branch rendering
the getting-started placeholder: it was built with `NavView.None`, so it selected cleanly and showed
nothing. It now has `NavView.RepositoryIssuesDetail` and `RepositoryIssuesView.razor`, which lists the
inbox with each item's verdict.

Issue nodes carry the verdict as a badge — "No auto-fix" on `ValidUncovered`, "Superseded" on
`AlreadySatisfied`, "Auto-fix covers it" on `ValidCovered` — with the reason on hover. Only
`ValidUncovered` is coloured to ask for attention, because it is the only verdict nothing else will
act on. `NavHealthRollup` and the staleness bands are unchanged: the verdict is extra information, not
a new severity.

## Actual behaviour on Athonet.Api

Measured against the local clone once the implementation was working, not predicted:

| PR | Verdict | Why |
|---|---|---|
| #1 refit 6.3.2 to 7.2.22 | `AlreadySatisfied` | `Directory.Packages.props` already declares 7.2.22 or better |
| #2 setup-dotnet 1 to 5 | `ValidCovered` via CI-06 | `codeql-analysis.yml` still uses `@v1`, and CI-06 is failing |
| #3 checkout 3 to 6 | `ValidCovered` via CI-05 | `codeql-analysis.yml` still uses `@v3`, and CI-05 is failing |
| #4 download-artifact 4 to 8 | `AlreadySatisfied` | `ci.yml` already uses `@v8` |
| #5 codeql-action 2 to 4 | `ValidUncovered` | `CodeQlWorkflowRule` is presence-only, so nothing enforces a version |
| #6 upload-artifact 4 to 7 | `AlreadySatisfied` | `ci.yml` already uses `@v7` |

So the first pass **closes three**, leaves **two** to the existing fix pipeline, and raises **one**
issue here — for `github/codeql-action`.

Three of the six were already stale, which is the point: they are the "old" pull requests the feature
was asked for. An earlier draft of this document predicted `ValidCovered` for those three, on the
assumption that the workflows were as far behind as the pull request titles implied. They were not.
Reading the declared versions rather than trusting the title is the whole mechanism.

Two things the real repository taught the implementation:

- **Sub-action paths.** Athonet declares `github/codeql-action/init@v2` and
  `github/codeql-action/analyze@v2`, but Dependabot bumps the repository `github/codeql-action`. A
  sub-action has no version of its own, so `ActionUsageScanner` attributes the usage to the
  repository and keeps the sub-path separately. Without this, a repository already on v4 could never
  be shown to satisfy a bump to v4, because the two names would never match.
- **The lowest usage really does decide.** `checkout` is at `@v6` in `ci.yml` and `@v3` in
  `codeql-analysis.yml`. Taking the highest, or the first found, would have closed #3 while a real
  workflow sat on v3.

## Testing

- **Parser table** over the six real titles above, plus grouped
  (`Bump the nuget group with 3 updates`) and malformed input, which must give `Unrecognised`.
- **`ActionUsageScanner`**: several workflows, several usages of one action, `@v3`, `@v3.1.2`, and
  SHA-pinned `@abc123 # v4`. The SHA case is the one most likely to close a pull request wrongly.
- **Verdict fixture** reproducing the Athonet.Api table exactly.
- **Coverage guard**, in the spirit of the existing `WorkExecutors` and `NavViewCoverage` tests: a
  rule that has a remediation and governs a named dependency must implement `IGovernsDependency`, so
  a rule added later cannot silently open a coverage hole.
- **Recording fake `IGitHubWriteApi`**: one comment and one close per satisfied pull request,
  idempotent on re-run, and zero calls for `Unrecognised`.
- No live GitHub in unit tests; the existing opt-in `GitHubIntegrationTests` is where a real
  round-trip belongs.
