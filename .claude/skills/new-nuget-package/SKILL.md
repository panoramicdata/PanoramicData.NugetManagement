---
name: new-nuget-package
description: Use when asked to create, build, bootstrap or develop a new Panoramic Data NuGet package (typically "<Vendor>.Api") for a vendor's REST API from its documentation URL, OpenAPI spec or product name, in the PanoramicData.NugetManagement repository.
---

# New NuGet package

## Overview

"Create a Foo.Api nuget package, docs are at <url>" is shorthand. It means: build the complete, typed client for every non-deprecated documented operation, with lead-coordinated parallel subagents, to the house quality bar. That bar is 100% line and branch coverage, every request pinned by a test, live verification, zero warnings and Codacy grade A. Then publish it, and finish only when this repository's governance assessment passes every rule. Splunk.Api is the reference run and the paragon repository. On 2026-10-08 it had 656 documented operations, of which 642 were implemented (the other 14 are deprecated), 1,628 tests, and 91/91 governance.

**Never** stop at a scaffold, a subset, "most endpoints", or a plan, unless the user narrows scope at a checkpoint.

## Guard (run first)

This skill runs only in the PanoramicData.NugetManagement repository, including its worktrees:

```powershell
(git remote get-url origin) -match 'panoramicdata/PanoramicData\.NugetManagement(\.git)?$'
```

If that is false, refuse in one sentence: "Package creation runs only from the PanoramicData.NugetManagement repository; open a session there." Do nothing else: no research, no plan, no files.

## Progress table (the user's view of the run)

The user asked for this table with these icons. Show it **at the start** (phase 1 🔄, the rest ⏳), **after every phase**, at each checkpoint, and whenever the user asks for progress. It has three columns, in this order:

| | Step | Status, findings, notes |
|---|---|---|
| ✅ / 🔄 / ⏳ / ⚠️ / ❌ | phase name from the table below | what was found or decided, counts, blockers |
| | **Overall** | **NN% complete. ETA for the package being published and 91/91 green: <time>** |

✅ done, 🔄 in progress, ⏳ not started, ⚠️ waiting on the user, ❌ failed.

The last row is always the overall % and the ETA:
- The **%** is the sum of the weights of the finished phases, plus a pro-rata share of the phase in progress (filled rows over total rows in phase 6, for example).
- The **ETA** is a local clock time with its UTC offset (`Get-Date -Format 'HH:mm K'`), plus the duration, for example "14:30 +01:00 (about 5 h 20 m) plus your time at checkpoints". It counts working time only; time spent waiting for the user at a checkpoint is excluded and called out.
- Revise both each time the table is shown.

## Phases

| # | Step | Weight | Detail |
|---|---|---|---|
| 1 | Research and naming | 5% | phases.md §1 |
| 2 | Checkpoint A: confirm | - | one form (below) |
| 3 | Endpoint inventory | 5% | phases.md §3 |
| 4 | Checkpoint B: scope and live access | - | form, only if needed |
| 5 | Bootstrap repo, core client, exemplar, GitHub, Codacy | 15% | phases.md §5 |
| 6 | Parallel implementation (category agents and core hardening) | 40% | phases.md §6, agent-prompts.md |
| 7 | Merge, integrate, live suite, 100% coverage | 15% | phases.md §7 |
| 8 | Quality gate: Codacy A, governance assessment | 10% | phases.md §8 |
| 9 | README and docs | 5% | phases.md §9 |
| 10 | Checkpoint C and release | 5% | phases.md §10 |
| 11 | Close | - | phases.md §11 |

ETA calibration (Splunk.Api, 642 implemented operations, 6 parallel agents): about 2 hours of setup, 3 to 4 hours of parallel implementation, 2 hours of merging and live runs, 2 hours of Codacy clean-up, and 30 minutes to release. Scale the implementation phase by operation count, at roughly 1 hour of wall time per 180 operations with 6 agents. With no live instance, halve the merge phase.

