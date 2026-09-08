# AI fix sessions: honest state, retained history, and per-turn timings

Date: 2026-09-08

## Why

Seven `Fix CQ-06 with AI` items were queued against `panoramicdata/Cherwell.Api`. The selected one
showed **State: Running**, an empty transcript, and `⏳ Waiting for the model (qwen3.8:27b)...`, with
nothing streaming for minutes. The feature looked broken or intolerably slow. It was neither, and the
UI could not say which.

Measurements against `pdl-rune-02` established what is actually true.

| Measurement | Value |
| --- | --- |
| Model residency | 17.9 GB, `size_vram == size` — fully on GPU, no CPU offload |
| Prompt evaluation | 811 tokens in 1.31 s (~620 tok/s) |
| Generation | 42 tokens in 1.24 s (**34 tok/s**) |
| GPU work for one realistic turn | **2.55 s** |
| `total_duration` for that same turn | **310.97 s** |

So 308 of 311 seconds were spent queued, not computing. A probe against `llama3.2` answered
immediately, which rules out a saturated box: the queue is **per model**, on `qwen3.8:27b`. The app
holds one connection to `:11434`; VS Code holds two.

Three distinct causes, none of them visible in the UI:

1. **The watched item was not talking to the model at all.** `WorkExecutors.FixWithAiRuleAsync` emits
   `⏳ Waiting for the model` *before* `await ollamaGate.EnterAsync(...)`, while `State` is already
   `Running`. The message means "waiting for a permit", not "waiting for a response". The transcript
   was empty because no request had been sent.
2. **`LaneKey` is `repo:<full name>`.** All seven items shared a lane, so the repository lane
   serialised them regardless of the gate. `MaxConcurrency` was not the binding constraint.
3. **Output tokens dominate.** `write_file` requires the model to re-emit an entire file as a tool
   argument. At 34 tok/s a 4,000-token C# file is roughly two minutes of generation for one turn, and
   the loop permits 12 turns across 3 attempts.

Additionally, a finished session leaves no trace: `WorkLaneService.Complete` removes the item from its
lane, so the node and its transcript disappear at the moment they become worth reading.

## Scope

In scope: work-item state honesty, retained completed sessions with outcome colour, manual delete,
per-turn timings, a patch-style edit tool, and a raised concurrency default.

Out of scope: parallelising AI fixes *within* one repository (would need a git worktree per session,
with merge conflicts between sessions editing the same file), batching all of a repository's rules
into one conversation, and persisting transcripts across restarts.

## Design

### 1. Session history, held apart from the lanes

`lane.Items` is load-bearing well beyond the tree. It feeds `WorkflowGate.FirstBlockedStep`
(`Home.razor:4181`), toolbar enablement (`Home.razor:5393`), the `Work (n)` badge
(`NavTreeDataProvider.cs:864`), and several "is this repository busy" checks. Retaining finished items
*in the lane* would silently change every one of those: the toolbar would stay disabled after work
completed, and the badge would count history.

So `Complete()` keeps removing the item from `lane.Items` exactly as it does now, and additionally
pushes it into a history collection keyed by lane. Existing consumers are untouched by construction.

`WorkItem` gains three stamps:

- `QueuedAtUtc` — set at enqueue.
- `StartedAtUtc` — set when the lane starts it.
- `CompletedAtUtc` — set by `Complete()`.

`WorkLaneService` gains:

- `HistoryFor(laneKey)` — completed items, newest first.
- `RemoveCompleted(id)` — backs delete-removes-node.
- A sweeper that drops history entries older than a single retention setting, plus a per-lane cap so
  an estate-wide sweep cannot grow without bound.

Concretely: `CompletedRetention` defaults to 15 minutes and the per-lane cap to 50 items, oldest
dropped first. Both live on the runtime settings so they can be changed without a restart.

**One clock for every outcome.** A green and a red expire on the same timer. This was chosen over
outcome-dependent expiry for predictability: one setting to explain, one to configure. Manual delete
removes any node immediately, whatever its age.

Transcripts remain in memory and are not persisted. `WorkTranscript`'s existing decision stands —
a transcript belongs to a run, and `PersistedWorkItem` restores work to be done rather than work
already narrated.

### 2. Queued and running told apart

`WorkItem` gains `WaitReason`: set while the item is blocked in `OllamaGate`, cleared once the permit
is held. The existing `Say("⏳ Waiting for the model")` moves to setting this rather than writing a
line that outlives the wait.

