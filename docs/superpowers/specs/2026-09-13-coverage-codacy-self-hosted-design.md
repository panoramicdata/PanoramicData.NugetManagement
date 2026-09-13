# Code coverage to Codacy, on the self-hosted runner, graded RED/AMBER/BLUE/GREEN

Date: 2026-09-13

## Problem

Three separate gaps, which turn out to be one problem.

1. **CI does not measure coverage.** [ci.yml](../../../.github/workflows/ci.yml) restores, builds, packs and
   publishes. It never runs the tests, let alone collects coverage. `Run-Coverage.ps1` exists and works,
   but only when a human remembers to run it.

2. **The estate-wide coverage rule is inert.** `CodeCoverageTrendRule` (TST-07) reads
   `RepositoryContext.LineCoveragePercent`. That property is never assigned anywhere in production code —
   only in `CoverageBaselineTests`. So TST-07 has always returned "No coverage has been measured" for
   every repository that has ever been assessed. The ratchet it describes has never ratcheted.

3. **GitHub-hosted minutes are expensive.** The organisation runs its own Actions Runner Controller scale
   set, `pdl-prod-cluster`, which is already used by the Magic Suite bake workflows. CI here does not use it.

Fixing (1) without (2) produces a number nobody reads. Fixing (2) without a source of truth for other
repositories produces a rule that grades this repository and shrugs at the rest. Codacy is the join:
every repository uploads coverage to it from CI, and the assessor reads it back for the whole estate.

## Goals

- CI runs on `pdl-prod-cluster`, not on GitHub-hosted runners.
- Every push and PR produces a Cobertura coverage report and uploads it to Codacy.
- The assessor reads coverage back from Codacy and grades every repository on a four-band scale.
- The bands are deliberately undemanding. The estate is nowhere near a figure worth enforcing, and a
  gate set where it would actually bite would bury every other finding.

## Non-goals

- Failing any build on coverage. CI reports; the assessor grades. Nothing blocks.
- Branch coverage bands. Line coverage only, for now; TST-07 already watches both for regressions.
- Per-repository threshold overrides. One scale for the estate until there is evidence one is needed.

## The bands

| Band | Condition | `AssessmentSeverity` | Badge class |
|---|---|---|---|
| RED | No coverage figure available, or 0% | `Error` | `badge-fail` |
| AMBER | Above 0% and below 25% | `Warning` | `badge-warn` |
| BLUE | At least 25% and below 50% | `Info` | `badge-info` |
| GREEN | At least 50% | (passes) | green |

The scale needs no new enum. `Home.razor` already maps `Error`/`Critical` to red, `Warning` to amber and
everything else to blue, and `DashboardService.SeverityRank` already orders the fix list red, amber, blue.
The four bands are the colours the dashboard has always drawn; this rule is the first thing to use all of
them deliberately.

"No coverage figure available" and "0%" are the same band on purpose. A repository that has never been
measured and a repository measured at nothing have both demonstrated the same thing.

A repository with no test projects is not applicable, not RED. The absence of tests is TST-01's finding,
and reporting it twice makes the fix list longer without making it more informative.

## Design

### 1. CI on the self-hosted runner

[ci.yml](../../../.github/workflows/ci.yml): every job moves to `runs-on: [pdl-prod-cluster]`, with the
environment block the Magic Suite workflows use:

```yaml
env:
  DOTNET_NOLOGO: 1
  DOTNET_CLI_TELEMETRY_OPTOUT: 1
  NUGET_XMLDOC_MODE: skip
  FORCE_JAVASCRIPT_ACTIONS_TO_NODE24: true
```

`FORCE_JAVASCRIPT_ACTIONS_TO_NODE24` is not optional: the `actions-runner` image the scale set runs does
not offer the Node version several of these actions request by default.

The runner image mounts a NuGet package cache from a hostPath and points `NUGET_PACKAGES` at it, so
restores across the three jobs share a warm cache without any `actions/cache` wiring.

**Risk, accepted on instruction:** the `publish` job uses NuGet Trusted Publishing, which needs a GitHub
OIDC token (`permissions: id-token: write`). ARC runners do receive OIDC tokens, so this is expected to
work, but it is unproven on this scale set and only exercises on a tag push. If the first release after
this change fails to publish, this is the first place to look; reverting that single job to
`ubuntu-latest` is the fix.

### 2. A coverage job

A new `coverage` job, parallel to `build` and depending on nothing:

1. `actions/checkout@v7` with `fetch-depth: 0`.
2. `actions/setup-dotnet@v6` for 10.0.x.
3. `dotnet tool install --global PowerShell` — the `actions-runner` image has no `pwsh`, and installing it
   through the SDK needs no root and no apt.
