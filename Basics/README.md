# Basics

`Basics` is the first real External World demo in this repo. It defines the shared environment contract that later task plugins and the future UI will build on for the first `2 -> 2` NBN curriculum.

## Current Scope

- A shared environment library in `src/Basics.Environment`.
- A task-plugin library in `src/Basics.Tasks`.
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

- `Multiplication` uses bounded scalar inputs `a,b in {0.0, 0.25, 0.5, 0.75, 1.0}`.
- The deterministic evaluation set is the full `5 x 5` grid over that domain.
- The expected output is the normalized product `a * b`. Because the input domain is already bounded to `[0,1]`, no extra remapping is applied.
- Accuracy is tolerance-based for this task: a sample counts as correct when the observed output is within `+/-0.05` of the canonical product target.
- The task value is still read from `output[0]`; `output[1]` is only the readiness signal.
- Shared breakdown keys remain `task_accuracy`, `mean_absolute_error`, `mean_squared_error`, `target_proximity_fitness`, and `dataset_coverage`; task-specific regression keys are `tolerance_accuracy`, `zero_product_mean_output`, `unit_product_gap`, `midrange_mean_absolute_error`, and `evaluation_set_coverage`.

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

Run a one-command local smoke validation with a temporary in-process runtime stack:

```bash
cd /home/hailey/AI/NBNv2-demos/Basics
dotnet run --project src/Basics.Harness/Basics.Harness.csproj -- smoke-local
```

`smoke-local` boots a temporary HiveMind/IO/Reproduction/Speciation/Worker stack in-process, waits for IO readiness, runs a reduced one-trial Basics harness profile, writes a report under `artifacts/live-trials/local-smoke/`, and then tears the stack down. It validates startup, readiness, IO wiring, and harness execution flow; it is intentionally a smoke check, not a guarantee that the AND task converges to a perfect candidate within one short local run.

Later issues will build broader operator workflows and additional task families on top of this shared environment, task library, UI shell, and live harness.
