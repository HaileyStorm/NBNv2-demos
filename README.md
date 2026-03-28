# NBNv2 Demos

This repository hosts External World demo projects for NBNv2.

Each demo lives in its own top-level folder and is expected to integrate with NBNv2 through the public IO surface rather than private runtime internals.

## Planned demos

- `Basics`
- `Tag`
- `Forager`
- `TunnelFlight`
- `SpaceFight`
- `Puzzler`

## Relationship to NBNv2

- Upstream runtime repo: `../NBNv2`
- Canonical specification: `../NBNv2/docs/NBNv2.md`
- Canonical IO runtime design notes: `../NBNv2/src/Nbn.Runtime.IO/Design.md`
- Canonical protobuf contracts: `../NBNv2/src/Nbn.Shared/Protos/*.proto`

If demo work reveals an NBNv2 bug or missing capability, update `../NBNv2` only after explicit user approval in the active session. When that happens, follow `../NBNv2/AGENTS.md` and keep the demo-repo and NBNv2 commits separate.

## Repo layout

- `Basics/`
- `Tag/`
- `Forager/`
- `TunnelFlight/`
- `SpaceFight/`
- `Puzzler/`

## Getting started

Read [`AGENTS.md`](AGENTS.md) before making demo changes. It includes the demo workflow, the external IO contract inventory, and the rules for approved cross-repo fixes in `../NBNv2`.
