"""Defensive stop helper for the future bundled local SearXNG process."""

import argparse
import json
import re
import shutil
import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))

from process_manager import is_pid_running, pid_for_listening_port, read_pid, stop_pid


def default_pid_path() -> Path:
    return Path(__file__).resolve().parents[1] / "runtime" / "searxng.pid"


def default_settings_path() -> Path:
    return Path(__file__).resolve().parents[1] / "runtime" / "settings-launch.yml"


def default_runtime_path() -> Path:
    return Path(__file__).resolve().parents[1] / "runtime"


def read_launch_port(settings_path: Path) -> int:
    try:
        text = settings_path.read_text(encoding="utf-8")
    except OSError:
        return 0
    match = re.search(r"(?m)^\s*port:\s*(\d+)\s*$", text)
    return int(match.group(1)) if match else 0


def remove_file(path: Path) -> None:
    try:
        path.unlink()
    except OSError:
        pass


def clear_directory(path: Path) -> None:
    try:
        if not path.exists() or not path.is_dir():
            return
        for child in path.iterdir():
            try:
                if child.is_dir() and not child.is_symlink():
                    shutil.rmtree(child)
                else:
                    child.unlink()
            except OSError:
                pass
    except OSError:
        pass


def purge_runtime(runtime_path: Path) -> None:
    remove_file(runtime_path / "searxng.pid")
    remove_file(runtime_path / "searxng.stdout.log")
    remove_file(runtime_path / "searxng.stderr.log")
    remove_file(runtime_path / "settings-launch.yml")
    clear_directory(runtime_path / "cache")
    clear_directory(runtime_path / "logs")
    clear_directory(runtime_path / "tmp")


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--pid-file", default=str(default_pid_path()))
    parser.add_argument("--settings-file", default=str(default_settings_path()))
    parser.add_argument("--purge-runtime", action="store_true")
    args = parser.parse_args()

    pid_path = Path(args.pid_file)
    settings_path = Path(args.settings_file)
    runtime_path = pid_path.parent if pid_path.parent else default_runtime_path()
    pid = read_pid(pid_path)
    port = read_launch_port(settings_path)

    stopped = False
    running = False
    port_pid = None

    if pid:
        stopped = stop_pid(pid)
        running = is_pid_running(pid)

    if port:
        port_pid = pid_for_listening_port(port)
        if port_pid and port_pid != pid:
            stopped = stop_pid(port_pid) or stopped
            running = is_pid_running(port_pid)

    if pid and (stopped or not running):
        remove_file(pid_path)

    if args.purge_runtime:
        purge_runtime(runtime_path)

    print(json.dumps({
        "ok": True,
        "purged": bool(args.purge_runtime),
        "stopped": stopped,
        "running": running,
        "pid": pid,
        "port": port,
        "port_pid": port_pid,
    }))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
