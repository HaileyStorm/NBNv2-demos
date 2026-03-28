# NBNv2 Demos

This repository holds External World demo projects for NBN. Each demo is a consumer of the supported NBN runtime interfaces, with the canonical runtime, docs, and protobuf contracts living in the sibling repository at `../NBNv2`.

## Start Here

- Agent guidance for this repo: [`AGENTS.md`](AGENTS.md)
- Canonical NBN specification: [`../NBNv2/docs/NBNv2.md`](../NBNv2/docs/NBNv2.md)
- Canonical IO/runtime design note: [`../NBNv2/src/Nbn.Runtime.IO/Design.md`](../NBNv2/src/Nbn.Runtime.IO/Design.md)
- Canonical shared contract ownership: [`../NBNv2/src/Nbn.Shared/Design.md`](../NBNv2/src/Nbn.Shared/Design.md)
- Canonical protobuf sources:
  - [`../NBNv2/src/Nbn.Shared/Protos/nbn_common.proto`](../NBNv2/src/Nbn.Shared/Protos/nbn_common.proto)
  - [`../NBNv2/src/Nbn.Shared/Protos/nbn_control.proto`](../NBNv2/src/Nbn.Shared/Protos/nbn_control.proto)
  - [`../NBNv2/src/Nbn.Shared/Protos/nbn_io.proto`](../NBNv2/src/Nbn.Shared/Protos/nbn_io.proto)
  - [`../NBNv2/src/Nbn.Shared/Protos/nbn_signals.proto`](../NBNv2/src/Nbn.Shared/Protos/nbn_signals.proto)
  - [`../NBNv2/src/Nbn.Shared/Protos/nbn_repro.proto`](../NBNv2/src/Nbn.Shared/Protos/nbn_repro.proto)
  - [`../NBNv2/src/Nbn.Shared/Protos/nbn_speciation.proto`](../NBNv2/src/Nbn.Shared/Protos/nbn_speciation.proto)

## Planned Demos

- `Basics`
- `Tag`
- `Forager`
- `TunnelFlight`
- `SpaceFight`
- `Puzzler`

## Working Model

- Demo projects should integrate with NBN through IO Gateway and the documented External World contract.
- Do not fork or hand-maintain copies of canonical NBN contracts here unless there is an explicit generation or vendoring workflow.
- If demo work uncovers an issue in `../NBNv2`, summarize it first. Cross-repo fixes in `../NBNv2` are allowed only after explicit user approval in the active session.
