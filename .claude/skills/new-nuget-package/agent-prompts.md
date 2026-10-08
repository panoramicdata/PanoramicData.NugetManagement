# Agent prompt templates

Fill in the `<...>` placeholders. Every agent runs in the background (`general-purpose`). Every agent's final message is
a report to you; it is not shown to the user. Relay what matters.

## Inventory

> Build a complete, accurate inventory of the <Vendor> <API name> REST API reference (<version>), to drive implementation of a .NET client library (<P>).
>
> **Source:** <docs root URL>. It has these sections/category pages: <list them if known>. Follow every category page to its endpoint pages. Use WebFetch, or curl through Bash when the summary loses detail. Also read the general pages on authentication, paging, errors, response formats and namespaces or tenancy.
>
> **Output:** one markdown file at <scratchpad>\<vendor>-endpoint-inventory.md, containing:
> 1. **General:** authentication schemes, request and response formats, common parameters and envelope, error format, deprecations.
> 2. **One section per category** (`## <slug>-endpoints`): a table with columns `Path` (exactly as documented, `{placeholders}`, no base prefix), `Methods` (all methods, comma-separated), `Purpose`, `Key params` (required ones marked `*`) and `Notes`. Notes cover deprecation (also mark the path `**(DEPRECATED)**`), streaming or non-JSON responses, and special roles or editions.
> 3. **Summary:** paths and operations per category, and the totals.
> 4. **Gaps:** pages that failed, inferred methods, and errors in the docs.
>
> Be exhaustive: every endpoint, not a sample. Accuracy of paths and methods matters more than prose. Report the totals and the gaps.

## Category

> You are implementing part of <P>, Panoramic Data's new open-source .NET 10 NuGet client for the <Vendor> <API> REST API (Refit + System.Text.Json, xUnit v3 + AwesomeAssertions). The quality bar is very high: complete coverage of the documented API, exact request tests, live verification, zero warnings, Codacy grade A.
>
> **Where to work:** your worktree is <path> (branch feat/<name>, already checked out). Work only there. Commit in small logical commits (author <the user's git name and email>, each message ending with a blank line and the `Co-Authored-By:` line from your session's commit-attribution instructions). Do not push, merge or open PRs. Other agents are working on other categories at the same time and share the live test instance.
>
> **Read first:**
> 1. docs/IMPLEMENTING.md, the binding conventions.
> 2. The worked example: <exemplar interface, model, client partial, unit test, integration test>, and the core.
> 3. Your rows: <docs/endpoints files and counts>.
> 4. The inventory: <scratchpad path>, sections <...>.
> 5. The official pages for each endpoint's parameters and response fields.
>
> **Scope:** every non-deprecated operation in those files, filling each row's Client method and Test. <Category-specific notes: high-level helpers wanted, streaming, JSON-bodied endpoints, a separate client for a separate service or port, read-only-only live tests for dangerous areas, families owned by another agent that you must leave alone.>
>
> **Live verification:** capture realistic JSON from the live instance, never printing or committing credentials. Integration tests go under <P>.IntegrationTest/<Category>/: reads for every family the instance supports, and create/update/delete round trips for objects you can create safely, named with the `<prefix>` prefix and deleted in finally blocks. Where a feature is unavailable on this instance, assert the real error. **Never:** restart the instance, change licensing or cluster roles or server-wide settings, change the admin credentials, or touch objects you did not create.
>
> **Done means:**
> - Every non-deprecated row filled.
> - Both test projects build with 0 warnings.
> - `dotnet test --project <P>.Test/<P>.Test.csproj` is all green. InventoryTests will still list other categories as pending; do NOT edit docs/pending-categories.txt.
> - Your live tests are green.
> - 100% line and branch coverage of what you added.
> - Do not define an extension method named `ShouldBe` on RecordedCall; give helpers group-specific names.
>
> **Report:** rows implemented and any left out (with reasons), interfaces and client properties added, shared or core files changed and why (core bugs go in separate commits), where the docs disagree with the live instance, test counts and results, coverage, and the commit list.

## Core hardening

> Harden the shared core of <P> (paths: <core files and folders>, Test/Core, Test/Support). Stay out of category folders. Keep public API changes minimal and backwards-compatible; justify any you make in your report.
>
> **Tasks:**
> 1. Review critically for bugs: thread safety, secret leakage (logs, ToString, exception messages, URLs carrying credentials), URI escaping (%2F, unicode, path-prefixed base URLs), retry semantics (replayability, Retry-After, timeout versus caller cancellation), authentication flows (single login under concurrency, one re-login on 401, a failed login surfacing as the API exception), serialization edge cases, the error mapper (JSON, XML, garbage, empty bodies), and disposal. Also check every item in lessons.md. Fix real bugs, one commit each.
> 2. Write unit tests to 100% line and branch coverage of the core. Include NoOptionalParametersTests (S2360) and InterfaceSurfaceTests. Tests must be deterministic, never skipped, have no network access and never sleep.
>
> **Report:** bugs fixed (cause and effect, one line each), public API changes, test count, coverage per area, and commits.

## Codacy

> Bring <P> to Codacy grade A with zero issues (governance rules CQ-05, CQ-06 and SEC-02). Worktree <path>, branch chore/codacy-grade-a.
>
> - **Issues:** `curl -s -X POST -H "Content-Type: application/json" -d '{}' "https://app.codacy.com/api/v3/analysis/organizations/gh/panoramicdata/repositories/<P>/issues/search?limit=100"`.
> - **File grades:** `https://app.codacy.com/api/v3/organizations/gh/panoramicdata/repositories/<P>/files` (no `/analysis/`).
> - **Refactor; never suppress.** No `.codacy.yml` exclusions and no disabled patterns. Remove model duplication with shared base classes (`XSettings : <Form request base>`; create and update derive from it). Remove test duplication with shared helpers and theories. Keep requests pinned exactly, keep the docs/endpoints Test names valid, and keep coverage at 100%.
> - **Duplication is invisible until you push,** so build a local token clone detector (about 50 tokens, ignoring attributes) under temp/ to judge it.
>
> **Report:** changes per pattern, public API changes (additive only; call out anything else), tests, coverage, live results and commits.
