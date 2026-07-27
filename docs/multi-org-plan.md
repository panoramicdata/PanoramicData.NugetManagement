# Multi-organisation support — plan

Status: **not started**. Agreed design, written down so it survives between sessions.

Today the application handles exactly one organisation, taken from `AppSettings:NuGetOrganization`
(the tree label and NuGet discovery) and `AppSettings:GitHubOrganization` (the GitHub API). The goal
is to handle several — `panoramicdata`, `panoramicsystems`, and whatever comes next.

## What makes this tractable

Three things were checked before planning, and each removes work we might have expected:

- **`RuntimeSettingsService` already persists typed settings to a JSON file** (`LocalReposRoot`,
  `PreferredIdeId`, `IncludeInfoInAiPrompt`). The organisation list can live there. No new storage.
- **Rows already carry their organisation.** `PackageDashboardRow.RepositoryFullName` is
  `owner/repo`, so a flat cache filtered by owner is enough. `DashboardCacheService` does not need
  restructuring.
- **Discovery is only lightly single-organisation.** `NuGetDiscoveryService.DiscoverOrganizationPackagesAsync`
  reads `_settings.NuGetOrganization` in exactly one place, so it parameterises cleanly.

## Steps

1. **Organisation registry.** Add `Organizations` to `RuntimeSettings` with add/remove, defaulting to
   the configured organisation when the list is empty so existing setups behave exactly as now.

2. **Per-organisation discovery.** Give `DiscoverOrganizationPackagesAsync` an owner parameter and
   have `DashboardService` loop the configured organisations, concatenating rows.

3. **Per-organisation tree nodes, with namespaced keys.** One node per organisation under
   `Organisations`, and every descendant key scoped to it: `org:x`, `repos:x`, `pkg:x:Foo`,
   `cat:x:Foo:Bar`. **This is the step to be careful with** — PDTree throws on a duplicate key and
   swallows the exception, so the entire tree renders empty with nothing in the console. See the
   PDTree notes in the team's notes on silent failures.

4. **Adding an organisation.** A **+** on the `Organisations` node. Removal is deliberately *not* a
   **−** on the tree: it belongs at the bottom of the per-organisation Settings screen, where a
   destructive action is harder to hit by accident.

5. **Organisation-scoped Issues.** `IssuesView` takes an `Organization` parameter and filters rows by
   owner. The component was extracted for exactly this reason, so both the `/issues` page and the
   dashboard panel can host it.

6. **Organisation-scoped re-assess.** Re-assess should act on the *selected* organisation, not every
   one. "Re-assess all repos" on the Dashboard node has the same "whose repos?" ambiguity that moved
   the Issues button onto the organisation node, and re-assessing every organisation multiplies the
   GitHub API cost for no benefit most of the time. The budget estimate should cover only the
   organisation being assessed.

## Direction for the Issues view

Rendering the issue tree inside the centre panel puts a tree next to a tree, which reads wrong. The
established pattern in this application is **hierarchy in the sidebar, detail and actions in the
pane** — that is exactly how `Repositories → package → category → rule` already works.

So the issue hierarchy should move into the navigation tree beneath the `Issues` node:

```
Organisations
└─ panoramicdata
   ├─ Issues
   │  └─ NuGetHygiene            (category)
   │     └─ PKG-07               (rule)
   ├─ Package Updates
   └─ Repositories
```

Selecting a category or rule shows its detail in the centre panel, which is where the affected
repositories and the bulk actions live — *Apply auto-fixes*, *Apply & push to N repositories*, *Copy
combined AI prompt*. That answers the obvious worry about the actions being unreachable in a narrow
sidebar: they never go in the sidebar.

Recommendation: stop the sidebar at **category → rule** and list the affected repositories in the
pane. A rule can affect ninety-nine repositories, and there are sixty rules, so putting repositories
in the sidebar would add well over a thousand nodes for no benefit — and the pane is where there is
room for per-repository checkboxes and actions anyway.

Ordering must be supplied explicitly: PDTree sorts children by their text unless given a `Sort`
comparison, which would present `Critical` below `Info`.
