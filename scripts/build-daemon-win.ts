// Windows counterpart of build-daemon.ts: compiles src/cli.ts into a single side.exe.
// The ONNX Runtime addon is NOT embedded: Bun would extract it to a temp folder where Windows
// could not find onnxruntime.dll next to it. Instead the addon and its DLLs ship in <exe dir>\lib
// and are loaded by absolute path (libuv uses LOAD_WITH_ALTERED_SEARCH_PATH, so sibling DLLs
// resolve from that folder).
import { copyFileSync, mkdirSync, readFileSync } from "node:fs"
import { dirname, join, resolve } from "node:path"

const [entryArg, outputArg] = process.argv.slice(2)
if (!entryArg || !outputArg) throw new TypeError("Expected entrypoint and output path")
if (process.platform !== "win32") throw new TypeError("Windows builds require Windows")

const entrypoint = resolve(entryArg)
const output = resolve(outputArg)
const libDir = join(dirname(output), "lib")
const nativeDir = join(
  dirname(import.meta.dir),
  "node_modules",
  "onnxruntime-node",
  "bin",
  "napi-v6",
  "win32",
  process.arch,
)
mkdirSync(libDir, { recursive: true })
for (const name of ["onnxruntime_binding.node", "onnxruntime.dll", "DirectML.dll"]) {
  try {
    copyFileSync(join(nativeDir, name), join(libDir, name))
  } catch (error) {
    // DirectML is optional (only used by the DirectML execution provider).
    if (name !== "DirectML.dll") throw error
  }
}

// sqlite-vec: openIndexDb() prefers <exe dir>\lib\vec0.dll over node_modules lookup.
copyFileSync(
  join(dirname(import.meta.dir), "node_modules", `sqlite-vec-windows-${process.arch}`, "vec0.dll"),
  join(libDir, "vec0.dll"),
)

const runtimeAddon =
  'require(require("node:path").join(require("node:path").dirname(process.execPath), "lib", "onnxruntime_binding.node"))'

const result = await Bun.build({
  entrypoints: [entrypoint],
  compile: { outfile: output },
  plugins: [
    {
      name: "side-text-only-native",
      setup(build) {
        build.onResolve({ filter: /^sharp$/ }, () => ({ path: "sharp", namespace: "side-text" }))
        build.onLoad({ filter: /^sharp$/, namespace: "side-text" }, () => ({
          contents:
            'export default function sharp() { throw new Error("Image processing is unavailable") }',
          loader: "js",
        }))
        build.onLoad(
          { filter: /@huggingface[\\/]transformers[\\/]dist[\\/]transformers\.node\.mjs$/ },
          (args) => {
            const source = readFileSync(args.path, "utf8")
            const original = 'requireFromHere("onnxruntime-node")'
            if (!source.includes(original))
              throw new Error("Transformers ONNX import layout changed")
            return {
              contents: source.replace(original, 'require("onnxruntime-node")'),
              loader: "js",
            }
          },
        )
        build.onLoad({ filter: /onnxruntime-node[\\/]dist[\\/]binding\.js$/ }, (args) => {
          const source = readFileSync(args.path, "utf8")
          const original = `require(\`../bin/napi-v6/\${process.platform}/\${process.arch}/onnxruntime_binding.node\`)`
          if (!source.includes(original)) throw new Error("ONNX native binding layout changed")
          return { contents: source.replace(original, runtimeAddon), loader: "js" }
        })
      },
    },
  ],
})
if (!result.success) throw new Error(result.logs.map((log) => log.message).join("\n"))
console.log(output)
