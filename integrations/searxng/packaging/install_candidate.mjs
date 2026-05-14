import { cp, mkdir, rm, stat } from 'node:fs/promises';
import path from 'node:path';
import { fileURLToPath, pathToFileURL } from 'node:url';

import { auditWebSearchBundle } from './bundle_audit.mjs';
import { preflightCandidateBundle } from './candidate_preflight.mjs';

const thisFile = fileURLToPath(import.meta.url);
const packagingRoot = path.dirname(thisFile);
const defaultTargetRoot = path.resolve(packagingRoot, '..', '..', '..');

export async function installCandidateBundle(options = {}) {
  const rawCandidatePath = String(options.candidatePath || '').trim();
  const candidatePath = rawCandidatePath ? path.resolve(rawCandidatePath) : '';
  const targetRoot = path.resolve(options.targetRoot || defaultTargetRoot);
  const confirmed = options.confirm === true;
  const allowlistPath = options.allowlistPath || path.join(packagingRoot, 'runtime-allowlist.txt');
  const denylistPath = options.denylistPath || path.join(packagingRoot, 'runtime-denylist.txt');

  if (!confirmed) {
    return rejected('install_confirmation_required', candidatePath, targetRoot, 'Runtime candidate copy requires explicit confirmation.');
  }

  if (!candidatePath) {
    return rejected('candidate_missing', candidatePath, targetRoot, 'Candidate directory is required.');
  }

  if (isPathInside(targetRoot, candidatePath)) {
    return rejected('candidate_inside_target', candidatePath, targetRoot, 'Candidate must be outside the target project.');
  }

  const targets = installTargets(targetRoot);
  for (const destination of [targets.pythonDir, targets.searxngDir, targets.manifestPath]) {
    if (!isPathInside(targetRoot, destination)) {
      return rejected('target_path_invalid', candidatePath, targetRoot, 'Install target resolved outside the project root.');
    }
  }

  const preflight = await preflightCandidateBundle({
    candidatePath,
    allowlistPath,
    denylistPath,
  });

  if (!preflight.ok || preflight.status !== 'candidate_ready') {
    return {
      ok: false,
      status: 'install_rejected',
      candidatePath,
      targetRoot,
      diagnostics: [
        ...preflight.diagnostics,
        diagnostic('candidate_not_ready', 'error', 'Candidate must pass preflight with candidate_ready before copy.'),
      ],
      backups: [],
      installed: [],
      preflight,
      audit: null,
    };
  }

  const sources = {
    pythonDir: path.join(candidatePath, 'runtime', 'python'),
    searxngDir: path.join(candidatePath, 'integrations', 'searxng', 'vendor', 'searxng'),
    manifestPath: path.join(candidatePath, 'runtime-manifest.json'),
  };

  const backupRoot = path.join(
    targetRoot,
    'integrations',
    'searxng',
    'packaging',
    'backups',
    `import-${timestampForPath()}`,
  );

  const backups = [];
  await backupExisting(targets.pythonDir, path.join(backupRoot, 'runtime', 'python'), backups);
  await backupExisting(targets.searxngDir, path.join(backupRoot, 'integrations', 'searxng', 'vendor', 'searxng'), backups);
  await backupExisting(targets.manifestPath, path.join(backupRoot, 'runtime-manifest.json'), backups);

  await replaceDirectory(sources.pythonDir, targets.pythonDir);
  await replaceDirectory(sources.searxngDir, targets.searxngDir);
  await replaceFile(sources.manifestPath, targets.manifestPath);

  const audit = await auditWebSearchBundle({
    repoRoot: targetRoot,
    allowlistPath,
    denylistPath,
    manifestPath: targets.manifestPath,
    pythonExePath: path.join(targets.pythonDir, 'python.exe'),
    searxngRoot: targets.searxngDir,
    bundleRoots: [
      targets.pythonDir,
      path.join(targetRoot, 'integrations', 'searxng', 'vendor'),
    ],
  });

  const ok = audit.ok && audit.status === 'bundle_ready';
  return {
    ok,
    status: ok ? 'install_completed' : 'install_failed_post_audit',
    candidatePath,
    targetRoot,
    diagnostics: ok
      ? []
      : [
          ...audit.diagnostics,
          diagnostic('post_install_audit_failed', 'error', 'Installed candidate did not pass the target bundle audit.'),
        ],
    backups,
    installed: [
      path.relative(targetRoot, targets.pythonDir),
      path.relative(targetRoot, targets.searxngDir),
      path.relative(targetRoot, targets.manifestPath),
    ],
    preflight,
    audit,
  };
}

