# Fix automation for the recurring estate findings

Date: 2026-10-06. Status: design approved in chat, awaiting spec review.

## Intent

Many repositories fail the same cluster of rules (CI-14, CI-15, TST-10, CQ-05, CQ-06, COM-05, PKG-12). Most
of these have no deterministic remediation, so the Fix button hands them to AI, which is expensive.
SECURITY.md graded F by Codacy is the most visible case. Success: Fix resolves as many of these as
possible by editing files or calling `gh`, with AI left only for work that needs judgement.

Stated by the user: every repository should have the standard A-grade SECURITY.md, so it should not vary.

## Findings behind the design

- COM-01 passes any SECURITY.md that exists, except one exact legacy version. A hand-written policy
  (e.g. AlienFx.Api, with a bare support-form URL) passes COM-01 yet grades F, so no remediation fires.
- COM-05, CI-14 and CI-15 advise a fix in prose but register no remediation.
- `DataDrivenRemediation` supports `replace_file_content`, `replace_regex_in_file` and `create_file`.
  `DefaultBranchMainRemediation` (REPO-06) shows the pattern for a remediation that shells out to `gh`.
- The cause of the Codacy issue on AlienFx's SECURITY.md is inferred from its contents; it was not read
  from Codacy.

## Changes, in build order

1. **COM-01 checks conformance.** Fail unless SECURITY.md equals `Standards.GetSecurityMdContent(repo)`
   after normalising line endings and trimming. The advisory carries `replace_file_content`, so no new
   apply code is needed. A repository that needs a different policy uses the existing waiver catalog.
   The now-redundant `LegacySecurityMdContent` branch is removed.
2. **COM-05 remediation.** New `IRemediation` for rule COM-05 that runs
   `gh api -X PUT repos/{repo}/private-vulnerability-reporting`. It must not add to `applied` unless the
   call succeeds, and reports insufficient rights plainly. It changes no files.
3. **CI-14 remediation.** Replace leading tabs in each broken workflow with four spaces, re-parse with
   YamlDotNet, and write only if the result parses. A file that still fails is untouched and stays flagged.
   If it is exactly the generated CodeQL template, replace it with `Standards.CodeQlWorkflowContent`.
4. **CI-15 remediation.** Add the template `coverage` job to the repository's CI workflow. Skip repositories
   that declare `defaultTestingLevel: None`. The plan must first confirm how the existing CI workflow
   remediations edit `ci.yml` and reuse that path.
5. **CQ-05 / CQ-06 on generated files.** Before queueing AI targets, regenerate files this tool owns
   (CLAUDE.md, Publish.ps1) from their templates. SECURITY.md is already covered by step 1.

## Out of scope

- TST-10: cleared by step 4 plus a Codacy project token, which cannot be created by editing files.
  Setting the token with `gh secret set` is a possible later addition.
- PKG-12 (xunit to xunit.v3): a migration, left to AI.

## Testing

Each step gets tests in the style of `GlobalJsonRemediationTests` and `AutoFixRoundTripTests`:
- the rule fails and carries a remediable advisory;
- Apply produces the expected file or call;
- the rule passes after Apply;
- the skip and failure paths do nothing.

Rule-count and registry tests (`RemediationRegistryExclusionTests`, `IssueCentricViewTests`) must keep
passing. Build and test only the affected projects.

## Risks

- Step 1 makes COM-01 fail on every repository whose SECURITY.md differs, then rewrites it on Fix.
  That is intended, but the first estate re-assessment will show a spike in COM-01 failures.
- Step 3 must never write a file it cannot show parses, or it repeats the incident CI-14 was written for.
