import { execFile } from 'node:child_process';
import { existsSync } from 'node:fs';
import { mkdir } from 'node:fs/promises';
import os from 'node:os';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { promisify } from 'node:util';

export const execFileAsync = promisify(execFile);
export const repoRoot = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');

export const pdfPigDllNames = [
  'UglyToad.PdfPig.dll',
  'UglyToad.PdfPig.Core.dll',
  'UglyToad.PdfPig.DocumentLayoutAnalysis.dll',
  'UglyToad.PdfPig.Fonts.dll',
  'UglyToad.PdfPig.Package.dll',
  'UglyToad.PdfPig.Tokenization.dll',
  'UglyToad.PdfPig.Tokens.dll',
  'Microsoft.Bcl.HashCode.dll',
  'System.Buffers.dll',
  'System.Memory.dll',
  'System.Numerics.Vectors.dll',
  'System.Runtime.CompilerServices.Unsafe.dll',
  'System.ValueTuple.dll',
];

export const backendTempDir = path.join(defaultTempRoot(), 'kivrio-chat-backend-tests');

function defaultTempRoot() {
  if (process.platform === 'win32' && existsSync('C:\\tmp')) {
    return 'C:\\tmp';
  }
  return os.tmpdir();
}

export async function ensureBackendTempDir() {
  await mkdir(backendTempDir, { recursive: true });
}

export function findCsc() {
  const windir = process.env.WINDIR || 'C:\\Windows';
  const candidates = [
    path.join(windir, 'Microsoft.NET', 'Framework64', 'v4.0.30319', 'csc.exe'),
    path.join(windir, 'Microsoft.NET', 'Framework', 'v4.0.30319', 'csc.exe'),
  ];
  return candidates.find((candidate) => existsSync(candidate)) || '';
}

export function pdfPigReferences(root = repoRoot) {
  return pdfPigDllNames.map((dllName) => `/r:${path.join(root, 'server', 'lib', 'pdfpig', dllName)}`);
}

export function assertBackendDependencies() {
  const csc = findCsc();
  if (!csc) {
    throw new Error('csc.exe introuvable pour compiler les tests backend.');
  }

  const serverSource = path.join(repoRoot, 'server', 'KivrioChatServer.cs');
  if (!existsSync(serverSource)) {
    throw new Error(`Serveur introuvable: ${serverSource}`);
  }

  for (const dllName of pdfPigDllNames) {
    const dllPath = path.join(repoRoot, 'server', 'lib', 'pdfpig', dllName);
    if (!existsSync(dllPath)) {
      throw new Error(`Dependance PDF introuvable: ${dllPath}`);
    }
  }

  return { csc, serverSource };
}

export async function compileCSharpBackendTest({ main, source, outputName }) {
  await ensureBackendTempDir();
  const { csc, serverSource } = assertBackendDependencies();
  const outputPath = path.join(backendTempDir, outputName);
  const args = [
    '/nologo',
    '/target:exe',
    `/main:${main}`,
    `/out:${outputPath}`,
    '/r:System.Web.Extensions.dll',
    ...pdfPigReferences(),
    serverSource,
    path.join(repoRoot, source),
  ];

  await execFileAsync(csc, args, {
    cwd: repoRoot,
    env: backendTestEnv(),
    windowsHide: true,
    maxBuffer: 1024 * 1024 * 8,
  });

  return outputPath;
}

export async function runExecutable(executablePath) {
  return execFileAsync(executablePath, [], {
    cwd: repoRoot,
    env: backendTestEnv(),
    windowsHide: true,
    maxBuffer: 1024 * 1024 * 8,
  });
}

export async function runNodeTest(relativePath) {
  await ensureBackendTempDir();
  return execFileAsync(process.execPath, [path.join(repoRoot, relativePath)], {
    cwd: repoRoot,
    env: backendTestEnv(),
    windowsHide: true,
    maxBuffer: 1024 * 1024 * 8,
  });
}

export async function listKivrioProcesses() {
  if (process.platform !== 'win32') {
    return [];
  }

  const command = [
    "Get-Process | Where-Object { $_.ProcessName -like '*Kivrio*' }",
    'Select-Object Id,ProcessName,Path | ConvertTo-Json -Compress',
  ].join(' | ');

  const { stdout } = await execFileAsync('powershell.exe', ['-NoProfile', '-Command', command], {
    cwd: repoRoot,
    windowsHide: true,
    maxBuffer: 1024 * 1024,
  });

  const text = stdout.trim();
  if (!text) return [];
  const parsed = JSON.parse(text);
  return Array.isArray(parsed) ? parsed : [parsed];
}

export function backendTestEnv() {
  return {
    ...process.env,
    TEMP: backendTempDir,
    TMP: backendTempDir,
  };
}
