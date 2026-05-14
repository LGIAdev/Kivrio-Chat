"""Defensive start contract for the future bundled local SearXNG process."""

import argparse
import json
import os
import re
import secrets
import sys
import time
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))

from healthcheck import check
from port_resolver import resolve_port
from process_manager import pid_for_listening_port, read_pid, start_hidden, stop_pid, write_pid


UNAVAILABLE = "SearXNG is not bundled yet."
MOCK_ALLOWED_ENV = "KIVRIO_WEB_SEARCH_ALLOW_MOCK"
MOCK_BASE_URL_ENV = "KIVRIO_WEB_SEARCH_MOCK_BASE_URL"
DEBUG_LOGS_ENV = "KIVRIO_WEB_SEARCH_DEBUG_LOGS"


def component_root() -> Path:
    return Path(__file__).resolve().parents[1]


def vendor_root() -> Path:
    return component_root() / "vendor" / "searxng"


def project_root() -> Path:
    return component_root().parents[1]


def python_exe_path() -> Path:
    return project_root() / "runtime" / "python" / "python.exe"


def default_pid_path() -> Path:
    return component_root() / "runtime" / "searxng.pid"


def launch_settings_path() -> Path:
    return component_root() / "runtime" / "settings-launch.yml"


def read_launch_port(settings_path: Path) -> int:
    try:
        text = settings_path.read_text(encoding="utf-8")
    except OSError:
        return 0
    match = re.search(r"(?m)^\s*port:\s*(\d+)\s*$", text)
    return int(match.group(1)) if match else 0


def remove_pid_file(pid_file: Path) -> None:
    try:
        pid_file.unlink()
    except OSError:
        pass


def debug_logs_enabled() -> bool:
    return os.environ.get(DEBUG_LOGS_ENV, "").strip().lower() in {"1", "true", "yes", "on"}


def resolve_log_targets(root: Path) -> tuple[Path | None, Path | None]:
    if not debug_logs_enabled():
        return None, None
    return root / "runtime" / "searxng.stdout.log", root / "runtime" / "searxng.stderr.log"


def write_launch_settings(port: int, target: Path) -> None:
    target.parent.mkdir(parents=True, exist_ok=True)
    secret_key = secrets.token_hex(32)
    settings = f"""use_default_settings: true

general:
  instance_name: "Kivrio Chat Search"

search:
  formats:
    - html
    - json
  default_lang: "auto"
  safe_search: 0

server:
  bind_address: "127.0.0.1"
  port: {int(port)}
  secret_key: "{secret_key}"
  public_instance: false
  limiter: false
  image_proxy: false

outgoing:
  request_timeout: 3.0
  max_request_timeout: 10.0
"""
    target.write_text(settings, encoding="utf-8")


def wait_until_ready(base_url: str, timeout: float, health_timeout: float) -> bool:
    deadline = time.monotonic() + max(float(timeout), 0.1)
    while time.monotonic() < deadline:
        try:
            if check(base_url, timeout=health_timeout, path="/healthz"):
                return True
        except Exception:
            pass
        time.sleep(0.25)
    return False


def resolve_recorded_process(existing_pid: int, requested_port: int, pid_file: Path) -> dict | None:
    recorded_port = requested_port or read_launch_port(launch_settings_path())
    if recorded_port:
        base_url = f"http://127.0.0.1:{recorded_port}/"
        port_pid = pid_for_listening_port(recorded_port)
        if port_pid == existing_pid:
            if wait_until_ready(base_url, timeout=1.0, health_timeout=0.5):
                return {
                    "ok": True,
                    "available": True,
                    "host": "127.0.0.1",
                    "port": recorded_port,
                    "base_url": base_url,
                    "pid": existing_pid,
                    "pid_file": str(pid_file),
                    "settings_path": str(launch_settings_path()),
                    "message": "",
                }
            stop_pid(existing_pid)
            time.sleep(0.2)

    remove_pid_file(pid_file)
    return None


