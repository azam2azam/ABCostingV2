import { Component, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { ActivatedRoute, Router } from '@angular/router';
import { Auth, toApiError } from '../core/auth';
import { Theme } from '../core/theme';
import { Icon } from '../shared/icon';

/**
 * Sign-in. Break-glass local accounts for this build (NFR-10); production signs in through the
 * organisation's identity provider with MFA. The persona list exists only for the seeded fixture.
 */
@Component({
  selector: 'v-login',
  imports: [FormsModule, Icon],
  template: `
    <div class="ambient" aria-hidden="true">
      <div class="blob b1"></div><div class="blob b2"></div><div class="blob b3"></div>
      <svg class="net" viewBox="0 0 1200 800" preserveAspectRatio="xMidYMid slice">
        <defs><linearGradient id="lg" x1="0" y1="0" x2="1" y2="1"><stop offset="0" stop-color="#0d9488"/><stop offset="1" stop-color="#e5262d"/></linearGradient></defs>
        <g stroke="url(#lg)" stroke-width="1" fill="none" opacity=".55">
          <path d="M0 400 Q300 200 600 380 T1200 350"/><path d="M0 520 Q250 620 550 490 T1200 530"/>
          <path d="M100 100 Q400 300 700 150 T1100 280"/><path d="M50 700 Q350 560 650 680 T1150 620"/>
        </g>
        <g fill="url(#lg)" opacity=".5">
          <circle cx="180" cy="320" r="4"/><circle cx="420" cy="282" r="3"/><circle cx="680" cy="360" r="5"/><circle cx="920" cy="300" r="3"/>
          <circle cx="300" cy="520" r="4"/><circle cx="760" cy="480" r="3"/><circle cx="550" cy="180" r="3"/><circle cx="1050" cy="450" r="4"/>
        </g>
      </svg>
    </div>

    <button class="theme" type="button" (click)="theme.toggle()" [attr.aria-label]="theme.mode() === 'dark' ? 'Switch to light theme' : 'Switch to dark theme'">
      <v-icon [name]="theme.mode() === 'dark' ? 'sun' : 'moon'" />
    </button>

    <main class="stage">
      <div class="card-login">
        <aside class="visual" aria-hidden="true">
          <svg class="helix" viewBox="0 0 160 420">
            @for (i of rungs; track i) {
              <line [attr.x1]="80 + 50 * sin(i)" [attr.y1]="20 + i * 19" [attr.x2]="80 - 50 * sin(i)" [attr.y2]="20 + i * 19"
                    [attr.opacity]="0.25 + 0.35 * abs(cos(i))" />
            }
            <path class="strand" [attr.d]="strand(1)" /><path class="strand two" [attr.d]="strand(-1)" />
          </svg>
          <svg class="ecg" viewBox="0 0 420 80"><path d="M0 46 H120 l10 -18 l10 34 l12 -50 l12 58 l10 -24 H260 l8 -10 l8 16 H420" /></svg>
          <svg class="waves" viewBox="0 0 420 220" preserveAspectRatio="none">
            <path d="M0 120 C 90 70 170 170 260 120 S 380 80 420 110 V220 H0Z" class="w1"/>
            <path d="M0 150 C 110 110 200 200 300 150 S 400 120 420 140 V220 H0Z" class="w2"/>
          </svg>
          <div class="pitch">
            <p class="eyebrow">Cost intelligence</p>
            <p class="claim">Every riyal traced from the ledger to the patient.</p>
            <ul>
              <li><v-icon name="check" [size]="15" /> Activity-based and time-driven costing</li>
              <li><v-icon name="check" [size]="15" /> Reconciled to the general ledger every run</li>
              <li><v-icon name="check" [size]="15" /> Four-eye approval and a full audit trail</li>
            </ul>
          </div>
        </aside>

        <section class="form-side">
          <div class="brand">
            <span class="mark" aria-hidden="true"><svg viewBox="0 0 24 24"><path d="M9 3h6v6h6v6h-6v6H9v-6H3V9h6z"/></svg></span>
            <span class="word">VERITA<small>Hospital cost intelligence</small></span>
          </div>
          <h1>Welcome back</h1>
          <p class="sub">Sign in to your costing workspace</p>

          <form (ngSubmit)="submit()" novalidate>
            <label class="lbl" for="email">Email</label>
            <div class="input">
              <v-icon name="mail" />
              <input id="email" name="email" type="email" autocomplete="username" placeholder="name@hospital.sa" [(ngModel)]="email" required />
            </div>

            <label class="lbl" for="password">Password</label>
            <div class="input">
              <v-icon name="lock" />
              <input id="password" name="password" [type]="show() ? 'text' : 'password'" autocomplete="current-password" placeholder="Enter your password" [(ngModel)]="password" required />
              <button type="button" class="eye" (click)="show.set(!show())" [attr.aria-label]="show() ? 'Hide password' : 'Show password'">
                <v-icon [name]="show() ? 'eye-off' : 'eye'" />
              </button>
            </div>

            <div class="row-between">
              <label class="remember"><input type="checkbox" name="remember" [(ngModel)]="remember" /> Keep me signed in</label>
              <span class="hint">Forgot it? Ask your platform administrator.</span>
            </div>

            @if (error()) { <div class="error" role="alert">{{ error() }}</div> }

            <button class="go" type="submit" [disabled]="busy()">
              {{ busy() ? 'Signing in…' : 'Sign in' }} @if (!busy()) { <v-icon name="arrow-right" /> }
            </button>
          </form>

          <div class="trust">
            <span><v-icon name="shield" [size]="15" /> Encrypted session</span>
            <span><v-icon name="lock" [size]="15" /> Role-based access</span>
          </div>

          <details class="personas">
            <summary>Demo accounts</summary>
            <p>Seeded roles for this build. Each sees a different slice of the platform.</p>
            <div class="chips">
              @for (p of personas; track p.email) {
                <button type="button" [class.on]="email === p.email" (click)="email = p.email">{{ p.label }}</button>
              }
            </div>
          </details>
        </section>
      </div>
      <p class="copy">© 2026 VERITA · Hospital activity-based costing</p>
    </main>
  `,
  styles: `
    :host { display: block; min-height: 100vh; position: relative; overflow: hidden; }
    .ambient { position: fixed; inset: 0; pointer-events: none; z-index: 0; }
    .blob { position: absolute; border-radius: 50%; filter: blur(80px); opacity: .45; animation: float 18s ease-in-out infinite; }
    .b1 { width: 520px; height: 520px; background: radial-gradient(circle, rgba(13,148,136,.4), transparent 70%); top: -140px; left: -110px; }
    .b2 { width: 480px; height: 480px; background: radial-gradient(circle, rgba(229,38,45,.22), transparent 70%); bottom: -100px; right: -80px; animation-delay: -6s; }
    .b3 { width: 380px; height: 380px; background: radial-gradient(circle, rgba(99,102,241,.2), transparent 70%); top: 38%; left: 52%; animation-delay: -12s; }
    @keyframes float { 0%, 100% { transform: translate(0, 0) scale(1); } 33% { transform: translate(30px, -25px) scale(1.05); } 66% { transform: translate(-20px, 15px) scale(.97); } }
    .net { position: absolute; inset: 0; width: 100%; height: 100%; opacity: .2; }

    .theme { position: fixed; top: calc(20px + env(safe-area-inset-top, 0px)); right: 20px; z-index: 5; width: 42px; height: 42px; border-radius: 12px;
             display: grid; place-items: center; cursor: pointer; color: var(--text); background: var(--surface); backdrop-filter: blur(12px);
             border: 1px solid var(--border); transition: color .2s, border-color .2s; }
    .theme:hover { color: var(--brand); border-color: var(--brand); }

    .stage { position: relative; z-index: 1; min-height: 100vh; display: grid; place-items: center; align-content: center; gap: 18px; padding: 32px 16px; }
    .card-login { width: min(980px, 100%); display: grid; grid-template-columns: 1fr 1.05fr; border-radius: 26px; overflow: hidden;
                  background: var(--surface); backdrop-filter: blur(24px); -webkit-backdrop-filter: blur(24px);
                  border: 1px solid rgba(255,255,255,.45); box-shadow: var(--shadow-lg); animation: rise .7s cubic-bezier(.16,1,.3,1) both; }
    :host-context([data-theme="dark"]) .card-login { border-color: rgba(255,255,255,.08); }
    @keyframes rise { from { opacity: 0; transform: translateY(20px) scale(.98); } to { opacity: 1; transform: none; } }

    .visual { position: relative; min-height: 560px; overflow: hidden;
              background: linear-gradient(160deg, rgba(13,148,136,.10), rgba(99,102,241,.08) 55%, rgba(13,148,136,.16)); }
    .helix { position: absolute; left: 6%; top: -2%; height: 78%; opacity: .85; }
    .helix line { stroke: #5fb7d4; stroke-width: 3; stroke-linecap: round; }
    .helix .strand { fill: none; stroke: #7cc7e0; stroke-width: 4; stroke-linecap: round; opacity: .75; }
    .helix .strand.two { stroke: #9bd5e8; opacity: .55; }
    .ecg { position: absolute; left: 0; right: 0; top: 52%; width: 100%; }
    .ecg path { fill: none; stroke: var(--accent); stroke-width: 2.2; stroke-linejoin: round; stroke-dasharray: 900; stroke-dashoffset: 900; animation: trace 3.2s ease-out .4s forwards; opacity: .8; }
    @keyframes trace { to { stroke-dashoffset: 0; } }
    .waves { position: absolute; left: 0; right: 0; bottom: 0; width: 100%; height: 42%; }
    .waves .w1 { fill: rgba(96, 165, 250, .28); }
    .waves .w2 { fill: rgba(45, 212, 191, .26); }
    .pitch { position: absolute; left: 32px; right: 32px; bottom: 30px; color: var(--text); }
    .eyebrow { font-size: 12px; font-weight: 700; letter-spacing: .14em; text-transform: uppercase; color: var(--accent-strong); margin: 0 0 6px; }
    .claim { font-size: 22px; font-weight: 700; letter-spacing: -.02em; line-height: 1.25; margin: 0 0 12px; max-width: 22ch; }
    .pitch ul { list-style: none; margin: 0; padding: 0; display: grid; gap: 6px; color: var(--text-2); font-size: 13.5px; }
    .pitch li { display: flex; gap: 8px; align-items: center; }
    .pitch li v-icon { color: var(--accent); }

    .form-side { padding: 44px 48px 34px; display: flex; flex-direction: column; }
    .brand { display: flex; align-items: center; gap: 12px; justify-content: center; margin-bottom: 26px; }
    .mark { width: 44px; height: 44px; border-radius: 13px; display: grid; place-items: center; background: linear-gradient(135deg, var(--brand), var(--brand-2));
            box-shadow: 0 10px 22px -8px rgba(229, 38, 45, .65); }
    .mark svg { width: 24px; height: 24px; fill: #fff; }
    .word { font-size: 26px; font-weight: 800; letter-spacing: .06em; line-height: 1; color: var(--text); }
    .word small { display: block; font-size: 10.5px; font-weight: 600; letter-spacing: .14em; text-transform: uppercase; color: var(--text-3); margin-top: 5px; }
    h1 { text-align: center; font-size: 30px; font-weight: 800; letter-spacing: -.03em; margin: 0; }
    .sub { text-align: center; color: var(--text-2); margin: 6px 0 26px; }
    form { display: grid; gap: 6px; }
    .lbl { font-size: 13px; font-weight: 600; color: var(--text-2); margin-top: 8px; }
    .input { position: relative; display: flex; align-items: center; }
    .input > v-icon { position: absolute; left: 16px; color: var(--text-3); pointer-events: none; transition: color .2s; }
    .input input { width: 100%; height: 50px; padding: 0 48px 0 46px; border-radius: 999px; border: 1.5px solid var(--border); background: var(--surface-solid); font-size: 14.5px; }
    .input input:focus { border-color: var(--accent); box-shadow: 0 0 0 4px var(--accent-soft); }
    .input:focus-within > v-icon { color: var(--accent); }
    .eye { position: absolute; right: 10px; width: 34px; height: 34px; border: 0; background: none; color: var(--text-3); cursor: pointer; border-radius: 50%; display: grid; place-items: center; }
    .eye:hover { color: var(--accent); }
    .row-between { display: flex; justify-content: space-between; align-items: center; gap: 10px; flex-wrap: wrap; margin: 12px 0 8px; font-size: 13px; }
    .remember { display: flex; align-items: center; gap: 8px; color: var(--text-2); cursor: pointer; }
    .remember input { width: 16px; height: 16px; accent-color: var(--accent); padding: 0; }
    .hint { color: var(--text-3); }
    .error { background: var(--bad-soft); color: var(--bad); padding: 10px 14px; border-radius: 12px; font-size: 13.5px; }
    .go { margin-top: 8px; height: 52px; border: 0; border-radius: 999px; cursor: pointer; color: #fff; font: 600 15.5px var(--font);
          display: flex; align-items: center; justify-content: center; gap: 8px;
          background: linear-gradient(135deg, #2bb3a6, #0d9488 60%, #0b7a71); box-shadow: 0 14px 28px -12px rgba(13,148,136,.8);
          transition: transform .2s, box-shadow .2s, filter .2s; }
    .go:hover { transform: translateY(-1px); box-shadow: 0 18px 32px -12px rgba(13,148,136,.85); filter: brightness(1.04); }
    .go:disabled { opacity: .7; cursor: wait; transform: none; }
    .trust { display: flex; justify-content: center; gap: 18px; flex-wrap: wrap; margin-top: 18px; color: var(--text-3); font-size: 12.5px; }
    .trust span { display: inline-flex; align-items: center; gap: 6px; }
    .trust v-icon { color: var(--accent); }
    .personas { margin-top: 18px; border-top: 1px solid var(--border); padding-top: 14px; font-size: 13px; color: var(--text-2); }
    .personas summary { font-weight: 600; text-align: center; list-style: none; color: var(--text-2); }
    .personas summary::-webkit-details-marker { display: none; }
    .personas summary::after { content: " ▾"; color: var(--text-3); }
    .personas p { margin: 10px 0; text-align: center; color: var(--text-3); }
    .chips { display: flex; flex-wrap: wrap; gap: 6px; justify-content: center; }
    .chips button { font: 500 12.5px var(--font); padding: 5px 11px; border-radius: 999px; border: 1px solid var(--border); background: var(--surface-solid); color: var(--text-2); cursor: pointer; }
    .chips button:hover, .chips button.on { border-color: var(--accent); color: var(--accent-strong); background: var(--accent-soft); }
    .copy { color: var(--text-3); font-size: 12.5px; margin: 0; }

    @media (max-width: 860px) {
      .card-login { grid-template-columns: 1fr; max-width: 480px; }
      .visual { display: none; }
      .form-side { padding: 34px 24px 26px; }
    }
  `,
})
export class Login {
  private auth = inject(Auth);
  private router = inject(Router);
  private route = inject(ActivatedRoute);
  theme = inject(Theme);
  email = 'cfo@verita.health';
  password = '';
  remember = true;
  show = signal(false);
  busy = signal(false);
  error = signal<string | null>(null);
  rungs = Array.from({ length: 21 }, (_, i) => i);
  personas = [
    { email: 'cfo@verita.health', label: 'CFO' }, { email: 'ceo@verita.health', label: 'CEO' },
    { email: 'costing.manager@verita.health', label: 'Costing manager' }, { email: 'analyst@verita.health', label: 'Costing analyst' },
    { email: 'controller@verita.health', label: 'Finance controller' }, { email: 'clinical.director@verita.health', label: 'Clinical director (Riyadh)' },
    { email: 'jeddah.director@verita.health', label: 'Clinical director (Jeddah)' }, { email: 'surgery.manager@verita.health', label: 'Department manager' },
    { email: 'rcm@verita.health', label: 'RCM manager' }, { email: 'supplychain@verita.health', label: 'Supply chain' },
    { email: 'auditor@verita.health', label: 'Internal auditor' }, { email: 'admin@verita.health', label: 'Platform admin' },
  ];

  sin(i: number): number { return Math.sin(i * 0.55); }
  cos(i: number): number { return Math.cos(i * 0.55); }
  abs(v: number): number { return Math.abs(v); }
  strand(dir: 1 | -1): string {
    return this.rungs.map((i, k) => `${k ? 'L' : 'M'}${(80 + dir * 50 * this.sin(i)).toFixed(1)} ${20 + i * 19}`).join(' ');
  }

  async submit(): Promise<void> {
    this.busy.set(true);
    this.error.set(null);
    try {
      await this.auth.login(this.email, this.password);
      await this.router.navigateByUrl(this.route.snapshot.queryParamMap.get('next') || '/dashboard');
    } catch (e) {
      this.error.set(toApiError(e).message);
    } finally {
      this.busy.set(false);
    }
  }
}

@Component({
  selector: 'v-forbidden',
  template: `<div class="card"><h1>Not available to your role</h1><p class="muted">Your role does not include this module. The navigation shows only what you can open.</p></div>`,
})
export class Forbidden {}
