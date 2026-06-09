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
import re
import socket
import subprocess
import sys
import time
from dataclasses import asdict, dataclass
from datetime import datetime, timezone
from pathlib import Path
from typing import Any, Mapping


GEN_RE = re.compile(
    r"\[trial\s+(?P<trial>\d+)\]\s+gen\s+(?P<generation>\d+)\s+"
    r"(?P<state>\w+):\s+accuracy=(?P<accuracy>[-+0-9.]+)\s+"
    r"fitness=(?P<fitness>[-+0-9.]+)",
    re.IGNORECASE,
)
REPORT_RE = re.compile(r"^Report:\s+(?P<path>.+)$")


@dataclass(frozen=True)
class PpoCombo:
    rollout_ticks: int
    rollout_batches: int
    epochs: int
    minibatch_size: int

    @property
    def label(self) -> str:
        return (
            f"ticks{self.rollout_ticks:03d}_"
            f"batches{self.rollout_batches:02d}_"
            f"epochs{self.epochs:02d}_"
            f"mb{self.minibatch_size:02d}"
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
        return is_infrastructure_failure_detail(self.outcome_detail) or is_infrastructure_failure_detail(self.error)

    @property
    def seconds_per_generation(self) -> float | None:
        if self.completed_generations <= 0:
            return None
        return self.duration_seconds / self.completed_generations


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


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(
        description="Sweep live Basics Multiplication PPO reward-policy settings."
    )
    parser.add_argument("--rollout-ticks", type=parse_int_list, default=parse_int_list("32"))
    parser.add_argument("--rollout-batches", type=parse_int_list, default=parse_int_list("1,2,4"))
    parser.add_argument("--epochs", type=parse_int_list, default=parse_int_list("5"))
    parser.add_argument("--minibatch-sizes", type=parse_int_list, default=parse_int_list("16"))
    parser.add_argument("--population", type=int, default=256)
    parser.add_argument("--max-concurrent-brains", type=int, default=32)
    parser.add_argument(
        "--reproduction-run-count",
        type=int,
        default=None,
        help="Scheduled child slots per breeding call. Defaults to max(--rollout-batches) so batch sweeps are not silently capped.",
    )
    parser.add_argument("--trial-timeout-seconds", type=int, default=900)
    parser.add_argument(
        "--max-generations",
        type=int,
        default=30,
        help="Maximum generations per trial. Use 0 to disable the generation cap.",
    )
    parser.add_argument("--request-timeout-seconds", type=int, default=120)
    parser.add_argument(
        "--progress-interval-seconds",
        type=int,
        default=10,
        help="Minimum seconds between repeated generation progress lines for the same generation.",
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
    args = parse_args()
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

    combos = [
        PpoCombo(ticks, batches, epochs, minibatch)
        for ticks, batches, epochs, minibatch in itertools.product(
            args.rollout_ticks,
            args.rollout_batches,
            args.epochs,
            args.minibatch_sizes,
        )
    ]
    args.effective_reproduction_run_count = args.reproduction_run_count or max(args.rollout_batches)

    print(f"Planned PPO reward-policy sweep: {len(combos)} combo(s)")
    generation_budget = args.max_generations if args.max_generations > 0 else None
    brain_budget = None if generation_budget is None else generation_budget * args.population
    print(
        f"Per combo: one trial, timeout={args.trial_timeout_seconds}s, "
        f"max_generations={generation_budget if generation_budget is not None else 'none'}, "
        f"approx_eval_brains={brain_budget if brain_budget is not None else 'uncapped'}"
    )
    print(
        "Note: first-pass results only produced useful completed generations at "
        "batch=1; higher batch counts are included as deliberate load probes."
    )
    for combo in combos:
        if combo.rollout_batches > args.effective_reproduction_run_count:
            raise SystemExit(
                f"{combo.label} asks for {combo.rollout_batches} rollout batches, but "
                f"reproduction run count is {args.effective_reproduction_run_count}."
            )
        print(
            f"  {combo.label}: ticks={combo.rollout_ticks} "
            f"batches={combo.rollout_batches} epochs={combo.epochs} "
            f"minibatch={combo.minibatch_size}"
        )

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

    for index, combo in enumerate(combos, start=1):
        print()
        print(f"=== [{index}/{len(combos)}] Running {combo.label} ===")
        result = run_combo(args, combo, index, len(combos))
        results.append(result)
        with results_path.open("a", encoding="utf-8") as writer:
            writer.write(json.dumps(result_to_json(result), sort_keys=True) + "\n")

        print_result(result)
        if result.infrastructure_failure:
            write_summary(args.output_root, results, best)
            print()
            print("Infrastructure failure")
            print(
                "  Aborting sweep: the harness could not spawn evaluation brains because "
                "HiveMind reported no eligible workers. Start/verify WorkerNode capacity in "
                "Workbench, then rerun the sweep."
            )
            print(f"  Detail: {result.outcome_detail or result.error}")
            print(f"Summary: {args.output_root / 'summary.json'}")
            print(f"Rows:    {results_path}")
            return 3
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


def build_harness(args: argparse.Namespace) -> None:
    cmd = [args.dotnet, "build", str(harness_project(args)), "-c", args.configuration]
    print(f"Building harness: {' '.join(cmd)}")
    completed = subprocess.run(cmd, cwd=args.repo_root, check=False)
    if completed.returncode != 0:
        raise SystemExit(completed.returncode)


def run_combo(args: argparse.Namespace, combo: PpoCombo, combo_index: int, combo_count: int) -> SweepResult:
    combo_dir = args.output_root / combo.label
    combo_dir.mkdir(parents=True, exist_ok=True)
    config_path = combo_dir / "harness_config.json"
    write_config(args, combo, combo_index, config_path, combo_dir)

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
    stdout_path = combo_dir / "harness.stdout.log"
    report_path: Path | None = None
    latest_generation = 0
    latest_accuracy = 0.0
    latest_fitness = 0.0
    last_progress_generation = 0
    last_progress_at = 0.0
    print(
        f"[combo] {combo_index}/{combo_count} {combo.label}: "
        f"ticks={combo.rollout_ticks} batches={combo.rollout_batches} "
        f"epochs={combo.epochs} minibatch={combo.minibatch_size}"
    )
    with stdout_path.open("w", encoding="utf-8") as log:
        log.write(f"# {' '.join(cmd)}\n")
        log.flush()
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
            log.write(line)
            log.flush()
            stripped = line.rstrip()
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
        exit_code = process.wait()

    duration = time.monotonic() - started
    if report_path is None:
        report_path = newest_report_path(combo_dir / "harness_reports")
    if report_path is None or not report_path.exists():
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
    config_path: Path,
    combo_dir: Path,
) -> None:
    client_port = args.client_port_base + combo_index
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
            "ClientName": f"nbn.basics.ppo-sweep.{combo.label}",
            "TaskId": "multiplication",
            "OutputObservationMode": "evented",
            "MaxReadyWindowTicks": 4,
            "SampleRepeatCount": 1,
            "StrengthSource": "base-only",
            "Template": {
                "TemplateId": f"ppo-sweep-template-{combo.label}",
                "Description": "PPO sweep seed template for Basics Multiplication.",
                "VariationBand": {
                    "MaxInternalNeuronDelta": 2,
                    "MaxAxonDelta": 8,
                    "MaxStrengthCodeDelta": 4,
                    "MaxParameterCodeDelta": 4,
                    "AllowFunctionMutation": False,
                    "AllowAxonReroute": True,
                    "AllowRegionSetChange": False,
                },
                "SeedShape": {},
            },
            "Sizing": {
                "InitialPopulationCount": args.population,
                "MinimumPopulationCount": args.population,
                "MaximumPopulationCount": args.population,
            "ReproductionRunCount": args.effective_reproduction_run_count,
                "MaxConcurrentBrains": args.max_concurrent_brains,
            },
            "PpoOptimizer": {
                "Enabled": True,
                "ObjectiveName": "multiplication",
                "RewardSignal": "basics.fitness",
                "RolloutTickCount": combo.rollout_ticks,
                "RolloutBatchCount": combo.rollout_batches,
                "ClipEpsilon": 0.20,
                "DiscountGamma": 0.99,
                "GaeLambda": 0.95,
                "LearningRate": 0.0003,
                "OptimizationEpochCount": combo.epochs,
                "MinibatchSize": combo.minibatch_size,
                "Seed": 42,
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
    }
    config_path.write_text(json.dumps(config, indent=2) + "\n", encoding="utf-8")


def summarize_report(combo: PpoCombo, report_path: Path, exit_code: int, duration: float) -> SweepResult:
    report = json.loads(report_path.read_text(encoding="utf-8"))
    trials = report.get("Trials") or []
    trial = trials[-1] if trials else {}
    snapshots = trial.get("Snapshots") or []
    terminal = trial.get("TerminalSnapshot") or (snapshots[-1] if snapshots else {})
    best_accuracy = max([float_or_zero(s.get("BestAccuracy")) for s in snapshots] + [float_or_zero(terminal.get("BestAccuracy"))])
    best_fitness = max([float_or_zero(s.get("BestFitness")) for s in snapshots] + [float_or_zero(terminal.get("BestFitness"))])
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
    return SweepResult(
        combo=combo,
        status=status,
        report_path=str(report_path),
        exit_code=exit_code,
        duration_seconds=float_or_zero(trial.get("DurationSeconds")) or duration,
        completed_generations=completed_generations,
        best_accuracy=best_accuracy,
        best_fitness=best_fitness,
        mean_fitness=float_or_zero(terminal.get("MeanFitness")),
        evaluation_failures=evaluation_failures,
        outcome=outcome,
        outcome_detail=outcome_detail,
        timed_out=timed_out,
    )


def newest_report_path(report_dir: Path) -> Path | None:
    if not report_dir.exists():
        return None
    reports = sorted(report_dir.glob("*.json"), key=lambda path: path.stat().st_mtime, reverse=True)
    return reports[0] if reports else None


def rank_key(result: SweepResult) -> tuple[float, float, int, float]:
    stability_penalty = 0 if result.evaluation_failures == 0 and not result.timed_out else -1
    speed = 0.0
    if result.seconds_per_generation is not None and result.seconds_per_generation > 0:
        speed = 1.0 / result.seconds_per_generation
    return (result.best_fitness, result.best_accuracy, stability_penalty, speed)


def print_result(result: SweepResult) -> None:
    sec_gen = result.seconds_per_generation
    sec_gen_text = "n/a" if sec_gen is None else f"{sec_gen:.1f}"
    print(
        f"[result] {result.combo.label} status={result.status} outcome={result.outcome} "
        f"gens={result.completed_generations} acc={result.best_accuracy:.4f} "
        f"fitness={result.best_fitness:.4f} eval_failures={result.evaluation_failures} "
        f"sec/gen={sec_gen_text}"
    )
    if result.error:
        print(f"[result] error={result.error}")


def print_current_best(best: SweepResult | None) -> None:
    if best is None:
        print("[current best] none yet")
        return
    print(
        f"[current best] ticks={best.combo.rollout_ticks} batches={best.combo.rollout_batches} "
        f"epochs={best.combo.epochs} minibatch={best.combo.minibatch_size} "
        f"fitness={best.best_fitness:.4f} acc={best.best_accuracy:.4f} "
        f"gens={best.completed_generations}"
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
    print(
        f"  rollout ticks:   {best.combo.rollout_ticks}\n"
        f"  rollout batches: {best.combo.rollout_batches}\n"
        f"  epochs:          {best.combo.epochs}\n"
        f"  minibatch size:  {best.combo.minibatch_size}\n"
        f"  observed fitness {best.best_fitness:.4f}, accuracy {best.best_accuracy:.4f}, "
        f"{best.completed_generations} generation(s)"
    )


def write_summary(output_root: Path, results: list[SweepResult], best: SweepResult | None) -> None:
    payload = {
        "completed_at_utc": datetime.now(timezone.utc).isoformat(),
        "best": None if best is None else result_to_json(best),
        "results": [result_to_json(result) for result in results],
    }
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
    )


if __name__ == "__main__":
    raise SystemExit(main())
