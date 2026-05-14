# Kivrio Chat Web Search / SearXNG

This directory is reserved for the local Web Search component.

Current scope:

- define the future SearXNG configuration files;
- define a small JSON client contract;
- keep the component isolated from the main Kivrio Chat server;
- expose a guarded mock launcher contract for local integration tests;
- define an auditable minimal packaging contract for the future runtime bundle;
- avoid installing Python, SearXNG, or any dependency system-wide;
- avoid launching any real background process unless a diagnostic or the local
  backend explicitly needs Web Search.

The runtime Python folder remains separate:

```text
runtime/python/
```

The temporary SearXNG runtime state remains local to this component:

```text
integrations/searxng/runtime/
```

The C# backend treats SearXNG as optional. If a local runtime is present, it can
start the managed SearXNG process on loopback and use it for `/api/web-search`.
If the runtime is absent or unhealthy, the endpoint returns an unavailable
response and Kivrio Chat continues normally without calling the model.

Managed startup is enabled by default when the local runtime is present. It can
still be disabled explicitly with:

```text
KIVRIO_WEB_SEARCH_ENABLE_MANAGED=0
```

For phase tests only, the managed launcher path can be simulated with:

```text
KIVRIO_WEB_SEARCH_ALLOW_MOCK=1
KIVRIO_WEB_SEARCH_MOCK_BASE_URL=http://127.0.0.1:<test-port>/
```

The mock base URL is still accepted only when it points to localhost or a
loopback address.

The future runtime bundle policy is documented in:

```text
integrations/searxng/packaging/
```

Phase 6 did not add Python or SearXNG. It only added the packaging allowlist,
denylist, and audit test used to reject development artifacts if a runtime
bundle is introduced later.

Phase 7 adds a reusable bundle diagnostic and manifest example. It reports
whether the bundle is absent, incomplete, ready, or non-compliant, without
installing Python, downloading SearXNG, changing the UI, or starting a real
process.

Phase 8 adds a read-only candidate preflight command. It can inspect a manually
prepared external bundle before any copy into Kivrio Chat, and reports whether
that candidate is missing, incomplete, ready, or non-compliant.

Phase 9 adds a controlled copy command for a validated runtime candidate. It
copies only the prepared Python runtime folder, SearXNG vendor folder, and
runtime manifest into the target project, with backups, and still does not start
Python or SearXNG.

Phase 12 adds a runtime smokecheck. It executes the bundled Python briefly to
verify version/import behavior and SearXNG import readiness, but it does not
start SearXNG as a server or activate real Web Search.

Phase 13 adds a dependency inventory. It reads SearXNG runtime requirements and
checks which declared Python dependencies are already importable by the bundled
runtime, without downloading packages, installing with pip, or launching
SearXNG.

Phase 18 adds a temporary launch smokecheck. It starts SearXNG on loopback,
checks `/healthz`, and stops it immediately. It does not activate Web Search in
Kivrio Chat or perform a real search query.

Phase 19 added the backend managed-start guard. The final default is now user
friendly: if the local runtime is present, the backend may start it automatically;
`KIVRIO_WEB_SEARCH_ENABLE_MANAGED=0` remains available to disable managed
startup explicitly. The backend still accepts only local loopback base URLs.

Phase 20 adds a managed API integration test. It starts the C# backend with the
normal managed-start default, lets the backend launch local SearXNG, calls
`/api/web-search`, validates the Kivrio JSON response contract, and then stops
the managed SearXNG process.
