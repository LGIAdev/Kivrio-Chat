import assert from 'node:assert/strict';
import { execFile } from 'node:child_process';
import { mkdir, mkdtemp, rm } from 'node:fs/promises';
import net from 'node:net';
import os from 'node:os';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { promisify } from 'node:util';

const execFileAsync = promisify(execFile);
const repoRoot = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');
const pythonExe = path.join(repoRoot, 'runtime', 'python', 'python.exe');
const launcherDir = path.join(repoRoot, 'integrations', 'searxng', 'launcher');

const root = await mkdtemp(path.join(os.tmpdir(), 'kivrio-stale-searxng-pid-'));

try {
  const port = await freePort();
  const runtimeDir = path.join(root, 'runtime');
  await mkdir(runtimeDir, { recursive: true });

  const code = [
    'import sys',
    'import os',
    'from pathlib import Path',
    `sys.path.insert(0, ${JSON.stringify(launcherDir)})`,
    'import start_searxng',
    'import stop_searxng',
    'root = Path(sys.argv[1])',
    'port = int(sys.argv[2])',
    'assert start_searxng.resolve_log_targets(root) == (None, None)',
    'os.environ["KIVRIO_WEB_SEARCH_DEBUG_LOGS"] = "1"',
    'stdout_path, stderr_path = start_searxng.resolve_log_targets(root)',
    'assert stdout_path.name == "searxng.stdout.log"',
    'assert stderr_path.name == "searxng.stderr.log"',
    'os.environ.pop("KIVRIO_WEB_SEARCH_DEBUG_LOGS", None)',
    'pid_file = root / "runtime" / "searxng.pid"',
    'settings_file = root / "runtime" / "settings-launch.yml"',
    'pid_file.write_text("999999", encoding="utf-8")',
    'settings_file.write_text("server:\\n  port: " + str(port) + "\\n", encoding="utf-8")',
    'start_searxng.launch_settings_path = lambda: settings_file',
    'result = start_searxng.resolve_recorded_process(999999, 0, pid_file)',
    'assert result is None',
    'assert not pid_file.exists()',
    '(root / "runtime" / "cache").mkdir(parents=True, exist_ok=True)',
    '(root / "runtime" / "logs").mkdir(parents=True, exist_ok=True)',
    '(root / "runtime" / "tmp").mkdir(parents=True, exist_ok=True)',
    '(root / "runtime" / "searxng.stderr.log").write_text("query", encoding="utf-8")',
    '(root / "runtime" / "settings-launch.yml").write_text("secret", encoding="utf-8")',
    '(root / "runtime" / "cache" / "cache.txt").write_text("query", encoding="utf-8")',
    'stop_searxng.purge_runtime(root / "runtime")',
    'assert not (root / "runtime" / "searxng.stderr.log").exists()',
    'assert not (root / "runtime" / "settings-launch.yml").exists()',
    'assert not any((root / "runtime" / "cache").iterdir())',
    'print("stale pid cleared")',
  ].join('\n');

  const completed = await execFileAsync(pythonExe, ['-c', code, root, String(port)], {
    cwd: repoRoot,
    windowsHide: true,
    timeout: 30000,
    maxBuffer: 1024 * 1024,
  });

  assert.match(completed.stdout, /stale pid cleared/);
  console.log('web search launcher stale pid tests passed');
} finally {
  await rm(root, { recursive: true, force: true });
}

function freePort() {
  return new Promise((resolve, reject) => {
    const server = net.createServer();
    server.listen(0, '127.0.0.1', () => {
      const address = server.address();
      const port = address.port;
      server.close(() => resolve(port));
    });
    server.on('error', reject);
  });
}
