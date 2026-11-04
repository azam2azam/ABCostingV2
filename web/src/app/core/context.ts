import { DestroyRef, Injectable, computed, inject, signal } from '@angular/core';
import { toSignal } from '@angular/core/rxjs-interop';
import { ActivatedRoute, CanActivateFn, NavigationEnd, Router } from '@angular/router';
import { filter, firstValueFrom, map } from 'rxjs';
import { Api, ContextDto, RunListItem } from './api';
import { Auth } from './auth';

const IN_FLIGHT = new Set(['VALIDATING', 'VALIDATED', 'CALCULATING', 'CALCULATED']);

/** List/header label: CALCULATED is the RECONCILE stage until the run reaches RECONCILED. */
export function runLiveStatus(r: { status: string; currentStep?: string | null }): string {
  if (r.currentStep === 'RECONCILE' || r.status === 'CALCULATED') return 'RECONCILING';
  return r.status;
}

export function runInFlight(status: string): boolean {
  return IN_FLIGHT.has(status);
}

export interface NavItem { path: string; label: string; permission: string; purpose: string; }
export interface NavGroup { label: string; items: NavItem[]; }

/**
 * The single declarations from /api/v1/meta (NFR-22): navigation, roles, the check register.
 * The route guard reads the same navigation-to-permission map the API enforces (NG-4).
 */
@Injectable({ providedIn: 'root' })
export class MetaService {
  private api = inject(Api);
  readonly meta = signal<Record<string, any> | null>(null);
  readonly navigation = computed(() => (this.meta()?.['navigation'] ?? []) as NavGroup[]);

  async load(): Promise<void> {
    if (!this.meta()) this.meta.set(await firstValueFrom(this.api.get<Record<string, any>>('meta')));
  }

  permissionFor(path: string): string | null {
    for (const g of this.navigation()) for (const i of g.items) if (path === i.path) return i.permission;
    return null;
  }
}

/** NG-4: module access checked once, centrally. The guard is a courtesy; the API is the control. */
export const moduleGuard: CanActivateFn = async (route) => {
  const meta = inject(MetaService);
  const auth = inject(Auth);
  const router = inject(Router);
  await meta.load();
  const module = route.routeConfig?.path?.split('/')[0] ?? '';
  const permission = meta.permissionFor(module);
  if (!permission || auth.can(permission)) return true;
  return router.createUrlTree(['/forbidden'], { queryParams: { module } });
};

/**
 * NG-3 / FR-11.13: hospital and period live in the URL (?hospital=&period=), not in a cookie
 * or a store. The API resolves those to a run (ARCH-14). Old ?run= bookmarks are rewritten.
 */
@Injectable({ providedIn: 'root' })
export class RunContext {
  private api = inject(Api);
  private router = inject(Router);
  private route = inject(ActivatedRoute);
  private poll: ReturnType<typeof setTimeout> | null = null;
  readonly context = signal<ContextDto | null>(null);

  constructor() {
    inject(DestroyRef).onDestroy(() => this.stopPoll());
  }

  private readonly url = toSignal(this.router.events.pipe(filter(e => e instanceof NavigationEnd), map(() => this.router.url)), { initialValue: this.router.url });
  private qp(name: string): string | null {
    this.url();
    const v = this.router.parseUrl(this.router.url).queryParams[name];
    const s = Array.isArray(v) ? v[0] : v;
    return s ? String(s) : null;
  }

  readonly hospitalParam = computed(() => this.qp('hospital'));
  readonly periodParam = computed(() => this.qp('period'));
  readonly versionParam = computed(() => this.qp('version'));
  /** Direct and Indirect costing are scoped by hospital and costing version, not by a run. */
  readonly configParams = computed<{ hospital: string | null; version: string | null }>(() => ({ hospital: this.hospitalParam(), version: this.versionParam() }));
  readonly hospitals = computed(() => this.context()?.hospitals ?? []);
  readonly runs = computed(() => this.context()?.runs ?? []);
  readonly periodsForHospital = computed(() => this.periodsOf(this.hospitalParam()));

  readonly run = computed<RunListItem | null>(() => {
    const h = this.hospitalParam(), p = this.periodParam();
    if (!h || !p) return null;
    return this.resolveRun(h, p);
  });

  /** Query params every scoped request and nav link should carry. */
  readonly scopeParams = computed<{ hospital?: string; period?: string }>(() => {
    const hospital = this.hospitalParam(), period = this.periodParam();
    if (!hospital) return {};
    return period ? { hospital, period } : { hospital };
  });

  /** DRGs are a group catalogue; the module only opens when this hospital has assigned ones. */
  readonly hasDrgs = computed(() => {
    const code = this.hospitalParam() ?? this.run()?.hospital;
    if (!code) return false;
    return this.hospitals().some(h => h.code === code && h.hasDrgs);
  });

