#!/usr/bin/env python3
"""Run live Basics Multiplication PPO reward-policy sweeps.

The runtime PPO path uses these settings to shape candidate rollouts and reward
feedback policy updates. This script ranks practical settings by observed
live-run quality and stability.
"""

from __future__ import annotations

import argparse
import itertools
import json
import os
import random
import re
import socket
import subprocess
import sys
import time
from dataclasses import asdict, dataclass, replace
from datetime import datetime, timezone
from pathlib import Path
from typing import Any, Callable, Mapping


GEN_RE = re.compile(
    r"\[trial\s+(?P<trial>\d+)\]\s+gen\s+(?P<generation>\d+)\s+"
    r"(?P<state>\w+):\s+accuracy=(?P<accuracy>[-+0-9.]+)\s+"
    r"fitness=(?P<fitness>[-+0-9.]+)",
    re.IGNORECASE,
)
REPORT_RE = re.compile(r"^Report:\s+(?P<path>.+)$")


@dataclass(frozen=True)
class PpoCombo:
    mode: str
    population: int
    rollout_ticks: int
    rollout_batches: int
    epochs: int
    minibatch_size: int
    repeat_index: int = 1

    @property
    def label(self) -> str:
        repeat_suffix = "" if self.repeat_index <= 1 else f"_rep{self.repeat_index:02d}"
        if self.mode == "direct":
            return f"modedirect_pop{self.population:03d}{repeat_suffix}"
        return (
            f"mode{self.mode}_"
            f"pop{self.population:03d}_"
            f"ticks{self.rollout_ticks:03d}_"
            f"batches{self.rollout_batches:02d}_"
            f"epochs{self.epochs:02d}_"
            f"mb{self.minibatch_size:02d}"
            f"{repeat_suffix}"
        )


@dataclass
class SweepResult:
    combo: PpoCombo
    status: str
    report_path: str | None = None
    exit_code: int | None = None
    duration_seconds: float = 0.0
    completed_generations: int = 0
    best_accuracy: float = 0.0
    best_fitness: float = 0.0
    best_accuracy_generation: int = 0
    best_fitness_generation: int = 0
    accuracy_slope_to_best: float = 0.0
    fitness_slope_to_best: float = 0.0
    mean_fitness: float = 0.0
    evaluation_failures: int = 0
    outcome: str = "unknown"
    outcome_detail: str = ""
    timed_out: bool = False
    error: str = ""

    @property
    def valid(self) -> bool:
        return (
            self.status in {"ok", "target_not_met"}
            and self.completed_generations > 0
            and not self.infrastructure_failure
        )

    @property
    def infrastructure_failure(self) -> bool:
        return (
            self.timed_out
            or is_infrastructure_failure_detail(self.outcome_detail)
            or is_infrastructure_failure_detail(self.error)
        )

    @property
    def seconds_per_generation(self) -> float | None:
        if self.completed_generations <= 0:
            return None
        return self.duration_seconds / self.completed_generations


@dataclass(frozen=True)
class ProgressMetric:
    generation: int
    best_accuracy: float
    best_fitness: float


def parse_int_list(value: str) -> list[int]:
    items: list[int] = []
    for raw in value.split(","):
        raw = raw.strip()
        if raw:
            items.append(int(raw))
    if not items:
        raise argparse.ArgumentTypeError("list must contain at least one integer")
    if any(item <= 0 for item in items):
        raise argparse.ArgumentTypeError("all values must be positive")
    return items


def parse_mode_list(value: str) -> list[str]:
    allowed = {"artifact", "direct", "combined"}
    modes: list[str] = []
    for raw in value.split(","):
        mode = raw.strip().lower()
        if not mode:
            continue
        if mode not in allowed:
            raise argparse.ArgumentTypeError(
                f"unsupported PPO mode '{mode}'. Expected one of: {', '.join(sorted(allowed))}"
            )
        modes.append(mode)
    if not modes:
        raise argparse.ArgumentTypeError("mode list must contain at least one mode")
    return modes


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(
        description="Sweep live Basics Multiplication PPO reward-policy settings."
    )
    parser.add_argument(
        "--search",
        choices=("grid", "optuna"),
        default="grid",
        help="Search strategy. Grid preserves the historical cartesian sweep; optuna samples trials from the same value lists.",
    )
    parser.add_argument("--ppo-modes", type=parse_mode_list, default=parse_mode_list("artifact"))
    parser.add_argument("--rollout-ticks", type=parse_int_list, default=parse_int_list("8,16,24"))
    parser.add_argument("--rollout-batches", type=parse_int_list, default=parse_int_list("1,2,4"))
    parser.add_argument("--epochs", type=parse_int_list, default=parse_int_list("2,3,5"))
    parser.add_argument("--minibatch-sizes", type=parse_int_list, default=parse_int_list("1,2,4"))
    parser.add_argument("--population", type=parse_int_list, default=parse_int_list("32"))
    parser.add_argument(
        "--repeat-count",
        type=int,
        default=1,
        help="Run each generated combo this many times. Repeats are separate rows with _repNN labels.",
    )
    parser.add_argument(
        "--shuffle-seed",
        type=int,
        default=None,
        help="Shuffle combo order with this deterministic seed to reduce order/runtime-state confounding.",
    )
    parser.add_argument(
        "--optuna-trials",
        type=int,
        default=20,
        help="Number of Optuna trials to run when --search optuna is selected.",
    )
    parser.add_argument(
        "--optuna-timeout-seconds",
        type=int,
        default=None,
        help="Optional total wall-clock cap for the Optuna study.",
    )
    parser.add_argument(
        "--optuna-study-name",
        default=None,
        help="Optional Optuna study name. Defaults to a timestamped Basics Multiplication study.",
    )
    parser.add_argument(
        "--optuna-storage",
        default=None,
        help="Optional Optuna storage URL, for example sqlite:///Basics/artifacts/ppo-sweeps/optuna.sqlite3.",
    )
    parser.add_argument(
        "--optuna-sampler-seed",
        type=int,
        default=None,
        help="Deterministic Optuna sampler seed. Defaults to --shuffle-seed when present.",
    )
    parser.add_argument(
        "--optuna-pruner",
        choices=("none", "median"),
        default="median",
        help="Optuna pruner to configure for the study. Optuna mode reports one intermediate value per generation.",
    )
    parser.add_argument(
        "--optuna-prune-min-generation",
        type=int,
        default=5,
        help="Do not allow Optuna to prune a trial before this completed generation.",
    )
    parser.add_argument(
        "--seed-summary",
        type=Path,
        action="append",
        default=[],
        help="Previous sweep summary.json to enqueue as Optuna seed trials. May be repeated.",
    )
    parser.add_argument(
        "--seed-top-k",
        type=int,
        default=5,
        help="Number of prior valid results to enqueue from each --seed-summary.",
    )
    parser.add_argument(
        "--narrow-from-seed",
        action="store_true",
        help="Restrict each value list to top seed values plus adjacent values from the original list.",
    )
    parser.add_argument("--max-concurrent-brains", type=int, default=32)
    parser.add_argument(
        "--reproduction-run-count",
        type=int,
        default=None,
        help="Scheduled child slots per breeding call. Defaults to max(--rollout-batches) so batch sweeps are not silently capped.",
    )
    parser.add_argument("--trial-timeout-seconds", type=int, default=600)
    parser.add_argument(
        "--max-generations",
        type=int,
        default=15,
        help="Maximum generations per trial. Use 0 to disable the generation cap.",
    )
    parser.add_argument("--request-timeout-seconds", type=int, default=15)
    parser.add_argument(
        "--progress-interval-seconds",
        type=int,
        default=10,
        help="Minimum seconds between repeated generation progress lines for the same generation.",
    )
    parser.add_argument(
        "--trial-retries",
        type=int,
        default=1,
        help="Retry retryable connection or infrastructure failures this many times before recording the row.",
    )
    parser.add_argument(
        "--retry-delay-seconds",
        type=float,
        default=5.0,
        help="Delay before retrying a retryable connection or infrastructure failure.",
    )
    parser.add_argument(
        "--max-consecutive-infrastructure-failures",
        type=int,
        default=2,
        help=(
            "Abort only after this many infrastructure/capacity failures in a row. "
            "Use 1 for the old fail-fast behavior."
        ),
    )
    parser.add_argument(
        "--verbose-harness-output",
        action="store_true",
        help="Mirror all harness stdout to the console. The full stream is always written to harness.stdout.log.",
    )
    parser.add_argument("--io-address", default="127.0.0.1:12050")
    parser.add_argument("--io-gateway-name", default="io-gateway")
    parser.add_argument("--bind-host", default="0.0.0.0")
    parser.add_argument("--advertise-host", default="127.0.0.1")
    parser.add_argument("--client-port-base", type=int, default=15120)
    parser.add_argument("--output-root", type=Path, default=None)
    parser.add_argument("--repo-root", type=Path, default=Path(__file__).resolve().parents[2])
    parser.add_argument("--dotnet", default="dotnet")
    parser.add_argument("--configuration", default="Release")
    parser.add_argument("--skip-build", action="store_true")
    parser.add_argument("--dry-run", action="store_true")
    return parser.parse_args()


