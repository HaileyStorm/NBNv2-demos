#!/usr/bin/env python3
"""Benchmark Basics Multiplication population/worker/concurrency throughput.

This tool is intentionally conservative about process ownership:
it starts and stops only the local WorkerNode children it creates, and assumes
the core NBN runtime stack is already running.
"""

from __future__ import annotations

import argparse
import atexit
import csv
import json
import os
import re
import signal
import socket
import subprocess
import sys
import time
from dataclasses import dataclass, field
from datetime import datetime, timezone
from pathlib import Path
from typing import Any, Iterable


POPULATIONS = (64, 128, 256)
BASE_WORKER_COUNTS = (8, 16, 32, 64)
BASE_CONCURRENCIES = (8, 16, 32, 64)
OPTIONAL_CONCURRENCY = 128
OPTIONAL_WORKER_COUNT = 4
MIN_DURATION_SECONDS = 300
DEGRADATION_RATIO = 1.35
MIN_COMPLETED_GENERATIONS_FOR_SKIP = 2
HARNESS_ASSEMBLY_NAME = "Nbn.Demos.Basics.Harness.dll"
WORKER_ASSEMBLY_NAME = "Nbn.Runtime.WorkerNode.dll"
LOCAL_PYTHON_PACKAGE_ROOT = Path("Basics") / "artifacts" / "benchmarks" / "python-packages"

GENERATION_EVALUATED_RE = re.compile(r"Generation\s+(\d+)\s+evaluated", re.IGNORECASE)


@dataclass(frozen=True, order=True)
class Combo:
    population: int
    worker_count: int
    max_concurrent: int
    reason: str = "base"

    @property
    def key(self) -> tuple[int, int, int]:
        return (self.population, self.worker_count, self.max_concurrent)


@dataclass
class RunMetrics:
    combo: Combo
    status: str
    attempt: int
    report_path: Path | None = None
    error: str = ""
    harness_exit_code: int | None = None
    duration_seconds: float = 0.0
    completed_generations: int = 0
    avg_seconds_per_generation: float | None = None
    generations_per_minute: float | None = None
    avg_timing_total_seconds: float | None = None
    avg_observation_seconds: float | None = None
    avg_spawn_request_seconds: float | None = None
    avg_placement_wait_seconds: float | None = None
    avg_setup_seconds: float | None = None
    failed_brains: int = 0
    evaluation_failures: int = 0
    best_accuracy: float | None = None
    best_fitness: float | None = None

    @property
    def success(self) -> bool:
        return self.status == "ok" and self.avg_seconds_per_generation is not None


@dataclass
class BenchmarkState:
    output_root: Path
    active_workers: list[subprocess.Popen[str]] = field(default_factory=list)
    attempt_rows: list[RunMetrics] = field(default_factory=list)
    final_rows: dict[tuple[int, int, int], RunMetrics] = field(default_factory=dict)
    queued_keys: set[tuple[int, int, int]] = field(default_factory=set)
    completed_by_key: dict[tuple[int, int, int], RunMetrics] = field(default_factory=dict)
    skipped_by_key: dict[tuple[int, int, int], str] = field(default_factory=dict)