An item is therefore *grey* when it is `Pending` in its lane **or** `Running` with a `WaitReason` —
that is, holding a lane but blocked on the model. It is *blue* only when genuinely mid-turn. Six of
the seven Cherwell items then read as queued instead of all claiming to run.

`WaitReason` is a string rather than a new `WorkItemState` because the item genuinely *is* running:
it holds its lane, and its cancellation token is live. What is blocked is the model, not the item.
Adding a state would force every `State == Running` test in the codebase to be re-examined.

### 3. Outcome colour

| Colour | Condition |
| --- | --- |
| Grey | `Pending`, or `Running` with a `WaitReason` |
| Blue | `Running` |
| Green | `Completed` and `Succeeded != false` |
| Amber | `Completed` and `Succeeded == false`, or `Cancelled` |
| Red | `Failed` |

`WorkItem.Succeeded` already exists precisely to separate "ran without throwing" from "did the thing
it was asked to do", so amber costs no new plumbing.

### 4. Per-turn timings

Ollama returns `prompt_eval_count`, `prompt_eval_duration`, `eval_count`, `eval_duration`,
`load_duration` and `total_duration` on the final streamed chunk. `AiModelTurn` gains an optional
metrics record and `OllamaChatModel` populates it.

The derived term is the valuable one:

```
waited = total_duration - load_duration - prompt_eval_duration - eval_duration
```

That is time queued at the server behind another caller. It was 308 of 311 seconds in the measurement
above and had no representation anywhere in the product.

`AiFixSession` logs one line per turn, plus a timed line per tool call and per rule re-check:

```
⏱ turn 3: waited 12.4s · prompt 3,214 tok in 5.2s · generated 412 tok in 12.1s (34 tok/s)
   → replace_in_file SaveLinkAttachmentRequest.cs (0.3s)
↻ CQ-06 still fails (re-check 1.8s)
```

The running node shows elapsed time. **This must re-render the node template against live state and
must not reload the tree** — reloading on a progress path is the known cause of tree flicker.

### 5. `replace_in_file`

A new tool beside `write_file`, not replacing it.

- Arguments: `path`, `old_text`, `new_text`.
- `old_text` must match the file exactly once. Zero matches or more than one is an error that names
  the problem and tells the model to widen the match or fall back to `write_file`.

A one-line "seal this class" fix costs roughly 50 output tokens instead of roughly 4,000 — at 34
tok/s, about a second and a half instead of about two minutes. `write_file` stays as the escape hatch
because small models miss exact matches on whitespace, and a failed patch should cost one turn rather
than the session.

The existing test holding `AiFixSession.ToolSpecs` and `AiFixToolbox` together covers the new tool: a
tool described but not executed wastes a turn, and one executed but not described is never called.

### 6. Concurrency default

`OllamaOptions.MaxConcurrency` default moves from 1 to 3. This buys **cross-repository** parallelism
only — same-repository AI fixes remain serialised by their shared lane, which is what keeps two
sessions off one working tree. The settings UI already exposes the value.

At 2.5 s of GPU per turn, one session leaves the box substantially idle. Three is chosen as
comfortably within capacity while leaving headroom for the other consumer already on that model's
queue.

## Testing

- `WorkLaneService`: completion moves an item to history and out of the lane; `lane.Items` counts and
  lane emptiness are unchanged by history; expiry drops aged entries; the per-lane cap drops oldest
  first; `RemoveCompleted` removes one node and leaves its siblings.
- State-to-colour mapping across all six `WorkItemState` values, both `Succeeded` values, and
  `WaitReason` set and unset.
- `AiFixToolbox.replace_in_file`: exact single match applies; zero matches errors; multiple matches
  errors without writing.
- `ToolSpecs`/toolbox parity (existing test, extended).
- Timing line formatting, including a turn where the server returns no metrics.
- `WorkExecutors`: `WaitReason` is set before the gate and cleared after it.

## Risks

- `Home.razor` is over 5,600 lines. A Razor comment placed inside a tag compiles cleanly and throws at
  render, so `.razor` changes need an actual render check — a green build does not prove one works.
- The elapsed-time tick on a running node is the flicker path. Re-render the node template; never
  reload the tree.
- `RuntimeSettingsService` builds its save snapshot by hand. A new setting that is not added to that
  snapshot is erased on every save, which presents as a value that will not stick. `CompletedRetention`
  and the per-lane cap must be added there.
- Raising `MaxConcurrency` increases pressure on a model queue already shared with another consumer.
  The per-turn `waited` figure is what makes that visible, so it should land before or with the
  concurrency change.
