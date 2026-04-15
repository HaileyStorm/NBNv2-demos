# Basics

`Basics` is the first real External World demo in this repo. It defines the shared environment contract that later task plugins and the future UI will build on for the first `2 -> 2` NBN curriculum.

## Current Scope

- A shared environment library in `src/Basics.Environment`.
- A task-plugin library in `src/Basics.Tasks`.
- Shared behavior-occupancy scoring primitives from the repo-level `src/Nbn.Demos.Behavior` library.
- A small Avalonia desktop UI in `src/Basics.Ui`, including target accuracy/fitness stop controls and winning-artifact export (`.nbn` and, when available, `.nbs`).
- A headless live-trial harness in `src/Basics.Harness`.
- A test project in `tests/Basics.Environment.Tests`.
- A task-plugin test project in `tests/Basics.Tasks.Tests`.
- A template-anchored seed-population contract: initial brains are exact-template or bounded minor deviations, not unconstrained random topologies.
- IO-only runtime plumbing for environment work. Capacity sizing is intended to come through IO, not direct SettingsMonitor calls by the demo.
- Implemented deterministic task plugins for `AND`, `OR`, `XOR`, `GT`, and `Multiplication`, all wired through the shared registry for the UI and harness.

## Design Direction

- Brain geometry is fixed at `2` inputs and `2` outputs.
- `output[0]` carries the task value and `output[1]` carries the shared ready bit.
- Per-sample scoring only accepts the first tick whose ready bit is asserted inside the configured ready-window; if no ready tick arrives in time, the sample fails evaluation.
- Seed populations are organized around a template family so reproduction/speciation starts from coherent parents instead of arbitrary unrelated brains.
- Reproduction defaults keep `protect_io_region_neuron_counts=true`.
- Parent selection and child-run allocation are part of the shared environment contract:
  - progress pressure favors fitter brains
  - diversity pressure avoids collapsing to one lineage too early
  - species-balance pressure prevents one bootstrap family from monopolizing the run budget
  - run counts stay bounded by IO-reported capacity recommendations and any explicit overrides
- Behavior occupancy is a permanent auxiliary demo-side signal: task evaluations can measure output/state occupancy, transition entropy, ready-timing entropy, and input-conditioned response diversity through the shared `Nbn.Demos.Behavior` library, then apply task-specific viability gates and selection pressure.
- Shared metrics expected by the future UI include accuracy, best/mean fitness, population count, active brains, species count, reproduction activity, and capacity utilization.

## Implemented Task Contracts

All implemented Basics tasks use the same `2 -> 2` geometry, require tick-aligned evaluation, validate finite sample and observation values, and reject any dataset that drifts from the plugin's canonical deterministic set before awarding fitness.

### Boolean truth-table tasks

- `AND`, `OR`, and `XOR` use canonical boolean inputs `a,b in {0,1}` and normalized boolean output `y in {0,1}`.
- Each plugin evaluates the full deterministic four-row truth table in fixed order: `00`, `01`, `10`, `11`.
- The task value is still read from `output[0]`; `output[1]` is only the readiness signal.
- Boolean scoring exposes shared keys `task_accuracy`, `mean_absolute_error`, `mean_squared_error`, `target_proximity_fitness`, and `dataset_coverage`, plus boolean-specific keys `classification_accuracy`, `negative_mean_output`, `positive_mean_gap`, and `truth_table_coverage`.

### GT (`a > b`)

- `GT` uses bounded scalar inputs `a,b in {0.0, 0.5, 1.0}` and normalized boolean output `y in {0,1}` where `1` means `a > b` and equality scores `0`.
- The deterministic comparison dataset is the full `3 x 3` grid over those bounded scalar inputs, including ties.
- The task value is still read from `output[0]`; `output[1]` is only the readiness signal.
- Scoring uses the same shared boolean breakdown contract as the truth-table tasks, with `comparison_set_coverage` as the task-specific coverage alias.

### Multiplication

- `Multiplication` defaults to bounded scalar inputs on an evenly spaced 7x7 grid in `[0,1]`, with all interior samples and a deterministic boundary subset.
- The deterministic evaluation set is stratified from that grid: all interior points are kept, while boundary samples where `a` or `b` is `0`/`1` are deterministically capped so edge cases do not outnumber interior cases on small grids.
- The expected output is the normalized product `a * b`. Because the input domain is already bounded to `[0,1]`, no extra remapping is applied.
- Accuracy is tolerance-based for this task: a sample counts as correct when the observed output is within the configured tolerance, defaulting to `+/-0.03`, of the canonical product target.
- Multiplication now keeps raw overall tolerance accuracy in the primary `task_accuracy`/`tolerance_accuracy` fields, but also reports `edge_tolerance_accuracy`, `interior_tolerance_accuracy`, and `balanced_tolerance_accuracy`. Multiplication fitness uses a moderately interior-biased balanced view plus an edge/interior agreement signal so `min(a,b)`-style edge memorization does not masquerade as real multiplication progress while edge recovery still matters.
- Multiplication applies the shared behavior-occupancy metrics as a small staged auxiliary pressure. The signal is gated by ready confidence, target proximity, and the configured balanced-accuracy ramp so noisy or not-ready output diversity does not outrank meaningful task progress.
- The task value is still read from `output[0]`; `output[1]` is only the readiness signal.
- Shared breakdown keys remain `task_accuracy`, `mean_absolute_error`, `mean_squared_error`, `target_proximity_fitness`, and `dataset_coverage`; behavior keys are `behavior_output_entropy`, `behavior_transition_entropy`, `behavior_state_occupancy`, `behavior_ready_timing_entropy`, `behavior_response_diversity`, `behavior_occupancy_signal`, `behavior_auxiliary_fitness`, `behavior_stage_gate`, and `behavior_selection_signal`; task-specific regression keys are `tolerance_accuracy`, `edge_tolerance_accuracy`, `interior_tolerance_accuracy`, `balanced_tolerance_accuracy`, `zero_product_mean_output`, `unit_product_gap`, `midrange_mean_absolute_error`, and `evaluation_set_coverage`.

