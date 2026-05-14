"""Create stable citation labels for normalized Web Search results."""

from typing import Dict, Iterable, List


def map_citations(results: Iterable[Dict[str, str]]) -> List[Dict[str, str]]:
    citations: List[Dict[str, str]] = []
    for index, result in enumerate(results or [], start=1):
        citations.append({
            "id": str(index),
            "label": f"[{index}]",
            "title": str(result.get("title") or "").strip(),
            "url": str(result.get("url") or "").strip(),
            "source": str(result.get("source") or "").strip(),
        })
    return citations
