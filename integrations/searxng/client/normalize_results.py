"""Normalize SearXNG JSON into the Kivrio Chat Web Search contract."""

from typing import Any, Dict, List


def _text(value: Any) -> str:
    if value is None:
        return ""
    return str(value).strip()


def normalize_result(raw: Dict[str, Any]) -> Dict[str, str]:
    title = _text(raw.get("title"))
    url = _text(raw.get("url"))
    snippet = _text(raw.get("content") or raw.get("snippet"))
    source = _text(raw.get("engine") or raw.get("source"))

    if not source and url:
        source = url.split("/")[2] if "://" in url and len(url.split("/")) > 2 else url

    return {
        "title": title,
        "url": url,
        "snippet": snippet,
        "source": source,
    }


def normalize_results(payload: Dict[str, Any], max_results: int = 5) -> List[Dict[str, str]]:
    max_results = max(1, min(int(max_results or 5), 5))
    raw_results = payload.get("results")
    if not isinstance(raw_results, list):
        return []

    output: List[Dict[str, str]] = []
    seen_urls = set()

    for item in raw_results:
        if not isinstance(item, dict):
            continue
        normalized = normalize_result(item)
        if not normalized["title"] or not normalized["url"]:
            continue
        if normalized["url"] in seen_urls:
            continue
        seen_urls.add(normalized["url"])
        output.append(normalized)
        if len(output) >= max_results:
            break

    return output
