import { httpResource } from '@angular/common/http';
import { computed, inject, Signal } from '@angular/core';
import { Envelope, Params } from './api';
import { apiV1 } from './api-url';
import { RunContext } from './context';

/**
 * A resource's value() throws once its request has failed, and a template reads it even inside a
 * closed v-state, which would stop the page rendering before the error shows. Read as empty instead.
 */
function readable<R extends { value: Signal<unknown>; error: Signal<unknown> }>(res: R): R {
  const value = res.value;
  (res as { value: Signal<unknown> }).value = computed(() => (res.error() ? undefined : value()));
  return res;
}

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
  return readable(httpResource<Envelope<T>>(() => {
    const scope = ctx.scopeParams();
    const p = path();
    if (!scope.hospital || !scope.period || !p) return undefined;
    return { url: `${apiV1}/${p}`, params: clean({ ...(extra?.() ?? {}), ...scope }) };
  }));
}

/** A catalogue read: needs only the hospital. The period, when known, adds a run's calculated figures. */
export function catalogueResource<T>(path: () => string | null | undefined, extra?: () => Params) {
  const ctx = inject(RunContext);
  return readable(httpResource<Envelope<T>>(() => {
    const hospital = ctx.hospitalParam();
    const p = path();
    if (!hospital || !p) return undefined;
    return { url: `${apiV1}/${p}`, params: clean({ ...(extra?.() ?? {}), hospital, period: ctx.periodParam() }) };
  }));
}

/** A read that does not depend on a run (governance, audit, costing models). */
export function apiResource<T>(path: () => string | null | undefined, extra?: () => Params) {
  return readable(httpResource<T>(() => {
    const p = path();
    if (!p) return undefined;
    return { url: `${apiV1}/${p}`, params: clean(extra?.() ?? {}) };
  }));
}
