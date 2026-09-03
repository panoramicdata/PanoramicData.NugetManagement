# Dependabot pull request adoption

## Problem

`Highlight.Api` is clean apart from one Codacy grade, and still carries five open Dependabot pull
requests that Fix will not touch:

| PR | Title | Waiting | Triage |
|---|---|---|---|
| #6 | Bump coverlet.collector from 8.0.1 to 10.0.0 | 138 days | No auto-fix |
| #26 | Bump Microsoft.Extensions.Configuration and Microsoft.Extensions.Configuration.Abstractions | 82 days | Left alone |
| #28 | Bump Microsoft.Extensions.Configuration.Abstractions and Microsoft.Extensions.Configuration.UserSecrets | 82 days | Left alone |
| #30 | Bump Microsoft.Extensions.DependencyInjection and 2 others | 82 days | Left alone |
| #33 | Bump Microsoft.Extensions.Configuration.Abstractions and 2 others | 82 days | Left alone |

Neither row is a missing remediation. `update_package_versions` and `replace_regex_in_files` already
write exactly the changes all five propose. Two other things are missing.

**Triage cannot read a grouped pull request.** `DependabotTitleParser` matches only
`Bump <name> from <x> to <y>`, so #26/#28/#30/#33 return `null`, become `Unrecognised`, and are left
strictly alone — not closed, no issue raised, no judgement at all. That was the documented cost of
failing silent, and it is now four of the five.

**A valid bump that no rule is currently failing for has no path to being applied.** #6 parses
cleanly. It is a major bump, PKG-07 gives major updates a 365-day grace, the release is ~138 days
old, and no other repository in the estate runs 10.0.0 — so PKG-07 passes and triage reports the
honest `Idle` verdict: governed, remediable, but nothing queued to move it. Coverage is defined as
"a *failing* rule will move this", and correctly so. The consequence is that a pull request can sit
open indefinitely inside a grace period, which is what 138 days looks like.

This adds a second, narrower source of fixes: **the open pull request itself**, once it is old
enough that the grace period has plainly stopped protecting anything.

## Scope

In scope: reading grouped Dependabot pull requests from their bodies; a new `Adoptable` verdict whose
runner applies the proposed bumps to the local clone and closes the pull request; the NuGet and
GitHub Actions writers for it; the age gate; and the UI and outcome-count changes that follow.

Out of scope: changing any grace period (PKG-05/06/07 and their thresholds are untouched); making
the age threshold configurable at runtime; merging pull requests on GitHub rather than adopting them
locally; adopting anything not authored by `dependabot[bot]`; and any new remediation writer —
adoption reuses the two that exist.

## Part 1 — Grouped pull requests

### The title is not enough

`Bump Microsoft.Extensions.DependencyInjection and 2 others` names neither the other two packages
nor a single version. No title regex can recover them. Dependabot does write the full list into the
body, one line per dependency:

```
Updates `Microsoft.Extensions.DependencyInjection` from 9.0.0 to 9.0.4
Updates `Microsoft.Extensions.Configuration.Abstractions` from 9.0.0 to 9.0.4
Updates `actions/checkout` from 4 to 5
```

and for a single-dependency pull request:

```
Bumps [coverlet.collector](https://github.com/coverlet-coverage/coverlet) from 8.0.1 to 10.0.0.
```

So the parser reads the body, with the title kept only as the eligibility check it already is.

**The exact wording must be verified against the real bodies of #6, #26, #28, #30 and #33 before any
parser is written.** Those five bodies become the test fixtures. The forms above are what Dependabot
is expected to emit, not something this design has confirmed; a parser built on an assumed format is
a parser that fails silent on the very pull requests it was written for. Fetching them is the first
implementation task, and the fixtures are committed alongside the tests.

The body is already fetched — `OctokitGitHubIssueApi` returns `issue.Body` on every open item, for
the gap-issue marker check — but `RepositoryIssueService` drops it when it builds `RepositoryIssue`.

