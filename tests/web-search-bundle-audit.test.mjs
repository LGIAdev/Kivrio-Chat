import assert from 'node:assert/strict';
import { mkdir, mkdtemp, rm, writeFile } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

import {
  auditWebSearchBundle,
  requiredAllowlistAnchors,
  requiredDenylistPatterns,
} from '../integrations/searxng/packaging/bundle_audit.mjs';

const repoRoot = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');
const allowlistPath = path.join(repoRoot, 'integrations', 'searxng', 'packaging', 'runtime-allowlist.txt');
const denylistPath = path.join(repoRoot, 'integrations', 'searxng', 'packaging', 'runtime-denylist.txt');

{
  const audit = await auditWebSearchBundle();

  assert.equal(audit.ok, true, formatViolations(audit));
  assert(
    ['bundle_absent', 'bundle_incomplete', 'bundle_ready'].includes(audit.status),
    `unexpected bundle status: ${audit.status}`,
  );

  for (const anchor of requiredAllowlistAnchors) {
    assert(
      audit.policy.allowlist.includes(anchor),
      `runtime allowlist should include ${anchor}`,
    );
  }

  for (const pattern of requiredDenylistPatterns) {
    assert(
      audit.policy.denylist.includes(pattern),
      `runtime denylist should include ${pattern}`,
    );
  }

  if (!audit.bundle.pythonPresent) {
    assert(hasDiagnostic(audit, 'python_absent'), 'diagnostic should report absent Python runtime');
  }
  if (!audit.bundle.searxngPresent) {
    assert(hasDiagnostic(audit, 'searxng_absent'), 'diagnostic should report absent SearXNG vendor');
  }
  if (!audit.bundle.manifestPresent) {
    assert(hasDiagnostic(audit, 'manifest_absent'), 'diagnostic should report absent runtime manifest');
  }
}

{
  const fixtureRoot = await mkdtemp(path.join(tmpdir(), 'kivrio-web-search-audit-'));
  try {
    const deniedCache = path.join(fixtureRoot, 'runtime', 'python', '__pycache__');
    await mkdir(deniedCache, { recursive: true });
    await writeFile(path.join(deniedCache, 'module.pyc'), 'bad cache');

    const audit = await auditWebSearchBundle({
      repoRoot: fixtureRoot,
      allowlistPath,
      denylistPath,
    });

    assert.equal(audit.ok, false, 'denied development artifacts should fail the audit');
    assert.equal(audit.status, 'bundle_non_compliant');
    assert(
      audit.bundle.violations.some((violation) => violation.includes('__pycache__')),
      'audit should identify denied Python bytecode caches',
    );
    assert(hasDiagnostic(audit, 'bundle_non_compliant'), 'audit should include a non-compliance diagnostic');
  } finally {
    await rm(fixtureRoot, { recursive: true, force: true });
  }
}

{
  const fixtureRoot = await mkdtemp(path.join(tmpdir(), 'kivrio-web-search-audit-wheel-metadata-'));
  try {
    const distInfo = path.join(fixtureRoot, 'runtime', 'python', 'Lib', 'site-packages', 'example-1.0.0.dist-info');
    await mkdir(distInfo, { recursive: true });
    await writeFile(path.join(distInfo, 'WHEEL'), 'Wheel-Version: 1.0\n');

    const audit = await auditWebSearchBundle({
      repoRoot: fixtureRoot,
      allowlistPath,
      denylistPath,
    });

    assert.equal(audit.ok, true, formatViolations(audit));
    assert(
      !audit.bundle.violations.some((violation) => violation.includes('dist-info/WHEEL')),
      'audit should allow installed package WHEEL metadata files',
    );
  } finally {
    await rm(fixtureRoot, { recursive: true, force: true });
  }
}

{
  const fixtureRoot = await mkdtemp(path.join(tmpdir(), 'kivrio-web-search-audit-wheel-folder-'));
  try {
    const wheelPackage = path.join(fixtureRoot, 'runtime', 'python', 'wheel');
    await mkdir(wheelPackage, { recursive: true });
    await writeFile(path.join(wheelPackage, '__init__.py'), 'def main():\n    return None\n');

    const audit = await auditWebSearchBundle({
      repoRoot: fixtureRoot,
      allowlistPath,
      denylistPath,
    });

    assert.equal(audit.ok, false, 'wheel installer package should fail the audit');
    assert.equal(audit.status, 'bundle_non_compliant');
    assert(
      audit.bundle.violations.some((violation) => violation.includes('runtime/python/wheel')),
      'audit should identify a denied wheel installer package directory',
    );
  } finally {
    await rm(fixtureRoot, { recursive: true, force: true });
  }
}

console.log('web search bundle audit tests passed');

function hasDiagnostic(audit, code) {
  return audit.diagnostics.some((item) => item.code === code);
}

function formatViolations(audit) {
  const violations = [...audit.policy.violations, ...audit.bundle.violations];
  return violations.length === 0
    ? 'web search runtime bundle audit should pass'
    : `web search runtime bundle audit should pass:\n${violations.join('\n')}`;
}
