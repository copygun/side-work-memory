# Side for Windows

Windows port of Side. The daemon (`src/`, TypeScript on Bun) is shared with macOS. The native helper
is `apps/side-win` (C# / .NET 10, WinForms tray + WebView2) instead of `apps/side-mac` (Swift).

Same scope as macOS: activity capture, OCR, 10-minute / 6-hour summaries, settings screen, MCP
server, tray menu.

## Requirements

| Item | Version | Notes |
| --- | --- | --- |
| Windows | 10 2004 (19041) or later, x64 | Windows 11 recommended (screen capture without a yellow border) |
| Bun | 1.3+ | build only |
| .NET SDK | 10.0 | build only. Runtime: .NET 10 Desktop Runtime, unless built self-contained |
| WebView2 Runtime | evergreen | preinstalled on Windows 11 |
| OCR languages | Korean, English | Settings > Time & language > Language: add Korean and English if OCR says a language is missing |

## Build

```powershell
bun install
bun test                 # daemon tests
bun run build:win        # -> dist\Side\
```

`build:win` runs `scripts/build-windows.ts`:

1. `scripts/build-daemon-win.ts` compiles `src/cli.ts` into `resources\side.exe` and copies
   `vec0.dll`, `onnxruntime.dll`, `DirectML.dll`, `onnxruntime_binding.node` into `resources\lib`.
2. Builds the settings web UI into `resources\web`.
3. Bundles the embedding model into `resources\models` (set `SIDE_MODEL_CACHE_SOURCE` to reuse a
   downloaded copy).
4. `dotnet publish apps/side-win` into `dist\Side` (`SIDE_WIN_SELF_CONTAINED=1` for a build that
   does not need the .NET runtime installed).

## Install layout

```
<install dir>\Side.exe                 tray helper (start this one)
<install dir>\resources\side.exe       daemon / CLI / MCP server
<install dir>\resources\lib\           vec0.dll, ONNX Runtime
<install dir>\resources\web\           settings UI
<install dir>\resources\models\        multilingual MiniLM
```

Suggested install dir: `%LOCALAPPDATA%\Programs\Side`. `Side.exe` and `side.exe` cannot share a
folder (Windows file names are case-insensitive), which is why the daemon lives in `resources`.

Data: `%APPDATA%\Side` (override with `SIDE_DATA_DIR`). The local API socket is
`%APPDATA%\Side\run\daemon.sock` (AF_UNIX, supported since Windows 10 1803).

## Connect an agent (MCP)

```powershell
claude mcp add --scope user side -- "%LOCALAPPDATA%\Programs\Side\resources\side.exe" mcp
codex mcp add side -- "%LOCALAPPDATA%\Programs\Side\resources\side.exe" mcp
```

The settings screen shows the same commands with the real path filled in.

## How it differs from macOS

| Area | macOS | Windows |
| --- | --- | --- |
| Accessibility capture | AXObserver | UI Automation |
| Input metadata | CGEventTap (listen-only) | WH_KEYBOARD_LL / WH_MOUSE_LL (listen-only) |
| Secure input | Secure Event Input | focused element `IsPassword` |
| OCR | ScreenCaptureKit + Vision | Windows.Graphics.Capture + Windows.Media.Ocr |
| Browser URL | AppleScript | UI Automation address bar |
| Master key / API keys | Keychain | Credential Manager (generic credential, DPAPI-wrapped, not roaming) |
| Permissions | TCC prompts | none needed; screen capture follows the Windows privacy toggle |
| Login item | SMAppService | `HKCU\...\Run` |
| App identity (`bundleId`) | bundle ID | lowercase executable name, e.g. `chrome.exe` |
| Graceful daemon stop | SIGTERM | helper closes the daemon's stdin |
| File privacy | 0600 / 0700 modes | per-user profile ACL (`%APPDATA%`) |

Observation `source` stays `mac_ax` on the wire: the ledger treats it as "native OS accessibility".

## Claude Code / Codex summaries

The daemon finds `claude` / `codex` on `PATH`, in `%USERPROFILE%\.local\bin`, or in `%APPDATA%\npm`.
npm `.cmd` shims are unwrapped to `node <script>` so no `cmd.exe` quoting is involved. Codex login
isolation copies `auth.json` into a private temp home (Windows has no unprivileged symlinks); a
changed login fails closed and is written back, same contract as the macOS symlink check.

## Known limits

- Firefox history search is not supported (same as macOS). Chrome, Edge, Brave, Whale and Aside are.
- Symlink-specific tests are skipped on Windows (no unprivileged symlinks).
- POSIX mode assertions in tests are skipped on Windows.