def parse_int_list(value: str) -> tuple[int, ...]:
    items: list[int] = []
    for raw in value.split(","):
        raw = raw.strip()
        if not raw:
            continue
        items.append(int(raw))
    if not items:
        raise argparse.ArgumentTypeError("list must contain at least one integer")
    return tuple(items)


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(
        description=(
            "Run a Basics Multiplication throughput benchmark across population, "
            "local Worker count, and max-concurrent settings."
        )
    )
    parser.add_argument(
        "--duration-seconds",
        type=int,
        default=MIN_DURATION_SECONDS,
        help="Seconds to run each configuration. Defaults to 300; values below 300 are rejected unless --allow-short-runs is set.",
    )
    parser.add_argument(
        "--allow-short-runs",
        action="store_true",
        help="Allow --duration-seconds below 300 for smoke/debug runs.",
    )
    parser.add_argument(
        "--populations",
        type=parse_int_list,
        default=POPULATIONS,
        help="Comma-separated populations. Default: 64,128,256.",
    )
    parser.add_argument(
        "--worker-counts",
        type=parse_int_list,
        default=BASE_WORKER_COUNTS,
        help="Comma-separated base local Worker counts. Default: 8,16,32,64.",
    )
    parser.add_argument(
        "--max-concurrencies",
        type=parse_int_list,
        default=BASE_CONCURRENCIES,
        help="Comma-separated base max-concurrent counts. Default: 8,16,32,64.",
    )
    parser.add_argument(
        "--include-optional-worker",
        type=int,
        default=OPTIONAL_WORKER_COUNT,
        help="Worker count to add dynamically when 8 workers wins for a population/concurrency. Default: 4.",
    )
    parser.add_argument(
        "--include-optional-concurrency",
        type=int,
        default=OPTIONAL_CONCURRENCY,
        help="Concurrency to add dynamically when 64 concurrency wins for a population/worker. Default: 128.",
    )
    parser.add_argument(
        "--repo-root",
        type=Path,
        default=Path(__file__).resolve().parents[2],
        help="NBNv2-demos repo root. Default: inferred from this script.",
    )
    parser.add_argument(
        "--runtime-root",
        type=Path,
        default=None,
        help="Sibling NBNv2 repo root. Default: ../NBNv2 relative to repo root.",
    )
    parser.add_argument(
        "--output-root",
        type=Path,
        default=None,
        help="Benchmark output root. Default: Basics/artifacts/benchmarks/multiplication_perf/<timestamp>.",
    )
    parser.add_argument(
        "--dotnet",
        default="dotnet",
        help="dotnet executable. Default: dotnet.",
    )
    parser.add_argument(
        "--configuration",
        default="Release",
        help="Build configuration for prebuilt assemblies. Default: Release.",
    )
    parser.add_argument(
        "--harness-assembly",
        type=Path,
        default=None,
        help="Explicit Basics harness DLL path. Auto-detected from standard bin/ and .artifacts-* outputs when omitted.",
    )
    parser.add_argument(
        "--worker-assembly",
        type=Path,
        default=None,
        help="Explicit WorkerNode DLL path. Auto-detected from standard bin/ and .artifacts-* outputs when omitted.",
    )
    parser.add_argument(
        "--harness-port-base",
        type=int,
        default=15000,
        help="Base port for sequential harness clients. Default: 15000.",
    )
    parser.add_argument(
        "--worker-port-base",
        type=int,
        default=13040,
        help="Base port for benchmark-owned local Workers. Default: 13040.",
    )
    parser.add_argument(
        "--bind-host",
        default="0.0.0.0",
        help="Bind host for benchmark-owned local Workers and harness template publishing. Default: 0.0.0.0.",
    )
    parser.add_argument(
        "--advertise-host",
        default="127.0.0.1",
        help="Advertise host for benchmark-owned local Workers. Default: 127.0.0.1.",
    )
    parser.add_argument(
        "--io-address",
        default="127.0.0.1:12050",
        help="IO Gateway address for the already-running runtime stack. Default: 127.0.0.1:12050.",
    )
    parser.add_argument(
        "--io-gateway-name",
        default="io-gateway",
        help="IO Gateway actor name. Default: io-gateway.",
    )
    parser.add_argument(
        "--settings-host",
        default="127.0.0.1",
        help="SettingsMonitor host for benchmark-owned local Workers. Default: 127.0.0.1.",
    )
    parser.add_argument(
        "--settings-port",
        type=int,
        default=12010,
        help="SettingsMonitor port for benchmark-owned local Workers. Default: 12010.",
    )
    parser.add_argument(
        "--settings-name",
        default="SettingsMonitor",
        help="SettingsMonitor actor name for benchmark-owned local Workers. Default: SettingsMonitor.",
    )
    parser.add_argument(
        "--storage-pct",
        type=int,
        default=95,
        help="Worker storage pressure percentage argument. Default: 95.",
    )
    parser.add_argument(
        "--worker-startup-timeout",
        type=float,
        default=20.0,
        help="Seconds to wait for each benchmark-owned Worker port to open. Default: 20.",
    )
    parser.add_argument(
        "--worker-warmup-seconds",
        type=float,
        default=8.0,
        help="Seconds to wait after starting Workers before launching the harness. Default: 8.",
    )
    parser.add_argument(
        "--request-timeout-seconds",
        type=int,
        default=30,
        help="Harness runtime request timeout. Default: 30.",
    )
    parser.add_argument(
        "--retries",
        type=int,
        default=1,
        help="Retry count after a failed combo. Default: 1.",
    )
    parser.add_argument(
        "--skip-degradation-ratio",
        type=float,
        default=DEGRADATION_RATIO,
        help="Skip larger concurrencies for same population/workers when a completed run is this much slower than best prior concurrency. Default: 1.35.",
    )
    parser.add_argument(
        "--install-tqdm",
        action="store_true",
        help="Install tqdm into a repo-local package directory if it is missing.",
    )
    parser.add_argument(
        "--dry-run",
        action="store_true",
        help="Print the planned initial matrix and exit without starting Workers or harness runs.",
    )
    parser.add_argument(
        "--allow-existing-workers",
        action="store_true",
        help="Allow the benchmark to run when WorkerNode processes already exist. By default this is blocked to avoid contaminating worker-count results.",
    )
    return parser.parse_args()


class SimpleProgress:
    """Small tqdm-compatible fallback for externally managed Python installs."""

    def __init__(self, total: int, unit: str = "item", dynamic_ncols: bool = True) -> None:
        self.total = total
        self.unit = unit
        self.dynamic_ncols = dynamic_ncols
        self.n = 0
        self.description = ""
        self._last_rendered_at = 0.0

    def __enter__(self) -> "SimpleProgress":
        self.refresh()
        return self

    def __exit__(self, exc_type: Any, exc: Any, traceback: Any) -> None:
        self.refresh()
        print()

    def set_description(self, description: str) -> None:
        self.description = description
        self.refresh()

    def update(self, count: int = 1) -> None:
        self.n += count
        self._render(force=self.n >= self.total)

    def refresh(self) -> None:
        self._render(force=True)

    def _render(self, *, force: bool = False) -> None:
        now = time.monotonic()
        if not force and now - self._last_rendered_at < 0.5:
            return
        self._last_rendered_at = now
        percent = 100.0 if self.total <= 0 else min(100.0, self.n * 100.0 / self.total)
        prefix = f"{self.description}: " if self.description else ""
        print(f"\r{prefix}{self.n}/{self.total} {self.unit} ({percent:5.1f}%)", end="", flush=True)


def load_tqdm(install_if_missing: bool, package_root: Path) -> Any:
    if package_root.exists():
        sys.path.insert(0, str(package_root))

    try:
        from tqdm import tqdm  # type: ignore

        return tqdm
    except ImportError:
        if not install_if_missing:
            print("tqdm is not installed; using built-in progress fallback.", file=sys.stderr)
            return SimpleProgress

        package_root.mkdir(parents=True, exist_ok=True)
        install = subprocess.run(
            [sys.executable, "-m", "pip", "install", "--target", str(package_root), "tqdm"],
            stdout=subprocess.PIPE,
            stderr=subprocess.STDOUT,
            text=True,
            check=False,
        )
        if install.returncode != 0:
            print(
                "Could not install tqdm into the repo-local package directory; using built-in progress fallback.",
                file=sys.stderr,
            )
            tail = "\n".join(install.stdout.splitlines()[-20:])
            if tail:
                print(tail, file=sys.stderr)
            return SimpleProgress

        sys.path.insert(0, str(package_root))
        try:
            from tqdm import tqdm  # type: ignore

            return tqdm
        except ImportError:
            print("tqdm installation completed but import still failed; using built-in progress fallback.", file=sys.stderr)
            return SimpleProgress


