#!/usr/bin/env bash
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$repo_root"

fail() {
  printf '%s\n' "$1" >&2
  exit 1
}

require_line() {
  local file="$1"
  local line="$2"
  grep -Fqx "$line" "$file" || fail "$file must contain: $line"
}

verify_root_picker_policy() {
  python3 - .codex/config.toml <<'PY'
import sys
import tomllib

path = sys.argv[1]
with open(path, "rb") as stream:
    config = tomllib.load(stream)

for key in ("model_context_window", "model_auto_compact_token_limit"):
    if config.get(key) != 480000:
        raise SystemExit(f"{path} must set top-level {key} = 480000.")

for key in ("model", "model_reasoning_effort"):
    if key in config:
        raise SystemExit(
            f"{path} must omit ambient {key} so the native picker controls interactive tasks."
        )

models = config.get("models", {})
if isinstance(models, dict) and "new_thread" in models:
    raise SystemExit(
        f"{path} must omit models.new_thread so the native picker controls interactive tasks."
    )
PY
}

verify_profile() {
  local file="$1"
  local model="$2"
  local effort="$3"

  require_line "$file" "model = \"$model\""
  require_line "$file" "model_reasoning_effort = \"$effort\""
  require_line "$file" 'model_context_window = 480000'
  require_line "$file" 'model_auto_compact_token_limit = 480000'
}

verify_model_effort_pair() {
  local file="$1"
  local model
  local effort

  model="$(sed -nE 's/^model = "([^"]+)"$/\1/p' "$file")"
  effort="$(sed -nE 's/^model_reasoning_effort = "([^"]+)"$/\1/p' "$file")"

  case "$model:$effort" in
    gpt-5.6-sol:high|gpt-5.6-sol:xhigh|gpt-5.6-sol:max|\
    gpt-5.6-terra:xhigh|gpt-5.6-terra:max|\
    gpt-5.6-luna:high|gpt-5.6-luna:xhigh|gpt-5.6-luna:max)
      ;;
    *)
      fail "$file has unsupported model/reasoning pair '$model:$effort'; allowed: Sol high/xhigh/max, Terra xhigh/max, Luna high/xhigh/max, and never ultra."
      ;;
  esac
}

verify_root_picker_policy

for profile in .codex/agents/*.toml; do
  require_line "$profile" 'model_context_window = 480000'
  require_line "$profile" 'model_auto_compact_token_limit = 480000'
  verify_model_effort_pair "$profile"
done

for role in nbn_demo_spec_guard nbn_demo_io_invariants nbn_demo_docs_guard; do
  verify_profile ".codex/agents/$role.toml" gpt-5.6-sol high
done

invalid_models="$(rg -n '^model\s*=' .codex/agents/*.toml \
  | rg -v 'model\s*=\s*"gpt-5\.6-(sol|terra|luna)"' || true)"
if [[ -n "$invalid_models" ]]; then
  printf 'Primary profiles may pin only GPT-5.6 Sol, Terra, or Luna; Spark is a manual quota fallback:\n%s\n' \
    "$invalid_models" >&2
  exit 1
fi

printf 'Repo-specific Codex guards verified: the root leaves model selection to the native picker, while named roles retain supported model/effort pairs and 480k limits.\n'
