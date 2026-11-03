import { httpResource } from '@angular/common/http';
import { inject } from '@angular/core';
import { Envelope, Params } from './api';
import { apiV1 } from './api-url';
import { RunContext } from './context';

function clean(p: Params): Record<string, string> {
  const out: Record<string, string> = {};
  for (const [k, v] of Object.entries(p)) if (v !== null && v !== undefined && v !== '') out[k] = String(v);
  return out;
}

/**
 * A scoped read that re-fetches whenever hospital or period in the URL changes. Signals hold
 * component state; HTTP stays observable underneath (NG-2).
 */
export function runResource<T>(path: () => string | null | undefined, extra?: () => Params) {
  const ctx = inject(RunContext);
  return httpResource<Envelope<T>>(() => {
    const scope = ctx.scopeParams();
    const p = path();
    if (!scope.hospital || !scope.period || !p) return undefined;
    return { url: `${apiV1}/${p}`, params: clean({ ...(extra?.() ?? {}), ...scope }) };
  });
}

/** A catalogue read: needs only the hospital. The period, when known, adds a run's calculated figures. */
export function catalogueResource<T>(path: () => string | null | undefined, extra?: () => Params) {
  const ctx = inject(RunContext);
  return httpResource<Envelope<T>>(() => {
    const hospital = ctx.hospitalParam();
    const p = path();
    if (!hospital || !p) return undefined;
    return { url: `${apiV1}/${p}`, params: clean({ ...(extra?.() ?? {}), hospital, period: ctx.periodParam() }) };
  });
}

/** A read that does not depend on a run (governance, audit, costing models). */
export function apiResource<T>(path: () => string | null | undefined, extra?: () => Params) {
  return httpResource<T>(() => {
    const p = path();
    if (!p) return undefined;
    return { url: `${apiV1}/${p}`, params: clean(extra?.() ?? {}) };
  });
}
