// Windows counterpart of apps/side-mac/scripts/build-app.sh.
// Produces dist\Side\ :
//   Side.exe                    C# tray helper (apps/side-win, framework-dependent .NET 10)
//   resources\side.exe          daemon / CLI / MCP (Bun single-file build)
//   resources\lib\*.dll|.node   vec0, ONNX Runtime
//   resources\web\              settings UI
//   resources\models\           multilingual MiniLM (q8)
//   resources\skills\           side-resume skill
// Usage: bun run build:win   (env: SIDE_MODEL_CACHE_SOURCE=<dir with Xenova/...> to skip download,
//                             DOTNET=<path to dotnet.exe>, SIDE_WIN_SELF_CONTAINED=1 for a self-contained helper)
import { cpSync, existsSync, mkdirSync, rmSync, statSync } from "node:fs"
import { join, resolve } from "node:path"

if (process.platform !== "win32") throw new TypeError("Windows builds require Windows")

const repo = resolve(import.meta.dir, "..")
const dist = join(repo, "dist", "Side")
const resources = join(dist, "resources")
const modelRel = join("Xenova", "paraphrase-multilingual-MiniLM-L12-v2")

function run(cmd: string[], cwd = repo): void {
  console.log(`> ${cmd.join(" ")}`)
  const result = Bun.spawnSync({ cmd, cwd, stdout: "inherit", stderr: "inherit" })
  if (result.exitCode !== 0) throw new Error(`Command failed (${result.exitCode}): ${cmd[0]}`)
}

rmSync(dist, { recursive: true, force: true })
mkdirSync(join(resources, "web"), { recursive: true })

// 1. Daemon + native libraries.
run([process.execPath, "run", "scripts/build-daemon-win.ts", "src/cli.ts", join(resources, "side.exe")])

// 2. Settings web UI.
run([process.execPath, "run", "build:web"])
cpSync(join(repo, "src", "web", "dist"), join(resources, "web"), { recursive: true })

// 3. Skills shipped with the app.
mkdirSync(join(resources, "skills", "side-resume"), { recursive: true })
cpSync(
  join(repo, "skills", "side-resume", "SKILL.md"),
  join(resources, "skills", "side-resume", "SKILL.md"),
)

// 4. Embedding model (bundled so first search works offline).
const modelDir = join(resources, "models", modelRel)
const source = process.env["SIDE_MODEL_CACHE_SOURCE"]
if (source) {
  cpSync(join(source, modelRel), modelDir, { recursive: true })
} else {
  run([process.execPath, "run", "scripts/prefetch-model.ts", join(resources, "models")])
}
for (const asset of ["config.json", "tokenizer_config.json", "tokenizer.json", join("onnx", "model_quantized.onnx")]) {
  const path = join(modelDir, asset)
  if (!existsSync(path) || statSync(path).size === 0) throw new Error(`Missing bundled model asset: ${path}`)
}

// 5. Tray helper.
const dotnet = process.env["DOTNET"] ?? "dotnet"
const selfContained = process.env["SIDE_WIN_SELF_CONTAINED"] === "1"
run([
  dotnet,
  "publish",
  join(repo, "apps", "side-win", "Side.Win.csproj"),
  "-c",
  "Release",
  "-r",
  "win-x64",
  "--self-contained",
  selfContained ? "true" : "false",
  "-o",
  dist,
])

// 6. Smoke check: the daemon binary starts and prints its command list.
run([join(resources, "side.exe"), "help"])
console.log(dist)
