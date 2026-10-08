# Phases in detail

Paths: this repository is `NM` (PanoramicData.NugetManagement). The new repository is a **sibling**,
`C:\Users\david\source\repos\panoramicdata\<Package>`, never inside NM. The paragon is the sibling `Splunk.Api`. Copy
its files and adapt them; do not copy older repositories (Highlight.Api, Salt.Api), which predate its improvements.

## 1. Research and naming

Use a background subagent if the docs are large. Settle these:

- **Spec:** an OpenAPI or Swagger document if one exists (check the docs for downloads, `/api/*/api.json`, the product's own console endpoint, or a GitHub repository). Otherwise use the HTML reference. Note which API versions and products the docs cover. Several APIs means a scope question at checkpoint A.
- **Size:** a rough operation count and the categories, for the ETA.
- **Package ID:**
  - Query `https://api.nuget.org/v3-flatcontainer/<id-lowercase>/index.json` (404 means the ID is free).
  - Check prefix reservation with `https://azuresearch-usnc.nuget.org/query?q=<Vendor>&prerelease=true&take=100`. The prefix is reserved only if a result whose ID starts `<Vendor>.` has `"verified": true`. No results, or only unverified ones, means it is not reserved.
  - Check `gh repo view panoramicdata/<id>`.
  - Apply the standing ID rule.
- **Version line:** apply the standing rule in SKILL.md.
- **Live instance:** a Docker image that serves this API (Docker Hub, or the vendor's registry). Note its size, licence-acceptance variables and health check. If an image exists for a different component of the product, record that it does not serve the API.
- **What shapes the core:**
  - the auth schemes, including 2FA or token headers;
  - the error shape;
  - the response envelope and paging style (a feed, HATEOAS `links` and `page`, cursors), which get a shared model and paging helper in the core;
  - the wire format (JSON or form-encoded bodies, and the default response format);
  - the **read-only POSTs** (searches, logins, queries sent as POST), which the read-only guard must allow.

## 3. Endpoint inventory

Start by creating the **local** repository: `git init -b main` in the sibling folder, then copy the paragon files (phase 5, step 1). That gives the inventory somewhere to go. Create the GitHub repository and Codacy project only in phase 5, after any checkpoint B answers. If the user abandons the run before phase 5, tell them the local folder exists and leave it for them to delete.

- **If the vendor publishes an OpenAPI or Swagger spec:** save it to `docs/openapi/` and generate the rows straight from its paths and methods, marking operations with `deprecated: true` as deprecated. Then dispatch the inventory agent only to cross-check the counts and to collect what the spec lacks: undocumented quirks and auth details.
- **Otherwise:** dispatch the inventory agent (agent-prompts.md §Inventory) in the background, and keep scaffolding while it runs.

Then generate `docs/endpoints/<category>.md` with **one row per path and method**: `| Method | Path | Client method | Test |`, with deprecated rows marked `(deprecated)`. Also create `docs/pending-categories.txt`, listing every category.

Do the generation with a PowerShell script, and check the total row count against the agent's summary. A mismatch means a parsing bug. One happened in the reference run: a regex `$Matches` was overwritten and the deprecated rows lost their methods.

## 5. Bootstrap, core and exemplar

1. **Create the repository from the paragon.** Copy `.editorconfig`, `.gitignore` (and add `/temp/`), `.gitattributes` (with `*.cs text eol=crlf`), `LICENSE`, `Publish.ps1`, `CLAUDE.md`, `AGENTS.md`, `coverage.config`, `.codacy.yml`, `global.json`, `version.json` (set the version line), `Directory.Build.props`, `.github/` (`copilot-instructions.md`, `dependabot.yml`, `workflows/codeql.yml` with setup-dotnet, and `workflows/ci.yml`), `CONTRIBUTING.md`, `SECURITY.md` (fix the repository URL), `docs/TESTING.md`, and `docker/Start-*TestInstance.ps1`. Adapt the names throughout. Put the latest stable version of every package in `Directory.Packages.props`.
2. **Create three projects:** `<P>` (library), `<P>.Test` (unit tests, `failSkips: true`) and `<P>.IntegrationTest` (live tests, with a `UserSecretsId`). Also create `<P>.slnx` and an original icon: never a vendor logo.
3. **Build the core, modelled on Splunk.Api:**
   - `<Vendor>Client` (a partial class, with one lazy `field ??= For<I...>()` property per interface, in a partial file per category)
   - options
   - a handler pipeline: authentication with session re-login if relevant, retry, timeouts, read-only guard, namespace or tenant scoping if relevant, and output-format forcing if relevant
   - a content serializer for the wire format
   - tolerant converters, the error mapper, the exceptions, and the shared envelope models

   Apply every item in lessons.md.
4. **Build one exemplar group** end to end. Pick a small, read-only family, such as server info or version, fill its rows, and tell the agents it is done. (interface, model, partial client file, unit test, live test), plus `InventoryTests` (copy Splunk.Api's, setting the operation count), `RequestEncodingTests`, `Support/StubHandler`, `TestClient`, `RecordedCallAssertions`, and the integration fixture (`SplunkFixture` style: user secrets, failing loudly when a setting is missing).
5. **Live test instance:**
   - **Docker:** start the container with a generated password, store the settings in user secrets, never print them, and add a `docker/Start-<Vendor>TestInstance.ps1` script. On Windows many ports are reserved, so pick a high one such as 38089 and check it first.
   - **User-supplied instance (from checkpoint B):** store its settings in user secrets.
   - **No access:** skip this step, and document in `docs/TESTING.md` how to supply settings.
   - **Every case:** pin the TLS certificate by its SHA-256 thumbprint; never disable validation.
6. **Write `docs/IMPLEMENTING.md`.** It is the contract every agent follows: layout, naming, interface shapes, request and response models, tests, live-test safety rules, and code quality. Adapt Splunk.Api's.
7. **Verify, then publish to GitHub:**
   - Run `dotnet format whitespace`, then build with zero warnings and run the unit and live tests.
   - Commit, then run `gh repo create panoramicdata/<P> --public --source . --push`.
   - Run `gh api -X PUT repos/panoramicdata/<P>/private-vulnerability-reporting`.
   - Run `dotnet ../PanoramicData.Skills/.github/skills/codacy/Codacy.cs add-repo --repo panoramicdata/<P>`, then `create-repo-token --repo panoramicdata/<P> --set-github-secret`. Record the token's expiry date.
   - Push, and check CI and CodeQL are green.
8. **Run the governance harness** (assess/Assess.cs) for a baseline.

## 6. Parallel implementation

- **Group the categories** into about 6 agents of roughly equal size (by operation count). A category larger than about 1.5 times the per-agent share is split between agents by path family, even if that takes the count past 6. Each agent's brief lists the exact path prefixes it owns, and it fills only those rows. Add one **core-hardening agent**, which reviews the core and brings it to 100% coverage. Stay under 10 agents.
- **Give each agent its own worktree:** `git worktree add -b feat/<name> ../<P>.wt/<name> main`. Use short paths: Windows path-length limits bite.
- **Brief each agent** with agent-prompts.md §Category, filled in. Launch them all in one message, in the background.
- **While they run:** do the README skeleton, CI checks, and the assessment baseline. When the user asks for progress, read branch commit counts and filled-row counts from the worktrees. Never read the agent transcript files.
- **When an agent reports a cross-cutting finding**, send it to the agent that owns it straight away (SendMessage). Examples: a core bug goes to the core agent; an endpoint family listed in two categories goes to the owner, and the other agent leaves it blank.

## 7. Merge and integrate (per agent, as each reports)

1. `git merge --no-ff feat/<name>`. Resolve conflicts by **combining** both sides' intent: two agents each adding an error shape, for example, means keep both.
2. Expect test-kit clashes. Two kits defining the same extension method made every call ambiguous. Consolidate them into the shared `Support/RecordedCallAssertions`, and tell the remaining agents.
3. Build both test projects, run the unit tests, then measure coverage and keep it at **100% line and branch**. Remove workarounds a core fix made redundant (they show up as uncovered branches).
4. Run the **full live suite**, unless live verification is pending. If it fails, rerun the failing tests in isolation before concluding anything. Investigate repeated transport failures for a real cause: splunkd's keep-alive timeout was one. Revert any speculative fix whose test does not fail without it.
5. Remove the finished categories from `docs/pending-categories.txt`. Commit, push, remove the worktree, and delete the branch.

## 8. Quality gate

- **Run the harness** (assess/Assess.cs). It must read Codacy: the TST-10 coverage figure must match `GET https://app.codacy.com/api/v3/analysis/organizations/gh/panoramicdata/repositories/<P>?branch=main`. If the harness reports the Codacy token absent, or its GitHub token is missing, stop and tell the user which user secret to set. Never judge governance without both.
- **Fix every failing rule.** Expect CQ-05 (issues), CQ-06 (file grades: duplicated property blocks between create, update and content models) and SEC-02. Dispatch the Codacy agent (agent-prompts.md §Codacy) in a worktree. Refactor rather than suppress: no `.codacy.yml` exclusions for source or tests.
- **After pushing,** wait until Codacy's `lastAnalysedCommit` and `lastCommitWithCoverage` both equal HEAD, then re-run the harness.
- **PKG-14 (never published)** clears only after release.

## 9. README and docs

The README has badges (NuGet, MIT, .NET 10, CI, and Codacy grade and coverage, with URLs from the Codacy API `badges` field), an independence and trademark disclaimer, installation, quick start, authentication, scoping, examples per major area, read-only mode, errors and retries, a coverage table and a quality section.

**Compile-check every example:** a file-based app under `temp/` with `#:project`. Fix the missing `using` directives it finds.

Also update `CHANGELOG.md`.

## 10. Checkpoint C and release

- **Ask the form:** decisions found along the way, plus "Publish <P> now?".
- **Trusted publishing** must be configured on nuget.org, and only the user can do it. Unless it is already confirmed, include in the form: "Add a trusted publishing policy on nuget.org (signed in as the account named by `user:` under `NuGet/login` in ci.yml): Account > Trusted Publishing > Add, type GitHub Actions, owner `panoramicdata`, repository `<P>`, workflow `ci.yml`, environment empty."
- **On a yes:** on a clean, synced `main`, run `./Publish.ps1`. The version comes from NBGV height, not your guess.
- **Confirm** the release run's publish job, and that the version appears at `https://api.nuget.org/v3-flatcontainer/<id>/index.json`.
- **Re-run the harness: 91/91 is done.**

## 11. Close

- Write a memory, `<package>-package-state.md`, with the repository, version, token expiry, test instances and lessons, and add its line to MEMORY.md.
- Remove leftover worktrees.
- Add any new generic lesson to this skill's lessons.md. Do not record one-off package facts there.
- Report the final progress table, then the shared Session Close Standard table (from PanoramicData.Skills `copilot-instructions.md`).
- If live verification is pending, the run is complete but must say so in the final table.