## Checkpoints (clarifying questions)

Ask questions **only** at checkpoints, with the AskUserQuestion form: at most four questions, recommended option first and marked "(Recommended)". Between checkpoints, run unattended and take the recommended default for anything minor. Record each default in the table notes.

- **A, after research (always).** One form confirming:
  - the package ID;
  - the version line;
  - the API scope, when the docs cover several APIs;
  - anything else research left ambiguous.

  For the ID and the version line, the "(Recommended)" option is the value the standing answers below produce, and the alternatives are the obvious variants.

  Answering it authorises creating the GitHub repository and the Codacy project in phase 5.
- **B, after inventory (only if something is open).** Ask about:
  - live test access, when no usable Docker image exists;
  - write tests against a non-disposable instance;
  - ambiguous scope;
  - operations that cannot be built safely.

  A form cannot carry secrets. For live access, the recommended option is "I'll set them myself", followed by the exact `dotnet user-secrets set <key> <value> --project <P>.IntegrationTest` commands to run. Never ask for a password in chat.
- **C, before release (always).** Findings that need a decision, then the publish go-ahead. **Never publish without an explicit yes at C.**

Standing answers. Never ask about coverage, visibility, live-test policy or side-effect policy. The ID and version line are defaults that checkpoint A confirms.
- **Coverage:** every non-deprecated operation of every in-scope REST API.
- **Package ID:** the name the user gave, exactly (they chose it, so do not second-guess a vendor-wide name). If they gave none, use `<Vendor>.Api`. Use `PanoramicData.<name>` if the ID is taken, prefix-reserved, or rejected at first push.
- **Visibility:** the repository is public. Governance, Codacy and CodeQL all assume it.
- **Version line:** the vendor's REST API version as `major.minor` (Splunk 10.6, TheHive 5.8). A major-only API version ("v3") becomes `3.0`. An unversioned API gets `1.0`. Never use the product release version. When the in-scope APIs have different versions, use the version of the one with the most operations.
- **Several APIs on separate hosts or auth schemes:** in scope by default, as one package with one client per API, like `SplunkClient` and `SplunkHecClient`. Every in-scope operation goes in docs/endpoints. Non-REST APIs (GraphQL, gRPC) are out of scope unless the user picks them at A.
- **Models and interfaces are hand-written to docs/IMPLEMENTING.md,** with the spec as the source of truth. A script may scaffold them, but the output must meet the same bar, including XML docs and tolerant types.
- **The vendor's spec file is committed to `docs/openapi/` only if its licence allows redistribution.** Otherwise keep it in git-ignored `temp/`. The docs/endpoints tables are always committed.
- **Live tests:** use a Docker image that serves the API under test, if one exists (an image for a different component, such as a scan engine, doesn't count). Otherwise ask once at B. With no access, write the integration tests anyway: they fail loudly with the missing setting named, and never skip. Don't run them, and mark live verification "pending: tests written, not run" in the table, the README and the report. Unit tests and coverage still have to reach the full bar. CI runs only the unit test project (the paragon's ci.yml does that already), and coverage counts unit tests only.
- **Operations with real-world side effects** (launching scans against networks, restarts, updates, licence changes, engine pairing, sending email): implement them and unit-test them; never run them live.

## Red flags: stop and correct course

- Implementing categories sequentially yourself instead of fanning out agents in worktrees.
- Counting a group as done because its agent said so, before you have run its unit and live tests on `main` after merging.
- Accepting less than 100% line and branch coverage, a skipped test, or a warning.
- Editing `nuget-owned.json` or adding the repository to this tool by hand. The running app discovers it.
- Judging governance with a harness that cannot read Codacy (see lessons.md).
- Publishing, tagging by hand, or pushing tags without the checkpoint C yes.
- Asking questions outside a checkpoint, or several forms in a row.

lessons.md lists the technical pitfalls every new client must avoid. Read it before phase 5. assess/Assess.cs is the governance harness.