4. `pwsh ./Run-Coverage.ps1`.
5. Upload `coverage.cobertura.xml` to Codacy via `codacy/codacy-coverage-reporter-action@v1`, with
   `continue-on-error: true`.
6. Upload the same file as a workflow artifact, so a Codacy outage does not lose the measurement.

The job never fails on a coverage figure. `Run-Coverage.ps1` already defaults `MinimumLineCoverage` to 0
and CI passes nothing, so the only way this job goes red is a genuine test failure or a collector that
produced no file at all — both of which are real defects and should go red.

### 3. `Run-Coverage.ps1` becomes cross-platform

The script hardcodes `PanoramicData.NugetManagement.Test.exe`. On the Linux runner the executable has no
extension. The script gains the extension only on Windows.

This is deliberately a fix to the existing script rather than a second, CI-only copy of the same three
commands. The script's own header claims it is "the same invocation CI uses"; that claim has been false
since it was written, and duplicating the invocation in YAML would keep it false.

### 4. `CodacyCoverageService`

New, in `PanoramicData.NugetManagement/Services`. Wraps the `Codacy.Api` client's coverage endpoint and
answers one question: what line coverage does Codacy hold for `gh/panoramicdata/{repo}`?

`Codacy.Api` 3.0.45 already exposes this (`CoveragePercentageWithDecimals` on the coverage response), so
this is a wrapper over an existing typed client, not a new HTTP surface.

It follows `CodacyRepositoryLookup`'s shape exactly, including its most important property: only a 404 is
translated into "no answer" (via the existing `CodacyNotFound` matcher). Every other exception propagates.
An unreachable Codacy must never be reported as RED — that is the same mistake `CodacyRepositoryLookup`'s
remarks already warn about, and grading a repository red because of a network blip would be worse here,
because the AI fixer acts on red.

Returns `double?`: null means Codacy holds no coverage for this repository.

### 5. Populating `RepositoryContext.LineCoveragePercent`

`OrganizationAssessor` sets `LineCoveragePercent` from `CodacyCoverageService` when a Codacy token is
configured, alongside the existing Codacy wiring at `OrganizationAssessor.cs:99-110`.

This is the change that also brings TST-07 to life. Once coverage arrives, the ratchet rule starts
recording per-repository floors for the first time, and its `coverage-baselines` catalogue begins to fill.
That is a behaviour change to an existing rule and should be expected in the first assessment run after
this ships: every repository with coverage records a first baseline and passes, and drops are caught from
the run after that.

### 6. `CodeCoverageBandRule` (TST-10)

New rule in `Rules/Testing`, category `Testing`.

Not applicable when `context.FindTestProjectFiles()` is empty. Otherwise it bands
`context.LineCoveragePercent` per the table above.

One framework wrinkle: `RuleBase.Fail()` stamps the rule's fixed `Severity` onto the result, and this rule
needs a severity that varies per result. It therefore constructs `RuleResult` directly. `IRule.Severity`
remains `Error`, the worst case, so rule listings and filters that read the declared severity still sort
it sensibly.

Its `RuleAdvisory` carries, in `Data`, the measured figure and the band, and its `Detail` points at the
coverage job in this repository's `ci.yml` as the reference implementation. That matters: the advisory is
what the AI fixer acts on, and "copy this job into your workflow" is a fix an agent can actually perform,
where "increase coverage" is not.

## Testing

TDD, tests first.

`CodeCoverageBandRuleTests`:
- Null coverage with test projects present gives RED.
- 0% gives RED.
- The boundaries, each side: 0.1, 24.9, 25, 49.9, 50.
- No test projects gives not-applicable, whatever the coverage figure.
- The emitted `Severity` is asserted per band, since the whole colour scheme depends on it.
- The advisory is populated for every failing band.

`CodacyCoverageServiceTests`, against a stubbed handler:
- A coverage response yields the percentage.
- A 404 yields null.
- Any other failure propagates rather than yielding null.

## Configuration and secrets

- `CODACY_PROJECT_TOKEN` — a repository Actions secret, used only by the coverage job's upload step.
- `AssessmentOptions:CodacyApiToken` — the account-level token the assessor already looks for, stored in
  the Web project's `dotnet user-secrets` (`UserSecretsId` is `PanoramicData.NugetManagement.Web`). It is
  currently unset, which is a second reason the Codacy rules have been quiet.

## Rollout

This repository is the reference implementation. Once its coverage job is green and Codacy shows a figure,
TST-10 grades the rest of the estate, and every repository it marks RED has an advisory describing exactly
the job to copy.
