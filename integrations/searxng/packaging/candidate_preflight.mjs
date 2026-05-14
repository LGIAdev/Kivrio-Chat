import { stat } from 'node:fs/promises';
import path from 'node:path';
import { fileURLToPath, pathToFileURL } from 'node:url';

import { auditWebSearchBundle } from './bundle_audit.mjs';

const thisFile = fileURLToPath(import.meta.url);
const packagingRoot = path.dirname(thisFile);
const repoRoot = path.resolve(packagingRoot, '..', '..', '..');

export async function preflightCandidateBundle(options = {}) {
  const rawCandidatePath = String(options.candidatePath || '').trim();
  const candidatePath = rawCandidatePath ? path.resolve(rawCandidatePath) : '';
  const allowlistPath = options.allowlistPath || path.join(packagingRoot, 'runtime-allowlist.txt');
  const denylistPath = options.denylistPath || path.join(packagingRoot, 'runtime-denylist.txt');

  if (!candidatePath || !(await isDirectory(candidatePath))) {
    return {
      ok: false,
      status: 'candidate_missing',
      candidatePath,
      diagnostics: [
        diagnostic('candidate_missing', 'error', 'Candidate directory is missing or is not a directory.'),
      ],
      audit: null,
    };
  }

  const audit = await auditWebSearchBundle({
    repoRoot: candidatePath,
    allowlistPath,
    denylistPath,
    manifestPath: path.join(candidatePath, 'runtime-manifest.json'),
    pythonExePath: path.join(candidatePath, 'runtime', 'python', 'python.exe'),
    searxngRoot: path.join(candidatePath, 'integrations', 'searxng', 'vendor', 'searxng'),
    bundleRoots: [
      path.join(candidatePath, 'runtime', 'python'),
      path.join(candidatePath, 'integrations', 'searxng', 'vendor'),
    ],
  });

  const status = candidateStatusFor(audit.status);
  const diagnostics = audit.diagnostics.slice();
  if (status === 'candidate_incomplete') {
    diagnostics.push(diagnostic('candidate_incomplete', 'error', 'Candidate bundle is missing required runtime files.'));
  }
  if (status === 'candidate_non_compliant') {
    diagnostics.push(diagnostic('candidate_non_compliant', 'error', 'Candidate bundle contains denied or invalid entries.'));
  }

  return {
    ok: status === 'candidate_ready' && audit.ok,
    status,
    candidatePath,
    diagnostics,
    audit,
  };
}

function candidateStatusFor(auditStatus) {
  if (auditStatus === 'bundle_ready') return 'candidate_ready';
  if (auditStatus === 'bundle_non_compliant') return 'candidate_non_compliant';
  return 'candidate_incomplete';
}

async function isDirectory(filePath) {
  try {
    const info = await stat(filePath);
    return info.isDirectory();
  } catch (error) {
    if (error && error.code === 'ENOENT') return false;
    throw error;
  }
}

function diagnostic(code, severity, message) {
  return { code, severity, message };
}

function formatSummary(result) {
  const diagnostics = result.diagnostics.map((item) => `${item.severity}:${item.code}`).join(', ');
  const pathLabel = path.relative(repoRoot, result.candidatePath) || result.candidatePath;
  return `web search candidate preflight: ${result.status}; ok=${result.ok}; candidate=${pathLabel}; diagnostics=${diagnostics || 'none'}`;
}

function parseArgs(argv) {
  const args = { candidatePath: '', json: false };
  for (let i = 0; i < argv.length; i += 1) {
    const arg = argv[i];
    if (arg === '--json') {
      args.json = true;
      continue;
    }
    if (arg === '--candidate' && i + 1 < argv.length) {
      args.candidatePath = argv[i + 1];
      i += 1;
      continue;
    }
    if (!args.candidatePath) {
      args.candidatePath = arg;
    }
  }
  return args;
}

if (isMain()) {
  const args = parseArgs(process.argv.slice(2));
  if (!args.candidatePath) {
    console.error('usage: node integrations/searxng/packaging/candidate_preflight.mjs --candidate <path> [--json]');
    process.exitCode = 2;
  } else {
    const result = await preflightCandidateBundle({ candidatePath: args.candidatePath });
    if (args.json) {
      console.log(JSON.stringify(result, null, 2));
    } else {
      console.log(formatSummary(result));
      const violations = result.audit
        ? [...result.audit.policy.violations, ...result.audit.bundle.violations]
        : [];
      for (const violation of violations) {
        console.log(`- ${violation}`);
      }
    }
    if (!result.ok) {
      process.exitCode = 1;
    }
  }
}

function isMain() {
  if (!process.argv[1]) return false;
  return import.meta.url === pathToFileURL(process.argv[1]).href;
}
