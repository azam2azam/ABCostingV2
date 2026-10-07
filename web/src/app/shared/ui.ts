import { NgTemplateOutlet } from '@angular/common';
import { Component, Pipe, PipeTransform, computed, input, output, signal } from '@angular/core';
import { RouterLink } from '@angular/router';
import { Direction, compareDec, directionClass, formatDec, formatMoney } from '../core/decimal';

// ── Pipes ────────────────────────────────────────────────────────────────────────

/** NG-6: presentation of money. `| money`, `| money:'compact'`, `| money:0`. */
@Pipe({ name: 'money' })
export class MoneyPipe implements PipeTransform {
  transform(v: string | number | null | undefined, mode: 'compact' | number = 2): string {
    if (v === null || v === undefined) return '—';
    return mode === 'compact' ? formatMoney(String(v), { compact: true }) : formatMoney(String(v), { dp: mode });
  }
}

@Pipe({ name: 'num' })
export class NumPipe implements PipeTransform {
  transform(v: string | number | null | undefined, dp = 2): string {
    return v === null || v === undefined ? '—' : formatDec(String(v), dp);
  }
}

@Pipe({ name: 'pct' })
export class PctPipe implements PipeTransform {
  transform(v: string | number | null | undefined, dp = 1): string {
    return v === null || v === undefined ? '—' : `${formatDec(String(v), dp)}%`;
  }
}

/** Colour class from money direction: `[class]="v | dir:'higher'"`. */
@Pipe({ name: 'dir' })
export class DirPipe implements PipeTransform {
  transform(v: string | number | null | undefined, dir: Direction): string {
    return directionClass(v === null || v === undefined ? null : String(v), dir);
  }
}

export const PIPES = [MoneyPipe, NumPipe, PctPipe, DirPipe] as const;

// ── Status chips ─────────────────────────────────────────────────────────────────

export function statusClass(s: string | null | undefined): string {
  switch (s) {
    case 'PUBLISHED': case 'PASS': case 'SUCCESS': case 'COMPLETED': case 'FAVOURABLE': case 'COSTED': return 'good';
    case 'FAILED': case 'FAIL': case 'BLOCKING': case 'ERROR': case 'UNFAVOURABLE': case 'REJECTED': return 'bad';
    case 'WARNING': case 'WARN': case 'REVIEWED': case 'APPROVED': case 'FINANCE_APPROVAL': case 'CLINICAL_APPROVAL': case 'REVIEW': return 'warn';
    case 'RECONCILED': case 'CALCULATED': case 'RUNNING': case 'INFO': return 'info';
    default: return '';
  }
}

@Component({
  selector: 'v-chip',
  template: `<span class="chip" [class]="'chip ' + cls()">{{ label() ?? value() }}</span>`,
})
export class Chip {
  value = input<string | null | undefined>('');
  label = input<string | null>(null);
  kind = input<string | null>(null);
  cls = computed(() => this.kind() ?? statusClass(this.value()));
}

// ── Page header ──────────────────────────────────────────────────────────────────

@Component({
  selector: 'v-page',
  template: `
    <header class="page-head">
      <div>
        <h1>{{ title() }}</h1>
        @if (purpose()) { <p class="muted">{{ purpose() }}</p> }
      </div>
      <div class="actions"><ng-content select="[actions]" /></div>
    </header>
    <ng-content />
  `,
  styles: `.page-head { display: flex; gap: 12px; align-items: flex-start; justify-content: space-between; flex-wrap: wrap; margin-bottom: 14px; }
           .actions { display: flex; gap: 8px; flex-wrap: wrap; }`,
})
export class Page {
  title = input.required<string>();
  purpose = input<string | null>(null);
}

// ── Stat tile ────────────────────────────────────────────────────────────────────

@Component({
  selector: 'v-tile',
  imports: [RouterLink, ...PIPES],
  template: `
    <div class="tile">
      <div class="label">{{ label() }}</div>
      <div class="value num" [class]="'value num ' + cls()">
        @if (link()) { <a [routerLink]="link()" queryParamsHandling="preserve">{{ text() }}</a> } @else { {{ text() }} }
      </div>
      @if (note()) { <div class="note">{{ note() }}</div> }
    </div>
  `,
  styles: `.tile { background: var(--surface); border: 1px solid var(--border); border-radius: var(--radius); padding: 12px 14px; }
           .label { color: var(--text-2); font-size: 13px; }
           .value { font-size: 22px; font-weight: 600; text-align: left; margin-top: 2px; }
           .value a { color: inherit; }
           .note { color: var(--text-3); font-size: 12.5px; margin-top: 2px; }`,
})
export class Tile {
  label = input.required<string>();
  value = input<string | number | null>(null);
  unit = input<string>('SAR');
  direction = input<string>('neutral');
  note = input<string | null>(null);
  link = input<string | null>(null);
  text = computed(() => {
    const v = this.value();
    if (v === null || v === undefined) return '—';
    if (this.unit() === 'SAR') {
      const digits = String(v).replace('-', '').split('.')[0].length;
      return digits > 5 ? formatMoney(String(v), { compact: true }) : formatMoney(String(v), { dp: digits > 3 ? 0 : 2 });
    }
    if (this.unit() === '%') return `${formatDec(String(v), 1)}%`;
    return formatDec(String(v), 0);
  });
  cls = computed(() => {
    const d = this.direction();
    return d === 'higher-better' ? directionClass(String(this.value()), 'higher') : '';
  });
}

