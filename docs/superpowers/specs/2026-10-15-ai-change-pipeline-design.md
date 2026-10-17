# AI changes go through tests and a pull request

## Problem

Fix with AI edits a clone and stops. It never commits, never opens a pull request, and its own messages
say "review the working tree before committing, a model wrote it". The consequences are visible in the
estate:

- Every AI edit leaves a dirty clone. The next Fix on that repository is refused by the clean-tree gate
  (`VerifyCleanWorkingTreeAsync`), so one repository's unreviewed edit blocks everything queued behind it.
- Issue-driven fixes are judged by `IssueFixOracle`, which only checks that the *existing* tests still
  pass. Its own pass message says this is "not proof the reported problem is solved". Nothing proves the
  bug was reproduced, so nothing proves it was fixed.
- Review is a person reading a working tree, which does not scale to an estate and is not evidence.

## Policy

An AI change is accepted on evidence from the tests, not on a person reading it.

1. A reported bug needs a test that **fails before the fix and passes after it**. If no valid red test
   can be written, that is a testing defect: it is reported on the issue and no fix is attempted.
2. The change reaches the repository only as a **pull request**, and **this app merges it** (not GitHub
   auto-merge, so no repository settings are required).
3. The merge needs two gates: the tests pass locally before the PR is opened, and the PR's CI checks pass
   before the app merges it. A repository with no CI checks merges on the local gate alone.
4. The bar is the existing standards (xunit v3, AwesomeAssertions, the TST rules). Where a repository has
   no test project the AI may create one, following written guidelines.

## Scope

In scope: one shared pipeline that every AI task hands its changes to (rule fixes via `FixWithAiRuleAsync`,
issue fixes via `FixWithAiIssueAsync`, and later AI tasks); the GitHub write operations it needs; a local
coverage measurement of changed lines; the test-project guidelines; the console and status reporting.

Out of scope: deterministic remediations (`Fix`) and Dependabot triage, which already apply automatically
and are unchanged; any change to what the rules assess; the Codacy dashboard.

## The pipeline

One `AiChangePipeline` per attempt, run on the repository's own lane so nothing else writes to the clone.

1. **Sync.** Fetch and fast-forward `main`. A clone that is dirty or not on `main` is refused, as today.
2. **Branch.** Create `ai/<rule-or-issue>-<short id>` from `main`. All AI writes happen on it, so `main`
   in the clone is never dirty.
3. **Red (issues only).** The model writes a reproducing test before touching production code. The
   pipeline runs that test alone against the unfixed code and requires it to **fail**, for a reason that
   matches the report (it fails by assertion or expected exception, not by a compile error or a missing
   file). It keeps that failing run's output as evidence. If three attempts produce no valid red test,
   it comments on the issue that the report cannot be reproduced by a test and stops.
4. **Fix.** The model changes code until the red test passes and the whole suite passes.
5. **Local gate.** All of the following hold, or the attempt fails:
   - the build succeeds with warnings as errors, as the repository requires;
   - the full suite passes with no skips (`failSkips` is already required by TST-05);
   - for a code change, the changed lines are covered (see Coverage of changed lines);
   - only paths on the existing allowlist were written, and no more than the existing unattended cap.
6. **Pull request.** Commit, push the branch, open a PR against `main`. Its body carries the issue or rule,
   the red run, the green run and the changed-line coverage. An issue PR says `Fixes #n`.
7. **CI gate.** Poll the PR's check runs. All completed and none failed: merge (squash). Any failed: leave
   the PR open and report which check failed. Still pending after the timeout: leave it open and report
   that it timed out. No checks after a 2-minute grace period: merge on the local gate.
8. **Finish.** Return the clone to `main`, fast-forward it, delete the local and remote branch.

A failed gate at any step restores the clone to `main` and removes the branch, and the console says
which gate failed. A failed CI gate is the one exception: the PR and its branch stay, because the
evidence is on GitHub and the failure is something a person may want to read.

## Rule fixes

Rule fixes go through the same pipeline with two differences. There is no red step, because a governance
rule is already its own specification and is re-checked by the rule itself (`ReEvaluateAsync`), which
replaces the red-then-green requirement. A rule fix that changes only non-code files (for example
instruction-file wording) needs a clean build and a passing suite but no changed-line coverage.

Rules graded remotely (TST-10, CQ-06) cannot be re-evaluated locally. They keep their current local check
(something was written and the build passes) and then the same local and CI gates; Codacy re-grades after
the merge.

## Coverage of changed lines

Existing standards are the source: TST-10 treats 60% line coverage as GREEN. For a code change the
pipeline applies the same 60% threshold to the **changed lines** only, measured locally with the
Microsoft.Testing.Extensions.CodeCoverage collector that TST-04 already requires. Lines that are changed
but not executable (comments, using directives, braces) do not count. A change with no executable lines
passes this gate.

`LocalRepoService.RunTestsAsync` returns only success and output today, so the pipeline adds a coverage
run alongside it rather than changing what existing callers get.

## Test projects the AI may create

Where a repository has no test project, the pipeline lets the model create one. The model is given a
written guide, kept in this repository and included in its prompt, covering: xunit v3 with the
Microsoft.Testing.Platform runner (`OutputType` Exe, `global.json` runner, no VSTest adapter),
AwesomeAssertions rather than FluentAssertions, central package versions, `xunit.runner.json` with
`failSkips`, the coverage collector, and the file layout the TST rules check. The existing TST-01 to
TST-06 and TST-08 rules then grade the result, so a malformed project fails a rule instead of silently
passing.

## GitHub operations

