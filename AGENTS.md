# NBNv2 Demos Agent Operating Guide

## Scope and precedence

- Repo-local guide for coding agents in `NBNv2-demos`.
- Global baseline remains `~/.codex/AGENTS.md`.
- This repo is for External World demo projects that integrate with the sibling NBN runtime repo at `../NBNv2`.
- If rules conflict, this file wins for demo-repo work. When work moves into `../NBNv2`, stop and follow `../NBNv2/AGENTS.md`.

## Purpose

- This repo holds demo projects that exercise NBN from the outside, through the supported IO/runtime interfaces.
- Treat `../NBNv2` as the product/runtime source of truth and this repo as consumer/integration territory.
- Demo code should validate and showcase supported behavior, not invent undocumented side channels into runtime internals.

## Relationship to `../NBNv2`

- Canonical specification entrypoint: `../NBNv2/docs/NBNv2.md`
- Canonical doc template: `../NBNv2/docs/INDEX.md`
- IO/runtime design notes: `../NBNv2/src/Nbn.Runtime.IO/Design.md`
- Shared contract ownership: `../NBNv2/src/Nbn.Shared/Design.md`
- Canonical wire contracts:
  - `../NBNv2/src/Nbn.Shared/Protos/nbn_common.proto`
  - `../NBNv2/src/Nbn.Shared/Protos/nbn_control.proto`
  - `../NBNv2/src/Nbn.Shared/Protos/nbn_io.proto`
  - `../NBNv2/src/Nbn.Shared/Protos/nbn_signals.proto`
  - `../NBNv2/src/Nbn.Shared/Protos/nbn_repro.proto`
  - `../NBNv2/src/Nbn.Shared/Protos/nbn_speciation.proto`

## Doc-first workflow

- For non-trivial demo work, read `../NBNv2/docs/NBNv2.md` before editing code.
- Minimum read set for External World work:
  - section 13, `I/O architecture and External World interface`
  - section 19, `Protocol schemas (.proto)`
  - `../NBNv2/src/Nbn.Runtime.IO/Design.md`
  - `../NBNv2/src/Nbn.Shared/Design.md`
- Prefer narrow scouting passes before edits:
  1. contract/ownership map in `../NBNv2`
  2. invariant/risk map for the intended demo interaction
  3. verification/test map for affected runtime contracts

## Cross-repo editing rule

- Demo agents may inspect `../NBNv2` freely.
- If a demo exposes an NBNv2 bug, missing contract, docs drift, or runtime gap, stop and summarize the issue.
- Do not edit `../NBNv2` until the user explicitly approves that cross-repo change in the active session.
- After approval, create a `.working` sentinel in `../NBNv2`, follow `../NBNv2/AGENTS.md`, and keep demo-repo and runtime-repo changes clearly separated.

## Demo repo layout

- Each demo lives in its own top-level folder.
- Keep demo-specific docs near the demo root; do not create duplicate copies of canonical NBN contracts here.
- If a demo needs generated client code or vendored schema snapshots, document the source path in `../NBNv2` and the regeneration command.

## External World interface fundamentals

- External World integration is actor-based: clients interact with NBN through Proto.Actor remoting over gRPC using NBN protobuf message types.
- External clients should talk to IO Gateway, not directly to brain shards or region hosts.
- Canonical discovery key for IO Gateway: `service.endpoint.io_gateway`
- Canonical endpoint encoding: `host:port/actor`
- Default IO actor names from runtime:
  - gateway actor: `io-gateway`
  - input coordinator actor prefix: `io-input-`
  - output coordinator actor prefix: `io-output-`
- Default runtime CLI values worth knowing:
  - SettingsMonitor host/port/name: `127.0.0.1`, `12010`, `SettingsMonitor`
  - IO port: `12020`
  - IO `ConnectAck.server_name`: `nbn.io`

## Critical invariants for demos

- Input region is always `0`; output region is always `31`.
- External input `input_index i` maps to `(region_id=0, neuron_id=i)`.
- External output `output_index i` maps to `(region_id=31, neuron_id=i)`.
- No axon may target region `0`.
- Region `31` may emit axons but may not target region `31`.
- Tick execution is global two-phase: compute, then deliver.
- Signals produced on tick `N` are visible to compute on tick `N+1`.
- Input writes are applied on the next tick by IO coordinators; clients do not need to supply tick IDs for normal input/output use.
- Input scalars and vectors must contain finite floats; `NaN` and infinities are invalid.
- Output vectors are full brain-level vectors ordered by `output_index` and may be assembled from shard-local segments by IO.
- Reproduction protects neuron counts in regions `0` and `31` by default; explicit manual IO-region add/remove is only legal when reproduction config disables that protection.

