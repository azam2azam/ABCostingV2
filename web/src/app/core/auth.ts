import { HttpErrorResponse, HttpInterceptorFn } from '@angular/common/http';
import { Injectable, computed, inject, signal } from '@angular/core';
import { CanActivateFn, Router } from '@angular/router';
import { catchError, firstValueFrom, throwError } from 'rxjs';
import { Api, LoginResponse, SessionDto } from './api';

const TOKEN_KEY = 'verita.token';

function readToken(): string | null {
  try { return localStorage.getItem(TOKEN_KEY); } catch { return null; }
}
function writeToken(token: string | null): void {
  try { token ? localStorage.setItem(TOKEN_KEY, token) : localStorage.removeItem(TOKEN_KEY); } catch { /* storage unavailable */ }
}

@Injectable({ providedIn: 'root' })
export class Auth {
  private api = inject(Api);
  private router = inject(Router);
  readonly token = signal<string | null>(readToken());
  readonly user = signal<SessionDto | null>(null);
  readonly permissions = computed(() => new Set(this.user()?.permissions ?? []));

  can(permission: string): boolean { return this.permissions().has(permission); }

  async login(email: string, password: string): Promise<void> {
    const res = await firstValueFrom(this.api.post<LoginResponse>('auth/login', { email, password }));
    writeToken(res.token);
    this.token.set(res.token);
    this.user.set(res.user);
  }

  /** Restore the session on reload; an expired token sends the user back to sign-in. */
  async restore(): Promise<boolean> {
    if (!this.token()) return false;
    if (this.user()) return true;
    try {
      this.user.set(await firstValueFrom(this.api.get<SessionDto>('auth/me')));
      return true;
    } catch {
      this.clear();
      return false;
    }
  }

  async logout(): Promise<void> {
    try { await firstValueFrom(this.api.post('auth/logout', {})); } catch { /* already gone */ }
    this.clear();
    await this.router.navigate(['/login']);
  }

  clear(): void {
    writeToken(null);
    this.token.set(null);
    this.user.set(null);
  }
}

/** ARCH-13 / NG-12: the typed error envelope, mapped by code to something a person can act on. */
export interface ApiError { code: string; message: string; detail?: unknown; status: number; }

const MESSAGES: Record<string, string> = {
  RUN_IMMUTABLE: 'This run is published and cannot be changed. Create a new run to correct the period.',
  OUT_OF_SCOPE: 'That belongs to a hospital or department outside your scope.',
  NOT_COSTED: 'Nothing has been costed for this selection yet.',
  FORBIDDEN: 'Your role does not allow this.',
  SELF_APPROVAL: 'You cannot approve a request you made yourself.',
  PUBLISH_BLOCKED: 'This run cannot be published yet.',
};

export function toApiError(e: unknown): ApiError {
  if (e instanceof HttpErrorResponse) {
    const err = (e.error as { error?: { code: string; message: string; detail?: unknown } } | null)?.error;
    if (err) return { code: err.code, message: err.message || MESSAGES[err.code] || 'Request failed.', detail: err.detail, status: e.status };
    return { code: 'NETWORK', message: e.status === 0 ? 'The API is not reachable.' : `Request failed (${e.status}).`, status: e.status };
  }
  return { code: 'UNKNOWN', message: String(e), status: 0 };
}

export const authInterceptor: HttpInterceptorFn = (req, next) => {
  const auth = inject(Auth);
  const router = inject(Router);
  const token = auth.token();
  const authed = token ? req.clone({ setHeaders: { Authorization: `Bearer ${token}` } }) : req;
  return next(authed).pipe(
    catchError((e: unknown) => {
      if (e instanceof HttpErrorResponse && e.status === 401 && !req.url.endsWith('/auth/login')) {
        auth.clear();
        void router.navigate(['/login'], { queryParams: { next: router.url } });
      }
      return throwError(() => e);
    }),
  );
};

export const authGuard: CanActivateFn = async (_route, state) => {
  const auth = inject(Auth);
  const router = inject(Router);
  return (await auth.restore()) ? true : router.createUrlTree(['/login'], { queryParams: { next: state.url } });
};
