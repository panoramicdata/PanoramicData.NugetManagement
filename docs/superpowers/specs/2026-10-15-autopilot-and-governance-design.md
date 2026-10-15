# Autopilot: unattended fixing, with a governance record and frontier-model review

Date: 2026-10-15

## Why

A session on 2026-10-14 ran the whole fix loop by hand through the agent API — re-assess, deterministic
fix, Fix with AI, verify, push, watch CI and Codacy, repeat — and it worked: Joker.Api went green, and
AlienFx.Api, LanSweeper.Api and Dell.CloudIq.Api were most of the way. It also showed what an unattended
version has to cope with, because every one of these happened:

- a model "fixing" a test suite by editing integration tests it did not understand;
- a session that spent 45 minutes in a loop on the same failing tests;
- a change that built but broke a test, and one that passed its rule but broke the build;
- tests run with real credentials that printed a live API key into a log;
- a restart that discarded unreviewed work;
- Codacy then grading the model's new tests for duplication, so one fix produced the next finding.

Each was caught because a person was reading the output. Autopilot removes the reader, so the reader's
job has to become something the system does and records: **verify mechanically before acting, keep a
record that can be audited, and have a stronger model judge the weaker one's work after the fact** and
feed what it learns back into the prompts, tools, access and model choice.

## Decisions already made

| Question | Answer |
| --- | --- |
| When does an AI change reach GitHub? | When its own checks pass. Review follows, and can revert. |
| How does frontier review run? | **Pull.** In a Claude Code session the reviewer reads the backlog through the agent API and writes verdicts back. The app holds no Claude API key. |
| What needs a person? | Nothing is excluded. Publishing, secrets, default-branch pushes and CI-workflow edits are each a policy switch, all permitted. |
| Who is the record for? | Internal engineering review. Local, hash-chained, 12-month retention, exportable. |

Because everything is permitted, **the safeguards are caps, a dry-run phase, a kill switch and a circuit
breaker, not prohibitions.** Section 6.

## Scope

In: the autopilot controller; the policy; the ledger; review packets, verdicts and the findings backlog;
a circuit breaker and kill switch; model routing and escalation; the agent-API surface for all of it; a
project skill that runs a review session.

Out: calling a frontier model from inside the app (revisit if pull review proves too slow); a UI for the
ledger beyond a status strip and a kill switch (the data is the product; a browser comes later); changing
what the governance rules measure.

## 1. The ledger

