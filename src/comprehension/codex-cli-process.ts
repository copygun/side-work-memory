import { spawn } from "node:child_process"
import { StringDecoder } from "node:string_decoder"
import { SUMMARY_CODEX_CONSENT_POLL_MS, SUMMARY_CODEX_MAX_OUTPUT_BYTES } from "../constants"
import {
  IS_WINDOWS,
  killProcessTree,
  type ResolvedExecutable,
  resolveCliExecutable,
} from "../platform/executable"

export class CodexCliUnavailableError extends Error {
  readonly name = "CodexCliUnavailableError"
  constructor() {
    super("Codex CLI ChatGPT login is unavailable or returned no usable output")
  }
}

export class CodexCliConsentRevokedError extends Error {
  readonly name = "CodexCliConsentRevokedError"
  constructor() {
    super("summary evidence permission was revoked during Codex CLI execution")
  }
}

type CapturedRun = {
  readonly stdout: string
  readonly stderr: string
  readonly responseBytes: number
}
type StopReason = "revoked" | "timeout" | "overflow" | "input-error" | "tool"
type RunOptions = {
  readonly executable: ResolvedExecutable
  readonly args: readonly string[]
  readonly input?: string
  readonly cwd: string
  readonly home: string
  readonly codexHome: string
  readonly deadline: number
  readonly canSendEvidence: () => boolean
}

function isToolEvent(line: string): boolean {
  let event: unknown
  try {
    event = JSON.parse(line)
  } catch (error) {
    if (!(error instanceof SyntaxError)) throw error
    return false
  }
  if (event === null || typeof event !== "object" || !("type" in event)) return false
  if (
    event.type !== "item.started" &&
    event.type !== "item.updated" &&
    event.type !== "item.completed"
  )
    return false
  if (
    !("item" in event) ||
    event.item === null ||
    typeof event.item !== "object" ||
    !("type" in event.item)
  )
    return false
  return event.item.type !== "agent_message" && event.item.type !== "reasoning"
}

export function resolveCodexExecutable(): ResolvedExecutable {
  const resolved = resolveCliExecutable("codex")
  if (resolved === null) throw new CodexCliUnavailableError()
  return resolved
}

function cliEnvironment(home: string, codexHome: string): NodeJS.ProcessEnv {
  const env = { ...process.env }
  for (const name of Object.keys(env)) {
    if (/(?:^|_)(?:API_KEY|AUTH_TOKEN)$/i.test(name) || name.startsWith("CODEX_")) delete env[name]
  }
  for (const name of [
    "OPENAI_BASE_URL",
    "OPENAI_API_BASE",
    "OPENAI_ORGANIZATION",
    "OPENAI_PROJECT",
    "OPENAI_PROJECT_ID",
    "AZURE_OPENAI_ENDPOINT",
  ])
    delete env[name]
  env["HOME"] = home
  if (IS_WINDOWS) env["USERPROFILE"] = home
  env["CODEX_HOME"] = codexHome
  return env
}

export async function runCodex(options: RunOptions): Promise<CapturedRun> {
  const { executable, args, input, cwd, home, codexHome, deadline, canSendEvidence } = options
  if (!canSendEvidence()) throw new CodexCliConsentRevokedError()
  if (performance.now() >= deadline) throw new CodexCliUnavailableError()
  const child = spawn(executable.command, [...executable.prefixArgs, ...args], {
    shell: false,
    // POSIX: new process group so the whole tree can be killed. Windows: detached would open
    // a console window; the tree is killed with taskkill /T instead.
    detached: !IS_WINDOWS,
    windowsHide: true,
    cwd,
    stdio: ["pipe", "pipe", "pipe"],
    env: cliEnvironment(home, codexHome),
  })
  const stdoutChunks: Buffer[] = []
  const stderrChunks: Buffer[] = []
  let responseBytes = 0
  let stopped: StopReason | undefined
  let pendingLine = ""
  const decoder = new StringDecoder("utf8")
  return new Promise<CapturedRun>((resolve, reject) => {
    const stop = (reason: StopReason): void => {
      if (stopped) return
      stopped = reason
      killProcessTree(child.pid, () => child.kill("SIGKILL"))
    }
    const timer = setTimeout(() => stop("timeout"), Math.max(0, deadline - performance.now()))
    const consentPoll = setInterval(() => {
      if (!canSendEvidence()) stop("revoked")
    }, SUMMARY_CODEX_CONSENT_POLL_MS)
    const cleanup = (): void => {
      clearTimeout(timer)
      clearInterval(consentPoll)
    }
    const collect = (chunk: Buffer, stdout: boolean): void => {
      responseBytes += chunk.length
      if (responseBytes > SUMMARY_CODEX_MAX_OUTPUT_BYTES) stop("overflow")
      else if (stdout) {
        stdoutChunks.push(chunk)
        if (args[0] === "exec") {
          pendingLine += decoder.write(chunk)
          const lines = pendingLine.split("\n")
          pendingLine = lines.pop() ?? ""
          if (lines.some(isToolEvent)) stop("tool")
        }
      } else stderrChunks.push(chunk)
    }
    child.stdout.on("data", (chunk: Buffer) => collect(chunk, true))
    child.stderr.on("data", (chunk: Buffer) => collect(chunk, false))
    child.stdin.on("error", () => stop("input-error"))
    child.once("error", () => {
      cleanup()
      reject(new CodexCliUnavailableError())
    })
    child.once("close", (code) => {
      cleanup()
      if (stopped === "revoked" || !canSendEvidence()) reject(new CodexCliConsentRevokedError())
      else if (stopped || code !== 0) reject(new CodexCliUnavailableError())
      else
        resolve({
          stdout: Buffer.concat(stdoutChunks).toString("utf8"),
          stderr: Buffer.concat(stderrChunks).toString("utf8"),
          responseBytes,
        })
    })
    child.stdin.end(input)
  })
}