## Full IO contract map

- Source of truth is the sibling proto files listed above plus `../NBNv2/docs/NBNv2.md`.
- These are actor messages, not a generated gRPC service surface. Do not invent RPC service names that do not exist in the canonical `.proto` files.

### Common contracts (`nbn_common.proto`)

- `Uuid`
- `Sha256`
- `Address32`
- `ShardId32`
- `ArtifactRef`
- `Severity`

### Control-side contracts referenced by IO (`nbn_control.proto`)

- Brain lifecycle and runtime control:
  - `SpawnBrain`
  - `SpawnBrainAck`
  - `PauseBrain`
  - `ResumeBrain`
  - `KillBrain`
  - `BrainTerminated`
- IO/runtime configuration:
  - `SetBrainCostEnergy`
  - `SetBrainPlasticity`
  - `SetBrainHomeostasis`
  - `GetBrainIoInfo`
  - `BrainIoInfo`
- IO-related enums:
  - `HomeostasisTargetMode`
  - `HomeostasisUpdateMode`
  - `InputCoordinatorMode`
  - `OutputVectorSource`
- Placement and routing metadata exposed through the same control schema:
  - `ShardPlanMode`
  - `ShardPlan`
  - `PlacementLifecycleState`
  - `PlacementFailureReason`
  - `PlacementAssignmentTarget`
  - `PlacementAssignmentState`
  - `PlacementReconcileState`
  - placement request/ack/report message families in `nbn_control.proto`

### Signal contracts referenced by IO (`nbn_signals.proto`)

- `Contribution`
- `SignalBatch`
- `SignalBatchAck`
- `OutboxBatch`

### IO session, lifecycle, and metadata contracts (`nbn_io.proto`)

- session:
  - `Connect`
  - `ConnectAck`
- brain metadata and registration:
  - `BrainInfoRequest`
  - `BrainInfo`
  - `BrainEnergyState`
  - `RegisterBrain`
  - `UnregisterBrain`
  - `RegisterIoGateway`
- spawn and artifact lifecycle:
  - `SpawnBrainViaIO`
  - `SpawnBrainViaIOAck`
  - `RequestSnapshot`
  - `SnapshotReady`
  - `ExportBrainDefinition`
  - `BrainDefinitionReady`

### IO input, output, and runtime-write contracts (`nbn_io.proto`)

- input path:
  - `InputWrite`
  - `InputVector`
  - `DrainInputs`
  - `InputDrain`
- direct runtime writes for tooling/debug/demo scenarios:
  - `RuntimeNeuronPulse`
  - `RuntimeNeuronStateWrite`
- output subscriptions:
  - `SubscribeOutputs`
  - `UnsubscribeOutputs`
  - `OutputEvent`
  - `SubscribeOutputsVector`
  - `UnsubscribeOutputsVector`
  - `OutputVectorEvent`
  - `OutputVectorSegment`
- energy and config writes:
  - `EnergyCredit`
  - `EnergyRate`
  - `SetCostEnergyEnabled`
  - `SetPlasticityEnabled`
  - `SetHomeostasisEnabled`
  - `IoCommandAck`

### IO reproduction/speciation wrapper contracts (`nbn_io.proto`)

- reproduction wrappers:
  - `ReproduceByBrainIds`
  - `ReproduceByArtifacts`
  - `AssessCompatibilityByBrainIds`
  - `AssessCompatibilityByArtifacts`
  - `ReproduceResult`
  - `AssessCompatibilityResult`
- speciation wrappers:
  - `SpeciationStatus`
  - `SpeciationStatusResult`
  - `SpeciationGetConfig`
  - `SpeciationGetConfigResult`
  - `SpeciationSetConfig`
  - `SpeciationSetConfigResult`
  - `SpeciationResetAll`
  - `SpeciationResetAllResult`
  - `SpeciationDeleteEpoch`
  - `SpeciationDeleteEpochResult`
  - `SpeciationEvaluate`
  - `SpeciationEvaluateResult`
  - `SpeciationAssign`
  - `SpeciationAssignResult`
  - `SpeciationBatchEvaluateApply`
  - `SpeciationBatchEvaluateApplyResult`
  - `SpeciationListMemberships`
  - `SpeciationListMembershipsResult`
  - `SpeciationQueryMembership`
  - `SpeciationQueryMembershipResult`
  - `SpeciationListHistory`
  - `SpeciationListHistoryResult`

