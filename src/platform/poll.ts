/**
 * Repeating poll for in-flight child processes.
 * Bun on Windows was observed never to fire setTimeout/setInterval timers armed right after a
 * previous child process closed (in-flight consent revocation never triggered), while Bun.sleep
 * kept working. The loop therefore awaits Bun.sleep when available and falls back to setTimeout.
 * Returns a stop function.
 */
export function startPoll(tick: () => void, intervalMs: number): () => void {
  let stopped = false
  const sleep = (ms: number): Promise<void> =>
    typeof Bun !== "undefined" ? Bun.sleep(ms) : new Promise((resolve) => setTimeout(resolve, ms))
  void (async () => {
    while (!stopped) {
      await sleep(intervalMs)
      if (stopped) return
      tick()
    }
  })()
  return () => {
    stopped = true
  }
}