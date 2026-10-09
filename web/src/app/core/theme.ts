import { Injectable, signal } from '@angular/core';

/** Light / dark, remembered per browser. With nothing saved, the operating-system setting applies. */
@Injectable({ providedIn: 'root' })
export class Theme {
  readonly mode = signal<'light' | 'dark'>(this.initial());

  private initial(): 'light' | 'dark' {
    try {
      const saved = localStorage.getItem('verita.theme');
      if (saved === 'light' || saved === 'dark') return saved;
    } catch { /* storage unavailable */ }
    return matchMedia('(prefers-color-scheme: dark)').matches ? 'dark' : 'light';
  }

  toggle(): void {
    const next = this.mode() === 'dark' ? 'light' : 'dark';
    this.mode.set(next);
    document.documentElement.setAttribute('data-theme', next);
    try { localStorage.setItem('verita.theme', next); } catch { /* storage unavailable */ }
  }
}