// ── Inline proportion bar (NG-10: bars in cells, not separate pie charts) ───────────

@Component({
  selector: 'v-bar',
  template: `<div class="bar" [class]="'bar ' + (nature() ? 'nature-' + nature() : '')" role="img" [attr.aria-label]="share() + '%'"><span [style.width.%]="width()"></span></div>`,
})
export class Bar {
  share = input<string | number | null>(0);
  nature = input<string | null>(null);
  width = computed(() => {
    const s = String(this.share() ?? '0').replace('-', '');
    const whole = s.split('.')[0];
    return Math.min(100, Number(whole.length > 3 ? '100' : s)); // a CSS width only; never used as money
  });
}

// ── Degraded states (FR-11.14) ───────────────────────────────────────────────────

@Component({
  selector: 'v-degraded',
  template: `<span class="faint" [title]="text()">{{ short() }}</span>`,
})
export class Degraded {
  state = input<string>('');
  short = computed(() => ({ NOT_COSTED: 'Not costed in this run', NO_CASES: 'No cases this period', FULLY_ALLOCATED_SUPPORT: 'Fully allocated support' } as Record<string, string>)[this.state()] ?? '');
  text = computed(() => ({
    NOT_COSTED: 'No costing definition exists under this run\'s costing version, so no standard cost was produced.',
    NO_CASES: 'Nothing was delivered in this period, so there is no actual cost to show.',
    FULLY_ALLOCATED_SUPPORT: 'This activity sits in a support centre that is fully allocated out to other centres; its cost appears where it was allocated.',
  } as Record<string, string>)[this.state()] ?? '');
}

// ── Loading / error wrapper ──────────────────────────────────────────────────────

@Component({
  selector: 'v-state',
  template: `
    @if (error()) { <div class="notice bad" role="alert"><strong>{{ errorCode() }}</strong> — {{ errorMessage() }}</div> }
    @else if (loading()) { <div class="faint" aria-live="polite">Loading…</div> }
    @else { <ng-content /> }
  `,
})
export class State {
  loading = input<boolean>(false);
  error = input<unknown>(null);
  errorCode = computed(() => (this.error() as any)?.error?.error?.code ?? 'ERROR');
  errorMessage = computed(() => (this.error() as any)?.error?.error?.message ?? (this.error() as any)?.message ?? 'Request failed.');
}

// ── The data table ───────────────────────────────────────────────────────────────

export interface Col<T = any> {
  key: string;
  label: string;
  type?: 'text' | 'money' | 'num' | 'pct' | 'chip' | 'date' | 'bar' | 'nature';
  dir?: Direction;
  dp?: number;
  value?: (r: T) => unknown;
  link?: (r: T) => string | null;
  sub?: (r: T) => string | null | undefined;
  chip?: (r: T) => string | null;
  sortable?: boolean;
}

/**
 * Tables are the primary instrument (NG-8): readable dense typography with the expression under
 * each row. Master-data lists sort client-side (NG-9); every linked figure opens its source.
 * Rows render in pages of 200 so very long lists stay responsive.
 */