function rejected(code, candidatePath, targetRoot, message) {
  return {
    ok: false,
    status: 'install_rejected',
    candidatePath,
    targetRoot,
    diagnostics: [diagnostic(code, 'error', message)],
    backups: [],
    installed: [],
    preflight: null,
    audit: null,
  };
}

function installTargets(targetRoot) {
  return {
    pythonDir: path.join(targetRoot, 'runtime', 'python'),
    searxngDir: path.join(targetRoot, 'integrations', 'searxng', 'vendor', 'searxng'),
    manifestPath: path.join(targetRoot, 'integrations', 'searxng', 'packaging', 'runtime-manifest.json'),
  };
}

async function backupExisting(sourcePath, backupPath, backups) {
  if (!(await exists(sourcePath))) return;
  await mkdir(path.dirname(backupPath), { recursive: true });
  await cp(sourcePath, backupPath, { recursive: true, force: true });
  backups.push({ from: sourcePath, to: backupPath });
}

async function replaceDirectory(sourcePath, destinationPath) {
  await rm(destinationPath, { recursive: true, force: true });
  await mkdir(path.dirname(destinationPath), { recursive: true });
  await cp(sourcePath, destinationPath, { recursive: true, force: true });
}

async function replaceFile(sourcePath, destinationPath) {
  await rm(destinationPath, { force: true });
  await mkdir(path.dirname(destinationPath), { recursive: true });
  await cp(sourcePath, destinationPath, { force: true });
}

async function exists(filePath) {
  try {
    await stat(filePath);
    return true;
  } catch (error) {
    if (error && error.code === 'ENOENT') return false;
    throw error;
  }
}

function isPathInside(rootPath, candidatePath) {
  const root = path.resolve(rootPath);
  const candidate = path.resolve(candidatePath);
  const relative = path.relative(root, candidate);
  return relative === '' || (!relative.startsWith('..') && !path.isAbsolute(relative));
}

function timestampForPath() {
  return new Date().toISOString().replace(/[:.]/g, '-');
}

function diagnostic(code, severity, message) {
  return { code, severity, message };
}

function parseArgs(argv) {
  const args = { candidatePath: '', targetRoot: defaultTargetRoot, json: false, confirm: false };
  for (let i = 0; i < argv.length; i += 1) {
    const arg = argv[i];
    if (arg === '--json') {
      args.json = true;
      continue;
    }
    if (arg === '--confirm-install') {
      args.confirm = true;
      continue;
    }
    if (arg === '--candidate' && i + 1 < argv.length) {
      args.candidatePath = argv[i + 1];
      i += 1;
      continue;
    }
    if (arg === '--target' && i + 1 < argv.length) {
      args.targetRoot = argv[i + 1];
      i += 1;
      continue;
    }
    if (!args.candidatePath) {
      args.candidatePath = arg;
    }
  }
  return args;
}

function formatSummary(result) {
  const diagnostics = result.diagnostics.map((item) => `${item.severity}:${item.code}`).join(', ');
  return `web search candidate install: ${result.status}; ok=${result.ok}; diagnostics=${diagnostics || 'none'}`;
}

if (isMain()) {
  const args = parseArgs(process.argv.slice(2));
  if (!args.candidatePath) {
    console.error('usage: node integrations/searxng/packaging/install_candidate.mjs --candidate <path> --confirm-install [--json]');
    process.exitCode = 2;
  } else {
    const result = await installCandidateBundle(args);
    if (args.json) {
      console.log(JSON.stringify(result, null, 2));
    } else {
      console.log(formatSummary(result));
      for (const item of result.diagnostics) {
        console.log(`- ${item.message}`);
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