def start_real(port: int, pid_file: Path, startup_timeout: float) -> dict:
    root = component_root()
    vendor = vendor_root()
    python_exe = python_exe_path()

    if not vendor.exists() or not any(vendor.iterdir()):
        return {"ok": False, "available": False, "message": UNAVAILABLE}
    if not python_exe.exists():
        return {"ok": False, "available": False, "message": "Embedded Python runtime is absent."}

    existing_pid = read_pid(pid_file)
    if existing_pid:
        existing = resolve_recorded_process(existing_pid, port, pid_file)
        if existing is not None:
            return existing

    resolved_port = port or resolve_port()
    base_url = f"http://127.0.0.1:{resolved_port}/"
    settings_path = launch_settings_path()
    stdout_path, stderr_path = resolve_log_targets(root)
    write_launch_settings(resolved_port, settings_path)

    code = "\n".join(
        [
            "import os",
            "import sys",
            "import types",
            "if os.name == 'nt':",
            "    sys.modules.setdefault('pwd', types.SimpleNamespace(getpwuid=lambda uid: types.SimpleNamespace(pw_name='windows', pw_uid=uid)))",
            f"sys.path.insert(0, {str(vendor)!r})",
            "from searx.webapp import run",
            "run()",
        ]
    )
    env = os.environ.copy()
    env["PYTHONNOUSERSITE"] = "1"
    env["PYTHONDONTWRITEBYTECODE"] = "1"
    env["SEARXNG_SETTINGS_PATH"] = str(settings_path)
    env["SEARXNG_DISABLE_ETC_SETTINGS"] = "1"

    process = start_hidden(
        [str(python_exe), "-c", code],
        cwd=vendor,
        env=env,
        stdout_path=stdout_path,
        stderr_path=stderr_path,
    )
    write_pid(pid_file, process.pid)

    if wait_until_ready(base_url, timeout=startup_timeout, health_timeout=1.0):
        payload = {
            "ok": True,
            "available": True,
            "host": "127.0.0.1",
            "port": resolved_port,
            "base_url": base_url,
            "pid": process.pid,
            "pid_file": str(pid_file),
            "settings_path": str(settings_path),
            "message": "",
        }
        if stdout_path is not None:
            payload["stdout_log"] = str(stdout_path)
        if stderr_path is not None:
            payload["stderr_log"] = str(stderr_path)
        return payload

    try:
        process.terminate()
    except OSError:
        pass
    try:
        pid_file.unlink()
    except OSError:
        pass
    payload = {
        "ok": False,
        "available": False,
        "host": "127.0.0.1",
        "port": resolved_port,
        "base_url": base_url,
        "message": "SearXNG process did not become healthy before timeout.",
    }
    if stdout_path is not None:
        payload["stdout_log"] = str(stdout_path)
    if stderr_path is not None:
        payload["stderr_log"] = str(stderr_path)
    return payload


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--json", action="store_true")
    parser.add_argument("--port", type=int, default=0)
    parser.add_argument("--mock-ready", action="store_true")
    parser.add_argument("--base-url", default="")
    parser.add_argument("--real-start", action="store_true")
    parser.add_argument("--pid-file", default=str(default_pid_path()))
    parser.add_argument("--startup-timeout", type=float, default=15.0)
    args = parser.parse_args()

    root = component_root()
    vendor = vendor_root()
    runtime = root / "runtime"
    runtime.mkdir(parents=True, exist_ok=True)

    if args.mock_ready:
        if os.environ.get(MOCK_ALLOWED_ENV) != "1":
            payload = {"ok": False, "available": False, "message": UNAVAILABLE}
            print(json.dumps(payload))
            return 0

        port = args.port or resolve_port()
        base_url = args.base_url or os.environ.get(MOCK_BASE_URL_ENV) or f"http://127.0.0.1:{port}/"
        payload = {
            "ok": True,
            "available": True,
            "host": "127.0.0.1",
            "port": port,
            "base_url": base_url,
            "message": "",
        }
        print(json.dumps(payload))
        return 0

    if args.real_start:
        payload = start_real(args.port, Path(args.pid_file), args.startup_timeout)
        print(json.dumps(payload))
        return 0 if payload.get("ok") else 1

    if not vendor.exists() or not any(vendor.iterdir()):
        payload = {"ok": False, "available": False, "message": UNAVAILABLE}
        print(json.dumps(payload))
        return 0

    port = args.port or resolve_port()
    payload = {
        "ok": False,
        "available": False,
        "host": "127.0.0.1",
        "port": port,
        "base_url": f"http://127.0.0.1:{port}/",
        "message": "SearXNG vendor is present, but process start is not enabled in this phase.",
    }
    print(json.dumps(payload))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