`IGitHubWriteApi` gains: create a pull request, read a pull request's check runs, merge a pull request,
and delete a branch. They sit behind the interface for the same reason `IGitHubIssueApi` does, so the
pipeline is tested against a fake and never needs GitHub. `CreateBranchCommitPushAsync` in
`LocalRepoService`, which nothing calls today, is the starting point for the branch and push steps.

Merging is the one step that cannot be taken back, so it is guarded the way a push already is: the
clone's identity is verified, and the app checks the PR's head is the commit it pushed before merging.

## What changes in existing code

- `IssueFixOracle` and its "not proof" wording are replaced by the red and local gates.
- The "review the working tree before committing" messages in `FixWithAiRuleAsync` and
  `FixWithAiIssueAsync` are replaced by the pipeline's outcome line.
- `IssueAutonomyGate` stays, and still decides whether an issue may be fixed unattended. The red-test
  requirement is added to what an unattended issue fix must satisfy.
- `AiFixSession` keeps its role as the loop that gets the model from task to a candidate change. The
  pipeline wraps it and owns branching, gating, the PR and the merge.
- The clean-tree refusals that AI edits caused disappear, because the clone is never left dirty.

## Failure and visibility

Each stage reports to the item's console and transcript. The outcomes are: merged, left open for failed CI,
left open for CI timeout, no valid red test, local gate failed (naming the gate), and refused (identity,
dirty clone, not on `main`). The work node shows the outcome and, where a PR exists, links to it.

## Testing

The pipeline takes its model, git, test runner, coverage runner and GitHub API as injected ports, as
`AiFixSession` already does. Tests cover each gate passing and failing, a red test that fails for the
wrong reason, a red test that passes (not red), CI pending/failed/absent/passing, a head that moved
before the merge, restore on every failure path, and that a failed gate never leaves the clone dirty or
a branch behind. The live-model integration tests stay opt-in.

## Decisions this spec assumes

These were not settled in discussion and are the places to correct:

1. **Changed-line coverage threshold is 60%**, taken from TST-10's GREEN band. The alternative is "every
   changed executable line is covered".
2. **Squash merge** for every PR.
3. **CI wait timeout of 30 minutes**, after which the PR is left open.
4. **A repository with no CI checks merges on the local gate alone**, as agreed, but the PR body says
   that no CI checks existed.
5. **Rules graded remotely** (TST-10, CQ-06) merge on the local and CI gates, since they cannot be
   re-evaluated locally.
6. **Three attempts** to produce a valid red test before reporting the testing gap, matching the existing
   `MaxAttempts`.

Settled while building, because the code showed the first version of the design was incomplete:

7. **No checks are believed only after a 2-minute grace.** GitHub registers check runs a moment after a
   push, so an empty list straight away is treated as "not yet", and only an empty list that persists
   past the grace period means the repository has no CI.
8. **Test files do not count against the path cap**, so a fix plus its reproducing test fits within it.
9. **Rule fixes are delivered after the model session, not wrapped around it.** The TST-10 coverage mode,
   progress-keeping and test baselines in the rule-fix session are unchanged; once it succeeds, its edits
   go to the pipeline's `DeliverAsync` (branch, gate, pull request, CI, merge). There is no red step: the
   rule is its own specification. Delivery is refused, leaving the edits in place, when the working tree
   holds changes the session did not write, because returning to `main` resets the tree.
10. **Every test run the pipeline makes goes through `TestSandbox`, which is env scrubbing and not a
    boundary.** It removes environment variables whose names look like credentials and redirects
    `APPDATA`. The test process still runs as this user, with this user's filesystem and network, so
    `~/.git-credentials`, `~/.ssh` and similar stores are readable by a test. Builds are not sandboxed at
    all, so an MSBuild `Exec` target in a project file the model edited runs with the full environment.
    That was already true of the rule-fix path; what the pipeline adds is that an issue from a stranger, via
    the brief, can now lead to a model-written test being executed here. Before issue fixes run against
    public repositories, run tests in a container or under a low-privilege account.
11. **No regressions means nothing new broke.** A rule-fix session that measured a baseline (TST-10
    coverage mode, and remote rules whose tests are checked) passes the gate when only those baseline
    failures fail. A session that measured none is strict: a repository whose tests already fail in the
    sandbox fails the local gate with the failing tests named and cannot auto-merge until its baseline is
    fixed. Issue fixes are always strict.
15. **A red test must fail on its own.** Every test the filter matched must have failed, and the run's
    summary must be readable. A filter that also matched passing tests, or that confirms red on somebody
    else's failure, is rejected.
16. **Changed-line coverage counts production code only.** A test file's own lines run by definition and
    would otherwise carry untested production lines over the bar.
17. **The path allowlist and cap see every touched path**, including deleted files, binaries and both sides
    of a rename, taken from `git diff --name-only --no-renames`. The line diff is used only for coverage.
18. **The clone's origin must be the repository the caller named**, checked before anything is created, and
    a failed return to `main` is reported in the console rather than swallowed.
19. **Both of GitHub's CI reports are read**, check runs and legacy commit statuses, so a repository whose
    CI uses only statuses is not mistaken for one with no CI.
20. **A push or pull request that fails is an outcome, not a crash**, and a branch that was pushed with no
    pull request to own it is deleted.
12. **Issue fixes need an existing test project.** The red-test step may add new files in the test folders
    and nothing else. A repository with none gets a project from the TST-01 rule fix, whose prompt carries
    the test guide.
13. **The model's server is held only around calls into the model**, never across the CI wait.
14. **A rule fix may edit `.github/workflows` and merge it after CI.** This is the one place the policy
    trusts model-written workflow files, and CI on the pull request runs the edited workflow with the
    repository's secrets. The model is the local Ollama and the prompt comes from an assessment, not from a
    stranger's text, but it is the decision most worth revisiting.
