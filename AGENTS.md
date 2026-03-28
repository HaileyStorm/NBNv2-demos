# NBNv2 Demos Agent Operating Guide

## Scope and precedence

- Repo-local guide for coding agents in this repository.
- Global baseline remains `~/.codex/AGENTS.md`.
- If rules conflict, this file wins for this repo.
- This repo is the default workspace for demo work. Do not edit `../NBNv2` unless the user explicitly approves that cross-repo work in the active session.

## Purpose

- This repository hosts external-world demo projects that exercise NBNv2 through the public IO surface.
- Each demo lives in its own top-level folder.
- Demo code should treat NBNv2 as an upstream runtime and contract source, not as a scratchpad dependency.

## Canonical upstream references

- Main runtime repo: `../NBNv2`
- Main runtime agent guide: `../NBNv2/AGENTS.md`
- Full specification: `../NBNv2/docs/NBNv2.md`
- IO runtime design notes: `../NBNv2/src/Nbn.Runtime.IO/Design.md`
- Shared contract design notes: `../NBNv2/src/Nbn.Shared/Design.md`
- Canonical proto source directory: `../NBNv2/src/Nbn.Shared/Protos`

## Cross-repo change rule

- Default assumption: demo tasks stay inside this repo.
- If demo work uncovers an NBNv2 bug, missing hook, contract drift, or documentation gap, collect evidence in this repo first, then ask the user for approval before touching `../NBNv2`.
- Once approval is granted, follow `../NBNv2/AGENTS.md` while working there.
- Keep demo-repo commits and NBNv2 commits separate. Do not mix both repos into one commit.
- When NBNv2 behavior or contracts change, update both repos as needed: the NBNv2 canonical docs/tests first, then any demo assumptions that depend on them.

## Demo-first workflow

- For any non-trivial demo or integration task, read `../NBNv2/docs/NBNv2.md` section `13. I/O architecture and External World interface` and section `19. Protocol schemas (.proto)` before editing code.
- Treat `../NBNv2/src/Nbn.Shared/Protos/*.proto` as the source of truth for wire contracts. The mirrored appendix in `../NBNv2/docs/NBNv2.md` is the human-readable copy.
- Use demo code to validate behavior through the public IO surface before proposing upstream runtime changes.
- Prefer small demo-specific adapters or clients over copying NBNv2 runtime code into this repo.

## External World contract baseline

### Transport and actor addressing

- External World talks to NBN through Proto.Actor remoting over gRPC, using NBN protobuf messages.
- The IO gateway actor name is `io-gateway`.
- Per-brain coordinator actor name prefixes are `io-input-` and `io-output-`.
- External clients should target the IO gateway, not region shards or runtime internals directly.
- `subscriber_actor` fields let callers provide a stable actor identity instead of relying on the transient sender PID.
- Coordinator placement is transparent. IO gateway tracks coordinator PID moves and ownership metadata so subscriptions and pending input state survive relocations.

### Stable mappings and invariants

- Input region is always `0`.
- Output region is always `31`.
- `input_index i` maps to `(region_id = 0, neuron_id = i)`.
- `output_index i` maps to `(region_id = 31, neuron_id = i)`.
- No axon may target region `0`.
- Region `31` may emit axons but may not target region `31`.
- Global tick execution is two-phase: compute, then deliver.
- Signals produced during tick `N` become visible at compute of tick `N+1`.
- External clients do not need to know tick IDs to write inputs or receive outputs.
- Input writes are applied on the next tick automatically by the IO coordinators.
- Input scalars and vectors must be finite real numbers. `NaN` and infinities are rejected.
- Input vectors must match `BrainInfo.input_width`.
- Output vectors are brain-wide vectors ordered by `output_index` and must match `BrainInfo.output_width`.
- Output width is fixed for a running brain and is not mutated by observed output events.

### Input coordinator and output vector modes

- Input coordinator modes are defined in `../NBNv2/src/Nbn.Shared/Protos/nbn_control.proto`.
- `dirty_on_change` is the default input mode. It buffers writes and emits only changed inputs during delivery.
- `replay_latest_vector` stores the latest full input vector and emits all indices every tick.
- When multiple writes hit the same input within one tick window, the most recent value wins.
- Output vector source defaults to `potential`.
- `buffer` output vector mode emits each output neuron's persistent buffer value every tick without requiring a fire event.

