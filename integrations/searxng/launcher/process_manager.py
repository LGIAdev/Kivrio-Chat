"""Small PID-file process helpers for the future SearXNG launcher."""

import os
import signal
import subprocess
from pathlib import Path
from typing import Dict, Iterable, Optional


def write_pid(pid_path: Path, pid: int) -> None:
    pid_path.parent.mkdir(parents=True, exist_ok=True)
    pid_path.write_text(str(int(pid)), encoding="utf-8")


def read_pid(pid_path: Path) -> Optional[int]:
    try:
        value = pid_path.read_text(encoding="utf-8").strip()
        return int(value) if value else None
    except (OSError, ValueError):
        return None


def start_hidden(
    command: Iterable[str],
    cwd: Path,
    env: Optional[Dict[str, str]] = None,
    stdout_path: Optional[Path] = None,
    stderr_path: Optional[Path] = None,
) -> subprocess.Popen:
    startupinfo = None
    creationflags = 0
    if os.name == "nt":
        startupinfo = subprocess.STARTUPINFO()
        startupinfo.dwFlags |= subprocess.STARTF_USESHOWWINDOW
        creationflags = subprocess.CREATE_NO_WINDOW

    stdout_target = subprocess.DEVNULL
    stderr_target = subprocess.DEVNULL
    if stdout_path is not None:
        stdout_path.parent.mkdir(parents=True, exist_ok=True)
        stdout_target = open(stdout_path, "ab")
    if stderr_path is not None:
        stderr_path.parent.mkdir(parents=True, exist_ok=True)
        stderr_target = open(stderr_path, "ab")

    return subprocess.Popen(
        list(command),
        cwd=str(cwd),
        env=env,
        stdin=subprocess.DEVNULL,
        stdout=stdout_target,
        stderr=stderr_target,
        startupinfo=startupinfo,
        creationflags=creationflags,
    )


def stop_pid(pid: int) -> bool:
    if not pid:
        return False
    try:
        if os.name == "nt":
            completed = subprocess.run(
                ["taskkill", "/PID", str(pid), "/T", "/F"],
                check=False,
                stdout=subprocess.DEVNULL,
                stderr=subprocess.DEVNULL,
            )
            return completed.returncode == 0
        else:
            os.kill(pid, signal.SIGTERM)
            return True
    except OSError:
        return False


def is_pid_running(pid: int) -> bool:
    if not pid:
        return False
    try:
        if os.name == "nt":
            completed = subprocess.run(
                ["tasklist", "/FI", f"PID eq {int(pid)}", "/FO", "CSV", "/NH"],
                check=False,
                stdout=subprocess.PIPE,
                stderr=subprocess.DEVNULL,
                text=True,
            )
            return str(int(pid)) in (completed.stdout or "")
        os.kill(pid, 0)
        return True
    except OSError:
        return False


def pid_for_listening_port(port: int) -> Optional[int]:
    if not port or os.name != "nt":
        return None
    try:
        completed = subprocess.run(
            ["netstat", "-ano", "-p", "tcp"],
            check=False,
            stdout=subprocess.PIPE,
            stderr=subprocess.DEVNULL,
            text=True,
            encoding="utf-8",
            errors="ignore",
        )
    except OSError:
        return None

    needle = f":{int(port)}"
    for raw_line in (completed.stdout or "").splitlines():
        line = " ".join(raw_line.split())
        if "LISTENING" not in line:
            continue
        parts = line.split()
        if len(parts) < 5:
            continue
        local_address = parts[1]
        if not local_address.endswith(needle):
            continue
        try:
            return int(parts[-1])
        except ValueError:
            return None
    return None