### `RepositoryIssue.Body`

Add `Body` to `RepositoryIssue` and map it through. It is marked `[JsonIgnore]`: Dependabot bodies
carry full changelogs and release notes, and the row cache holds every open item of every repository.
Persisting them would inflate the cache by orders of magnitude to store text that is only ever read
during the pass that fetched it.

The consequence is that a restored row has titles but no bodies, so triage cannot judge a grouped
pull request from cache alone. That is acceptable and matches the existing shape: the triage lane
already refuses to run without a current assessment, and `row.OpenIssuesKnown` already gates it. A
grouped pull request whose body is absent parses to `null` and is `Unrecognised` — today's
behaviour, which is the safe direction.

### A proposal becomes many bumps

`DependabotProposal` currently holds one dependency and one version pair. It becomes a pull request
and a list:

```csharp
public sealed record DependabotBump(
	DependencyRef Dependency,
	string FromVersion,
	string ToVersion,
	string? Directory);

public sealed record DependabotProposal(
	int Number,
	IReadOnlyList<DependabotBump> Bumps,
	string HtmlUrl);
```

A single-dependency pull request is a proposal with one bump, so there is no special case anywhere
downstream. Ecosystem inference stays where it is — a name containing `/` is an action.

### Folding many bumps into one verdict

`DependabotTriage` keeps one verdict per pull request, because that is what the tree shows and what
the runner acts on. For a grouped pull request it is the **least resolved** state across the bumps,
so a group is never reported as better handled than its worst part.

Bumps already satisfied are removed from consideration first: a group of three where two are done
should be judged on the one that is not. If every bump is satisfied, the verdict is
`AlreadySatisfied` and the existing close path runs unchanged.

For the remaining unsatisfied bumps, in precedence order:

1. **`ValidCovered`** — every one of them has a failing rule with a remediation that will move it.
   The existing fix pipeline handles the pull request; the next pass reads it as satisfied.
2. **`Adoptable`** (new) — every one of them is adoptable, and the pull request is old enough. Part 2.
3. **`ValidUncovered`, gap** — any one of them is ungoverned, or governed by a rule that never reads
   where it is declared.
4. **`ValidUncovered`, idle** — otherwise.

Coverage is checked before adoption deliberately. If a failing rule is already going to move a
dependency, letting the rule do it keeps one mechanism responsible for one change, and the estate
floors and rule thresholds keep deciding the target version rather than Dependabot.

Gap **issues** are still raised per bump, independently of the summary verdict. A mixed group where
one bump is covered and another is ungoverned reports as a gap by rule 3, and the runner raises the
gap issue for the ungoverned bump only — the covered one is not somebody's work. The runner already
accumulates uncovered sightings into a per-dependency dictionary, so this is a loop over
`proposal.Bumps` where it currently reads `proposal.Dependency`.

The `Reason` sentence names which bump drove the verdict. A grouped pull request reported as a gap
with no indication of *which* of its three dependencies is the gap is a sentence that sends someone
back to GitHub to find out.

## Part 2 — Adoption

### What it is

A pull request is **adoptable** when every unsatisfied bump in it can be written by a writer that
already exists:

- **NuGet** — the package is declared somewhere `PackageReferenceScanner` reads, and both the
  declared and target versions parse as `NuGetVersion`.
- **GitHub Actions** — the action is used somewhere `ActionUsageScanner` reads, and the target
  version yields a major.

All-or-nothing. A pull request is adopted only when *every* unsatisfied bump in it is adoptable;
otherwise it falls through to the gap or idle verdict and is left alone. Adopting part of a group and
closing it would silently drop the rest, and adopting part of a group without closing it leaves a
pull request whose content is mostly already applied — noise on every subsequent pass. "Closed"
keeps meaning "fully superseded".

The price is that a group containing one unsupported bump never clears until that bump is handled.
That is visible in the reason sentence rather than silent.

### The age gate

