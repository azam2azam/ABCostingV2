import { Component, computed, inject, signal } from '@angular/core';
import { Router, RouterLink, RouterLinkActive, RouterOutlet } from '@angular/router';
import { Auth } from '../core/auth';
import { MetaService, RunContext } from '../core/context';
import { Chip } from '../shared/ui';

/**
 * The shell: navigation grouped by what a person is doing (FR-11.4), filtered to the modules the
 * role may open, and a persistent context bar naming the hospital, period, costing version and
 * run status (FR-11.2) — a number whose scope is off-screen is a number waiting to be misquoted.
 */
@Component({
  selector: 'v-shell',
  imports: [RouterOutlet, RouterLink, RouterLinkActive, Chip],
  template: `
    <a class="skip" href="#main">Skip to content</a>
    <div class="layout" [class.open]="menuOpen()">
      <nav class="side" aria-label="Modules">
        <div class="brand"><span class="logo" aria-hidden="true">V</span> VERITA <span class="faint">ABC</span></div>
        @for (g of groups(); track g.label) {
          <div class="group">{{ g.label }}</div>
          @for (i of g.items; track i.path) {
            <a [routerLink]="['/', i.path]" routerLinkActive="active" [queryParams]="runQuery()" [title]="i.purpose" (click)="menuOpen.set(false)">{{ i.label }}</a>
          }
        }
      </nav>
      <div class="main-col">
        <header class="ctx" aria-label="Scope">
          <button class="btn menu" type="button" (click)="menuOpen.set(!menuOpen())" [attr.aria-expanded]="menuOpen()">Menu</button>
          @if (run(); as r) {
            <div class="scope">
              <span><span class="faint">Hospital</span> <strong>{{ r.hospitalName }}</strong></span>
              <span><span class="faint">Period</span> <strong>{{ r.period }}</strong></span>
              <span><span class="faint">Version</span> <strong>{{ r.costingVersion }}</strong></span>
              <span><span class="faint">Run</span> <strong class="mono">{{ r.code }}</strong> <v-chip [value]="r.status" /></span>
              @if (r.forced) { <v-chip value="FORCED" label="Forced — cannot publish" kind="bad" /> }
            </div>
          } @else { <div class="scope faint">No run selected</div> }
          <label class="pick">
            <span class="sr-only">Select run</span>
            <select (change)="pick($any($event.target).value)" aria-label="Select run">
              @for (r of ctx.runs(); track r.id) {
                <option [value]="r.code" [selected]="r.code === run()?.code">{{ r.code }} · {{ r.status }}</option>
              }
            </select>
          </label>
          <span class="who"><strong>{{ auth.user()?.name }}</strong> <span class="faint">{{ auth.user()?.roleName }}</span></span>
          <button class="btn" type="button" (click)="auth.logout()">Sign out</button>
        </header>
        <main id="main" tabindex="-1"><router-outlet /></main>
      </div>
    </div>
  `,
  styles: `
    .skip { position: absolute; left: -999px; } .skip:focus { left: 8px; top: 8px; z-index: 10; background: var(--surface); padding: 6px; }
    .layout { display: grid; grid-template-columns: 220px 1fr; min-height: 100vh; }
    .side { background: var(--surface); border-right: 1px solid var(--border); padding: 12px 8px; position: sticky; top: 0; height: 100vh; overflow-y: auto; }
    .brand { font-weight: 700; letter-spacing: .5px; padding: 4px 10px 12px; display: flex; gap: 8px; align-items: center; }
    .logo { display: inline-grid; place-items: center; width: 24px; height: 24px; border-radius: 5px; background: var(--accent); color: #fff; font-size: 13px; }
    .group { font-size: 12px; text-transform: uppercase; letter-spacing: .6px; color: var(--text-3); padding: 12px 10px 4px; }
    .side a { display: block; padding: 6px 10px; border-radius: 5px; color: var(--text); }
    .side a:hover { background: var(--surface-2); text-decoration: none; }
    .side a.active { background: var(--accent-soft); color: var(--accent); font-weight: 600; }
    .main-col { min-width: 0; }
    .ctx { display: flex; gap: 14px; align-items: center; flex-wrap: wrap; padding: 10px 20px; background: var(--surface); border-bottom: 1px solid var(--border); position: sticky; top: 0; z-index: 5; }
    .scope { display: flex; gap: 16px; flex-wrap: wrap; align-items: center; flex: 1; min-width: 0; }
    .pick select { max-width: 280px; }
    .who { white-space: nowrap; }
    main { padding: 20px; max-width: 1500px; }
    main:focus { outline: none; }
    .menu { display: none; }
    .sr-only { position: absolute; width: 1px; height: 1px; overflow: hidden; clip: rect(0 0 0 0); }
    @media (max-width: 900px) {
      .layout { grid-template-columns: 1fr; }
      .side { display: none; position: fixed; inset: 0 30% 0 0; z-index: 20; height: 100vh; }
      .layout.open .side { display: block; box-shadow: 0 0 40px rgba(0,0,0,.3); }
      .menu { display: inline-block; }
      main { padding: 16px; }
      .ctx { padding: 10px 16px; }
    }
  `,
})
export class Shell {
  auth = inject(Auth);
  ctx = inject(RunContext);
  meta = inject(MetaService);
  private router = inject(Router);
  menuOpen = signal(false);
  run = this.ctx.run;
  runQuery = computed(() => (this.ctx.runCode() ? { run: this.ctx.runCode() } : {}));
  groups = computed(() => this.meta.navigation()
    .map(g => ({ ...g, items: g.items.filter(i => this.auth.can(i.permission)) }))
    .filter(g => g.items.length > 0));

  constructor() {
    void this.meta.load();
    void this.ctx.load();
  }

  pick(code: string): void {
    void this.router.navigate([], { queryParams: { run: code }, queryParamsHandling: 'merge' });
  }
}