def local_python_package_root(args: argparse.Namespace) -> Path:
    version = f"py{sys.version_info.major}{sys.version_info.minor}"
    return (args.repo_root / LOCAL_PYTHON_PACKAGE_ROOT / version).resolve()


def now_stamp() -> str:
    return datetime.now(timezone.utc).strftime("%Y%m%dT%H%M%SZ")


def normalize_paths(args: argparse.Namespace) -> None:
    args.repo_root = args.repo_root.resolve()
    if args.runtime_root is None:
        args.runtime_root = (args.repo_root / ".." / "NBNv2").resolve()
    else:
        args.runtime_root = args.runtime_root.resolve()
    if args.harness_assembly is not None:
        args.harness_assembly = args.harness_assembly.resolve()
    if args.worker_assembly is not None:
        args.worker_assembly = args.worker_assembly.resolve()

    if args.output_root is None:
        args.output_root = (
            args.repo_root
            / "Basics"
            / "artifacts"
            / "benchmarks"
            / "multiplication_perf"
            / now_stamp()
        )
    else:
        args.output_root = args.output_root.resolve()


def validate_args(args: argparse.Namespace, *, require_binaries: bool) -> None:
    if args.duration_seconds < MIN_DURATION_SECONDS and not args.allow_short_runs:
        raise SystemExit(
            f"--duration-seconds must be >= {MIN_DURATION_SECONDS} unless --allow-short-runs is set."
        )
    if args.retries < 0:
        raise SystemExit("--retries must be >= 0.")
    if args.worker_startup_timeout <= 0:
        raise SystemExit("--worker-startup-timeout must be > 0.")
    if args.worker_warmup_seconds < 0:
        raise SystemExit("--worker-warmup-seconds must be >= 0.")
    if args.skip_degradation_ratio < 1.0:
        raise SystemExit("--skip-degradation-ratio must be >= 1.0.")

    if not require_binaries:
        return

    harness_assembly = harness_assembly_path(args)
    worker_assembly = worker_assembly_path(args)
    if not harness_assembly.exists():
        raise SystemExit(
            f"Basics harness assembly not found: {harness_assembly}\n"
            f"Expected assembly name: {HARNESS_ASSEMBLY_NAME}\n"
            f"Build Basics first, for example: {args.dotnet} build {harness_project_path(args)} -c {args.configuration}\n"
            f"If you built to a custom artifacts path, pass --harness-assembly <path>."
        )
    if not worker_assembly.exists():
        raise SystemExit(
            f"WorkerNode assembly not found: {worker_assembly}\n"
            f"Build the runtime first, for example: {args.dotnet} build {worker_project_path(args)} -c {args.configuration}\n"
            f"If you built to a custom artifacts path, pass --worker-assembly <path>."
        )


def harness_project_path(args: argparse.Namespace) -> Path:
    return args.repo_root / "Basics" / "src" / "Basics.Harness" / "Basics.Harness.csproj"


def harness_assembly_path(args: argparse.Namespace) -> Path:
    if args.harness_assembly is not None:
        return args.harness_assembly
    standard = (
        args.repo_root
        / "Basics"
        / "src"
        / "Basics.Harness"
        / "bin"
        / args.configuration
        / "net8.0"
        / HARNESS_ASSEMBLY_NAME
    )
    return resolve_assembly_path(
        standard,
        search_root=args.repo_root / "Basics",
        assembly_name=HARNESS_ASSEMBLY_NAME,
        project_dir_name="Basics.Harness",
        configuration=args.configuration,
    )


def worker_project_path(args: argparse.Namespace) -> Path:
    return args.runtime_root / "src" / "Nbn.Runtime.WorkerNode" / "Nbn.Runtime.WorkerNode.csproj"


def worker_assembly_path(args: argparse.Namespace) -> Path:
    if args.worker_assembly is not None:
        return args.worker_assembly
    standard = (
        args.runtime_root
        / "src"
        / "Nbn.Runtime.WorkerNode"
        / "bin"
        / args.configuration
        / "net8.0"
        / WORKER_ASSEMBLY_NAME
    )
    return resolve_assembly_path(
        standard,
        search_root=args.runtime_root,
        assembly_name=WORKER_ASSEMBLY_NAME,
        project_dir_name="Nbn.Runtime.WorkerNode",
        configuration=args.configuration,
    )


def resolve_assembly_path(
    standard: Path,
    *,
    search_root: Path,
    assembly_name: str,
    project_dir_name: str,
    configuration: str,
) -> Path:
    candidates: dict[Path, float] = {}
    if standard.exists():
        candidates[standard] = standard.stat().st_mtime

    if search_root.exists():
        config_segment = configuration.lower()
        for path in search_root.glob(f"**/{assembly_name}"):
            parts = {part.lower() for part in path.parts}
            if "bin" not in parts or "obj" in parts or "ref" in parts or "refint" in parts:
                continue
            if project_dir_name.lower() not in parts:
                continue
            if config_segment not in parts:
                continue
            candidates[path] = path.stat().st_mtime

    if not candidates:
        return standard
    return max(candidates, key=candidates.get)


def initial_combos(args: argparse.Namespace) -> list[Combo]:
    combos: list[Combo] = []
    for population in args.populations:
        for worker_count in args.worker_counts:
            for concurrency in args.max_concurrencies:
                if not combo_makes_sense(population, worker_count, concurrency):
                    continue
                combos.append(Combo(population, worker_count, concurrency))
    return combos


def combo_makes_sense(population: int, worker_count: int, max_concurrent: int) -> bool:
    return worker_count <= max_concurrent <= population


def stable_combo_id(combo: Combo) -> str:
    return f"p{combo.population:03d}_w{combo.worker_count:03d}_c{combo.max_concurrent:03d}"