## Project Layout

- `Basics.sln`: local solution for the Basics demo.
- `src/Basics.Environment`: shared environment contract, sizing heuristics, runtime client, and planner.
- `src/Basics.Tasks`: concrete Basics task plugins plus the plugin registry.
- `src/Basics.Ui`: small operator UI for connection/configuration, capacity fetch, task selection, stop targets, scheduling settings, metric surfaces, and winning-artifact export.
- `src/Basics.Harness`: console entrypoint for repeatable live-runtime trial runs, JSON reports, and simple auto-tuning.
- `tests/Basics.Environment.Tests`: contract and planner tests.
- `tests/Basics.Tasks.Tests`: task-plugin evaluation tests.

## Runtime Dependency

`Basics.Environment` references the sibling runtime repo at `../NBNv2/src/Nbn.Shared/Nbn.Shared.csproj`. That keeps the demo on the canonical protobuf/contracts source instead of copying them into this repo.

## Development

```bash
cd /home/hailey/AI/NBNv2-demos/Basics
dotnet build Basics.sln -c Release
dotnet test Basics.sln -c Release --no-restore
```

## UI Run Logs

The Basics UI writes JSONL run logs under `artifacts/ui-runs/`. Run-log snapshots are compact summaries rather than full chart-history dumps, runtime memory samples are only emitted on the configured sampling interval, and long runs rotate to `.partNNN.jsonl` segments between records when a segment reaches the configured size cap. A single oversized record may exceed the segment cap.

Default UI log retention is enabled for future runs: unmarked `basics-ui-*.jsonl` files are pruned on run-log creation and rotation to keep unmarked logs near the default caps (`256 MiB` per segment, `4 GiB` total unmarked bytes, `32` unmarked files, `14` days). To preserve a selected log segment from automatic pruning, create a sibling marker named `<log-file>.keep`; marked logs do not count against automatic retention caps. For example:

```bash
touch artifacts/ui-runs/basics-ui-multiplication-20260413-162707.jsonl.keep
```

Advanced overrides are available through environment variables: `NBN_BASICS_UI_RUN_LOG_RETENTION_ENABLED`, `NBN_BASICS_UI_RUN_LOG_MAX_FILE_MB`, `NBN_BASICS_UI_RUN_LOG_MAX_TOTAL_MB`, `NBN_BASICS_UI_RUN_LOG_MAX_FILES`, `NBN_BASICS_UI_RUN_LOG_MAX_AGE_DAYS`, and `NBN_BASICS_UI_RUN_LOG_KEEP_MARKER_SUFFIX`.

## Live Harness

Generate a sample config:

```bash
cd /home/hailey/AI/NBNv2-demos/Basics
dotnet run --project src/Basics.Harness/Basics.Harness.csproj -- --write-sample-config ./artifacts/live-trials/sample-config.json
```

Run repeated live trials against a running NBN stack:

```bash
cd /home/hailey/AI/NBNv2-demos/Basics
dotnet run --project src/Basics.Harness/Basics.Harness.csproj -- --config ./artifacts/live-trials/sample-config.json
```

The harness writes a JSON report under `artifacts/live-trials/`, replays the shared `BasicsExecutionSession` against real IO, captures per-generation snapshots, and can apply simple follow-up tuning between trials while staying on the IO-only demo contract.
`artifacts/` is local scratch, not canonical repo state, so old JSON configs/reports should be regenerated after contract changes instead of treated as authoritative baselines.

Run a one-command local smoke validation with a temporary in-process runtime stack:

```bash
cd /home/hailey/AI/NBNv2-demos/Basics
dotnet run --project src/Basics.Harness/Basics.Harness.csproj -- smoke-local
```

`smoke-local` boots a temporary HiveMind/IO/Reproduction/Speciation/Worker stack in-process, waits for IO readiness, runs a reduced one-trial Basics harness profile, writes a report under `artifacts/live-trials/local-smoke/`, and then tears the stack down. It validates startup, readiness, IO wiring, and harness execution flow; it is intentionally a smoke check, not a guarantee that the AND task converges to a perfect candidate within one short local run.

Later issues will build broader operator workflows and additional task families on top of this shared environment, task library, UI shell, and live harness.
