"""Shared data shapes for the future Kivrio Chat SearXNG client."""

from dataclasses import dataclass
from typing import List


@dataclass(frozen=True)
class SearchResult:
    title: str
    url: str
    snippet: str
    source: str


@dataclass(frozen=True)
class SearchResponse:
    ok: bool
    available: bool
    results: List[SearchResult]
    message: str = ""