def tcp_address_parts(address: str) -> tuple[str, int]:
    if ":" not in address:
        raise ValueError(f"address must be host:port, got {address!r}")
    host, port_text = address.rsplit(":", 1)
    return host.strip("[]"), int(port_text)


def wait_for_port(host: str, port: int, timeout_seconds: float) -> bool:
    deadline = time.monotonic() + timeout_seconds
    while time.monotonic() < deadline:
        try:
            with socket.create_connection((host, port), timeout=0.25):
                return True
        except OSError:
            time.sleep(0.2)
    return False


def is_port_available(host: str, port: int) -> bool:
    try:
        with socket.socket(socket.AF_INET, socket.SOCK_STREAM) as sock:
            sock.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1)
            sock.bind((host, port))
            return True
    except OSError:
        return False


def find_available_ports(bind_host: str, start_port: int, count: int) -> list[int]:
    ports: list[int] = []
    candidate = start_port
    bind_target = "127.0.0.1" if bind_host in {"0.0.0.0", "::"} else bind_host
    while len(ports) < count:
        if is_port_available(bind_target, candidate):
            ports.append(candidate)
        candidate += 1
        if candidate - start_port > max(1024, count * 16):
            raise RuntimeError(f"unable to find {count} available ports starting at {start_port}")
    return ports


def launch_workers(args: argparse.Namespace, state: BenchmarkState, combo: Combo, combo_dir: Path) -> None:
    cleanup_workers(state)

    ports = find_available_ports(args.bind_host, args.worker_port_base, combo.worker_count)
    logs_dir = combo_dir / "workers"
    logs_dir.mkdir(parents=True, exist_ok=True)

    for index, port in enumerate(ports, start=1):
        ordinal = f"{stable_combo_id(combo)}_{index:03d}"
        log_path = logs_dir / f"worker_{index:03d}_{port}.log"
        log_file = log_path.open("w", encoding="utf-8")
        cmd = [
            args.dotnet,
            str(worker_assembly_path(args)),
            "--bind-host",
            args.bind_host,
            "--advertise-host",
            args.advertise_host,
            "--port",
            str(port),
            "--logical-name",
            f"nbn.worker.bench.{ordinal}",
            "--root-name",
            f"worker-node-bench-{ordinal}",
            "--settings-host",
            args.settings_host,
            "--settings-port",
            str(args.settings_port),
            "--settings-name",
            args.settings_name,
            "--storage-pct",
            str(args.storage_pct),
        ]
        log_file.write(f"# {' '.join(cmd)}\n")
        log_file.flush()
        process = subprocess.Popen(
            cmd,
            cwd=args.repo_root,
            stdout=log_file,
            stderr=subprocess.STDOUT,
            text=True,
            start_new_session=True,
        )
        process._nbn_log_file = log_file  # type: ignore[attr-defined]
        state.active_workers.append(process)

        if not wait_for_port(args.advertise_host, port, args.worker_startup_timeout):
            raise RuntimeError(f"worker {index}/{combo.worker_count} did not open {args.advertise_host}:{port}")

    if args.worker_warmup_seconds > 0:
        time.sleep(args.worker_warmup_seconds)


def cleanup_workers(state: BenchmarkState) -> None:
    processes = state.active_workers
    state.active_workers = []
    for process in processes:
        if process.poll() is None:
            try:
                os.killpg(process.pid, signal.SIGTERM)
            except ProcessLookupError:
                pass
    deadline = time.monotonic() + 5.0
    for process in processes:
        while process.poll() is None and time.monotonic() < deadline:
            time.sleep(0.1)
        if process.poll() is None:
            try:
                os.killpg(process.pid, signal.SIGKILL)
            except ProcessLookupError:
                pass
        close_process_log(process)


def close_process_log(process: subprocess.Popen[str]) -> None:
    log_file = getattr(process, "_nbn_log_file", None)
    if log_file is not None:
        try:
            log_file.close()
        except Exception:
            pass


def write_harness_config(args: argparse.Namespace, combo: Combo, combo_dir: Path, combo_index: int) -> Path:
    config_path = combo_dir / "harness_config.json"
    template_store = combo_dir / "templates"
    harness_port = args.harness_port_base + combo_index
    config = {
        "RunLabel": f"bench-multiplication-{stable_combo_id(combo)}",
        "OutputDirectory": str(combo_dir / "harness_reports"),
        "Runtime": {
            "IoAddress": args.io_address,
            "IoGatewayName": args.io_gateway_name,
            "BindHost": args.bind_host,
            "Port": harness_port,
            "AdvertiseHost": args.advertise_host,
            "AdvertisePort": harness_port,
            "RequestTimeoutSeconds": args.request_timeout_seconds,
        },
        "TemplatePublishing": {
            "BindHost": args.bind_host,
            "AdvertiseHost": args.advertise_host,
            "BackingStoreRoot": str(template_store),
        },
        "Environment": {
            "ClientName": f"nbn.basics.bench.{stable_combo_id(combo)}",
            "TaskId": "multiplication",
            "OutputObservationMode": "evented",
            "MaxReadyWindowTicks": 4,
            "SampleRepeatCount": 1,
            "StrengthSource": "base-only",
            "Template": {
                "TemplateId": f"bench-multiplication-template-{stable_combo_id(combo)}",
                "Description": "Benchmark seed template for Basics Multiplication throughput runs.",
                "VariationBand": {
                    "MaxInternalNeuronDelta": 1,
                    "MaxAxonDelta": 2,
                    "MaxStrengthCodeDelta": 2,
                    "MaxParameterCodeDelta": 1,
                    "AllowFunctionMutation": True,
                    "AllowAxonReroute": True,
                    "AllowRegionSetChange": False,
                },
                "SeedShape": {},
            },
            "Sizing": {
                "InitialPopulationCount": combo.population,
                "MinimumPopulationCount": combo.population,
                "ReproductionRunCount": 2,
                "MaxConcurrentBrains": combo.max_concurrent,
            },
            "Scheduling": {
                "FitnessWeight": 0.65,
                "DiversityWeight": 0.20,
                "SpeciesBalanceWeight": 0.15,
                "EliteFraction": 0.12,
                "ExplorationFraction": 0.10,
                "MaxParentsPerSpecies": 6,
                "MinRunsPerPair": 1,
                "MaxRunsPerPair": 6,
                "FitnessExponent": 1.30,
                "DiversityBoost": 0.15,
            },
        },
        "Trials": {
            "MaxTrialCount": 1,
            "TrialTimeoutSeconds": args.duration_seconds,
            "TargetAccuracy": 1.0,
            "TargetFitness": 0.999,
            "RequiredSuccessfulTrials": 1,
            "AutoTuneEnabled": False,
            "PreferVectorPotentialOnFailures": False,
            "ReduceSizingOnFailures": False,
        },
    }
    config_path.write_text(json.dumps(config, indent=2) + "\n", encoding="utf-8")
    return config_path


