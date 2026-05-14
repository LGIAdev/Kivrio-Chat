"""Healthcheck helper for a local SearXNG-compatible endpoint."""

import argparse
import json
from urllib.error import URLError
from urllib.request import Request, urlopen


def check(base_url: str, timeout: float = 2.0, path: str = "/healthz") -> bool:
    health_path = path if path.startswith("/") else f"/{path}"
    url = base_url.rstrip("/") + health_path
    request = Request(url, headers={"Accept": "text/plain, application/json"})
    with urlopen(request, timeout=timeout) as response:
        return 200 <= int(response.status) < 300


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--base-url", required=True)
    parser.add_argument("--timeout", type=float, default=2.0)
    parser.add_argument("--path", default="/healthz")
    args = parser.parse_args()

    try:
        ok = check(args.base_url, args.timeout, args.path)
        print(json.dumps({"ok": ok, "base_url": args.base_url, "path": args.path}))
        return 0 if ok else 1
    except (OSError, URLError, TimeoutError) as exc:
        print(json.dumps({"ok": False, "base_url": args.base_url, "path": args.path, "error": str(exc)}))
        return 1


if __name__ == "__main__":
    raise SystemExit(main())