def main() -> int:
    if hasattr(sys.stdout, "reconfigure"):
        sys.stdout.reconfigure(line_buffering=True)

    args = parse_args()
    if args.repeat_count <= 0:
        raise SystemExit("--repeat-count must be > 0")
    args.repo_root = args.repo_root.resolve()
    if args.output_root is None:
        args.output_root = (
            args.repo_root
            / "Basics"
            / "artifacts"
            / "ppo-sweeps"
            / datetime.now(timezone.utc).strftime("%Y%m%dT%H%M%SZ")
        )
    else:
        args.output_root = args.output_root.resolve()

    args.effective_reproduction_run_count = args.reproduction_run_count or max(args.rollout_batches)
    if args.optuna_trials <= 0:
        raise SystemExit("--optuna-trials must be > 0")
    if args.optuna_prune_min_generation < 1:
        raise SystemExit("--optuna-prune-min-generation must be >= 1")
    if args.seed_top_k <= 0:
        raise SystemExit("--seed-top-k must be > 0")

    if args.search == "optuna":
        return run_optuna_search(args)

    combos = build_combos(args)
    return run_grid_search(args, combos)


def run_grid_search(args: argparse.Namespace, combos: list[PpoCombo]) -> int:
    print(f"Planned PPO reward-policy sweep: {len(combos)} combo(s)")
    generation_budget = args.max_generations if args.max_generations > 0 else None
    population_text = ",".join(str(population) for population in args.population)
    brain_budget_text = (
        "uncapped"
        if generation_budget is None
        else ",".join(str(generation_budget * population) for population in args.population)
    )
    print(
        f"Per combo: one trial, timeout={args.trial_timeout_seconds}s, "
        f"max_generations={generation_budget if generation_budget is not None else 'none'}, "
        f"population={population_text}, modes={','.join(args.ppo_modes)}, "
        f"repeat_count={args.repeat_count}, "
        f"shuffle_seed={args.shuffle_seed if args.shuffle_seed is not None else 'none'}, "
        f"approx_eval_brains={brain_budget_text}"
    )
    print(
        "Note: latest completed 8-generation artifact-PPO sweep favored "
        "rollout_ticks=24, rollout_batches=1, epochs=3, minibatch_size=2, population=64 "
        "for Multiplication. Direct/combined modes still need fresh sweep evidence."
    )
    for combo in combos:
        if combo.mode != "direct" and combo.rollout_batches > args.effective_reproduction_run_count:
            raise SystemExit(
                f"{combo.label} asks for {combo.rollout_batches} rollout batches, but "
                f"reproduction run count is {args.effective_reproduction_run_count}."
            )
        print(f"  {format_combo(combo)}")

    if args.dry_run:
        return 0

    ensure_io_reachable(args.io_address)
    if not args.skip_build:
        build_harness(args)

    args.output_root.mkdir(parents=True, exist_ok=True)
    results_path = args.output_root / "results.jsonl"
    best: SweepResult | None = None
    results: list[SweepResult] = []
    sweep_started = time.monotonic()
    consecutive_infrastructure_failures = 0

    for index, combo in enumerate(combos, start=1):
        print()
        print(f"=== [{index}/{len(combos)}] Running {combo.label} ===")
        result = run_combo(args, combo, index, len(combos))
        results.append(result)
        with results_path.open("a", encoding="utf-8") as writer:
            writer.write(json.dumps(result_to_json(result), sort_keys=True) + "\n")

        print_result(result)
        if result.infrastructure_failure:
            consecutive_infrastructure_failures += 1
            write_summary(args.output_root, results, best)
            print_infrastructure_failure(
                result,
                consecutive_infrastructure_failures,
                args.max_consecutive_infrastructure_failures)
            if consecutive_infrastructure_failures >= args.max_consecutive_infrastructure_failures:
                print(
                    "  Aborting sweep: repeated runtime liveness/capacity failures indicate the "
                    "runtime should be restarted or WorkerNode placement inventory should be checked."
                )
                print(f"Summary: {args.output_root / 'summary.json'}")
                print(f"Rows:    {results_path}")
                return 3
            print("  Preserving prior results and continuing with the next combo.")
            print_current_best(best)
            print_sweep_progress(index, len(combos), sweep_started)
            continue
        consecutive_infrastructure_failures = 0
        if result.valid and (best is None or rank_key(result) > rank_key(best)):
            best = result
        print_current_best(best)
        print_sweep_progress(index, len(combos), sweep_started)

    write_summary(args.output_root, results, best)
    print()
    print("Final recommendation")
    if best is None:
        print("  No valid completed generation was observed; reduce load or inspect the run logs above.")
        return 2

    print_recommendation(best)
    print(f"Summary: {args.output_root / 'summary.json'}")
    print(f"Rows:    {results_path}")
    return 0