def run_harness(args: argparse.Namespace, config_path: Path, combo_dir: Path) -> tuple[int, Path | None]:
    output_log = combo_dir / "harness.stdout.log"
    cmd = [
        args.dotnet,
        str(harness_assembly_path(args)),
        "--config",
        str(config_path),
    ]
    with output_log.open("w", encoding="utf-8") as log_file:
        log_file.write(f"# {' '.join(cmd)}\n")
        log_file.flush()
        process = subprocess.run(
            cmd,
            cwd=args.repo_root,
            stdout=log_file,
            stderr=subprocess.STDOUT,
            text=True,
            check=False,
        )

    report_path = newest_report_path(combo_dir / "harness_reports")
    return process.returncode, report_path


def newest_report_path(report_dir: Path) -> Path | None:
    if not report_dir.exists():
        return None
    reports = sorted(report_dir.glob("*.json"), key=lambda path: path.stat().st_mtime, reverse=True)
    return reports[0] if reports else None


def json_get(obj: Any, *names: str, default: Any = None) -> Any:
    if not isinstance(obj, dict):
        return default
    for name in names:
        if name in obj:
            return obj[name]
    lower = {str(key).lower(): value for key, value in obj.items()}
    for name in names:
        key = name.lower()
        if key in lower:
            return lower[key]
    return default


def as_float(value: Any) -> float | None:
    if isinstance(value, bool) or value is None:
        return None
    try:
        return float(value)
    except (TypeError, ValueError):
        return None


def as_int(value: Any) -> int | None:
    if isinstance(value, bool) or value is None:
        return None
    try:
        return int(value)
    except (TypeError, ValueError):
        return None


def parse_report(combo: Combo, attempt: int, exit_code: int, report_path: Path | None, error: str = "") -> RunMetrics:
    if report_path is None or not report_path.exists():
        return RunMetrics(
            combo=combo,
            status="failed",
            attempt=attempt,
            harness_exit_code=exit_code,
            error=error or "harness did not produce a report",
        )

    try:
        report = json.loads(report_path.read_text(encoding="utf-8"))
    except Exception as exc:
        return RunMetrics(
            combo=combo,
            status="failed",
            attempt=attempt,
            report_path=report_path,
            harness_exit_code=exit_code,
            error=f"could not parse report: {exc}",
        )

    trials = json_get(report, "Trials", default=[])
    trial = trials[0] if isinstance(trials, list) and trials else {}
    snapshots = json_get(trial, "Snapshots", default=[])
    if not isinstance(snapshots, list):
        snapshots = []
    terminal = json_get(trial, "TerminalSnapshot", default=None)
    duration = as_float(json_get(trial, "DurationSeconds", default=None)) or 0.0

    completed_generation_ids: set[int] = set()
    timing_totals: list[float] = []
    observation_times: list[float] = []
    spawn_times: list[float] = []
    placement_times: list[float] = []
    setup_times: list[float] = []
    failed_brains = 0
    evaluation_failures = 0
    best_accuracy: float | None = None
    best_fitness: float | None = None

    for snapshot in snapshots:
        if not isinstance(snapshot, dict):
            continue
        generation = as_int(json_get(snapshot, "Generation", default=None))
        status_text = str(json_get(snapshot, "StatusText", default=""))
        match = GENERATION_EVALUATED_RE.search(status_text)
        if match:
            completed_generation_ids.add(int(match.group(1)))
        elif generation is not None and json_get(snapshot, "LatestGenerationTiming", default=None):
            completed_generation_ids.add(generation)

        timing = json_get(snapshot, "LatestGenerationTiming", default=None)
        if isinstance(timing, dict):
            append_float(timing_totals, json_get(timing, "TotalDurationSeconds", default=None))
            append_float(observation_times, json_get(timing, "AverageObservationSeconds", default=None))
            append_float(spawn_times, json_get(timing, "AverageSpawnRequestSeconds", default=None))
            append_float(placement_times, json_get(timing, "AveragePlacementWaitSeconds", default=None))
            append_float(setup_times, json_get(timing, "AverageSetupSeconds", default=None))
            failed_brains = max(failed_brains, as_int(json_get(timing, "FailedBrainCount", default=0)) or 0)

        evaluation_failures = max(
            evaluation_failures,
            as_int(json_get(snapshot, "EvaluationFailureCount", default=0)) or 0,
        )
        accuracy = as_float(json_get(snapshot, "BestAccuracy", default=None))
        fitness = as_float(json_get(snapshot, "BestFitness", default=None))
        if accuracy is not None:
            best_accuracy = accuracy if best_accuracy is None else max(best_accuracy, accuracy)
        if fitness is not None:
            best_fitness = fitness if best_fitness is None else max(best_fitness, fitness)

    if isinstance(terminal, dict):
        terminal_generation = as_int(json_get(terminal, "Generation", default=None))
        terminal_timing = json_get(terminal, "LatestGenerationTiming", default=None)
        if terminal_generation is not None and terminal_timing:
            completed_generation_ids.add(terminal_generation)

    completed_generations = len(completed_generation_ids)
    avg_seconds_per_generation = duration / completed_generations if duration > 0 and completed_generations > 0 else None
    generations_per_minute = completed_generations * 60.0 / duration if duration > 0 else None
    status = "ok" if completed_generations > 0 and exit_code in (0, 2) else "failed"

    return RunMetrics(
        combo=combo,
        status=status,
        attempt=attempt,
        report_path=report_path,
        harness_exit_code=exit_code,
        duration_seconds=duration,
        completed_generations=completed_generations,
        avg_seconds_per_generation=avg_seconds_per_generation,
        generations_per_minute=generations_per_minute,
        avg_timing_total_seconds=average(timing_totals),
        avg_observation_seconds=average(observation_times),
        avg_spawn_request_seconds=average(spawn_times),
        avg_placement_wait_seconds=average(placement_times),
        avg_setup_seconds=average(setup_times),
        failed_brains=failed_brains,
        evaluation_failures=evaluation_failures,
        best_accuracy=best_accuracy,
        best_fitness=best_fitness,
        error=error,
    )


