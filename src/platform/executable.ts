import { accessSync, constants, readFileSync, statSync } from "node:fs"
import { homedir } from "node:os"
import { delimiter, dirname, isAbsolute, join } from "node:path"

/**
 * A resolved command line prefix. On Windows an npm `.cmd` shim cannot be spawned with
 * `shell: false`, so it is unwrapped to `node <script>` instead of going through cmd.exe
 * (cmd.exe quoting would expose JSON arguments and prompts to metacharacter injection).
 */
export type ResolvedExecutable = {
  readonly command: string
  readonly prefixArgs: readonly string[]
}

export const IS_WINDOWS = process.platform === "win32"

function isExecutableFile(path: string): boolean {
  try {
    if (!statSync(path).isFile()) return false
    accessSync(path, IS_WINDOWS ? constants.F_OK : constants.X_OK)
    return true
  } catch {
    return false
  }
}

/** User home that works on both platforms (HOME on POSIX, USERPROFILE on Windows). */
export function userHome(): string {
  const env = IS_WINDOWS
    ? (process.env["USERPROFILE"] ?? process.env["HOME"])
    : process.env["HOME"]
  return env && isAbsolute(env) ? env : homedir()
}

function searchDirectories(extra: readonly string[]): string[] {
  return [...(process.env["PATH"] ?? "").split(delimiter), ...extra].filter(
    (directory) => directory !== "" && isAbsolute(directory),
  )
}

/** Extract the JS entry point from an npm-generated `.cmd` shim. */
export function npmShimScript(shimPath: string): string | null {
  let text: string
  try {
    text = readFileSync(shimPath, "utf8")
  } catch {
    return null
  }
  // npm/cmd-shim writes e.g.  "%_prog%"  "%dp0%\node_modules\pkg\bin\cli.js" %*
  const match = /"%~?dp0%?\\([^"]+\.(?:c|m)?js)"/i.exec(text)
  if (!match?.[1]) return null
  const script = join(dirname(shimPath), match[1])
  return isExecutableFile(script) ? script : null
}

function resolveNode(shimDirectory: string, extra: readonly string[]): string | null {
  const local = join(shimDirectory, "node.exe")
  if (isExecutableFile(local)) return local
  for (const directory of searchDirectories(extra)) {
    const candidate = join(directory, "node.exe")
    if (isExecutableFile(candidate)) return candidate
  }
  return null
}

/**
 * Resolve a CLI by name from PATH, then from well-known per-user install directories.
 * Returns null when nothing usable is installed.
 */
export function resolveCliExecutable(name: string): ResolvedExecutable | null {
  const home = userHome()
  const extra = IS_WINDOWS
    ? [
        join(home, ".local", "bin"),
        join(process.env["APPDATA"] ?? join(home, "AppData", "Roaming"), "npm"),
        join(process.env["LOCALAPPDATA"] ?? join(home, "AppData", "Local"), "Programs", name),
      ]
    : [join(home, ".local", "bin")]
  const directories = searchDirectories(extra)
  if (!IS_WINDOWS) {
    for (const directory of directories) {
      const candidate = join(directory, name)
      if (isExecutableFile(candidate)) return { command: candidate, prefixArgs: [] }
    }
    return null
  }
  for (const directory of directories) {
    const exe = join(directory, `${name}.exe`)
    if (isExecutableFile(exe)) return { command: exe, prefixArgs: [] }
    const shim = join(directory, `${name}.cmd`)
    if (isExecutableFile(shim)) {
      const script = npmShimScript(shim)
      const node = script === null ? null : resolveNode(directory, extra)
      if (script !== null && node !== null) return { command: node, prefixArgs: [script] }
    }
  }
  return null
}

/** Kill a process and, where the platform allows it, its whole process tree. */
export function killProcessTree(pid: number | undefined, fallback: () => void): void {
  if (pid === undefined) {
    fallback()
    return
  }
  if (IS_WINDOWS) {
    try {
      // taskkill /T walks the tree; spawnSync keeps this synchronous like process.kill.
      Bun.spawnSync(["taskkill.exe", "/PID", String(pid), "/T", "/F"], {
        stdout: "ignore",
        stderr: "ignore",
      })
    } catch {
      // fall through to the direct kill below
    }
    fallback()
    return
  }
  try {
    process.kill(-pid, "SIGKILL")
  } catch (error) {
    if (!(error instanceof Error)) throw error
    fallback()
  }
}