## Full IO proto inventory

### Canonical proto files used by demos

- `../NBNv2/src/Nbn.Shared/Protos/nbn_common.proto`
- `../NBNv2/src/Nbn.Shared/Protos/nbn_control.proto`
- `../NBNv2/src/Nbn.Shared/Protos/nbn_signals.proto`
- `../NBNv2/src/Nbn.Shared/Protos/nbn_io.proto`
- `../NBNv2/src/Nbn.Shared/Protos/nbn_repro.proto`
- `../NBNv2/src/Nbn.Shared/Protos/nbn_speciation.proto`

### Shared types that appear in IO flows

- `nbn.Uuid`
- `nbn.Sha256`
- `nbn.ArtifactRef`
- `nbn.ShardId32`
- `nbn.signal.Contribution`
- `nbn.control.SpawnBrain`
- `nbn.control.SpawnBrainAck`
- `nbn.control.InputCoordinatorMode`
- `nbn.control.OutputVectorSource`
- `nbn.control.HomeostasisTargetMode`
- `nbn.control.HomeostasisUpdateMode`
- `nbn.control.BrainTerminated`

### `nbn_io.proto` message groups

- Session handshake: `Connect`, `ConnectAck`
- Brain metadata and lifecycle: `BrainInfoRequest`, `BrainInfo`, `RegisterBrain`, `UnregisterBrain`, `RegisterIoGateway`, `SpawnBrainViaIO`, `SpawnBrainViaIOAck`
- Input path: `InputWrite`, `InputVector`, `RuntimeNeuronPulse`, `RuntimeNeuronStateWrite`, `DrainInputs`, `InputDrain`
- Output subscriptions and events: `SubscribeOutputs`, `UnsubscribeOutputs`, `OutputEvent`, `SubscribeOutputsVector`, `UnsubscribeOutputsVector`, `OutputVectorEvent`, `OutputVectorSegment`
- Energy and runtime config: `BrainEnergyState`, `EnergyCredit`, `EnergyRate`, `SetCostEnergyEnabled`, `SetPlasticityEnabled`, `SetHomeostasisEnabled`, `IoCommandAck`
- Artifact and snapshot flow: `RequestSnapshot`, `SnapshotReady`, `ExportBrainDefinition`, `BrainDefinitionReady`
- Reproduction wrappers: `ReproduceByBrainIds`, `ReproduceByArtifacts`, `AssessCompatibilityByBrainIds`, `AssessCompatibilityByArtifacts`, `ReproduceResult`, `AssessCompatibilityResult`
- Speciation wrappers: `SpeciationStatus`, `SpeciationStatusResult`, `SpeciationGetConfig`, `SpeciationGetConfigResult`, `SpeciationSetConfig`, `SpeciationSetConfigResult`, `SpeciationResetAll`, `SpeciationResetAllResult`, `SpeciationDeleteEpoch`, `SpeciationDeleteEpochResult`, `SpeciationEvaluate`, `SpeciationEvaluateResult`, `SpeciationAssign`, `SpeciationAssignResult`, `SpeciationBatchEvaluateApply`, `SpeciationBatchEvaluateApplyResult`, `SpeciationListMemberships`, `SpeciationListMembershipsResult`, `SpeciationQueryMembership`, `SpeciationQueryMembershipResult`, `SpeciationListHistory`, `SpeciationListHistoryResult`

### Practical command semantics for demos

- `Connect` and `ConnectAck` provide a simple client/server handshake.
- `BrainInfoRequest` and `BrainInfo` are the first-stop discovery calls for input width, output width, energy state, runtime modes, and artifact references.
- `SpawnBrainViaIO` wraps `nbn.control.SpawnBrain` so a demo can request a new brain through IO rather than talking to HiveMind directly.
- `InputWrite` sends one input value by index.
- `InputVector` sends a full input vector.
- `RuntimeNeuronPulse` and `RuntimeNeuronStateWrite` are advanced runtime injection commands and should only be used when the demo explicitly needs low-level neuron manipulation.
- `SubscribeOutputs` and `SubscribeOutputsVector` subscribe the caller to output events. The matching unsubscribe calls must be used during shutdown or reconnect logic.
- `OutputEvent` is sparse and fire-driven. `OutputVectorEvent` is dense and tick-driven.
- `EnergyCredit`, `EnergyRate`, `SetCostEnergyEnabled`, `SetPlasticityEnabled`, and `SetHomeostasisEnabled` are command-style writes that return `IoCommandAck`.
- `RequestSnapshot` and `ExportBrainDefinition` return artifact references suitable for persisting demo state or reproducing bugs.
- Reproduction and speciation wrappers are available through IO, but the nested request and response payloads come from `nbn_repro.proto` and `nbn_speciation.proto`.

