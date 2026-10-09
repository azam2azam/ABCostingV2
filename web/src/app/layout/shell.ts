import { Component, computed, inject, signal } from '@angular/core';
import { Router, RouterLink, RouterLinkActive, RouterOutlet } from '@angular/router';
import { Auth } from '../core/auth';
import { MetaService, RunContext } from '../core/context';
import { Theme } from '../core/theme';
import { Icon } from '../shared/icon';
import { Chip } from '../shared/ui';

/**
 * The shell: navigation grouped by what a person is doing (FR-11.4), filtered to the modules the
 * role may open, and a persistent context bar naming the hospital, period, costing version and
 * run status (FR-11.2).
 */
@Component({
  selector: 'v-shell',
  imports: [RouterOutlet, RouterLink, RouterLinkActive, Chip, Icon],
  template: `
    <a class="skip" href="#main">Skip to content</a>
    <div class="layout" [class.open]="menuOpen()">
      <nav class="side" aria-label="Modules">
        <a class="brand" routerLink="/dashboard" [queryParams]="runQuery()">
          <span class="mark" aria-hidden="true"><svg viewBox="0 0 24 24"><path d="M9 3h6v6h6v6h-6v6H9v-6H3V9h6z"/></svg></span>
          <span class="word">VERITA<small>Cost intelligence</small></span>
        </a>
        <div class="nav-scroll">
          @for (g of groups(); track g.label) {
            <div class="group">{{ g.label }}</div>
            @for (i of g.items; track i.path) {
              <a [routerLink]="['/', i.path]" routerLinkActive="active" [queryParams]="runQuery()" [title]="i.purpose" (click)="menuOpen.set(false)">
                <v-icon [name]="i.path" />
                <span>{{ i.label }}</span>
              </a>
            }
          }
        </div>
        <div class="me">
          <span class="avatar" aria-hidden="true">{{ initials() }}</span>
          <span class="who"><strong>{{ auth.user()?.name }}</strong><small>{{ auth.user()?.roleName }}</small></span>
          <button class="icon-btn" type="button" (click)="auth.logout()" aria-label="Sign out" title="Sign out"><v-icon name="logout" /></button>
        </div>
      </nav>
      <div class="scrim" (click)="menuOpen.set(false)" aria-hidden="true"></div>

      <div class="main-col">
        <header class="ctx" aria-label="Scope">
          <button class="icon-btn menu" type="button" (click)="menuOpen.set(!menuOpen())" [attr.aria-expanded]="menuOpen()" aria-label="Menu"><v-icon name="menu" /></button>
          @if (run(); as r) {
            <div class="scope">
              <span class="pill"><v-icon name="hospital" [size]="15" /> {{ r.hospitalName }}</span>
              <span class="pill"><v-icon name="calendar" [size]="15" /> {{ r.period }}</span>
              <span class="pill"><v-icon name="layers" [size]="15" /> {{ r.costingVersion }}</span>
              <v-chip [value]="r.status" />
              @if (r.forced) { <v-chip value="FORCED" label="Forced — cannot publish" kind="bad" /> }
            </div>
          } @else { <div class="scope faint">No run selected</div> }
          <label class="pick">
            <span class="sr-only">Run</span>
            <select (change)="pick($any($event.target).value)" aria-label="Select run">
              @for (r of ctx.runs(); track r.id) {
                <option [value]="r.code" [selected]="r.code === run()?.code">{{ r.code }} · {{ r.status }}</option>
              }
            </select>
          </label>
          <button class="icon-btn" type="button" (click)="theme.toggle()" [attr.aria-label]="theme.mode() === 'dark' ? 'Switch to light theme' : 'Switch to dark theme'">
            <v-icon [name]="theme.mode() === 'dark' ? 'sun' : 'moon'" />
          </button>
        </header>
        <main id="main" tabindex="-1"><router-outlet /></main>
      </div>
    </div>
  `,
  styles: `
    .skip { position: absolute; left: -999px; } .skip:focus { left: 8px; top: 8px; z-index: 30; background: var(--surface-solid); padding: 6px 10px; border-radius: 8px; }
    .layout { display: grid; grid-template-columns: 252px 1fr; min-height: 100vh; }
    .side { position: sticky; top: 0; height: 100vh; display: flex; flex-direction: column; padding: 16px 12px;
            background: var(--surface); backdrop-filter: blur(18px); -webkit-backdrop-filter: blur(18px); border-right: 1px solid var(--border); z-index: 20; }
    .brand { display: flex; align-items: center; gap: 10px; padding: 4px 8px 16px; color: var(--text); text-decoration: none; }
    .brand:hover { text-decoration: none; }
    .mark { width: 36px; height: 36px; border-radius: 11px; display: grid; place-items: center; background: linear-gradient(135deg, var(--brand), var(--brand-2));
            box-shadow: 0 8px 18px -8px rgba(229,38,45,.65); flex: none; }
    .mark svg { width: 20px; height: 20px; fill: #fff; }
    .word { font-size: 18px; font-weight: 800; letter-spacing: .06em; line-height: 1; }
    .word small { display: block; font-size: 10px; font-weight: 600; letter-spacing: .14em; text-transform: uppercase; color: var(--text-3); margin-top: 4px; }
    .nav-scroll { flex: 1; overflow-y: auto; margin: 0 -4px; padding: 0 4px; }
    .group { font-size: 11px; font-weight: 700; text-transform: uppercase; letter-spacing: .1em; color: var(--text-3); padding: 14px 10px 6px; }
    .nav-scroll a { display: flex; align-items: center; gap: 10px; padding: 7px 10px; margin: 1px 0; border-radius: 10px; color: var(--text-2); font-weight: 500; text-decoration: none;
                    transition: background .15s, color .15s; }
    .nav-scroll a v-icon { color: var(--text-3); transition: color .15s; }
    .nav-scroll a:hover { background: var(--surface-2); color: var(--text); }
    .nav-scroll a:hover v-icon { color: var(--accent); }
    .nav-scroll a.active { background: var(--accent-soft); color: var(--accent-strong); font-weight: 600; }
    .nav-scroll a.active v-icon { color: var(--accent); }
    .me { display: flex; align-items: center; gap: 10px; padding: 12px 8px 4px; margin-top: 8px; border-top: 1px solid var(--border); }
    .avatar { width: 34px; height: 34px; border-radius: 50%; display: grid; place-items: center; flex: none; font-weight: 700; font-size: 13px; color: #fff;
              background: linear-gradient(135deg, var(--accent), #6366f1); }
    .who { display: flex; flex-direction: column; min-width: 0; flex: 1; line-height: 1.25; }
    .who strong { font-size: 13.5px; white-space: nowrap; overflow: hidden; text-overflow: ellipsis; }
    .who small { color: var(--text-3); font-size: 12px; white-space: nowrap; overflow: hidden; text-overflow: ellipsis; }
    .icon-btn { width: 36px; height: 36px; flex: none; display: grid; place-items: center; border-radius: 10px; border: 1px solid var(--border); background: var(--surface-solid);
                color: var(--text-2); cursor: pointer; transition: color .15s, border-color .15s; }
    .icon-btn:hover { color: var(--accent); border-color: var(--accent); }
    .main-col { min-width: 0; }
    .ctx { position: sticky; top: env(safe-area-inset-top, 0px); z-index: 10; display: flex; gap: 12px; align-items: center; flex-wrap: wrap; padding: 12px 28px;
           background: var(--surface); backdrop-filter: blur(18px); -webkit-backdrop-filter: blur(18px); border-bottom: 1px solid var(--border); }
    .scope { display: flex; gap: 8px; flex-wrap: wrap; align-items: center; flex: 1; min-width: 0; }
    .pill { display: inline-flex; align-items: center; gap: 6px; padding: 5px 11px; border-radius: 999px; background: var(--surface-2); border: 1px solid var(--border);
            font-size: 13px; font-weight: 500; color: var(--text); white-space: nowrap; }
    .pill v-icon { color: var(--accent); }
    .pick select { max-width: 290px; border-radius: 999px; padding: 6px 14px; font-size: 13px; }
    main { padding: 26px 28px 40px; max-width: 1560px; }
    main:focus { outline: none; }
    .menu, .scrim { display: none; }
    .sr-only { position: absolute; width: 1px; height: 1px; overflow: hidden; clip: rect(0 0 0 0); }
    @media (max-width: 960px) {
      .layout { grid-template-columns: 1fr; }
      .side { position: fixed; inset: 0 auto 0 0; width: min(280px, 82vw); transform: translateX(-105%); transition: transform .25s ease; box-shadow: var(--shadow-lg); }
      .layout.open .side { transform: none; }
      .layout.open .scrim { display: block; position: fixed; inset: 0; background: rgba(15,23,42,.35); z-index: 15; }
      .menu { display: grid; }
      main { padding: 18px 16px 32px; }
      .ctx { padding: 10px 16px; }
      .pick select { max-width: 200px; }
    }
  `,
})
export class Shell {
  auth = inject(Auth);
  ctx = inject(RunContext);
  meta = inject(MetaService);
  theme = inject(Theme);
  private router = inject(Router);
  menuOpen = signal(false);
  run = this.ctx.run;
  runQuery = computed(() => (this.ctx.runCode() ? { run: this.ctx.runCode() } : {}));
  initials = computed(() => (this.auth.user()?.name ?? '?').split(' ').filter(Boolean).slice(0, 2).map(p => p[0]).join('').toUpperCase());
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
