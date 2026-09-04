#!/usr/bin/env bash
set -euo pipefail

repo_root="$(cd "$(dirname "$0")/.." && pwd)"
cd "$repo_root"

python3 - <<'PY'
from pathlib import Path
import tomllib

root_path = Path(".codex/config.toml")
with root_path.open("rb") as stream:
    root = tomllib.load(stream)

forbidden_root_keys = {
    "model",
    "model_reasoning_effort",
    "model_context_window",
    "model_auto_compact_token_limit",
}
present = sorted(forbidden_root_keys.intersection(root))
if present:
    raise SystemExit(
        f"{root_path} must omit ambient picker/catalog keys: {', '.join(present)}"
    )

models = root.get("models", {})
if isinstance(models, dict) and "new_thread" in models:
    raise SystemExit(
        f"{root_path} must omit models.new_thread so the native picker controls interactive tasks."
    )

expected_roles = {
    "nbn_demo_spec_guard": "agents/nbn_demo_spec_guard.toml",
    "nbn_demo_io_invariants": "agents/nbn_demo_io_invariants.toml",
    "nbn_demo_docs_guard": "agents/nbn_demo_docs_guard.toml",
}
agents = root.get("agents", {})
if set(agents) != set(expected_roles):
    raise SystemExit(
        f"{root_path} must register exactly: {', '.join(sorted(expected_roles))}"
    )

profile_dir = root_path.parent / "agents"
actual_profile_paths = {
    path.relative_to(root_path.parent).as_posix() for path in profile_dir.glob("*.toml")
}
expected_profile_paths = set(expected_roles.values())
if actual_profile_paths != expected_profile_paths:
    raise SystemExit(
        f"{profile_dir} profile set must be exactly: "
        f"{', '.join(sorted(expected_profile_paths))}"
    )

expected_profile = {
    "model": "gpt-6-astra",
    "model_reasoning_effort": "high",
    "model_context_window": 602000,
    "model_auto_compact_token_limit": 512000,
}
for role, relative_path in expected_roles.items():
    configured_path = agents[role].get("config_file")
    if configured_path != relative_path:
        raise SystemExit(
            f"{root_path} must map agents.{role}.config_file to {relative_path!r}"
        )

    profile_path = root_path.parent / relative_path
    with profile_path.open("rb") as stream:
        profile = tomllib.load(stream)

    for key, expected in expected_profile.items():
        actual = profile.get(key)
        if actual != expected:
            raise SystemExit(
                f"{profile_path} must set {key} = {expected!r}; found {actual!r}"
            )

    if "sandbox_mode" in profile:
        raise SystemExit(f"{profile_path} must inherit the global sandbox policy")
    if "Do not edit files." not in profile.get("developer_instructions", ""):
        raise SystemExit(f"{profile_path} must retain the no-edit guard instruction")

print(
    "Repo-specific Codex guards verified: root model/context selection is picker/catalog-owned; "
    "named Astra/high roles use 602k context and 512k compaction."
)
PY
