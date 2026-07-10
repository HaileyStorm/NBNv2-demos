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

verify_profile() {
  local file="$1"
  local model="$2"
  local effort="$3"

  require_line "$file" "model = \"$model\""
  require_line "$file" "model_reasoning_effort = \"$effort\""
  require_line "$file" 'model_context_window = 480000'
  require_line "$file" 'model_auto_compact_token_limit = 480000'
}

for profile in .codex/config.toml .codex/agents/*.toml; do
  require_line "$profile" 'model_context_window = 480000'
  require_line "$profile" 'model_auto_compact_token_limit = 480000'
  rg -qx 'model_reasoning_effort = "(high|xhigh|max)"' "$profile" \
    || fail "$profile must use reasoning effort high, xhigh, or max."
done

verify_profile .codex/config.toml gpt-5.6-sol high

for role in explorer ownership_mapper invariant_mapper test_mapper; do
  verify_profile ".codex/agents/$role.toml" gpt-5.6-luna xhigh
done

for role in docs_researcher packetizer; do
  verify_profile ".codex/agents/$role.toml" gpt-5.6-luna high
done

for role in nbn_demo_spec_guard nbn_demo_io_invariants nbn_demo_docs_guard; do
  verify_profile ".codex/agents/$role.toml" gpt-5.6-sol high
done

invalid_models="$(rg -n '^model\s*=' .codex/config.toml .codex/agents/*.toml \
  | rg -v 'model\s*=\s*"gpt-5\.6-(sol|terra|luna)"' || true)"
if [[ -n "$invalid_models" ]]; then
  printf 'Primary profiles may pin only GPT-5.6 Sol, Terra, or Luna; Spark is a manual quota fallback:\n%s\n' \
    "$invalid_models" >&2
  exit 1
fi

printf 'Codex model policy verified: Sol/Terra/Luna roles, reasoning floors, and 480k context limits are explicit.\n'