Adoption is gated on how long the pull request has been open, measured from `Issue.CreatedAtUtc`:

```csharp
public static readonly TimeSpan AdoptAfter = TimeSpan.FromDays(60);
```

Sixty days sits above PKG-05's 30-day build grace and below PKG-06's 90-day minor grace. The gate is
not trying to mirror the graces — it is a backstop against a pull request rotting, and its job is to
be long enough that nothing is adopted while a grace period is still doing useful work. All five
pull requests in the table qualify; a pull request Dependabot raised this morning does not.

Deliberately a constant with a test-only constructor override, not a `RuntimeSettings` field. One
number that nobody has yet wanted to change is not worth a settings row — and a new
`RuntimeSettings` property has to be added to the hand-written `SaveToDisk` snapshot or every save
erases it, which is a trap to walk into only for a setting somebody actually asked for.

This gives `DependabotTriageService` a clock, which it does not have today. It takes a
`TimeProvider`, defaulting to `TimeProvider.System`, and its class documentation — which currently
promises "no I/O, no GitHub, no clock" — is corrected. Tests use `FakeTimeProvider` and cover both
sides of the boundary.

### Applying it

Adoption writes through the existing data-driven remediations. For each adopted pull request, triage
produces the advisory `Data` a failing rule would have produced:

**NuGet** — `update_package_versions`, whose `updates` are the pipe-delimited records
`RemediationHelpers.UpdatePackageVersions` already parses, one per declaration site the scanner
found:

```
<filePath>|<packageId>|<versionKind>|<currentVersion>|<targetVersion>
```

**GitHub Actions** — `replace_regex_in_files` over the workflow globs, with the same pattern and
`${1}<version>` replacement shape CI-12 builds. CI-12's `PatternFor` is private; it is extracted
to a shared helper next to `GitHubActionVersion` and CI-12 calls the extracted one, so there is one
definition of how a `uses:` line is rewritten rather than two that can drift.

A pull request needing both gets both payloads, applied in sequence.

The seam is smaller than it first appears. `IRemediation.Apply(localPath, result, applied, onOutput)`
reads nothing from `result` except `Advisory!.Data`, so adoption needs no new interface and no change
to `DataDrivenRemediation`: it synthesizes a failing `RuleResult` carrying the advisory and hands it
to a `DependabotAdoptionRemediation : DataDrivenRemediation` instance.

That instance is **constructed directly and never registered**. `RemediationRegistry` is keyed by
rule id, and triage's own `canRemediate` predicate reads it — registering adoption under a
pretend rule id would make it answer "yes, a remediation exists" for a rule that does not exist, and
would surface in the registry-coverage and self-assessment tests as a rule with no rule.

### Preconditions

Adoption writes to a working tree, which triage has never done. It runs inside the existing
`TriageDependabotAsync` lane, which already holds the row, so it reuses that lane's guards plus the
clone guards the `ApplyRemediations` lane already uses:

- No local clone (`row.LocalPath is null || !row.IsClonedLocally`) — nothing is adopted. Every
  otherwise-adoptable pull request reports as idle with a reason saying the clone is missing, and no
  pull request is closed.
- Clone not safe to write to — same guard and same outcome as the remediation lane, whatever it
  decides; adoption does not invent a second opinion about when a clone can be written.

Per-repository work queues already serialize the triage lane against the remediation lane, so the
two never write the same file concurrently. Both write the same idempotent thing — a version
rewrite to a specific target — so lane order does not affect the result.

### Closing the pull request

Adoption comments and closes in the same pass, reusing the runner's existing comment-then-close
machinery with a new marker:

```
<!-- nugetmgmt:closed:adopted -->
```

separate from `ClosedMarker`, so the two reasons for a close stay distinguishable in a pull request's
history.

**The close is conditional on the write having happened.** The remediation reports what it applied;
if it applied nothing, the pull request is not closed and the pass says so. Without that, a writer
that silently matched nothing — the exact failure mode that made
`DataDrivenRemediation.CanRemediate` check for required data — would close pull requests against no
change at all.

