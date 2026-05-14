"""Resolve a local loopback port for the future SearXNG process."""

import argparse
import json
import socket


DEFAULT_START = 8030
DEFAULT_END = 8039
HOST = "127.0.0.1"


def is_port_free(port: int) -> bool:
    with socket.socket(socket.AF_INET, socket.SOCK_STREAM) as sock:
        sock.settimeout(0.2)
        return sock.connect_ex((HOST, int(port))) != 0


def resolve_port(start: int = DEFAULT_START, end: int = DEFAULT_END) -> int:
    for port in range(int(start), int(end) + 1):
        if is_port_free(port):
            return port
    raise RuntimeError(f"No free local port between {start} and {end}.")


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--start", type=int, default=DEFAULT_START)
    parser.add_argument("--end", type=int, default=DEFAULT_END)
    args = parser.parse_args()

    try:
        port = resolve_port(args.start, args.end)
        print(json.dumps({"ok": True, "host": HOST, "port": port, "base_url": f"http://{HOST}:{port}/"}))
        return 0
    except Exception as exc:
        print(json.dumps({"ok": False, "error": str(exc)}))
        return 1


if __name__ == "__main__":
    raise SystemExit(main())
