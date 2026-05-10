# Security Policy

## Supported Versions
We currently support the latest version of **Kivrio**.  
Older versions may not receive security updates.

---

## Reporting a Vulnerability
If you discover a security vulnerability in Kivrio:

1. **Do not open a public issue.**
2. Instead, send a private report to: **contact@lg-ia-researchlab.fr**
3. Include:
   - A clear description of the issue
   - Steps to reproduce
   - Possible impact
   - Suggested fix (if any)

---

## Response Process
- We will acknowledge receipt of your report within **7 days**.
- We aim to investigate and provide a fix or mitigation within **14-30 days**.
- Once resolved, we will publish a new release and mention the fix in the release notes.

---

## Responsible Disclosure
We ask that you **do not publicly disclose the vulnerability** until a fix has been released,  
to protect users of Kivrio.

---

## Local Dependency Inventory

Kivrio Chat does not use a package-manager manifest in this repository: no `package.json`, lockfile, `.csproj`, or external runtime dependency file is present.

Vendored browser dependency:

- KaTeX `0.16.10`, stored under `assets/vendor/katex/` and loaded locally for math rendering.

The dependency scan performed here is local-only. It inventories vendored code and obvious manifests, but it does not query external vulnerability databases. A release process should add an explicit online CVE/advisory check when network access is approved.

---

## Operational Logging

Kivrio Chat server logs are emitted as single-line JSON events on stderr.

The logger uses an explicit allowlist of fields and is not meant to record request bodies, cookies, authorization headers, passwords, conversation content, attachment contents, or full local filesystem paths.

Unexpected server errors are reported to clients with a generic message. The structured log keeps only operational metadata such as event name, level, method, path, status, exception type, and a stable reason code.
