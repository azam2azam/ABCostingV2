import { Injectable, computed, inject, signal } from '@angular/core';
import { toSignal } from '@angular/core/rxjs-interop';
import { ActivatedRoute, CanActivateFn, NavigationEnd, Router } from '@angular/router';
import { filter, firstValueFrom, map } from 'rxjs';
import { Api, ContextDto, RunListItem } from './api';
import { Auth } from './auth';

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
 * NG-3 / FR-11.13: the selected run lives in the URL (?run=), not in a cookie or a store, so
 * every screen can be linked or bookmarked with the exact run its numbers came from.
 */
@Injectable({ providedIn: 'root' })
export class RunContext {
  private api = inject(Api);
  private router = inject(Router);
  private route = inject(ActivatedRoute);
  readonly context = signal<ContextDto | null>(null);

  private readonly url = toSignal(this.router.events.pipe(filter(e => e instanceof NavigationEnd), map(() => this.router.url)), { initialValue: this.router.url });
  readonly runParam = computed(() => {
    this.url();
    return this.router.parseUrl(this.router.url).queryParams['run'] as string | undefined ?? null;
  });

  readonly runs = computed(() => this.context()?.runs ?? []);
  readonly run = computed<RunListItem | null>(() => {
    const param = this.runParam();
    const runs = this.runs();
    return runs.find(r => r.id === param || r.code === param) ?? null;
  });
  readonly runCode = computed(() => this.run()?.code ?? this.runParam());

  async load(force = false): Promise<void> {
    if (!this.context() || force) this.context.set(await firstValueFrom(this.api.get<ContextDto>('context')));
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

  select(code: string): void {
    void this.router.navigate([], { relativeTo: this.route, queryParams: { run: code }, queryParamsHandling: 'merge' });
  }
}

/** Ensures a run is in the URL before a run-scoped module renders. */
export const runGuard: CanActivateFn = async (route) => {
  const ctx = inject(RunContext);
  const router = inject(Router);
  await ctx.load();
  if (route.queryParamMap.get('run')) return true;
  const def = ctx.defaultRun();
  if (!def) return true;
  const tree = router.parseUrl(router.getCurrentNavigation()?.extractedUrl.toString() ?? '/');
  tree.queryParams = { ...tree.queryParams, run: def.code };
  return tree;
};
