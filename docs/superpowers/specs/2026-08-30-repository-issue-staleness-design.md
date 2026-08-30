# Repository issue and pull request staleness

Date: 2026-08-30
Status: approved, not yet implemented

## Problem

The tool governs the shape of a repository — its rules, its packaging, its CI — and says nothing
about the conversations happening in it. A repository can pass every rule while an issue opened
three months ago has never had a reply, and a Dependabot pull request has sat unmerged since
spring. Nothing in the sidebar shows it, so nothing prompts anyone to act.

The estate has no view of its own inbox. Someone would have to open each repository on github.com
to find out, which means nobody does.

### What "no reply" has to mean

The obvious measures are both wrong.

`updated_at` is not a reply. A label change, an assignment, or an edit all bump it, so an issue
nobody has answered can look freshly attended to.

The *last comment by anyone* is not a reply either. When a reporter chases us after eight weeks of
silence, the newest comment is a day old and the issue looks healthy — but it is precisely the case
we most need to see. A follow-up from the person waiting is evidence of neglect, not of attention.

The measure that means something is **the last comment by a maintainer**: someone whose GitHub
`author_association` on that comment is `Owner`, `Member` or `Collaborator`. If no maintainer has
ever commented, the clock starts when the item was opened.

## Design

### Severity bands

For every open issue and pull request, the clock starts at the last maintainer comment, or at
creation if there has never been one. Age is measured from that instant:

| Age since last maintainer reply | Severity |
|---|---|
| Under 7 days | `Info` |
| 7 days up to 30 days | `Error` |
| 30 days or more | `Critical` |

There is no `Warning` band. Two escalations were asked for and two exist; inventing a third would
put a step in the middle that means nothing.

7 and 30 are named constants on the service, not configuration. Nobody has asked for different
thresholds, and a setting nobody changes is a setting that has to be documented, defaulted,
migrated and tested for no return.

**Bots get no exemption.** A Dependabot pull request that has sat for a month with nobody saying
anything about it is a month of unreviewed dependency changes, and it should read `Critical`. The
estate is expected to show a lot of red the first time this runs. That is the finding, not a defect
in the measure.

### Data model

New in `PanoramicData.NugetManagement/Models`:

```
RepositoryIssue
    int             Number
    string          Title
    bool            IsPullRequest
    string          HtmlUrl
    string          AuthorLogin
    DateTimeOffset  CreatedAtUtc
    DateTimeOffset? LastMaintainerReplyUtc
```

Severity is derived, never stored: `ClockStartUtc => LastMaintainerReplyUtc ?? CreatedAtUtc`, and
`SeverityAt(DateTimeOffset now)` applies the bands above. Deriving it means a cache written
yesterday reports today's severity when it is read back, rather than a stale verdict that only
refreshes when the network does. It also makes the bands testable without a clock abstraction —
the caller passes `now`.

Issues and pull requests share one type. GitHub's own model treats a pull request as an issue, the
list endpoint returns both, and the staleness question is identical for each. `IsPullRequest`
separates them where the UI needs to, and nowhere else.

### Fetching — `RepositoryIssueService`

Lives in `PanoramicData.NugetManagement/Services`, takes the existing `IGitHubClient`, and answers
one question: given `owner/name`, what are the open items and when did a maintainer last speak on
each?

The naive implementation — fetch each item's comments — costs one request per open item. An estate
with a Dependabot backlog would spend a thousand requests of a 5,000/hour budget on every refresh.
So:

1. **List open items.** One paged call for open issues, which returns pull requests in the same
   list. Everything needed except the reply time.
2. **Sweep the repository's comments.** The repository-wide issue-comments endpoint returns every
   comment across every issue and pull request, each carrying its `author_association` and the
   issue it belongs to. Walked newest-first, the first maintainer comment encountered for an item
   *is* that item's last maintainer reply. The walk stops as soon as every open item is resolved.
   On a repository whose recent conversation is mostly on currently-open items — the normal case —
   this is one or two pages.
3. **Budget and fall back.** The walk stops unconditionally after 5 pages (500 comments). Items
   still unresolved then get an individual comment fetch. This bounds the cost of a repository with
   thousands of comments on long-closed issues while keeping every answer exact.

An item genuinely never commented on by a maintainer is resolved by the fallback returning nothing,
which is the correct `LastMaintainerReplyUtc = null`.

Note the endpoint returns issue comments, which for a pull request are its conversation comments.
Review comments on individual lines of a diff are a separate endpoint and are deliberately not
consulted: a line comment is a review artefact, and a review that concluded leaves a conversation
comment too.

### Where the data lives

`RepositoryDashboardRow` gains `List<RepositoryIssue> OpenIssues`. It is populated during the same
refresh that assesses the repository, persisted by `DashboardCacheService` in its existing on-disk
JSON, and expires with the existing one-hour staleness. One refresh path, free persistence, and the
rollup below becomes arithmetic on a single object.

`DashboardCacheService.DiscoveryVersion` goes 3 → 4. A cache written before this change has no
issue data, and a row with an empty `OpenIssues` list is indistinguishable from a repository with a
clean inbox. Discarding the old cache is the difference between "not yet known" and "nothing to
report".

