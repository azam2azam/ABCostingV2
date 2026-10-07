import { httpResource } from '@angular/common/http';
import { inject } from '@angular/core';
import { Envelope, Params } from './api';
import { RunContext } from './context';

function clean(p: Params): Record<string, string> {
  const out: Record<string, string> = {};
  for (const [k, v] of Object.entries(p)) if (v !== null && v !== undefined && v !== '') out[k] = String(v);
  return out;
}

/**
 * A run-scoped read that re-fetches whenever the run in the URL changes. Signals hold component
 * state; HTTP stays observable underneath (NG-2).
 */
export function runResource<T>(path: () => string | null | undefined, extra?: () => Params) {
  const ctx = inject(RunContext);
  return httpResource<Envelope<T>>(() => {
    const run = ctx.runParam();
    const p = path();
    if (!run || !p) return undefined;
    return { url: `/api/v1/${p}`, params: clean({ ...(extra?.() ?? {}), run }) };
  });
}

/** A read that does not depend on a run (governance, audit, costing models). */
export function apiResource<T>(path: () => string | null | undefined, extra?: () => Params) {
  return httpResource<T>(() => {
    const p = path();
    if (!p) return undefined;
    return { url: `/api/v1/${p}`, params: clean(extra?.() ?? {}) };
  });
}