@Component({
  selector: 'v-table',
  imports: [RouterLink, Chip, Bar, NgTemplateOutlet, ...PIPES],
  template: `
    <div class="table-wrap">
      <table class="data">
        <caption class="sr-only">{{ caption() }}</caption>
        <thead>
          <tr>
            @for (c of columns(); track c.key) {
              <th scope="col" [class.num]="isNum(c)" [attr.aria-sort]="sortKey() === c.key ? (desc() ? 'descending' : 'ascending') : null">
                @if (c.sortable !== false) {
                  <button type="button" (click)="sort(c)">{{ c.label }}{{ sortKey() === c.key ? (desc() ? ' ▾' : ' ▴') : '' }}</button>
                } @else { {{ c.label }} }
              </th>
            }
          </tr>
        </thead>
        <tbody>
          @for (r of visible(); track $index) {
            <tr>
              @for (c of columns(); track c.key) {
                <td [class.num]="isNum(c)">
                  @switch (c.type ?? 'text') {
                    @case ('money') { <ng-container *ngTemplateOutlet="linked; context: { c, r, text: (val(c, r) | money:(c.dp ?? 2)) }" /> }
                    @case ('num') { <ng-container *ngTemplateOutlet="linked; context: { c, r, text: (val(c, r) | num:(c.dp ?? 2)) }" /> }
                    @case ('pct') { <ng-container *ngTemplateOutlet="linked; context: { c, r, text: (val(c, r) | pct:(c.dp ?? 1)) }" /> }
                    @case ('chip') { <v-chip [value]="$any(val(c, r))" [kind]="c.chip ? c.chip(r) : null" /> }
                    @case ('nature') { <v-chip [value]="$any(val(c, r))" [kind]="'nature-' + val(c, r)" /> }
                    @case ('bar') { <v-bar [share]="$any(val(c, r))" /> }
                    @case ('date') { {{ dateText(val(c, r)) }} }
                    @default { <ng-container *ngTemplateOutlet="linked; context: { c, r, text: val(c, r) ?? '—' }" /> }
                  }
                  @if (c.sub && c.sub(r)) { <span class="expr">{{ c.sub(r) }}</span> }
                </td>
              }
            </tr>
          } @empty {
            <tr><td [attr.colspan]="columns().length" class="faint">{{ empty() }}</td></tr>
          }
          @if (total()) {
            <tr class="total">
              @for (c of columns(); track c.key) {
                <td [class.num]="isNum(c)">
                  @if ($first) { Total }
                  @else if (c.type === 'money') { {{ val(c, total()) | money:(c.dp ?? 2) }} }
                  @else if (c.type === 'num') { {{ val(c, total()) | num:(c.dp ?? 2) }} }
                  @else if (c.type === 'pct') { {{ val(c, total()) | pct }} }
                </td>
              }
            </tr>
          }
        </tbody>
      </table>
    </div>
    @if (sorted().length > limit()) {
      <p class="faint more">Showing {{ limit() }} of {{ sorted().length }} rows. <button class="btn" type="button" (click)="limit.set(limit() + 200)">Show 200 more</button></p>
    }
    <ng-template #linked let-c="c" let-r="r" let-text="text">
      @if (c.link && c.link(r)) { <a [routerLink]="c.link(r)" queryParamsHandling="preserve" [class]="dirClass(c, r)">{{ text }}</a> }
      @else { <span [class]="dirClass(c, r)">{{ text }}</span> }
    </ng-template>
  `,
  styles: `.sr-only { position: absolute; width: 1px; height: 1px; overflow: hidden; clip: rect(0 0 0 0); }
           .more { margin-top: 8px; }`,
})
export class Table {
  rows = input<any[] | null | undefined>([]);
  columns = input.required<Col[]>();
  total = input<any>(null);
  caption = input<string>('');
  empty = input<string>('No rows.');
  sortKey = signal<string | null>(null);
  desc = signal(true);
  limit = signal(200);
  rowClick = output<any>();

  sorted = computed(() => {
    const rows = [...(this.rows() ?? [])];
    const key = this.sortKey();
    if (!key) return rows;
    const col = this.columns().find(c => c.key === key)!;
    const numeric = this.isNum(col);
    rows.sort((a, b) => {
      const va = this.val(col, a), vb = this.val(col, b);
      const cmp = numeric ? compareDec(String(va ?? ''), String(vb ?? '')) : String(va ?? '').localeCompare(String(vb ?? ''));
      return this.desc() ? -cmp : cmp;
    });
    return rows;
  });
  visible = computed(() => this.sorted().slice(0, this.limit()));

  isNum(c: Col): boolean { return c.type === 'money' || c.type === 'num' || c.type === 'pct'; }
  val(c: Col, r: any): any { return c.value ? c.value(r) : r?.[c.key]; }
  dirClass(c: Col, r: any): string { return c.dir ? directionClass(String(this.val(c, r) ?? ''), c.dir) : ''; }
  dateText(v: unknown): string { return v ? String(v).slice(0, 10) : '—'; }
  sort(c: Col): void {
    if (this.sortKey() === c.key) this.desc.set(!this.desc());
    else { this.sortKey.set(c.key); this.desc.set(this.isNum(c)); }
  }
}

export const UI = [Page, Tile, Table, Chip, Bar, Degraded, State, ...PIPES] as const;
