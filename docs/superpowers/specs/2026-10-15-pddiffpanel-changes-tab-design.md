# PDDiffPanel and the AI Fix "Changes" tab

Date: 2026-10-15. Status: awaiting review.

## Intent

When an AI Fix session edits a clone, the session view shows each edit only as plain `-`/`+` lines
(`AiFixSession.CallDetail`), built from the tool call's `old_text`/`new_text` snippets. The user wants a
proper diff view: a reusable `PDDiffPanel` in PanoramicData.Blazor, using Monaco, shown in a Changes tab
beside the live log.

Success: while a session runs, the user can open Changes, pick any touched file, and see a Monaco
side-by-side (or inline) diff of git HEAD against the clone's current content, updating after each edit.

## Decisions already made

- `PDDiffPanel` lives in `../PanoramicData.Blazor`, wraps BlazorMonaco's `StandaloneDiffEditor`
  (BlazorMonaco 3.5.0, already a dependency).
- Placement: a Changes tab beside the log. The log keeps its compact `-`/`+` lines.
- Out of scope: diffs for non-AI remediations, a working-tree diff for Dirty repos, inline diffs in the log.

## 1. PDDiffPanel (PanoramicData.Blazor)

- `PDDiffPanel.razor` / `.razor.cs`, following `PDMonacoEditor` conventions (`pd-` CSS class, tabs,
  entry in `ComponentDocumentation.md`).
- Parameters: `Original`, `Modified`, `Language` (inferred from `FileName` when null), `FileName`,
  `SideBySide` (default true; toolbar toggle to inline), `ReadOnly` (default true), `Height`.
- Changing `Original`/`Modified` replaces text in the existing models rather than recreating the editor,
  preserving scroll position and avoiding flicker under live refresh.
- Theme and options follow `PDMonacoEditor`. Identical text shows a "No changes" state.
- Monaco is loaded by the host page as a global script (as for `PDMonacoEditor`); no new loading.
- Demo page, component tests, and a Playwright check like sibling components. Release as a new
  PanoramicData.Blazor version.

## 2. Changes tab (this repo)

- `WorkItemDetailView` gains a Log / Changes switch for AI items. Changes has a file list (modified /
  added / deleted) and a `PDDiffPanel` for the selected file: `Original` = file at git HEAD,
  `Modified` = the clone's current file on disk.
- New `CloneChangeReader` service: `git diff --name-status`, `git show HEAD:path`, and a disk read.
  It re-runs when the transcript records a successful `replace_in_file` or `write_file`, on the existing
  200 ms refresh, debounced, and only while the Changes tab is visible.
- Binary or very large files show "not diffable" with their size. Files already dirty before the session
  and not touched by it are labelled "pre-existing".
- Bump the PanoramicData.Blazor package reference here once released.

## 3. Testing

- `CloneChangeReader`: unit tests against a temp git repo (modified, added, deleted, binary, untouched-dirty).
- Component tests for the tab switch and file selection.
- Manual check by running the app on a spare port with the queue idle (starting the app claims real work,
  so this needs the user's go-ahead).

## Risks

- Monaco diff editor inside Blazor Server: model swapping must be done through the editor's models,
  not by re-rendering, or live refresh will flicker.
- Package ordering: the PanoramicData.Blazor release must be published before this repo can reference it.