**Accepted risk.** Even so, the close lands against a change that exists only in the local clone. If
that clone is never committed and pushed, the bump is lost and the pull request is gone; recovering
it means reopening the pull request on GitHub. This was chosen over deferring the close, with these
mitigations:

- Every intended write and close is announced before it is made, so the lane's output is the audit
  trail — the existing rule for this lane.
- The close only follows a write the remediation confirmed.
- The adopted change is left in the working tree, where the dashboard's own git status shows it as
  uncommitted and Commit & Push picks it up.

### Reporting

`DependabotVerdict` gains `Adoptable`, named for the state triage found rather than the act the
runner performs — the same way `ValidCovered` names a state. Triage decides; the runner adopts.

`DependabotTriageOutcome` gains `Adopted`, counting the pull requests actually written and closed
rather than the ones found adoptable, and the lane's closing summary names it.

The tree needs a label for the new verdict in `RepositoryIssuesView`, whose current switch maps
`ValidUncovered` to "No auto-fix" and everything else to "Left alone". `Adoptable` renders as
**"Adopting"**, present tense, because of when it is actually visible: an adopted pull request is
closed and `Restamp` drops it from the open list, so the only rows that survive a pass carrying this
verdict are the ones where the write applied nothing and the close was therefore withheld. "Adopted"
on a row that is still open, still unbumped, would be a lie. The reason sentence carries why the
write did nothing.

`FixScope` is **unchanged**. Adoption is part of what triaging a Dependabot pull request means, so it
arrives under the existing `TriageDependabot` action and the existing Fix button. Fix stays the only
control that fixes things. `FixScope.Describe`'s "triage N Dependabot pull requests" already covers
it, because adoption is triage acting on its own verdict rather than a separate thing Fix does.

## Testing

`dotnet test` reports "Zero tests ran" in this repository; the xunit v3 executable is run directly.

- **Parser** — the five real bodies from `Highlight.Api` as committed fixtures: single-dependency,
  two-dependency, and "and 2 others" grouped forms, plus a body naming a directory and one naming an
  action. A body the parser does not recognise returns `null`, and the existing
  `DependabotTitleParserTests` non-Dependabot and non-bump cases still pass.
- **Folding** — satisfied/covered/adoptable/gap/idle for a single bump and for groups; a group where
  some bumps are satisfied is judged on the rest; a mixed covered-and-gap group reports the gap and
  raises the issue only for the ungoverned bump.
- **Age gate** — `FakeTimeProvider` either side of 60 days; an adoptable pull request one day short
  is idle, not adopted.
- **All-or-nothing** — a group with one unsupported bump is not adopted and nothing is written.
- **Payloads** — the exact `updates` strings and the exact pattern/replacement pair, asserted
  against what `RemediationHelpers` parses. `PatternFor` behaviour is unchanged by the extraction:
  CI-12's existing tests are the regression check for that.
- **Runner** — adopted with a successful write comments, closes, and stamps the marker; adopted with
  a write that applied nothing does neither; no clone adopts nothing and closes nothing.
- **Restamp** — the new verdict survives a round trip and adopted pull requests leave the open list.

## Consequences for the five pull requests

Assuming their bodies parse as expected and their packages are declared where the scanner reads:

- **#6** is adoptable and 138 days old, so it is adopted: `coverlet.collector` is written to 10.0.0
  in the declaring file and the pull request is closed. PKG-07's grace is untouched and still governs
  every repository without a Dependabot pull request open against it.
- **#26/#28/#30/#33** become readable. Any bump among them already satisfied drops out; the rest are
  `Microsoft.Extensions.*` packages the scanner reads, so at 82 days each pull request is adopted in
  full, unless one of them turns out to contain a bump no writer covers — in which case that pull
  request is reported with the reason naming it, and left alone.

That is a prediction, not a guarantee. The bodies decide it, which is why fetching them comes first.
