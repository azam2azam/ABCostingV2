import { Component, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { ActivatedRoute, Router } from '@angular/router';
import { Auth, toApiError } from '../core/auth';

/**
 * Break-glass local sign-in for this development build (NFR-10). Production authenticates through
 * the organisation's OIDC provider with MFA; the persona list exists only for the seeded fixture.
 */
@Component({
  selector: 'v-login',
  imports: [FormsModule],
  template: `
    <main class="wrap">
      <form class="card box" (ngSubmit)="submit()">
        <h1>VERITA</h1>
        <p class="muted">Hospital activity-based costing</p>
        <label class="field">Email <input name="email" type="email" autocomplete="username" [(ngModel)]="email" required /></label>
        <label class="field">Password <input name="password" type="password" autocomplete="current-password" [(ngModel)]="password" required /></label>
        @if (error()) { <div class="notice bad" role="alert">{{ error() }}</div> }
        <button class="btn primary" type="submit" [disabled]="busy()">{{ busy() ? 'Signing in…' : 'Sign in' }}</button>
        <details>
          <summary class="muted">Development personas</summary>
          <p class="faint">Seeded accounts for the reference fixture. Each shows a different slice of the platform.</p>
          <div class="personas">
            @for (p of personas; track p.email) {
              <button type="button" class="btn" (click)="email = p.email">{{ p.label }}</button>
            }
          </div>
        </details>
      </form>
    </main>
  `,
  styles: `.wrap { min-height: 100vh; display: grid; place-items: center; padding: 16px; }
           .box { width: min(420px, 100%); display: grid; gap: 12px; }
           .personas { display: flex; flex-wrap: wrap; gap: 6px; }`,
})
export class Login {
  private auth = inject(Auth);
  private router = inject(Router);
  private route = inject(ActivatedRoute);
  email = 'cfo@verita.health';
  password = '';
  busy = signal(false);
  error = signal<string | null>(null);
  personas = [
    { email: 'cfo@verita.health', label: 'CFO' }, { email: 'ceo@verita.health', label: 'CEO' },
    { email: 'costing.manager@verita.health', label: 'Costing manager' }, { email: 'analyst@verita.health', label: 'Costing analyst' },
    { email: 'controller@verita.health', label: 'Finance controller' }, { email: 'clinical.director@verita.health', label: 'Clinical director (Riyadh)' },
    { email: 'jeddah.director@verita.health', label: 'Clinical director (Jeddah)' }, { email: 'surgery.manager@verita.health', label: 'Department manager' },
    { email: 'rcm@verita.health', label: 'RCM manager' }, { email: 'supplychain@verita.health', label: 'Supply chain' },
    { email: 'auditor@verita.health', label: 'Internal auditor' }, { email: 'admin@verita.health', label: 'Platform admin' },
  ];

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
