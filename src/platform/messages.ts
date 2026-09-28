// User-facing hints that name OS-specific places. Keep one source so CLI, MCP and tests agree.
const WINDOWS = process.platform === "win32"

export const DAEMON_UNAVAILABLE_MESSAGE = WINDOWS
  ? "Side is not running. Start Side from the Start menu."
  : "Side is not running. Open Side.app."

export const DOCTOR_KEYCHAIN_HINT = WINDOWS
  ? "Sign in to Windows and restart Side if the helper handshake is unavailable"
  : "Unlock this Mac and restart Side.app if the helper handshake is unavailable"

export const DOCTOR_PERMISSIONS_HINT = WINDOWS
  ? "Allow Side in Windows Settings > Privacy & security; screen capture is required when OCR is enabled"
  : "Grant Accessibility and Input Monitoring in System Settings; Screen Recording is required when OCR is enabled"

export const DOCTOR_SQLITE_HINT = WINDOWS
  ? "Reinstall Side; its built-in SQLite must allow extension loading"
  : "Install or bundle libsqlite3.dylib with extension loading support"

export const DOCTOR_PROVIDER_HINT = WINDOWS
  ? "Start Side to inspect configured providers"
  : "Start Side.app to inspect configured providers"
