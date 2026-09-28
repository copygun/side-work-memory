import { expect } from "bun:test"
import { chmodSync, linkSync, writeFileSync } from "node:fs"
import { delimiter, join } from "node:path"

export const IS_WINDOWS = process.platform === "win32"

/**
 * POSIX permission bits carry the privacy guarantee on macOS. Windows reports synthetic modes
 * (0o666/0o444) and relies on the per-user profile ACL instead, so the bit check is skipped there.
 */
export function expectPrivateMode(mode: number | bigint, expected: number): void {
  if (IS_WINDOWS) return
  expect(Number(mode) & 0o777).toBe(expected)
}

/** A PATH that contains no user CLIs (used to prove per-user install fallbacks). */
export const MINIMAL_SYSTEM_PATH = IS_WINDOWS
  ? "C:\\Windows\\System32;C:\\Windows"
  : "/usr/bin:/bin:/usr/sbin:/sbin"

export function prependPath(directory: string, path: string | undefined): string {
  return `${directory}${delimiter}${path ?? ""}`
}

/**
 * Install a Bun script as a CLI named `name` inside `directory`.
 * POSIX: an executable file with a Bun shebang. Windows: an npm-style `name.cmd` shim next to
 * `name.js`, plus `node.exe` hard-linked to the running Bun so the shim unwraps to Bun.
 * Returns the path the production resolver will find first.
 */
export function installCliStub(directory: string, name: string, script: string): string {
  const body = script.replace(/^#![^\n]*\n/, "")
  if (!IS_WINDOWS) {
    const executable = join(directory, name)
    writeFileSync(executable, `#!${process.execPath}\n${body}`)
    chmodSync(executable, 0o700)
    return executable
  }
  writeFileSync(join(directory, `${name}.js`), body)
  const shim = join(directory, `${name}.cmd`)
  writeFileSync(shim, `@ECHO off\r\n"%dp0%\\node.exe" "%dp0%\\${name}.js" %*\r\n`)
  try {
    linkSync(process.execPath, join(directory, "node.exe"))
  } catch (error) {
    if (!(error instanceof Error && "code" in error && error.code === "EEXIST")) throw error
  }
  return shim
}
