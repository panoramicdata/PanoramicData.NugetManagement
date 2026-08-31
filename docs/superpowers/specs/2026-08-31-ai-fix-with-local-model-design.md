# Fix with AI, against a local model

## Problem

Many governance rules have no deterministic remediation. Today their fix is a prompt: the tool builds
one with `CombinedRemediationPromptBuilder`, the user copies it, launches an IDE, and pays a frontier
model to do the work. That is expensive per fix, needs a human in the loop for every one, and does not
scale to an estate.

A local Ollama box — a GX10 at `pdl-rune-02.panoramicdata.com:11434` running `qwen3.8:27b` — can do
much of this work for nothing. The model is far weaker than a frontier one, which is the whole design
constraint: it needs a small task, explicit instructions, tools, and an objective test of whether it
succeeded.

## Scope

In scope: an Ollama-backed agent loop that fixes one rule in one repository, queued as work, gated so
the server is not overloaded; the settings to configure it; a "Fix with AI" action; and opt-in
integration tests that prove the prompts work against real models.

Out of scope: replacing the existing prompt-and-IDE path, which stays for the hard cases; any change to
what the rules assess.

## Two buttons, disjoint scopes

`Fix` remains the only button for deterministic remediation. `Fix with AI` does **only** what `Fix`
cannot: rules that are failing and have no `IRemediation`. Nothing is fixable by both, so there is no
question of which to press — and this does not reopen the one-Fix-button rule, because the two never
overlap.

## Ollama.Api (done first, separately)

`OllamaClientOptions` gained:

- `ApiKey` — sent as a bearer token, optional. Nothing is sent when absent: an empty `Bearer` is not
  the same as no authentication.
- `Timeout` — defaulting to the previous hardcoded 30 minutes; zero or negative is rejected.

Authentication is a `DelegatingHandler` in front of the existing logging handler, which is terminal and
therefore untestable in isolation.

Tools needed nothing: `ChatTool`, `ChatToolFunction`, `ChatToolFunctionParameters` and `ChatToolCall`
already model native tool calling.

## The model is behind a port

`IChatModel` — one method: given a system prompt, a conversation and the available tools, return the
next assistant message (text, or tool calls). `OllamaChatModel` is the only implementation that talks
to a server; everything else in this feature is tested against a scripted fake.

This is the same seam as `IGitHubIssueApi`: the interesting logic is the loop, the tools and the
prompt, and none of that should need a GPU to test.

## The tools

Every path is resolved against the clone's root and rejected — not sanitised — if it escapes.
Rejection is a tool *result* the model can read and correct, not an exception that ends the run.

| Tool | Contract |
|---|---|
| `list_files` | Relative paths under the clone, optional glob. |
| `read_file` | One file, truncated at a byte cap with an explicit marker, so a large file cannot eat the context window. |
| `write_file` | Full replacement of one file. |
| `run_build` | The repository's build; output tail, errors first. |
| `run_tests` | The repository's tests, same shape. |
| `finish` | The model declares itself done, with a one-line summary. |

`run_build` and `run_tests` exist because a weak model does much better when it can see a compiler
error, and they wrap plumbing `DashboardService` already has.

## The loop

`AiFixSession` runs one attempt at one rule in one repository:

1. Build the prompt (below) and send it with the tool definitions.
2. For each tool call, execute it and append the result. Repeat until `finish`, or the turn limit.
3. Re-evaluate **that one rule** against a freshly built `RepositoryContext`.
4. Pass → done. Fail → append the rule's own failure message and start the next attempt, up to the
   attempt limit.

The rule is the specification, so step 3 is a real pass/fail signal that costs nothing to compute. It
is what makes a 27b model usable rather than a gamble.

Every tool call and result is written to the work item's output, so the queue node is the audit trail.
The cancellation token is checked between turns, so Stop works mid-session.

**On final failure the clone's changes are discarded**, as `RevertPartAppliedFixAsync` already does for
a stopped fix. Half-finished edits left in a working tree for someone to unpick by hand are worse than
no attempt; the transcript survives either way.

## The prompt: three layers

1. **System prompt** — fixed, terse, imperative. Tool contracts and the rules of engagement: smallest
   change that satisfies the goal, do not reformat, do not touch files you were not pointed at, read
   before writing, call `finish` only when done. Short sentences, no hedging — written for a small
   model, not for a reader.
2. **Playbook**, where one exists — the goal in one line, the files to touch, the expected end state,
   and a worked before/after. This is where a 27b's success rate comes from. Declared by the rule via
   an opt-in `IRuleAiPlaybook`, discovered exactly as `IGovernsDependency` is.
3. **Instance data** — this repository's `RuleAdvisory.Summary` and `Data`, and the relevant file list.

`Advisory.Detail` is included **only** in the no-playbook fallback. It is prose written for a frontier
model — "adopt the releases within the grace period" — and for a small model it misleads more than it
helps.

A rule with no playbook still works, on layer 3 alone. The integration tests then say which rules
actually pass on a 27b, and playbooks get written where the evidence points.

## Work and gating

`WorkKind.FixWithAiRule`, parameterised by `ruleId`, on a **dedicated AI lane** rather than the
repository's. That serialises AI work naturally, keeps it off the repository lanes while it waits, and
guarantees two AI sessions never share a working tree. The lane count is the configured concurrency,
default **1** — one GX10 does not want twenty concurrent sessions.

## Settings

`OllamaOptions` in `RuntimeSettingsService`, edited under an **Ollama Config** section: base URL, API
key, model, context window (131072), request timeout (300000 ms), max concurrency (1), turns per
attempt, attempts per rule.

The key is stored in the runtime settings file in plain text, beside non-secret preferences. Accepted
deliberately — the common case is a local box with no key at all — but it is a real consideration for a
hosted instance, and the settings UI says so.

## Tests

- **Unit, always run.** A scripted `IChatModel` returning canned tool calls: the loop, the tool
  dispatch, the path scoping, the retry-on-rule-failure, the turn and attempt limits, the revert on
  failure, and the prompt's composition. No server.
- **Path escape**, specifically: `..`, absolute paths, and symlink-ish trickery must be refused as tool
  results.
- **Integration, opt-in** — skipped unless an Ollama URL is configured, exactly as
  `GitHubIntegrationTests` is gated. Copies `Fixtures/PanoramicData.NugetFailArmy` to a temporary
  directory, runs the real loop against the configured model, and asserts the rule now passes. The
  fixture already exists and `FailArmyTests` already asserts every applicable rule fails against it, so
  it is a ready-made before-and-after.