  periodsOf(hospital: string | null): ContextDto['periods'] {
    if (!hospital) return [];
    return (this.context()?.periods ?? []).filter(p => p.hospital === hospital).reverse();
  }

  async load(force = false): Promise<void> {
    if (!this.context() || force) this.context.set(await firstValueFrom(this.api.get<ContextDto>('context')));
    this.schedulePoll();
  }

  private schedulePoll(): void {
    this.stopPoll();
    if (!this.runs().some(r => runInFlight(r.status))) return;
    this.poll = setTimeout(() => void this.load(true), 1500);
  }

  private stopPoll(): void {
    if (this.poll) { clearTimeout(this.poll); this.poll = null; }
  }

  /**
   * Default selection: the latest published run in scope; failing that, the latest run that
   * reconciled. The context bar always shows the status, so an unpublished run is never mistaken
   * for an approved one (FR-11.2).
   */
  defaultRun(): RunListItem | null {
    const runs = this.runs();
    return runs.find(r => r.status === 'PUBLISHED') ?? runs.find(r => r.status !== 'FAILED' && r.status !== 'DRAFT') ?? runs[0] ?? null;
  }

  defaultScope(): { hospital: string; period: string | null } | null {
    const run = this.defaultRun();
    if (run) return { hospital: run.hospital, period: run.period };
    const h = this.hospitals()[0];
    if (!h) return null;
    return { hospital: h.code, period: this.periodsOf(h.code)[0]?.code ?? null };
  }

  preferredPeriod(hospital: string): string | null {
    const fromRun = this.resolveRun(hospital, null) ?? this.runs().find(r => r.hospital === hospital);
    if (fromRun) return fromRun.period;
    return this.periodsOf(hospital)[0]?.code ?? null;
  }

  /**
   * Same preference as the API: published first, then most recently started, for a hospital
   * (and period when given).
   */
  resolveRun(hospital: string, period: string | null): RunListItem | null {
    const ofPair = this.runs().filter(r => r.hospital === hospital && (!period || r.period === period));
    if (!ofPair.length) return null;
    const published = ofPair.filter(r => r.status === 'PUBLISHED');
    const pool = published.length ? published : ofPair;
    return [...pool].sort((a, b) => {
      const ta = a.publishedAt || a.startedAt || a.finishedAt || '';
      const tb = b.publishedAt || b.startedAt || b.finishedAt || '';
      return tb.localeCompare(ta);
    })[0] ?? null;
  }

  /** Build hospital+period from the URL, an old ?run=, or the default in scope. */
  resolveScope(q: { hospital?: string | null; period?: string | null; run?: string | null }): { hospital: string; period: string | null } | null {
    const hospital = q.hospital || null;
    const period = q.period || null;
    const run = q.run || null;
    if (hospital && period && this.periodsOf(hospital).some(p => p.code === period)) return { hospital, period };
    if (run) {
      const row = this.runs().find(r => r.code === run || r.id === run);
      if (row) return { hospital: row.hospital, period: row.period };
    }
    if (hospital && this.hospitals().some(h => h.code === hospital)) return { hospital, period: this.preferredPeriod(hospital) };
    return this.defaultScope();
  }

  configQuery(): string {
    const q = new URLSearchParams();
    const { hospital, version } = this.configParams();
    if (hospital) q.set('hospital', hospital);
    if (version) q.set('version', version);
    return `?${q.toString()}`;
  }

  scopeQuery(): string {
    const s = this.scopeParams();
    if (!s.hospital || !s.period) return '';
    return `?hospital=${encodeURIComponent(s.hospital)}&period=${encodeURIComponent(s.period)}`;
  }

  selectScope(hospital: string, period: string | null): void {
    void this.router.navigate([], {
      relativeTo: this.route,
      queryParams: { hospital, period, run: null },
      queryParamsHandling: 'merge',
    });
  }
}

/** Ensures ?hospital=&period= is in the URL before a scoped module renders. */
export const runGuard: CanActivateFn = async (route) => {
  const ctx = inject(RunContext);
  const router = inject(Router);
  await ctx.load();
  const scope = ctx.resolveScope({
    hospital: route.queryParamMap.get('hospital'),
    period: route.queryParamMap.get('period'),
    run: route.queryParamMap.get('run'),
  });
  if (!scope) return true;
  if (route.queryParamMap.get('hospital') === scope.hospital
    && (route.queryParamMap.get('period') ?? null) === scope.period
    && !route.queryParamMap.get('run')) return true;
  const tree = router.parseUrl(router.getCurrentNavigation()?.extractedUrl.toString() ?? '/');
  const { run: _ignored, ...rest } = tree.queryParams;
  const { period: _stale, ...others } = rest;
  tree.queryParams = scope.period ? { ...others, hospital: scope.hospital, period: scope.period } : { ...others, hospital: scope.hospital };
  return tree;
};