### `IoCommandAck` rules

- `IoCommandAck.command` names the command the runtime believes it handled.
- `IoCommandAck.success` reports acceptance or rejection.
- `IoCommandAck.message` carries the reason text or failure detail.
- `IoCommandAck.energy_state` is optional and provides immediate feedback for operator surfaces.
- `set_plasticity` acknowledgements may also include both configured and effective plasticity flags.
- When HiveMind is available, effective plasticity state becomes authoritative after runtime reconciliation.

### Reproduction and speciation rules that demos must respect

- `ReproduceConfig.protect_io_region_neuron_counts` defaults to `true`.
- With protection enabled, reproduction cannot add or remove neurons in regions `0` and `31`.
- If protection is disabled, IO-region neuron edits must be explicit caller-supplied manual operations.
- Reproduction `run_count` defaults to `1` and values above the current runtime max of `64` are rejected.
- Axon invariants for regions `0` and `31` always apply, even when manual IO-region edits are allowed.
- Demo code should not assume speciation assignment policy; it should treat speciation request and response payloads as canonical contract data from `nbn_speciation.proto`.

### Runtime parameter ranges demos must honor

- `homeostasis_base_probability`: `[0,1]`
- `homeostasis_min_step_codes`: `>= 1`
- `homeostasis_energy_target_scale`: `[0,4]`
- `homeostasis_energy_probability_scale`: `[0,4]`
- `plasticity_rate`: `>= 0`
- `plasticity_delta`: `>= 0`
- `plasticity_rebase_threshold`: `>= 0`
- `plasticity_rebase_threshold_pct`: `[0,1]`
- `plasticity_energy_cost_reference_tick_cost`: `> 0` when modulation is enabled
- `plasticity_energy_cost_response_strength`: `[0,8]`
- `plasticity_energy_cost_min_scale`: `[0,1]`
- `plasticity_energy_cost_max_scale`: `[0,1]` and `>= min_scale`

## Documentation maintenance

- Keep this repo's `README.md` and `AGENTS.md` aligned with stable demo workflow and contract assumptions.
- Do not copy large chunks of NBNv2 canonical documentation into demo-specific docs unless the demo truly needs a local operator guide.
- If the upstream contract changes, update `../NBNv2` canonical docs and proto mirrors there first, then refresh any demo docs that depend on those changes.

## Verification when NBNv2 changes are approved

- Proto drift gate:
  `dotnet test ../NBNv2/tests/Nbn.Tests/Nbn.Tests.csproj -c Release --disable-build-servers --filter FullyQualifiedName~Nbn.Tests.Proto.ProtoCompatibilityTests`
- Docs freshness gate:
  `bash ../NBNv2/tools/docs/render-nbnv2-docs.sh --check`
- Targeted IO tests:
  `dotnet test ../NBNv2/tests/Nbn.Tests/Nbn.Tests.csproj -c Release --disable-build-servers --filter FullyQualifiedName~InputCoordinatorActorTests`
- Targeted output/coordinator tests:
  `dotnet test ../NBNv2/tests/Nbn.Tests/Nbn.Tests.csproj -c Release --disable-build-servers --filter FullyQualifiedName~OutputCoordinatorActorTests`
- Full upstream suite before landing upstream behavior changes:
  `dotnet test ../NBNv2/NBNv2.sln -c Release --disable-build-servers`

## Landing the plane

- Demo-only work is not complete until this repo is committed and pushed.
- If approved cross-repo work touched `../NBNv2`, that repo must also be committed and pushed separately before declaring the session complete.
- Remove any `.working` sentinels you created before finishing.