Append-only JSONL under `%LOCALAPPDATA%\PanoramicData.NugetManagement\ledger\`, one file per month, plus a
`head.json` holding the last entry's hash. Each entry carries the SHA-256 of its predecessor, so any edit
or deletion breaks the chain from that point; `GET /agent/ledger/verify` re-walks it.

Every entry has `seq`, `atUtc`, `actor` (`autopilot`, `user`, `claude-review`), `kind`, `repository`,
`ruleId`, `policyHash`, `prevHash`, `hash`. The kinds:

| Kind | What it records |
| --- | --- |
| `cycle_started`, `cycle_ended` | A pass over one repository, and why it ended. |
| `action_planned` | What Autopilot decided to do and why. In dry-run this is all that is written. |
| `ai_session` | Model, model digest, prompt version, tool set, write scope, turns, tokens, wall time, outcome, and a pointer to a **review packet**. |
| `verification` | Build, tests against baseline, coverage before and after, and the verdict. |
| `push` | Commit SHA, branch, files, diff hash, and the checks it was gated on. |
| `ci_result`, `codacy_result` | What happened to the pushed commit, minutes later. |
| `revert` | A revert commit, and the review verdict or breaker trip that caused it. |
| `publish` | Package, version, commit, and the checks it was gated on. |
| `secret_written` | The secret's **name** and repository. Never the value. |
| `policy_changed`, `breaker_tripped`, `breaker_reset`, `kill_switch` | Control-plane events, each with who and why. |
| `review_verdict` | A frontier review of one earlier action (section 4). |

**Review packets** are what make a 12-month record useful. Session logs expire after 30 days, and a
review a year later needs the evidence. A packet is stored as a compressed blob beside the ledger and
holds the prompt and task, the tool calls with arguments in order, the model's text, tool results cut to
a sensible size, the final diff, every check's output, and the later CI and Codacy outcomes. Packets are
scrubbed for credential-shaped strings before they are written; a packet that cannot be scrubbed is
refused.

Retention deletes whole months only, after export, and writes a `retention_applied` entry.

## 2. The policy

A JSON file, `autopilot-policy.json`, edited through the agent API or by hand, hashed into every ledger
entry so an action can be traced to the rules in force when it was taken. Changing it writes
`policy_changed`.

```
enabled, dryRun
schedule:    { windowsUtc: ["08:00-20:00"], cycleCooldownMinutes, maxConcurrentRepositories: 1 }
eligibility: { organisations, includeRepositories, excludeRepositories, requireCleanTree: true, skipArchived: true }
rules:       { include: [...], exclude: [...] }               // e.g. TST-10 only at first
caps:        { pushesPerHour, pushesPerDay, publishesPerDay, aiSessionsPerHour, maxFilesPerChange, maxLinesPerChange }
actions:     { push: true, pushDefaultBranch: true, publish: true, writeSecrets: true, editWorkflows: true, editPublishScript: true }
breaker:     { consecutiveFailures, ciRedAfterPush: "revert+quarantine", negativeReviewRate: { window: 20, threshold: 0.25 } }
routing:     { default: "qwen3.8:27b", byRule: { "TST-10": { model: ..., budgetMinutes: 25, escalateTo: ... } } }
```

`requireCleanTree` is not optional in effect: Autopilot never starts on a clone with uncommitted changes,
because the first thing the old restart cleanup and a failed session both did was discard exactly that.

## 3. The Autopilot controller

A hosted service. One **cycle** takes one repository through the same steps the manual loop used, each
one a ledger entry:

1. **Gate.** Policy enabled, inside the window, breaker closed, caps not reached, repository eligible, tree clean.
2. **Assess.** Re-assess, with a fresh GitHub context (paced: the secondary rate limit was hit by an
   unpaced burst, and the controller backs off on 403).
3. **Plan.** Rules to act on, in order: deterministic remediations first, then AI candidates, minus
   anything the policy excludes and anything whose `fix` route is `outside-tree` or `remote` with no
   oracle. Written as `action_planned`. In dry-run the cycle ends here.
4. **Deterministic fix.** `fix-all`. Verify. Push.
5. **AI fix.** Per candidate, via the existing session with the routed model. Its own oracle decides
   success: the rule, the build, new test failures against the pre-session baseline, local coverage.
6. **Verify the whole change** in the sandbox (no secrets, CI's environment) before any push.
7. **Push** only if every check passed and the change is inside `maxFilesPerChange` and
   `maxLinesPerChange`. Directly or by branch, per policy.
8. **Wait and observe.** Poll CI by GraphQL, then re-assess until Codacy has re-graded, with a timeout. A red
   CI on the pushed commit triggers the policy's response (default: revert and quarantine the repository).
9. **Follow up.** The model's own output creates the next findings (new duplication, new style issues), so a
   repository gets a bounded number of passes before it is left for review.
10. **Publish**, if the policy permits and the repository is green, CI is green at HEAD, the version is not
    already on nuget.org, and a soak period has passed since the last push.

State is persisted, so a restart resumes at a step instead of repeating it. A restart with a cycle in
flight records `cycle_interrupted` and **does not** reset the clone: Autopilot's own interrupted work is
reverted only by its own undo, and only of files it wrote.

## 4. Frontier review (pull)

The reviewer is a Claude Code session, run on request. The app provides:

| Endpoint | Purpose |
| --- | --- |
| `GET /agent/review/queue` | Unreviewed ledger actions, newest first, with a one-line summary each. |
| `GET /agent/review/packet/{seq}` | The packet for one action, including the diff and later outcomes. |
| `POST /agent/review/{seq}` | Writes a verdict (below). |
| `GET /agent/review/summary` | Rates by rule, model and prompt version: success, regression, revert, negative review. |
| `GET/POST /agent/backlog` | The improvement backlog the reviewer maintains. |
| `GET /agent/ledger/verify` | Chain integrity. |

A **verdict** records what the reviewer actually judged, so it can be aggregated and argued with:

- *Suitability*: was this a job for the model it was given (`fit` / `marginal` / `unsuitable`) and why.
- *Correctness*: `correct`, `partially`, `incorrect`, plus whether it would have been merged by a person.
- *Quality*: minimality of change, fidelity to the repo's conventions, any weakened or deleted tests,
  any change outside what the task asked for, any security concern.
- *Cause*, when it went wrong or was inefficient, drawn from a fixed set: `prompt`, `playbook`, `tool`,
  `oracle`, `access`, `model`, `rule-ambiguity`, `repo-specific`, `environment`.
- *Disposition*: `none`, `revert`, `quarantine-repository`, `pause-rule`, `escalate-model`.

A disposition other than `none` is carried out by Autopilot on its next cycle and recorded as its own entry,
so a review never edits a repository directly.

The reviewer's standing questions, which the skill puts at the top of every session:

1. Did the change do what the rule asked and nothing else?
2. Was the oracle strong enough? Name a wrong change it would have passed.
3. Would a person have been comfortable merging this without reading it?
4. What single change to the prompt, tools, access or model would have made this better or cheaper?
5. Is this *kind* of job one Qwen should be doing at all, or should it be deterministic code, a
   different model, or left to a person?

A **project skill** (`.claude/skills/autopilot-review`) carries the procedure, the checklist and the verdict
schema, so any session can run it and produce the same shape of output.

## 5. Findings, routing and escalation

Verdicts roll up into the **backlog**: items of kind `prompt`, `playbook`, `tool`, `oracle`, `access`,
`model` or `new-deterministic-fix`, each with evidence (ledger sequence numbers), a status and an owner. This
is where "newer models would do better here" lives, and it is concrete: *rule X, 11 sessions, 3 correct, all
8 failures were the model not holding the structure of a 900-line file in context.*

**Routing** is policy. Each rule class names a model, a time budget and an escalation target. Escalation
fires on repeated failure of the same rule, a `model`-caused negative review, or a reviewer `escalate-model`
disposition. Candidates are whatever the Ollama server serves, to begin with. A hosted model is a routing
target like any other, behind an explicit per-day spend cap, and every call is a ledger entry; it is off
until the policy names one.

## 6. Safeguards (because nothing is prohibited)

- **Dry-run first.** `dryRun: true` runs the controller through step 3 for every repository and writes the
  plan. It costs nothing and is what the first review sessions read.
- **Staged enablement.** The recommended order is deterministic fixes only, then AI on one rule, then AI on
  all rules, then secrets, then workflow edits, then publish. Each is a policy change, so each is a ledger
  entry and each can be reversed by flipping it back.
- **Caps.** Pushes and publishes per hour and per day, and a size cap on any one change.
- **Kill switch.** `POST /agent/autopilot/stop`, a button, and a file flag. It stops at the next safe point
  and says so in the ledger. Nothing it does is half-applied past a step boundary.
- **Circuit breaker.** Trips on consecutive failures, a red CI after a push, a run of negative reviews, a
  rate-limit storm, or an unreachable model server. A tripped breaker stops *new* cycles and leaves running
  ones to finish. It resets only by a person or an explicit review disposition.
- **Quarantine.** A repository that has been reverted is skipped until a reviewer clears it.
- **Publish is conditional, not just permitted.** Green, CI green at HEAD, version unpublished, soak period
  elapsed, and under the daily cap. A version on nuget.org can only be unlisted, so this is the action the
  caps are tightest on.
- **Secrets.** Written through the existing credential tooling; the ledger records the name and repository.
  The agent token and any credential never enter a packet.

## 7. Build order

Each phase ships and is useful on its own.

1. **Ledger, policy and packets.** Writer, hash chain, verify, retention, scrubber. The existing session
   logging and manual pushes start writing to it. No autopilot yet; the record exists first.
2. **Review surface.** Queue, packet, verdict and summary endpoints, the backlog, and the project skill.
   The first reviews are of work already done by hand.
3. **Dry-run controller.** Gate, assess, plan, persist state. Reads and records, writes nothing.
4. **Deterministic live.** Steps 4, 6, 7 and 8 for remediations only, with caps, kill switch and breaker.
5. **AI live.** Step 5 with routing, the review-driven dispositions, and the follow-up pass limit.
6. **Secrets, workflows, publish.** Gated as in section 6.
7. **Escalation.** Routing to a second model once the backlog shows where it would pay.

## 8. Testing

- Ledger: chain verifies; editing, deleting or reordering any entry fails verification at that point;
  retention removes whole months only; a packet containing a key-shaped string is refused.
- Policy: each switch blocks exactly its action; a cap stops the cycle at the cap, not after; a policy change
  is hashed into every later entry.
- Controller, against a fake model and a fake GitHub: a failed check never pushes; a red CI reverts; a dirty
  tree is skipped; a restart resumes without repeating a push; the kill switch stops at a step boundary;
  dry-run changes nothing on disk or on the remote.
- Review: a verdict with a disposition is executed once and recorded; an unreviewed backlog is ordered
  newest first; summaries count what the verdicts say.

## Risks

- **Unreviewed autonomy compounds.** Pushing first and reviewing after means an early mistake can be copied
  across repositories before anyone reads it. The breaker's negative-review window and the caps bound that;
  the dry-run phase and staged enablement are the main defence.
- **The oracle is the ceiling.** A change is only as good as the check that let it through. The reviewer's
  question 2 exists to find checks that would pass a wrong change, and every answer becomes an `oracle`
  backlog item.
- **A pull review can lag.** If nobody opens a session, verdicts stop and the review-rate breaker should
  eventually pause Autopilot for want of oversight. That trade is deliberate and the threshold is policy.
- **Publishing and secrets are irreversible or sensitive.** They are permitted, as decided, and capped.
- **Ledger growth.** Packets dominate. Cut tool results and compress; measure after phase 1.

## Open questions

1. Starting limits: how many pushes per day, and how many publishes per day, should the first live phase allow?
2. Should Autopilot ever act outside working hours, or stay inside a window you set?
3. Which repositories are in scope to start: every governed, cloned, non-excluded one, or an allowlist you name?
4. On a negative review that says `revert`: revert immediately on the next cycle (default), or only after you
   confirm?
5. Is a hosted model acceptable as an escalation target at all, and if so with what daily spend cap?