def append_float(values: list[float], value: Any) -> None:
    parsed = as_float(value)
    if parsed is not None:
        values.append(parsed)


def average(values: Iterable[float]) -> float | None:
    items = list(values)
    if not items:
        return None
    return sum(items) / len(items)


def run_combo(args: argparse.Namespace, state: BenchmarkState, combo: Combo, combo_index: int) -> RunMetrics:
    combo_dir = state.output_root / stable_combo_id(combo)
    combo_dir.mkdir(parents=True, exist_ok=True)

    last_metrics: RunMetrics | None = None
    attempts = args.retries + 1
    for attempt in range(1, attempts + 1):
        attempt_dir = combo_dir / f"attempt_{attempt:02d}"
        attempt_dir.mkdir(parents=True, exist_ok=True)
        try:
            launch_workers(args, state, combo, attempt_dir)
            config_path = write_harness_config(args, combo, attempt_dir, combo_index)
            exit_code, report_path = run_harness(args, config_path, attempt_dir)
            metrics = parse_report(combo, attempt, exit_code, report_path)
            last_metrics = metrics
            append_result_files(state, metrics)
            if metrics.success:
                return metrics
        except Exception as exc:
            metrics = RunMetrics(combo=combo, status="failed", attempt=attempt, error=str(exc))
            last_metrics = metrics
            append_result_files(state, metrics)
        finally:
            cleanup_workers(state)

    return last_metrics or RunMetrics(combo=combo, status="failed", attempt=0, error="run did not start")


def result_row(metrics: RunMetrics) -> dict[str, Any]:
    return {
        "population": metrics.combo.population,
        "worker_count": metrics.combo.worker_count,
        "max_concurrent": metrics.combo.max_concurrent,
        "reason": metrics.combo.reason,
        "status": metrics.status,
        "attempt": metrics.attempt,
        "duration_seconds": round_or_empty(metrics.duration_seconds),
        "completed_generations": metrics.completed_generations,
        "avg_seconds_per_generation": round_or_empty(metrics.avg_seconds_per_generation),
        "generations_per_minute": round_or_empty(metrics.generations_per_minute),
        "avg_timing_total_seconds": round_or_empty(metrics.avg_timing_total_seconds),
        "avg_observation_seconds": round_or_empty(metrics.avg_observation_seconds),
        "avg_spawn_request_seconds": round_or_empty(metrics.avg_spawn_request_seconds),
        "avg_placement_wait_seconds": round_or_empty(metrics.avg_placement_wait_seconds),
        "avg_setup_seconds": round_or_empty(metrics.avg_setup_seconds),
        "failed_brains": metrics.failed_brains,
        "evaluation_failures": metrics.evaluation_failures,
        "best_accuracy": round_or_empty(metrics.best_accuracy),
        "best_fitness": round_or_empty(metrics.best_fitness),
        "harness_exit_code": "" if metrics.harness_exit_code is None else metrics.harness_exit_code,
        "report_path": "" if metrics.report_path is None else str(metrics.report_path),
        "error": metrics.error,
    }


def round_or_empty(value: float | int | None) -> str:
    if value is None:
        return ""
    if isinstance(value, int):
        return str(value)
    return f"{value:.6f}"


def append_result_files(state: BenchmarkState, metrics: RunMetrics) -> None:
    state.attempt_rows.append(metrics)
    state.final_rows[metrics.combo.key] = metrics

    attempts_jsonl = state.output_root / "attempts.jsonl"
    with attempts_jsonl.open("a", encoding="utf-8") as handle:
        handle.write(json.dumps(result_row(metrics), sort_keys=True) + "\n")
    write_attempts_csv(state)
    write_final_results_csv(state)


def write_attempts_csv(state: BenchmarkState) -> None:
    rows = [result_row(metrics) for metrics in state.attempt_rows]
    if not rows:
        return
    path = state.output_root / "attempts.csv"
    with path.open("w", newline="", encoding="utf-8") as handle:
        writer = csv.DictWriter(handle, fieldnames=list(rows[0].keys()))
        writer.writeheader()
        writer.writerows(rows)


def write_final_results_csv(state: BenchmarkState) -> None:
    rows = [
        result_row(metrics)
        for metrics in sorted(
            state.final_rows.values(),
            key=lambda item: (item.combo.population, item.combo.worker_count, item.combo.max_concurrent),
        )
    ]
    if not rows:
        return
    path = state.output_root / "results.csv"
    with path.open("w", newline="", encoding="utf-8") as handle:
        writer = csv.DictWriter(handle, fieldnames=list(rows[0].keys()))
        writer.writeheader()
        writer.writerows(rows)