def run_optuna_search(args: argparse.Namespace) -> int:
    optuna = import_optuna()
    seed_combos = load_seed_combos(args)
    if args.narrow_from_seed and seed_combos:
        apply_seed_narrowing(args, seed_combos)

    study_name = args.optuna_study_name or f"basics-multiplication-ppo-{datetime.now(timezone.utc).strftime('%Y%m%dT%H%M%SZ')}"
    sampler_seed = args.optuna_sampler_seed if args.optuna_sampler_seed is not None else args.shuffle_seed
    sampler = optuna.samplers.TPESampler(seed=sampler_seed)
    pruner = (
        optuna.pruners.NopPruner()
        if args.optuna_pruner == "none"
        else optuna.pruners.MedianPruner(n_startup_trials=max(3, min(5, args.optuna_trials // 2)))
    )
    study = optuna.create_study(
        direction="maximize",
        study_name=study_name,
        storage=args.optuna_storage,
        load_if_exists=bool(args.optuna_storage),
        sampler=sampler,
        pruner=pruner,
    )

    for combo in seed_combos:
        if combo_is_in_search_space(args, combo):
            study.enqueue_trial(combo_to_trial_params(combo), skip_if_exists=True)

    print(f"Planned Optuna PPO reward-policy search: {args.optuna_trials} trial(s)")
    print(
        f"Search space: modes={','.join(args.ppo_modes)} population={','.join(map(str, args.population))} "
        f"ticks={','.join(map(str, args.rollout_ticks))} batches={','.join(map(str, args.rollout_batches))} "
        f"epochs={','.join(map(str, args.epochs))} minibatch={','.join(map(str, args.minibatch_sizes))}"
    )
    print(
        f"Optuna: study={study_name} storage={args.optuna_storage or 'memory'} "
        f"seed={sampler_seed if sampler_seed is not None else 'none'} pruner={args.optuna_pruner} "
        f"timeout={format_optuna_timeout(args.optuna_timeout_seconds)}"
    )
    if seed_combos:
        print(f"Seeded {sum(1 for combo in seed_combos if combo_is_in_search_space(args, combo))} prior combo(s).")

    if args.dry_run:
        for trial_number in range(1, args.optuna_trials + 1):
            trial = study.ask()
            combo = suggest_combo(args, trial, trial_number)
            print(f"  {format_combo(combo)}")
            study.tell(trial, objective_for_invalid_result())
        return 0

    ensure_io_reachable(args.io_address)
    if not args.skip_build:
        build_harness(args)

    args.output_root.mkdir(parents=True, exist_ok=True)
    results_path = args.output_root / "results.jsonl"
    best: SweepResult | None = None
    results: list[SweepResult] = []
    sweep_started = time.monotonic()
    study_deadline = None if args.optuna_timeout_seconds is None else sweep_started + max(1, args.optuna_timeout_seconds)
    consecutive_infrastructure_failures = 0

    for trial_index in range(1, args.optuna_trials + 1):
        if study_deadline is not None and time.monotonic() >= study_deadline:
            print("[optuna] stopping before next trial because the study timeout was reached.")
            break

        trial = study.ask()
        combo = suggest_combo(args, trial, trial_index)
        trial.set_user_attr("combo", asdict(combo))
        reported_generations: set[int] = set()

        def optuna_progress(progress: ProgressMetric) -> str | None:
            if progress.generation <= 0 or progress.generation in reported_generations:
                return None
            reported_generations.add(progress.generation)
            objective = objective_from_progress(progress)
            trial.report(objective, step=progress.generation)
            if progress.generation < args.optuna_prune_min_generation:
                return None
            if trial.should_prune():
                return (
                    f"optuna_pruned:generation={progress.generation}:"
                    f"objective={objective:.6f}:fitness={progress.best_fitness:.6f}:"
                    f"accuracy={progress.best_accuracy:.6f}"
                )
            return None

        print()
        print(f"=== [trial {trial_index}/{args.optuna_trials}] Running {combo.label} ===")
        result = run_combo(args, combo, trial_index, args.optuna_trials, optuna_progress)
        results.append(result)
        with results_path.open("a", encoding="utf-8") as writer:
            writer.write(json.dumps(result_to_json(result), sort_keys=True) + "\n")

        trial.set_user_attr("result", result_to_json(result))
        print_result(result)
        if result.status == "pruned":
            consecutive_infrastructure_failures = 0
            study.tell(trial, state=optuna.trial.TrialState.PRUNED)
            write_summary(args.output_root, results, best, optuna_summary(args, study))
            print(f"[optuna] pruned {combo.label}: {result.outcome_detail or result.error}")
            print_current_best(best)
            print_sweep_progress(len(results), args.optuna_trials, sweep_started)
            continue
        if result.infrastructure_failure:
            consecutive_infrastructure_failures += 1
            study.tell(trial, state=optuna.trial.TrialState.PRUNED)
            write_summary(args.output_root, results, best, optuna_summary(args, study))
            print_infrastructure_failure(
                result,
                consecutive_infrastructure_failures,
                args.max_consecutive_infrastructure_failures)
            if consecutive_infrastructure_failures >= args.max_consecutive_infrastructure_failures:
                print(
                    "  Aborting search: repeated runtime liveness/capacity failures indicate the "
                    "runtime should be restarted or WorkerNode placement inventory should be checked."
                )
                print(f"Summary: {args.output_root / 'summary.json'}")
                print(f"Rows:    {results_path}")
                return 3
            print("  Preserving prior results and continuing with the next sampled combo.")
            print_current_best(best)
            print_sweep_progress(len(results), args.optuna_trials, sweep_started)
            continue

        consecutive_infrastructure_failures = 0
        objective = objective_value(result)
        study.tell(trial, objective)
        print(f"[optuna] objective={objective:.6f} best_value={study.best_value:.6f}")
        if result.valid and (best is None or rank_key(result) > rank_key(best)):
            best = result
        print_current_best(best)
        print_sweep_progress(len(results), args.optuna_trials, sweep_started)

    write_summary(args.output_root, results, best, optuna_summary(args, study))
    print()
    print("Final recommendation")
    if best is None:
        print("  No valid completed generation was observed; reduce load or inspect the run logs above.")
        return 2

    print_recommendation(best)
    print(f"Summary: {args.output_root / 'summary.json'}")
    print(f"Rows:    {results_path}")
    return 0


def import_optuna() -> Any:
    try:
        import optuna  # type: ignore[import-not-found]
    except ModuleNotFoundError as ex:
        raise SystemExit(
            "Optuna is required for --search optuna. Install it with "
            "python3 -m pip install optuna, or rerun with --search grid."
        ) from ex
    return optuna


def suggest_combo(args: argparse.Namespace, trial: Any, trial_index: int) -> PpoCombo:
    mode = trial.suggest_categorical("mode", args.ppo_modes)
    population = trial.suggest_categorical("population", args.population)
    if mode == "direct":
        return PpoCombo(
            mode,
            population,
            args.rollout_ticks[0],
            args.rollout_batches[0],
            args.epochs[0],
            args.minibatch_sizes[0],
            repeat_index=trial_index)

    return PpoCombo(
        mode,
        population,
        trial.suggest_categorical("rollout_ticks", args.rollout_ticks),
        trial.suggest_categorical("rollout_batches", args.rollout_batches),
        trial.suggest_categorical("epochs", args.epochs),
        trial.suggest_categorical("minibatch_size", args.minibatch_sizes),
        repeat_index=trial_index)


def combo_to_trial_params(combo: PpoCombo) -> dict[str, Any]:
    return {
        "mode": combo.mode,
        "population": combo.population,
        "rollout_ticks": combo.rollout_ticks,
        "rollout_batches": combo.rollout_batches,
        "epochs": combo.epochs,
        "minibatch_size": combo.minibatch_size,
    }


def combo_is_in_search_space(args: argparse.Namespace, combo: PpoCombo) -> bool:
    return (
        combo.mode in args.ppo_modes
        and combo.population in args.population
        and (combo.mode == "direct" or combo.rollout_ticks in args.rollout_ticks)
        and (combo.mode == "direct" or combo.rollout_batches in args.rollout_batches)
        and (combo.mode == "direct" or combo.epochs in args.epochs)
        and (combo.mode == "direct" or combo.minibatch_size in args.minibatch_sizes)
    )


def objective_value(result: SweepResult) -> float:
    if not result.valid:
        return objective_for_invalid_result()
    stability_penalty = 0.0 if result.evaluation_failures == 0 and not result.timed_out else 0.05
    speed_bonus = 0.0
    if result.seconds_per_generation is not None and result.seconds_per_generation > 0:
        speed_bonus = min(0.02, 0.01 / result.seconds_per_generation)
    return (
        result.best_fitness
        + (0.10 * result.best_accuracy)
        + (0.025 * result.fitness_slope_to_best)
        + (0.010 * result.accuracy_slope_to_best)
        + speed_bonus
        - stability_penalty
    )


def objective_from_progress(progress: ProgressMetric) -> float:
    return progress.best_fitness + (0.10 * progress.best_accuracy)


def objective_for_invalid_result() -> float:
    return -1.0


def load_seed_combos(args: argparse.Namespace) -> list[PpoCombo]:
    combos: list[PpoCombo] = []
    for summary_path in args.seed_summary:
        path = summary_path.expanduser()
        if not path.is_absolute():
            path = args.repo_root / path
        if not path.exists():
            raise SystemExit(f"--seed-summary not found: {path}")
        payload = json.loads(path.read_text(encoding="utf-8"))
        result_payloads = payload.get("results") or []
        ranked = sorted(
            (item for item in result_payloads if item.get("valid")),
            key=seed_rank_key,
            reverse=True,
        )
        for item in ranked[: args.seed_top_k]:
            combo = combo_from_result_payload(item)
            if combo is not None:
                combos.append(combo)
    return dedupe_combos(combos)


def combo_from_result_payload(payload: Mapping[str, Any]) -> PpoCombo | None:
    combo = payload.get("combo")
    if not isinstance(combo, Mapping):
        return None
    mode = str(combo.get("mode") or "artifact").lower()
    if mode not in {"artifact", "direct", "combined"}:
        mode = "artifact"
    try:
        return PpoCombo(
            mode=mode,
            population=int(combo.get("population") or 0),
            rollout_ticks=int(combo.get("rollout_ticks") or combo.get("rolloutTicks") or 1),
            rollout_batches=int(combo.get("rollout_batches") or combo.get("rolloutBatches") or 1),
            epochs=int(combo.get("epochs") or 1),
            minibatch_size=int(combo.get("minibatch_size") or combo.get("minibatchSize") or 1),
        )
    except (TypeError, ValueError):
        return None


def seed_rank_key(payload: Mapping[str, Any]) -> tuple[float, float, float, float]:
    return (
        float_or_zero(payload.get("best_fitness")),
        float_or_zero(payload.get("best_accuracy")),
        float_or_zero(payload.get("fitness_slope_to_best")),
        float_or_zero(payload.get("accuracy_slope_to_best")),
    )


def dedupe_combos(combos: list[PpoCombo]) -> list[PpoCombo]:
    seen: set[tuple[str, int, int, int, int, int]] = set()
    result: list[PpoCombo] = []
    for combo in combos:
        key = (
            combo.mode,
            combo.population,
            combo.rollout_ticks,
            combo.rollout_batches,
            combo.epochs,
            combo.minibatch_size,
        )
        if key in seen:
            continue
        seen.add(key)
        result.append(combo)
    return result


def apply_seed_narrowing(args: argparse.Namespace, seed_combos: list[PpoCombo]) -> None:
    args.ppo_modes = narrow_modes(args.ppo_modes, [combo.mode for combo in seed_combos])
    args.population = narrow_int_values(args.population, [combo.population for combo in seed_combos])
    args.rollout_ticks = narrow_int_values(args.rollout_ticks, [combo.rollout_ticks for combo in seed_combos if combo.mode != "direct"])
    args.rollout_batches = narrow_int_values(args.rollout_batches, [combo.rollout_batches for combo in seed_combos if combo.mode != "direct"])
    args.epochs = narrow_int_values(args.epochs, [combo.epochs for combo in seed_combos if combo.mode != "direct"])
    args.minibatch_sizes = narrow_int_values(args.minibatch_sizes, [combo.minibatch_size for combo in seed_combos if combo.mode != "direct"])


def narrow_modes(values: list[str], selected: list[str]) -> list[str]:
    narrowed = [mode for mode in values if mode in set(selected)]
    return narrowed or values


def narrow_int_values(values: list[int], selected: list[int]) -> list[int]:
    selected_set = set(selected)
    if not selected_set:
        return values
    keep: set[int] = set()
    for index, value in enumerate(values):
        if value not in selected_set:
            continue
        keep.add(value)
        if index > 0:
            keep.add(values[index - 1])
        if index + 1 < len(values):
            keep.add(values[index + 1])
    narrowed = [value for value in values if value in keep]
    return narrowed or values


def optuna_summary(args: argparse.Namespace, study: Any) -> dict[str, Any]:
    try:
        best_value = study.best_value
    except ValueError:
        best_value = None
    return {
        "mode": "optuna",
        "study_name": study.study_name,
        "storage": args.optuna_storage,
        "trials_requested": args.optuna_trials,
        "timeout_seconds": args.optuna_timeout_seconds,
        "sampler_seed": args.optuna_sampler_seed if args.optuna_sampler_seed is not None else args.shuffle_seed,
        "pruner": args.optuna_pruner,
        "prune_min_generation": args.optuna_prune_min_generation,
        "best_value": best_value,
    }


def format_optuna_timeout(timeout_seconds: int | None) -> str:
    return "none" if timeout_seconds is None else f"{timeout_seconds}s"


def harness_project(args: argparse.Namespace) -> Path:
    return args.repo_root / "Basics" / "src" / "Basics.Harness" / "Basics.Harness.csproj"


def harness_dll(args: argparse.Namespace) -> Path:
    return (
        args.repo_root
        / "Basics"
        / "src"
        / "Basics.Harness"
        / "bin"
        / args.configuration
        / "net8.0"
        / "Nbn.Demos.Basics.Harness.dll"
    )


def build_combos(args: argparse.Namespace) -> list[PpoCombo]:
    base_combos: list[PpoCombo] = []
    for mode in args.ppo_modes:
        if mode == "direct":
            base_combos.extend(
                PpoCombo(
                    mode,
                    population,
                    args.rollout_ticks[0],
                    args.rollout_batches[0],
                    args.epochs[0],
                    args.minibatch_sizes[0])
                for population in args.population)
            continue

        base_combos.extend(
            PpoCombo(mode, population, ticks, batches, epochs, minibatch)
            for population, ticks, batches, epochs, minibatch in itertools.product(
                args.population,
                args.rollout_ticks,
                args.rollout_batches,
                args.epochs,
                args.minibatch_sizes,
            ))
    combos = [
        combo if repeat_index == 1 else replace(combo, repeat_index=repeat_index)
        for repeat_index in range(1, args.repeat_count + 1)
        for combo in base_combos
    ]
    if args.shuffle_seed is not None:
        random.Random(args.shuffle_seed).shuffle(combos)
    return combos


def build_harness(args: argparse.Namespace) -> None:
    cmd = [args.dotnet, "build", str(harness_project(args)), "-c", args.configuration]
    print(f"Building harness: {' '.join(cmd)}")
    completed = subprocess.run(cmd, cwd=args.repo_root, check=False)
    if completed.returncode != 0:
        raise SystemExit(completed.returncode)


def run_combo(
    args: argparse.Namespace,
    combo: PpoCombo,
    combo_index: int,
    combo_count: int,
    progress_callback: Callable[[ProgressMetric], str | None] | None = None,
) -> SweepResult:
    attempts = max(1, args.trial_retries + 1)
    result: SweepResult | None = None
    for attempt in range(1, attempts + 1):
        result = run_combo_attempt(args, combo, combo_index, combo_count, attempt, progress_callback)
        if not is_retryable_trial_failure(result) or attempt >= attempts:
            return result
        print(
            f"[retry] {combo.label} attempt {attempt}/{attempts} hit "
            f"{result.outcome_detail or result.error}; retrying in {args.retry_delay_seconds:g}s"
        )
        time.sleep(max(0.0, args.retry_delay_seconds))
    assert result is not None
    return result


def run_combo_attempt(
    args: argparse.Namespace,
    combo: PpoCombo,
    combo_index: int,
    combo_count: int,
    attempt: int,
    progress_callback: Callable[[ProgressMetric], str | None] | None = None,
) -> SweepResult:
    combo_dir = args.output_root / combo.label
    combo_dir.mkdir(parents=True, exist_ok=True)
    attempt_dir = combo_dir if attempt == 1 else combo_dir / f"attempt-{attempt:02d}"
    attempt_dir.mkdir(parents=True, exist_ok=True)
    config_path = attempt_dir / "harness_config.json"
    stop_request_path = attempt_dir / "optuna-prune-request.txt"
    if stop_request_path.exists():
        stop_request_path.unlink()
    write_config(args, combo, combo_index, attempt, config_path, attempt_dir, stop_request_path)

    cmd = [
        args.dotnet,
        str(harness_dll(args)),
        "--config",
        str(config_path),
    ]
    if not harness_dll(args).exists():
        cmd = [
            args.dotnet,
            "run",
            "--project",
            str(harness_project(args)),
            "-c",
            args.configuration,
            "--",
            "--config",
            str(config_path),
        ]

    started = time.monotonic()
    stdout_path = attempt_dir / "harness.stdout.log"
    report_path: Path | None = None
    latest_generation = 0
    latest_accuracy = 0.0
    latest_fitness = 0.0
    last_progress_generation = 0
    last_progress_at = 0.0
    prune_requested = False
    prune_reason = ""
    attempt_text = "" if attempt == 1 else f" attempt={attempt}"
    print(
        f"[combo] {combo_index}/{combo_count} {combo.label}{attempt_text}: "
        f"{format_combo(combo)}"
    )
    with stdout_path.open("w", encoding="utf-8") as log:
        log.write(f"# {' '.join(cmd)}\n")
        log.flush()
        last_logged_progress_line: str | None = None
        repeated_progress_lines = 0

        def flush_repeated_progress() -> None:
            nonlocal repeated_progress_lines
            if repeated_progress_lines > 0:
                log.write(f"# repeated previous progress line {repeated_progress_lines} time(s)\n")
                repeated_progress_lines = 0

        env = dict(os.environ) if args.verbose_harness_output else quiet_harness_environment(os.environ)
        process = subprocess.Popen(
            cmd,
            cwd=args.repo_root,
            stdout=subprocess.PIPE,
            stderr=subprocess.STDOUT,
            text=True,
            bufsize=1,
            env=env,
        )
        assert process.stdout is not None
        for line in process.stdout:
            stripped = line.rstrip()
            is_repeated_progress = (
                not args.verbose_harness_output
                and last_logged_progress_line == stripped
                and GEN_RE.search(stripped) is not None
            )
            if is_repeated_progress:
                repeated_progress_lines += 1
            else:
                flush_repeated_progress()
                log.write(line)
                log.flush()
                last_logged_progress_line = stripped if GEN_RE.search(stripped) is not None else None

            report_match = REPORT_RE.match(stripped)
            if report_match:
                report_path = Path(report_match.group("path")).expanduser()
                print(stripped)
                continue
            gen_match = GEN_RE.search(stripped)
            if gen_match:
                latest_generation = int(gen_match.group("generation"))
                latest_accuracy = parse_float(gen_match.group("accuracy"))
                latest_fitness = parse_float(gen_match.group("fitness"))
                if progress_callback is not None and not prune_requested:
                    prune_request = progress_callback(ProgressMetric(
                        latest_generation,
                        latest_accuracy,
                        latest_fitness))
                    if prune_request:
                        prune_requested = True
                        prune_reason = prune_request
                        stop_request_path.write_text(prune_reason + "\n", encoding="utf-8")
                        print(f"[optuna] prune requested for {combo.label}: {prune_reason}")
                now = time.monotonic()
                should_print_progress = (
                    latest_generation != last_progress_generation
                    or now - last_progress_at >= max(1, args.progress_interval_seconds)
                )
                if should_print_progress:
                    print(
                        f"[progress] {combo.label} gen={latest_generation} "
                        f"best_acc={latest_accuracy:.4f} best_fit={latest_fitness:.4f}"
                    )
                    last_progress_generation = latest_generation
                    last_progress_at = now
                continue
            if args.verbose_harness_output or should_echo_harness_line(stripped):
                print(stripped)
        flush_repeated_progress()
        log.flush()
        exit_code = process.wait()

    duration = time.monotonic() - started
    if report_path is None:
        report_path = newest_report_path(attempt_dir / "harness_reports", started)
    if report_path is None or not report_path.exists():
        if prune_requested:
            return SweepResult(
                combo=combo,
                status="pruned",
                exit_code=exit_code,
                duration_seconds=duration,
                completed_generations=latest_generation,
                best_accuracy=latest_accuracy,
                best_fitness=latest_fitness,
                best_accuracy_generation=latest_generation,
                best_fitness_generation=latest_generation,
                accuracy_slope_to_best=latest_accuracy / latest_generation if latest_generation > 0 else 0.0,
                fitness_slope_to_best=latest_fitness / latest_generation if latest_generation > 0 else 0.0,
                outcome="Pruned",
                outcome_detail=prune_reason or "optuna_pruned",
                error=f"pruned before harness report was written; see {stdout_path}",
            )
        status = "failed" if exit_code != 2 else "target_not_met"
        return SweepResult(
            combo=combo,
            status=status,
            exit_code=exit_code,
            duration_seconds=duration,
            completed_generations=latest_generation,
            best_accuracy=latest_accuracy,
            best_fitness=latest_fitness,
            error=f"no harness report found; see {stdout_path}",
        )

    return summarize_report(combo, report_path, exit_code, duration)


def write_config(
    args: argparse.Namespace,
    combo: PpoCombo,
    combo_index: int,
    attempt: int,
    config_path: Path,
    combo_dir: Path,
    stop_request_path: Path,
) -> None:
    client_port = args.client_port_base + (combo_index * 10) + (attempt - 1)
    config = {
        "RunLabel": f"ppo-sweep-multiplication-{combo.label}",
        "OutputDirectory": str(combo_dir / "harness_reports"),
        "Runtime": {
            "IoAddress": args.io_address,
            "IoGatewayName": args.io_gateway_name,
            "BindHost": args.bind_host,
            "Port": client_port,
            "AdvertiseHost": args.advertise_host,
            "AdvertisePort": client_port,
            "RequestTimeoutSeconds": args.request_timeout_seconds,
        },
        "TemplatePublishing": {
            "BindHost": args.bind_host,
            "AdvertiseHost": args.advertise_host,
            "BackingStoreRoot": str(combo_dir / "templates"),
        },
        "Environment": {
            "ClientName": f"nbn.basics.ppo-sweep.{combo.label}.attempt{attempt:02d}",
            "TaskId": "multiplication",
            "OutputObservationMode": "evented",
            "MaxReadyWindowTicks": 4,
            "SampleRepeatCount": 1,
            "StrengthSource": "base-only",
            "Template": {
                "TemplateId": f"ppo-sweep-template-{combo.label}",
                "Description": "PPO sweep seed template for Basics Multiplication.",
                "VariationBand": {
                    "MaxInternalNeuronDelta": 3,
                    "MaxAxonDelta": 14,
                    "MaxStrengthCodeDelta": 6,
                    "MaxParameterCodeDelta": 6,
                    "AllowFunctionMutation": True,
                    "AllowAxonReroute": True,
                    "AllowRegionSetChange": False,
                },
                "SeedShape": {},
            },
            "Sizing": {
                "InitialPopulationCount": combo.population,
                "MinimumPopulationCount": combo.population,
                "MaximumPopulationCount": combo.population,
                "ReproductionRunCount": args.effective_reproduction_run_count,
                "MaxConcurrentBrains": args.max_concurrent_brains,
            },
            "PpoOptimizer": {
                "Enabled": combo.mode in {"artifact", "combined"},
                "DirectRuntimeControlEnabled": combo.mode in {"direct", "combined"},
                "ObjectiveName": "multiplication",
                "RewardSignal": "basics.record_score",
                "RolloutTickCount": combo.rollout_ticks,
                "RolloutBatchCount": combo.rollout_batches,
                "ClipEpsilon": 0.20,
                "DiscountGamma": 0.99,
                "GaeLambda": 0.95,
                "LearningRate": 0.0003,
                "OptimizationEpochCount": combo.epochs,
                "MinibatchSize": combo.minibatch_size,
                "Seed": 42,
                "DirectPlasticityRateMin": 0.0005,
                "DirectPlasticityRateMax": 0.02,
                "DirectHomeostasisBaseProbabilityMin": 0.001,
                "DirectHomeostasisBaseProbabilityMax": 0.05,
            },
        },
        "Trials": {
            "MaxTrialCount": 1,
            "TrialTimeoutSeconds": args.trial_timeout_seconds,
            "MaximumGenerations": args.max_generations if args.max_generations > 0 else None,
            "TargetAccuracy": 1.0,
            "TargetFitness": 0.999,
            "RequiredSuccessfulTrials": 1,
            "AutoTuneEnabled": False,
            "PreferVectorPotentialOnFailures": False,
            "ReduceSizingOnFailures": False,
        },
        "Control": {
            "StopRequestPath": str(stop_request_path),
        },
    }
    config_path.write_text(json.dumps(config, indent=2) + "\n", encoding="utf-8")


def summarize_report(combo: PpoCombo, report_path: Path, exit_code: int, duration: float) -> SweepResult:
    report = json.loads(report_path.read_text(encoding="utf-8"))
    trials = report.get("Trials") or []
    trial = trials[-1] if trials else {}
    snapshots = trial.get("Snapshots") or []
    terminal = trial.get("TerminalSnapshot") or (snapshots[-1] if snapshots else {})
    metric_points = extract_metric_points(snapshots, terminal)
    best_accuracy, best_accuracy_generation, accuracy_slope = summarize_metric(metric_points, "BestAccuracy")
    best_fitness, best_fitness_generation, fitness_slope = summarize_metric(metric_points, "BestFitness")
    completed_generations = max([int_or_zero(s.get("Generation")) for s in snapshots] + [int_or_zero(terminal.get("Generation"))])
    evaluation_failures = max(
        [int_or_zero(s.get("EvaluationFailureCount")) for s in snapshots]
        + [int_or_zero(terminal.get("EvaluationFailureCount"))]
    )
    outcome = str(trial.get("Outcome") or "unknown")
    status = "ok" if exit_code == 0 else "target_not_met" if exit_code == 2 else "failed"
    outcome_detail = str(trial.get("OutcomeDetail") or "")
    outcome_detail_normalized = outcome_detail.lower()
    timed_out = (
        outcome.lower() == "timedout"
        or "timed out" in outcome_detail_normalized
        or "trial_timeout" in outcome_detail_normalized
    )
    if is_infrastructure_failure_detail(outcome_detail):
        status = "infrastructure_failed"
    elif outcome_detail.startswith("optuna_pruned"):
        status = "pruned"
    return SweepResult(
        combo=combo,
        status=status,
        report_path=str(report_path),
        exit_code=exit_code,
        duration_seconds=float_or_zero(trial.get("DurationSeconds")) or duration,
        completed_generations=completed_generations,
        best_accuracy=best_accuracy,
        best_fitness=best_fitness,
        best_accuracy_generation=best_accuracy_generation,
        best_fitness_generation=best_fitness_generation,
        accuracy_slope_to_best=accuracy_slope,
        fitness_slope_to_best=fitness_slope,
        mean_fitness=float_or_zero(terminal.get("MeanFitness")),
        evaluation_failures=evaluation_failures,
        outcome=outcome,
        outcome_detail=outcome_detail,
        timed_out=timed_out,
    )


def newest_report_path(report_dir: Path, started_monotonic: float | None = None) -> Path | None:
    if not report_dir.exists():
        return None
    reports = sorted(report_dir.glob("*.json"), key=lambda path: path.stat().st_mtime, reverse=True)
    if started_monotonic is not None:
        started_wall = time.time() - max(0.0, time.monotonic() - started_monotonic) - 1.0
        reports = [path for path in reports if path.stat().st_mtime >= started_wall]
    return reports[0] if reports else None


def rank_key(result: SweepResult) -> tuple[float, float, float, float, int, float]:
    stability_penalty = 0 if result.evaluation_failures == 0 and not result.timed_out else -1
    speed = 0.0
    if result.seconds_per_generation is not None and result.seconds_per_generation > 0:
        speed = 1.0 / result.seconds_per_generation
    return (
        result.best_fitness,
        result.best_accuracy,
        result.fitness_slope_to_best,
        result.accuracy_slope_to_best,
        stability_penalty,
        speed,
    )


def print_result(result: SweepResult) -> None:
    sec_gen = result.seconds_per_generation
    sec_gen_text = "n/a" if sec_gen is None else f"{sec_gen:.1f}"
    print(
        f"[result] {result.combo.label} status={result.status} outcome={result.outcome} "
        f"gens={result.completed_generations} acc={result.best_accuracy:.4f} "
        f"fitness={result.best_fitness:.4f} eval_failures={result.evaluation_failures} "
        f"slope_acc={result.accuracy_slope_to_best:.4f}/gen "
        f"slope_fit={result.fitness_slope_to_best:.4f}/gen sec/gen={sec_gen_text}"
    )
    if result.error:
        print(f"[result] error={result.error}")


def print_current_best(best: SweepResult | None) -> None:
    if best is None:
        print("[current best] none yet")
        return
    if best.combo.mode == "direct":
        print(
            f"[current best] mode=direct population={best.combo.population} "
            f"fitness={best.best_fitness:.4f} acc={best.best_accuracy:.4f} "
            f"gens={best.completed_generations} "
            f"slope_fit={best.fitness_slope_to_best:.4f}/gen "
            f"slope_acc={best.accuracy_slope_to_best:.4f}/gen"
        )
        return
    print(
        f"[current best] mode={best.combo.mode} ticks={best.combo.rollout_ticks} batches={best.combo.rollout_batches} "
        f"epochs={best.combo.epochs} minibatch={best.combo.minibatch_size} "
        f"population={best.combo.population} "
        f"fitness={best.best_fitness:.4f} acc={best.best_accuracy:.4f} "
        f"gens={best.completed_generations} "
        f"slope_fit={best.fitness_slope_to_best:.4f}/gen "
        f"slope_acc={best.accuracy_slope_to_best:.4f}/gen"
    )


def print_sweep_progress(completed: int, total: int, started: float) -> None:
    elapsed = time.monotonic() - started
    remaining = total - completed
    eta = "n/a"
    if completed > 0 and remaining > 0:
        eta = format_duration((elapsed / completed) * remaining)
    print(
        f"[sweep] completed={completed}/{total} elapsed={format_duration(elapsed)} "
        f"eta={eta}"
    )


def print_recommendation(best: SweepResult) -> None:
    if best.combo.mode == "direct":
        print(
            f"  mode:            {best.combo.mode}\n"
            f"  population:      {best.combo.population}\n"
            "  artifact params: ignored in direct-only mode\n"
            f"  observed fitness {best.best_fitness:.4f}, accuracy {best.best_accuracy:.4f}, "
            f"{best.completed_generations} generation(s)\n"
            f"  best fitness gen {best.best_fitness_generation}, slope {best.fitness_slope_to_best:.4f}/gen\n"
            f"  best accuracy gen {best.best_accuracy_generation}, slope {best.accuracy_slope_to_best:.4f}/gen"
        )
        return

    print(
        f"  mode:            {best.combo.mode}\n"
        f"  rollout ticks:   {best.combo.rollout_ticks}\n"
        f"  rollout batches: {best.combo.rollout_batches}\n"
        f"  epochs:          {best.combo.epochs}\n"
        f"  minibatch size:  {best.combo.minibatch_size}\n"
        f"  population:      {best.combo.population}\n"
        f"  observed fitness {best.best_fitness:.4f}, accuracy {best.best_accuracy:.4f}, "
        f"{best.completed_generations} generation(s)\n"
        f"  best fitness gen {best.best_fitness_generation}, slope {best.fitness_slope_to_best:.4f}/gen\n"
        f"  best accuracy gen {best.best_accuracy_generation}, slope {best.accuracy_slope_to_best:.4f}/gen"
    )


def format_combo(combo: PpoCombo) -> str:
    if combo.mode == "direct":
        return (
            f"{combo.label}: mode=direct population={combo.population} "
            "direct_runtime_control=true artifact_params=ignored"
        )

    return (
        f"{combo.label}: mode={combo.mode} population={combo.population} ticks={combo.rollout_ticks} "
        f"batches={combo.rollout_batches} epochs={combo.epochs} minibatch={combo.minibatch_size}"
    )


def is_retryable_trial_failure(result: SweepResult) -> bool:
    return is_retryable_connection_failure(result) or (
        result.infrastructure_failure
        and not result.timed_out
        and result.completed_generations == 0)


def is_retryable_connection_failure(result: SweepResult) -> bool:
    detail = f"{result.outcome_detail} {result.error}".lower()
    return result.completed_generations == 0 and (
        "connect_failed" in detail
        or "address already in use" in detail
        or "connection refused" in detail
        or "runtime_client_failed" in detail
        or "runtimeclientfailed" in detail
    )


def extract_metric_points(snapshots: list[Any], terminal: Mapping[str, Any]) -> list[tuple[int, Mapping[str, Any]]]:
    points: list[tuple[int, Mapping[str, Any]]] = [(0, {})]
    for snapshot in snapshots:
        if isinstance(snapshot, Mapping):
            points.append((int_or_zero(snapshot.get("Generation")), snapshot))
    if terminal:
        points.append((int_or_zero(terminal.get("Generation")), terminal))
    return points


def summarize_metric(points: list[tuple[int, Mapping[str, Any]]], field_name: str) -> tuple[float, int, float]:
    best_value = max([float_or_zero(snapshot.get(field_name)) for _, snapshot in points] + [0.0])
    best_generation = 0
    for generation, snapshot in points:
        value = float_or_zero(snapshot.get(field_name))
        if abs(value - best_value) <= 0.000_001:
            best_generation = generation
            break
    slope = best_value / best_generation if best_generation > 0 else 0.0
    return best_value, best_generation, slope


def print_infrastructure_failure(
    result: SweepResult,
    consecutive_failures: int,
    max_consecutive_failures: int,
) -> None:
    print()
    print("Infrastructure failure")
    print(
        f"  Row failed with runtime liveness/capacity detail "
        f"({consecutive_failures}/{max_consecutive_failures} consecutive)."
    )
    print(f"  Detail: {result.outcome_detail or result.error}")


def write_summary(
    output_root: Path,
    results: list[SweepResult],
    best: SweepResult | None,
    search: Mapping[str, Any] | None = None,
) -> None:
    payload = {
        "completed_at_utc": datetime.now(timezone.utc).isoformat(),
        "best": None if best is None else result_to_json(best),
        "results": [result_to_json(result) for result in results],
    }
    if search is not None:
        payload["search"] = dict(search)
    (output_root / "summary.json").write_text(json.dumps(payload, indent=2, sort_keys=True) + "\n", encoding="utf-8")


def result_to_json(result: SweepResult) -> dict[str, Any]:
    payload = asdict(result)
    payload["combo"] = asdict(result.combo)
    payload["seconds_per_generation"] = result.seconds_per_generation
    payload["valid"] = result.valid
    return payload


def ensure_io_reachable(address: str) -> None:
    host, port = address.rsplit(":", 1)
    deadline = time.monotonic() + 3.0
    while time.monotonic() < deadline:
        try:
            with socket.create_connection((host.strip("[]"), int(port)), timeout=0.5):
                return
        except OSError:
            time.sleep(0.2)
    raise SystemExit(f"IO Gateway is not reachable at {address}; start the runtime stack first.")


def quiet_harness_environment(source: Mapping[str, str]) -> dict[str, str]:
    env = dict(source)
    env["Logging__LogLevel__Microsoft"] = "Warning"
    env["Logging__LogLevel__Microsoft.AspNetCore"] = "Warning"
    env["Logging__LogLevel__Microsoft.AspNetCore.Hosting.Diagnostics"] = "Warning"
    env["Logging__LogLevel__Microsoft.AspNetCore.Routing.EndpointMiddleware"] = "Warning"
    env["Logging__LogLevel__Microsoft.Hosting.Lifetime"] = "Warning"
    return env


def should_echo_harness_line(line: str) -> bool:
    if not line:
        return False
    lowered = line.lower()
    return (
        line.startswith("[trial ")
        or line.startswith("Report:")
        or line.startswith("Stable target ")
        or lowered.startswith("warn:")
        or lowered.startswith("fail:")
        or lowered.startswith("critical:")
        or "exception" in lowered
        or "failed" in lowered
        or "timed out" in lowered
        or "trial_timeout" in lowered
    )


def format_duration(seconds: float) -> str:
    seconds = max(0, int(seconds))
    minutes, sec = divmod(seconds, 60)
    hours, minutes = divmod(minutes, 60)
    if hours:
        return f"{hours}h{minutes:02d}m{sec:02d}s"
    if minutes:
        return f"{minutes}m{sec:02d}s"
    return f"{sec}s"


def parse_float(value: str) -> float:
    try:
        return float(value)
    except (TypeError, ValueError):
        return 0.0


def float_or_zero(value: Any) -> float:
    try:
        return float(value)
    except (TypeError, ValueError):
        return 0.0


def int_or_zero(value: Any) -> int:
    try:
        return int(value)
    except (TypeError, ValueError):
        return 0


def is_infrastructure_failure_detail(value: str | None) -> bool:
    if not value:
        return False
    normalized = value.lower()
    return (
        "spawn_worker_unavailable" in normalized
        or "no eligible workers are available for placement" in normalized
        or (
            "output_timeout_or_width_mismatch:vector_missing" in normalized
            and "vectors_seen=0" in normalized
            and "last_tick=0" in normalized
        )
    )


if __name__ == "__main__":
    raise SystemExit(main())
