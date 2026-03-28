# Basics

`Basics` is the first real External World demo in this repo. It defines the shared environment contract that later task plugins and the future UI will build on for the first `2 -> 1` NBN curriculum.

## Current Scope

- A shared environment library in `src/Basics.Environment`.
- A task-plugin library in `src/Basics.Tasks`.
- A small Avalonia desktop UI in `src/Basics.Ui`.
- A test project in `tests/Basics.Environment.Tests`.
- A task-plugin test project in `tests/Basics.Tasks.Tests`.
- A template-anchored seed-population contract: initial brains are exact-template or bounded minor deviations, not unconstrained random topologies.
- IO-only runtime plumbing for environment work. Capacity sizing is intended to come through IO, not direct SettingsMonitor calls by the demo.
- The first real task plugin (`AND`) and registry wiring so the UI can resolve at least one available task instead of showing only placeholders.

## Design Direction

- Brain geometry is fixed at `2` inputs and `1` output.
- Seed populations are organized around a template family so reproduction/speciation starts from coherent parents instead of arbitrary unrelated brains.
- Reproduction defaults keep `protect_io_region_neuron_counts=true`.
- Parent selection and child-run allocation are part of the shared environment contract:
  - progress pressure favors fitter brains
  - diversity pressure avoids collapsing to one lineage too early
  - species-balance pressure prevents one bootstrap family from monopolizing the run budget
  - run counts stay bounded by IO-reported capacity recommendations and any explicit overrides
- Shared metrics expected by the future UI include accuracy, best/mean fitness, population count, active brains, species count, reproduction activity, and capacity utilization.

## Project Layout

- `Basics.sln`: local solution for the Basics demo.
- `src/Basics.Environment`: shared environment contract, sizing heuristics, runtime client, and planner.
- `src/Basics.Tasks`: concrete Basics task plugins plus the plugin registry.
- `src/Basics.Ui`: small operator UI for connection/configuration, capacity fetch, task selection, scheduling settings, and metric surfaces.
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

Later issues will add the remaining task plugins, live metric feeds, and the remaining operator workflows on top of this shared environment, task library, and UI shell.
