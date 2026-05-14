"""Minimal standard-library SearXNG JSON client for Kivrio Chat."""

import json
from typing import Any, Dict, List
from urllib.parse import urlencode, urljoin
from urllib.request import urlopen

from normalize_results import normalize_results


def build_search_url(base_url: str, query: str, max_results: int = 5) -> str:
    base = str(base_url or "").rstrip("/") + "/"
    params = urlencode({
        "q": str(query or "").strip(),
        "format": "json",
        "language": "auto",
        "safesearch": "0",
        "pageno": "1",
        "categories": "general",
        "max_results": str(max(1, min(int(max_results or 5), 5))),
    })
    return urljoin(base, "search") + "?" + params


def search(base_url: str, query: str, max_results: int = 5, timeout: float = 3.0) -> Dict[str, Any]:
    trimmed = str(query or "").strip()
    if not trimmed:
        return {"ok": False, "available": False, "results": [], "message": "Empty query."}

    with urlopen(build_search_url(base_url, trimmed, max_results), timeout=timeout) as response:
        payload = json.loads(response.read().decode("utf-8"))

    results: List[Dict[str, str]] = normalize_results(payload, max_results=max_results)
    return {
        "ok": True,
        "available": True,
        "results": results,
        "message": "",
    }
