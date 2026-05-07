#!/usr/bin/env bash
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$repo_root"

invalid_models="$(rg -n '^model\s*=\s*"gpt-5\.4"' .codex/agents/*.toml || true)"
if [[ -n "$invalid_models" ]]; then
  printf 'GPT-5.4 is retired for repo-local Codex agents:\n%s\n' "$invalid_models" >&2
  exit 1
fi

model_lines="$(rg -n '^model\s*=' .codex/agents/*.toml | sort)"
allowed_model_lines="$(rg -n '^model\s*=\s*"(gpt-5\.5|gpt-5\.3-codex-spark)"' .codex/agents/*.toml | sort)"

if [[ "$model_lines" != "$allowed_model_lines" ]]; then
  printf 'Repo-local Codex agents may use only gpt-5.5 or gpt-5.3-codex-spark.\n' >&2
  printf 'Current model lines:\n%s\n' "$model_lines" >&2
  exit 1
fi

printf 'Codex model policy verified: all repo-local agents use GPT-5.5 or Spark.\n'
