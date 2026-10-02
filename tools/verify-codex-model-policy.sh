#!/usr/bin/env bash
set -euo pipefail

repo_root="$(cd "$(dirname "$0")/.." && pwd)"
cd "$repo_root"

python3 - <<'PY'
from pathlib import Path
import tomllib

root_path = Path(".codex/config.toml")
root_text = root_path.read_text(encoding="utf-8")
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

if "global GPT-6.1 Sol `high`" not in root_text:
    raise SystemExit(f"{root_path} must document the inherited GPT-6.1 Sol/high controller.")

policy_path = Path("AGENTS.md")
policy = policy_path.read_text(encoding="utf-8")
expected_controller = "Use GPT-6.1 Sol at high for the controller and default of every existing project"
stale_controllers = (
    "New unpinned interactive tasks inherit GPT-6 Astra",
    "global Astra `medium`",
    "gpt-6-sol at xhigh",
)
if expected_controller not in policy or any(value in policy for value in stale_controllers):
    raise SystemExit(
        f"{policy_path} must set GPT-6.1 Sol/high as default and remove stale routing."
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
    "model": "gpt-6.1-sol",
    "model_reasoning_effort": "high",
    "model_context_window": 291000,
    "model_auto_compact_token_limit": 208000,
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
    "Repo-specific Codex routing verified: GPT-6.1 Sol/high is inherited; "
    "correctness/spec/docs guard roles use Sol/high with 291k context and 208k compaction."
)
PY