def skip_combo(state: BenchmarkState, combo: Combo, reason: str) -> None:
    state.skipped_by_key[combo.key] = reason
    metrics = RunMetrics(combo=combo, status="skipped", attempt=0, error=reason)
    append_result_files(state, metrics)


def maybe_skip_for_degradation(args: argparse.Namespace, state: BenchmarkState, combo: Combo) -> str | None:
    same_axis = [
        metrics
        for metrics in state.completed_by_key.values()
        if metrics.combo.population == combo.population
        and metrics.combo.worker_count == combo.worker_count
        and metrics.combo.max_concurrent < combo.max_concurrent
        and metrics.success
        and metrics.completed_generations >= MIN_COMPLETED_GENERATIONS_FOR_SKIP
    ]
    if not same_axis:
        return None
    best_prior = min(same_axis, key=lambda item: item.avg_seconds_per_generation or float("inf"))
    previous = max(same_axis, key=lambda item: item.combo.max_concurrent)
    if (
        previous.avg_seconds_per_generation is not None
        and best_prior.avg_seconds_per_generation is not None
        and previous.avg_seconds_per_generation >= best_prior.avg_seconds_per_generation * args.skip_degradation_ratio
    ):
        return (
            f"skipped because concurrency {previous.combo.max_concurrent} was "
            f"{previous.avg_seconds_per_generation / best_prior.avg_seconds_per_generation:.2f}x slower "
            f"than best prior concurrency {best_prior.combo.max_concurrent} for population {combo.population}, "
            f"workers {combo.worker_count}"
        )
    return None


def maybe_add_optional_concurrency(args: argparse.Namespace, state: BenchmarkState, pending: list[Combo], progress: Any) -> None:
    base_concurrency_max = max(args.max_concurrencies)
    optional = args.include_optional_concurrency
    if optional <= base_concurrency_max:
        return

    for population in args.populations:
        for worker_count in args.worker_counts:
            if not combo_makes_sense(population, worker_count, optional):
                continue
            optional_key = (population, worker_count, optional)
            if optional_key in state.queued_keys or optional_key in state.completed_by_key or optional_key in state.skipped_by_key:
                continue

            base_keys = [
                (population, worker_count, concurrency)
                for concurrency in args.max_concurrencies
                if combo_makes_sense(population, worker_count, concurrency)
            ]
            if not base_keys or not all(key in state.completed_by_key or key in state.skipped_by_key for key in base_keys):
                continue

            completed = [state.completed_by_key[key] for key in base_keys if key in state.completed_by_key and state.completed_by_key[key].success]
            if not completed:
                continue
            best = min(completed, key=lambda item: item.avg_seconds_per_generation or float("inf"))
            if best.combo.max_concurrent != base_concurrency_max:
                continue

            combo = Combo(population, worker_count, optional, reason=f"dynamic: concurrency {base_concurrency_max} won")
            pending.append(combo)
            state.queued_keys.add(combo.key)
            progress.total += 1
            progress.refresh()


def maybe_add_optional_worker(args: argparse.Namespace, state: BenchmarkState, pending: list[Combo], progress: Any) -> None:
    if not optional_concurrency_resolved(args, state):
        return

    optional_worker = args.include_optional_worker
    if optional_worker in args.worker_counts:
        return

    concurrency_values = sorted(set(args.max_concurrencies) | {args.include_optional_concurrency})
    for population in args.populations:
        for concurrency in concurrency_values:
            if not combo_makes_sense(population, optional_worker, concurrency):
                continue
            optional_key = (population, optional_worker, concurrency)
            if optional_key in state.queued_keys or optional_key in state.completed_by_key or optional_key in state.skipped_by_key:
                continue

            base_keys = [
                (population, worker_count, concurrency)
                for worker_count in args.worker_counts
                if combo_makes_sense(population, worker_count, concurrency)
            ]
            if not base_keys:
                continue
            if not all(key in state.completed_by_key or key in state.skipped_by_key for key in base_keys):
                continue

            completed = [state.completed_by_key[key] for key in base_keys if key in state.completed_by_key and state.completed_by_key[key].success]
            if not completed:
                continue
            best = min(completed, key=lambda item: item.avg_seconds_per_generation or float("inf"))
            if best.combo.worker_count != min(args.worker_counts):
                continue

            combo = Combo(population, optional_worker, concurrency, reason=f"dynamic: {min(args.worker_counts)} workers won")
            pending.append(combo)
            state.queued_keys.add(combo.key)
            progress.total += 1
            progress.refresh()


def optional_concurrency_resolved(args: argparse.Namespace, state: BenchmarkState) -> bool:
    base_concurrency_max = max(args.max_concurrencies)
    optional = args.include_optional_concurrency
    if optional <= base_concurrency_max:
        return True

    for population in args.populations:
        for worker_count in args.worker_counts:
            if not combo_makes_sense(population, worker_count, base_concurrency_max):
                continue

            base_keys = [
                (population, worker_count, concurrency)
                for concurrency in args.max_concurrencies
                if combo_makes_sense(population, worker_count, concurrency)
            ]
            if not base_keys:
                continue
            if not all(key in state.completed_by_key or key in state.skipped_by_key for key in base_keys):
                return False

            completed = [
                state.completed_by_key[key]
                for key in base_keys
                if key in state.completed_by_key and state.completed_by_key[key].success
            ]
            if not completed:
                continue
            best = min(completed, key=lambda item: item.avg_seconds_per_generation or float("inf"))
            if best.combo.max_concurrent != base_concurrency_max:
                continue

            optional_key = (population, worker_count, optional)
            if optional_key not in state.completed_by_key and optional_key not in state.skipped_by_key:
                return False

    return True


