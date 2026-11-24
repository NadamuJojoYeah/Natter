import { useCallback, useEffect, useRef, useState } from "react";
import { describeError } from "./api";

export type AsyncPhase = "loading" | "ready" | "error";

export interface AsyncResult<T> {
  phase: AsyncPhase;
  data: T | null;
  error: { code: string; message: string } | null;
  reload: () => void;
}

/**
 * Minimal data hook: every console region gets the same three-state contract (loading / error /
 * ready) so the UI never has to guess what an empty render means.
 */
export function useAsync<T>(load: (signal: AbortSignal) => Promise<T>, deps: unknown[] = []): AsyncResult<T> {
  const [phase, setPhase] = useState<AsyncPhase>("loading");
  const [data, setData] = useState<T | null>(null);
  const [error, setError] = useState<{ code: string; message: string } | null>(null);
  const generation = useRef(0);

  // The loader identity is intentionally not part of the dependency list: callers pass an inline
  // closure and drive re-fetching through `deps` (same contract as useEffect).
  // eslint-disable-next-line react-hooks/exhaustive-deps
  const run = useCallback(load, deps);

  const reload = useCallback(() => {
    const current = ++generation.current;
    const controller = new AbortController();
    setPhase("loading");
    setError(null);

    run(controller.signal)
      .then((value) => {
        if (current !== generation.current) {
          return;
        }
        setData(value);
        setPhase("ready");
      })
      .catch((cause: unknown) => {
        if (current !== generation.current || controller.signal.aborted) {
          return;
        }
        setError(describeError(cause));
        setPhase("error");
      });

    return () => controller.abort();
  }, [run]);

  useEffect(() => reload(), [reload]);

  return { phase, data, error, reload };
}