### Reproduction payload contracts used through IO (`nbn_repro.proto`)

- enums:
  - `StrengthSource`
  - `SpawnChildPolicy`
  - `PrunePolicy`
- request/config payloads:
  - `RegionOutDegreeCap`
  - `ReproduceLimits`
  - `ReproduceConfig`
  - `ManualIoNeuronEdit`
  - `ReproduceByBrainIdsRequest`
  - `ReproduceByArtifactsRequest`
  - `AssessCompatibilityByBrainIdsRequest`
  - `AssessCompatibilityByArtifactsRequest`
- responses:
  - `SimilarityReport`
  - `MutationSummary`
  - `ReproduceRunOutcome`
  - `ReproduceResult`

### Speciation payload contracts used through IO (`nbn_speciation.proto`)

- External World demos that touch speciation must treat `nbn_speciation.proto` as required reading.
- At minimum, account for the full request/response families referenced by the IO wrappers:
  - status/config: `SpeciationStatusRequest/Response`, `SpeciationGetConfigRequest/Response`, `SpeciationSetConfigRequest/Response`
  - epoch administration: `SpeciationResetAllRequest/Response`, `SpeciationDeleteEpochRequest/Response`
  - assignment/evaluation: `SpeciationEvaluateRequest/Response`, `SpeciationAssignRequest/Response`, `SpeciationBatchEvaluateApplyRequest/Response`
  - membership/history queries: `SpeciationListMembershipsRequest/Response`, `SpeciationQueryMembershipRequest/Response`, `SpeciationListHistoryRequest/Response`
- Also read the shared speciation types and enums in `../NBNv2/src/Nbn.Shared/Protos/nbn_speciation.proto` before changing speciation-oriented demos.

## Practical command semantics demos must respect

- `Connect` registers a client actor with IO Gateway and returns `ConnectAck`.
- `SpawnBrainViaIO` forwards `nbn.control.SpawnBrain` through IO and returns `SpawnBrainViaIOAck`.
- Spawn failures are normalized into stable reason codes such as `spawn_unavailable`, `spawn_empty_response`, and `spawn_request_failed`.
- Coordinator-routed command writes return `IoCommandAck` with command name, success flag, message text, and optionally `BrainEnergyState`.
- `set_plasticity` acknowledgments may also carry configured-vs-effective enablement snapshots.
- Input coordinator modes:
  - `INPUT_COORDINATOR_MODE_DIRTY_ON_CHANGE`
  - `INPUT_COORDINATOR_MODE_REPLAY_LATEST_VECTOR`
- Output vector sources:
  - `OUTPUT_VECTOR_SOURCE_POTENTIAL`
  - `OUTPUT_VECTOR_SOURCE_BUFFER`
- Subscriber identity may be explicit via `subscriber_actor`; otherwise sender PID is used.
- Placement of per-brain coordinators is transparent to the client. Demo code should not assume coordinators are local to IO Gateway.

## Documentation maintenance expectations

- If demo work changes NBN-visible behavior, update canonical docs in `../NBNv2`, not ad hoc copies here.
- If a `.proto` contract changes in `../NBNv2`, update the mirrored appendix content in `../NBNv2/docs/NBNv2.md`, re-render docs, and run proto compatibility checks.
- Keep demo-repo docs focused on integration guidance and scenario intent; keep stable runtime contracts in the runtime repo.

## Verification expectations

- For demo-only changes, run the demo-local checks that actually exist.
- For approved `../NBNv2` changes triggered by demo work, minimum verification is:
  - `dotnet test ../NBNv2/tests/Nbn.Tests/Nbn.Tests.csproj -c Release --disable-build-servers --filter FullyQualifiedName~Nbn.Tests.Proto.ProtoCompatibilityTests`
  - `bash ../NBNv2/tools/docs/render-nbnv2-docs.sh --check`
  - `dotnet test ../NBNv2/NBNv2.sln -c Release --disable-build-servers`
- If file locks occur, use the runtime repo’s `.artifacts-temp` guidance from `../NBNv2/AGENTS.md`.

## Landing the work

- Demo work is not complete until intended local commits are created and pushed.
- Keep commit history scoped: demo repo commits here, runtime repo commits there.
- Hand off open gaps clearly, especially any runtime issues discovered but not yet approved for `../NBNv2` changes.