def print_initial_plan(combos: list[Combo]) -> None:
    print(f"Initial planned combos: {len(combos)}")
    for combo in combos:
        print(f"  population={combo.population:<3} workers={combo.worker_count:<3} max_concurrent={combo.max_concurrent:<3}")


def print_summary_table(state: BenchmarkState) -> None:
    rows = [metrics for metrics in state.final_rows.values() if metrics.status in {"ok", "failed", "skipped"}]
    rows.sort(key=lambda item: (item.combo.population, item.combo.worker_count, item.combo.max_concurrent, item.attempt))
    headers = ["pop", "workers", "conc", "status", "gens", "sec/gen", "gen/min", "obs", "spawn", "place", "setup", "reason/error"]
    table_rows = [
        [
            str(metrics.combo.population),
            str(metrics.combo.worker_count),
            str(metrics.combo.max_concurrent),
            metrics.status,
            str(metrics.completed_generations),
            round_or_empty(metrics.avg_seconds_per_generation),
            round_or_empty(metrics.generations_per_minute),
            round_or_empty(metrics.avg_observation_seconds),
            round_or_empty(metrics.avg_spawn_request_seconds),
            round_or_empty(metrics.avg_placement_wait_seconds),
            round_or_empty(metrics.avg_setup_seconds),
            metrics.error or metrics.combo.reason,
        ]
        for metrics in rows
    ]
    widths = [len(header) for header in headers]
    for row in table_rows:
        for index, value in enumerate(row):
            widths[index] = max(widths[index], len(value))

    print()
    print("Benchmark results")
    print(" ".join(header.ljust(widths[index]) for index, header in enumerate(headers)))
    print(" ".join("-" * width for width in widths))
    for row in table_rows:
        print(" ".join(value.ljust(widths[index]) for index, value in enumerate(row)))
    print()
    print(f"CSV:   {state.output_root / 'results.csv'}")
    print(f"Log:   {state.output_root / 'attempts.jsonl'}")


def ensure_core_runtime_reachable(args: argparse.Namespace) -> None:
    host, port = tcp_address_parts(args.io_address)
    if not wait_for_port(host, port, timeout_seconds=2.0):
        raise SystemExit(
            f"IO Gateway is not reachable at {args.io_address}. Start the core runtime stack before running this benchmark."
        )


def existing_worker_processes() -> list[str]:
    try:
        result = subprocess.run(
            ["ps", "-eo", "pid=,args="],
            stdout=subprocess.PIPE,
            stderr=subprocess.DEVNULL,
            text=True,
            check=False,
        )
    except OSError:
        return []
    if result.returncode != 0:
        return []

    workers: list[str] = []
    for line in result.stdout.splitlines():
        if "Nbn.Runtime.WorkerNode.dll" in line or "Nbn.Runtime.WorkerNode " in line:
            workers.append(line.strip())
    return workers


def ensure_no_existing_workers(args: argparse.Namespace) -> None:
    if args.allow_existing_workers:
        return
    workers = existing_worker_processes()
    if not workers:
        return
    preview = "\n".join(f"  {line}" for line in workers[:8])
    extra = "" if len(workers) <= 8 else f"\n  ... {len(workers) - 8} more"
    raise SystemExit(
        "Existing WorkerNode processes are running, so worker-count results would be contaminated.\n"
        "Stop them first, or re-run with --allow-existing-workers if that is intentional.\n"
        f"{preview}{extra}"
    )


def main() -> int:
    args = parse_args()
    normalize_paths(args)
    validate_args(args, require_binaries=not args.dry_run)

    combos = initial_combos(args)
    if args.dry_run:
        print_initial_plan(combos)
        return 0

    tqdm = load_tqdm(args.install_tqdm, local_python_package_root(args))
    ensure_core_runtime_reachable(args)
    ensure_no_existing_workers(args)

    args.output_root.mkdir(parents=True, exist_ok=True)
    state = BenchmarkState(output_root=args.output_root)
    atexit.register(cleanup_workers, state)

    def handle_signal(signum: int, _frame: Any) -> None:
        cleanup_workers(state)
        raise SystemExit(128 + signum)

    signal.signal(signal.SIGINT, handle_signal)
    signal.signal(signal.SIGTERM, handle_signal)
    pending = list(combos)
    for combo in pending:
        state.queued_keys.add(combo.key)

    metadata = {
        "started_at_utc": datetime.now(timezone.utc).isoformat(),
        "duration_seconds": args.duration_seconds,
        "populations": list(args.populations),
        "base_worker_counts": list(args.worker_counts),
        "base_max_concurrencies": list(args.max_concurrencies),
        "optional_worker": args.include_optional_worker,
        "optional_concurrency": args.include_optional_concurrency,
        "io_address": args.io_address,
        "runtime_root": str(args.runtime_root),
        "repo_root": str(args.repo_root),
    }
    (args.output_root / "metadata.json").write_text(json.dumps(metadata, indent=2) + "\n", encoding="utf-8")

    combo_index = 0
    try:
        with tqdm(total=len(pending), unit="combo", dynamic_ncols=True) as progress:
            while pending:
                combo = pending.pop(0)
                combo_index += 1
                progress.set_description(f"p{combo.population} w{combo.worker_count} c{combo.max_concurrent}")

                skip_reason = maybe_skip_for_degradation(args, state, combo)
                if skip_reason is not None:
                    skip_combo(state, combo, skip_reason)
                    progress.update(1)
                    maybe_add_optional_concurrency(args, state, pending, progress)
                    maybe_add_optional_worker(args, state, pending, progress)
                    continue

                metrics = run_combo(args, state, combo, combo_index)
                if metrics.success:
                    state.completed_by_key[combo.key] = metrics
                else:
                    state.skipped_by_key[combo.key] = metrics.error or metrics.status

                progress.update(1)
                maybe_add_optional_concurrency(args, state, pending, progress)
                maybe_add_optional_worker(args, state, pending, progress)
    finally:
        cleanup_workers(state)

    print_summary_table(state)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