### Rollup into repository health

Stale items count as repository failures. `RepositoryDashboardRow`'s totals, currently reading
straight off `Assessment`, add the issue contribution:

```
TotalCriticals => (Assessment?.CriticalCount ?? 0) + OpenIssues.Count(Critical)
TotalErrors    => (Assessment?.ErrorCount    ?? 0) + OpenIssues.Count(Error)
TotalFailures  => (Assessment?.FailedCount   ?? 0) + OpenIssues.Count(Critical or Error)
```

`Info` items contribute to neither. An issue answered yesterday is not a failure, and counting it
as one would mean a healthy, responsive repository could never reach zero — which would destroy the
meaning of every figure on the dashboard.

Everything downstream follows without further change: `HealthStatus` already derives from these
totals, the repository node's glyph derives from `HealthStatus`, `NavHealthRollup` already takes
the worst of the children for the organisation node, and the dashboard's error totals already sum
the rows.

One consequence is kept deliberately: `HealthStatus` returns `Unknown` when `Assessment` is null,
even if that repository has critical stale issues. `Unknown` already sorts as the worst state in
the rollup, so the repository still surfaces; it just says "not assessed" rather than a colour it
has not earned.

The organisation-level `Issues` branch — the issue-centric flip of the rule failures — is **not**
touched. It is keyed on rule ID and category, and a GitHub issue has neither.

### Tree

Under each repository node, after `Packages (N)`:

| Node | SortOrder |
|---|---|
| `Packages (N)` | 0 (unchanged) |
| `Issues (N)` | 1 |
| `Pull requests (N)` | 2 |
| Category nodes | 3 (was 1) |

`N` is the count of *all* open items of that kind, healthy ones included — it answers "what is in
this inbox", which is the informational count that was asked for. The node's glyph is coloured by
the worst severity beneath it, via the existing `NavHealthRollup.Worst`, so a repository with
eleven fresh issues and one three-month-old one reads red.

The label count and the failure count are deliberately different numbers. `Issues (12)` says twelve
things are open; the repository's `IssueCount`, fed by `TotalFailures` above, counts only the one
that has gone unanswered. The first is inventory, the second is work.

Each item is a leaf below its node, titled `#123 Title`, with a glyph coloured by its own severity.
Leaves sort worst-first, then oldest-first within a band: `SortOrder = severityRank * 1_000_000 +
min(Number, 999_999)` where `severityRank` is Critical 0, Error 1, Info 2. `NavItem` breaks
`SortOrder` ties on `Text`, and alphabetical order on `#1000` versus `#99` is meaningless, so the
rank has to carry the number rather than leave it to the tie-break.

`NavItem` gains an `IssueNumber` field so a selected leaf can be resolved back to its
`RepositoryIssue` without parsing the key.

Selecting a leaf opens a new `NavView.RepositoryIssueDetail` in the centre panel: title, author,
whether it is an issue or a pull request, when it was opened, when a maintainer last replied (or
that none ever has), the resulting age and severity, and a link out to GitHub.

#### The name collision

The organisation node already has a child called `Issues` meaning *failing rules across the
organisation*. The repository node will now have a child called `Issues` meaning *open GitHub
issues*. The same word means two different things one level apart in the same tree.

This is accepted, not overlooked. `Issues` is what a GitHub issue is called, and renaming the
repository node to avoid the clash would make it worse, not better. The nodes sit at different
levels, carry different icons, and open different views. Recorded here so the next person to read
the tree code knows it was a decision.

## Testing

Unit tests in `PanoramicData.NugetManagement.Test`, against a faked `IGitHubClient`. Run the xunit
v3 executable directly — `dotnet test` reports "Zero tests ran" in this repository.

Severity:
- Exactly 7 days since the last maintainer reply is `Error`; a moment under is `Info`.
- Exactly 30 days is `Critical`; a moment under is `Error`.
- An item with no maintainer comment ever is banded on its creation date.
- A comment from the reporter after the last maintainer reply does not move the clock.
- A bot-authored item bands exactly as a human-authored one does.

Fetching:
- Open issues and pull requests both appear, with `IsPullRequest` set correctly.
- The comment sweep stops as soon as every open item is resolved, without paging further.
- The sweep stops at the 5-page budget and the per-item fallback resolves the stragglers.
- A maintainer comment beyond the budget is still found, by the fallback.
- `Owner`, `Member` and `Collaborator` count as maintainers; `Contributor`, `None` and
  `FirstTimeContributor` do not.

Rollup:
- Critical and error items add to `TotalCriticals`, `TotalErrors` and `TotalFailures`.
- Info items add to none of them.
- A repository with a clean assessment and one 30-day-old issue reads `Error` health.
- A null assessment still reads `Unknown` regardless of issue severity.

Tree:
- Both nodes appear under a repository, with counts including healthy items.
- Category nodes still sort below them.
- Leaves sort critical-first, then by ascending number within a band.
- A repository with no open items of a kind still shows that node, as a leaf reading `(0)`.
