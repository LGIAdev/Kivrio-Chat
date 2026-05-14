# Web Search Runtime Packaging

This directory defines the packaging contract for the future local Web Search
runtime. It does not provide Python, SearXNG, dependencies, installers, or
download scripts.

Current goal:

- keep the future bundle minimal and auditable;
- document the runtime paths that are allowed;
- reject development artifacts before they can be shipped;
- explain why the runtime bundle is not ready yet;
- keep Kivrio Chat working when the bundle is absent.

The expected runtime bundle remains optional. When it is not present, the
backend continues to return the soft "Web Search unavailable" response.

## Runtime Roots

The future bundle is limited to these areas:

```text
runtime/python/
integrations/searxng/vendor/searxng/
integrations/searxng/config/
integrations/searxng/client/
integrations/searxng/launcher/
```

`integrations/searxng/runtime/` is reserved for local state created at runtime.
It is not part of the distributable bundle.

## Audit

The audit test is intentionally conservative:

```text
node tests/web-search-bundle-audit.test.mjs
```

If the bundle is absent or empty, the test passes. If a future bundle is added,
the test scans it and fails on denied paths such as caches, tests, development
documentation, Git metadata, build outputs, installer tooling, or frontend
dependencies.

The reusable diagnostic can also be run directly:

```text
node integrations/searxng/packaging/bundle_audit.mjs
node integrations/searxng/packaging/bundle_audit.mjs --json
```

The diagnostic status is one of:

```text
bundle_absent
bundle_incomplete
bundle_ready
bundle_non_compliant
```

Typical diagnostic codes include:

```text
python_absent
searxng_absent
manifest_absent
manifest_invalid
policy_non_compliant
bundle_non_compliant
```

`runtime-manifest.example.json` documents the expected metadata for a future
candidate bundle. A real candidate should use `runtime-manifest.json`.

## Candidate Preflight

A manually prepared candidate bundle can be checked before it is copied into
Kivrio Chat:

```text
node integrations/searxng/packaging/candidate_preflight.mjs --candidate C:\path\to\candidate
node integrations/searxng/packaging/candidate_preflight.mjs --candidate C:\path\to\candidate --json
```

The candidate directory is scanned in read-only mode. The command does not copy
files, install Python, download SearXNG, start a process, or modify Kivrio Chat.

The expected candidate layout is:

```text
runtime-manifest.json
runtime/python/python.exe
integrations/searxng/vendor/searxng/
```

The candidate status is one of:

```text
candidate_missing
candidate_incomplete
candidate_ready
candidate_non_compliant
```

`candidate_ready` is the only successful status.

## Controlled Candidate Copy

After a candidate returns `candidate_ready`, it can be copied into a Kivrio Chat
project with:

```text
node integrations/searxng/packaging/install_candidate.mjs --candidate C:\path\to\candidate --confirm-install
node integrations/searxng/packaging/install_candidate.mjs --candidate C:\path\to\candidate --confirm-install --json
```

This means "copy the validated runtime candidate into the expected project
folders". It does not install Python on Windows, download SearXNG, start
SearXNG, change the UI, or call the network.

The copy targets are:

```text
runtime/python/
integrations/searxng/vendor/searxng/
integrations/searxng/packaging/runtime-manifest.json
```

The command refuses to copy unless:

- `--confirm-install` is present;
- the candidate passes preflight with `candidate_ready`;
- the candidate is outside the target project;
- all resolved target paths stay inside the target project.

Existing target runtime files are backed up under:

```text
integrations/searxng/packaging/backups/
```

## Runtime Smokecheck

After the candidate is copied, the runtime can be checked without starting a
SearXNG server:

```text
node integrations/searxng/packaging/runtime_smokecheck.mjs
node integrations/searxng/packaging/runtime_smokecheck.mjs --json
```

The smokecheck runs `runtime/python/python.exe` only long enough to verify:

- Python version output;
- standard runtime imports such as `ssl`, `socket`, and `sqlite3`;
- required SearXNG files;
- `import searx` with the local vendor path injected.

It does not start SearXNG as a web server, call the network, change the UI, or
modify backend behavior.

The smokecheck status is one of:

```text
runtime_ready
python_missing
python_broken
searx_missing
searx_import_failed
dependency_missing
```

## Dependency Inventory

When the smokecheck reports `dependency_missing`, the declared SearXNG runtime
dependencies can be inventoried without installing anything:

```text
node integrations/searxng/packaging/dependency_inventory.mjs
node integrations/searxng/packaging/dependency_inventory.mjs --json
```

The inventory reads the vendored SearXNG `requirements.txt`, maps package names
to Python import names, runs the bundled Python briefly, and reports which
declared dependencies are importable.

It does not download packages, run `pip`, start SearXNG, change Python path
files, or modify backend/frontend behavior.

The dependency status is one of:

```text
dependencies_ready
dependencies_missing
python_missing
python_broken
requirements_missing
```

## Runtime Launch Smokecheck

After the runtime smokecheck and dependency inventory are ready, SearXNG can be
started briefly on loopback to verify process launch behavior:

```text
node integrations/searxng/packaging/runtime_launch_smokecheck.mjs
node integrations/searxng/packaging/runtime_launch_smokecheck.mjs --json
```

The launch smokecheck starts SearXNG with the embedded Python runtime, checks
`/healthz` on `127.0.0.1`, and then stops the managed process immediately.

It does not call search engines, activate the frontend Web Search flow, change
backend routing, download packages, install with pip, or install Python
system-wide.

The launch status is one of:

```text
launch_ready
launch_failed
healthcheck_failed
stop_failed
python_missing
launcher_missing
```
