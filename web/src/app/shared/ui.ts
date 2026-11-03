import { NgTemplateOutlet } from '@angular/common';
import { Component, ElementRef, Pipe, PipeTransform, TemplateRef, computed, contentChild, inject, input, model, output, signal } from '@angular/core';
import { RouterLink } from '@angular/router';
import { Direction, compareDec, directionClass, formatDec, formatMoney } from '../core/decimal';
import { Icon } from './icon';

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
  const live = s === 'VALIDATING' || s === 'VALIDATED' || s === 'CALCULATING' || s === 'CALCULATED' || s === 'RECONCILING' || s === 'RUNNING';
  switch (s) {
    case 'PUBLISHED': case 'PASS': case 'SUCCESS': case 'COMPLETED': case 'FAVOURABLE': case 'COSTED': return 'good';
    case 'FAILED': case 'FAIL': case 'BLOCKING': case 'ERROR': case 'UNFAVOURABLE': case 'REJECTED': return 'bad';
    case 'WARNING': case 'WARN': case 'REVIEWED': case 'APPROVED': case 'FINANCE_APPROVAL': case 'CLINICAL_APPROVAL': case 'REVIEW': return 'warn';
    case 'RECONCILED': case 'CALCULATED': case 'RECONCILING': case 'RUNNING': case 'INFO':
    case 'VALIDATING': case 'VALIDATED': case 'CALCULATING': return live ? 'info live' : 'info';
    default: return live ? 'info live' : '';
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
  styles: `.page-head { display: flex; gap: 16px; align-items: flex-end; justify-content: space-between; flex-wrap: wrap; margin-bottom: 20px; }
           .page-head h1 { font-size: 26px; }
           .page-head p { max-width: 80ch; margin: 0; }
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
  styles: `.tile { position: relative; overflow: hidden; height: 100%; background: var(--surface); backdrop-filter: blur(14px); border: 1px solid var(--border);
                   border-radius: var(--radius); padding: 14px 16px 14px 18px; box-shadow: var(--shadow-sm); transition: box-shadow .15s, transform .15s; }
           .tile::before { content: ""; position: absolute; left: 0; top: 14px; bottom: 14px; width: 3px; border-radius: 0 3px 3px 0; background: linear-gradient(var(--accent), var(--accent-strong)); opacity: .8; }
           .tile:hover { box-shadow: var(--shadow); transform: translateY(-1px); }
           .label { color: var(--text-3); font-size: 12px; font-weight: 600; letter-spacing: .04em; text-transform: uppercase; }
           .value { font-size: 24px; font-weight: 700; letter-spacing: -0.02em; text-align: left; margin-top: 6px; }
           .value a { color: inherit; }
           .note { color: var(--text-3); font-size: 12.5px; margin-top: 4px; }`,
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
  type?: 'text' | 'money' | 'num' | 'pct' | 'chip' | 'date' | 'bar' | 'nature' | 'button';
  dir?: Direction;
  dp?: number;
  value?: (r: T) => unknown;
  /** Money and number cells show nothing when the value is zero or missing. */
  blankZero?: boolean;
  /** Shows the cell in red when this is true for the row. */
  bad?: (r: T) => boolean;
  link?: (r: T) => string | null;
  sub?: (r: T) => string | null | undefined;
  chip?: (r: T) => string | null;
  button?: (r: T) => string | null;
  icon?: (r: T) => string | null;
  expanded?: (r: T) => boolean;
  onButton?: (r: T) => void;
  sortable?: boolean;
}

/**
 * Tables are the primary instrument (NG-8): readable dense typography with the expression under
 * each row. Master-data lists sort client-side (NG-9); every linked figure opens its source.
 * Rows render in pages of 100 so very long lists stay responsive.
 */
@Component({
  selector: 'v-table',
  imports: [RouterLink, Chip, Bar, Icon, NgTemplateOutlet, ...PIPES],
  template: `
    <div class="table-wrap">
      <table class="data">
        <caption class="sr-only">{{ caption() }}</caption>
        <thead>
          <tr>
            @for (c of columns(); track c.key) {
              <th scope="col" [class.num]="isNum(c)" [attr.aria-sort]="activeSort() === c.key ? (activeDesc() ? 'descending' : 'ascending') : null">
                @if (c.sortable !== false) {
                  <button type="button" (click)="sort(c)">{{ c.label }}{{ activeSort() === c.key ? (activeDesc() ? ' ▾' : ' ▴') : '' }}</button>
                } @else { {{ c.label }} }
              </th>
            }
          </tr>
        </thead>
        <tbody>
          @for (r of visible(); track $index) {
            <tr>
              @for (c of columns(); track c.key) {
                <td [class.num]="isNum(c)" [class.bad]="!!c.bad?.(r)">
                  @switch (c.type ?? 'text') {
                    @case ('money') { <ng-container *ngTemplateOutlet="linked; context: { c, r, text: blank(c, r) ? '' : (val(c, r) | money:(c.dp ?? 2)) }" /> }
                    @case ('num') { <ng-container *ngTemplateOutlet="linked; context: { c, r, text: blank(c, r) ? '' : (val(c, r) | num:(c.dp ?? 2)) }" /> }
                    @case ('pct') { <ng-container *ngTemplateOutlet="linked; context: { c, r, text: (val(c, r) | pct:(c.dp ?? 1)) }" /> }
                    @case ('chip') { <v-chip [value]="$any(val(c, r))" [kind]="c.chip ? c.chip(r) : null" /> }
                    @case ('nature') { @if (val(c, r)) { <v-chip [value]="$any(val(c, r))" [kind]="'nature-' + val(c, r)" /> } }
                    @case ('bar') { <v-bar [share]="$any(val(c, r))" /> }
                    @case ('date') { <span style="white-space:nowrap">{{ dateText(val(c, r)) }}</span> }
                    @case ('button') {
                      @if (c.button && c.button(r); as label) {
                        <button class="btn" [class.icon-only]="!!c.icon" type="button"
                          [attr.aria-label]="label" [attr.title]="label"
                          [attr.aria-expanded]="c.expanded ? c.expanded(r) : null"
                          (click)="c.onButton?.(r); pinExpand(); $event.stopPropagation()">
                          @if (c.icon) { <v-icon [name]="c.icon(r) ?? 'chevron'" /> } @else { {{ label }} }
                        </button>
                      }
                    }
                    @default { <ng-container *ngTemplateOutlet="linked; context: { c, r, text: val(c, r) ?? '—' }" /> }
                  }
                  @if (c.sub && c.sub(r)) { <span class="expr">{{ c.sub(r) }}</span> }
                </td>
              }
            </tr>
            @if (expandTpl(); as tpl) {
              @if (rowExpanded(r)) {
                <tr class="expand-row">
                  <td [attr.colspan]="columns().length">
                    <div class="expand-pane">
                      <ng-container *ngTemplateOutlet="tpl; context: { $implicit: r }" />
                    </div>
                  </td>
                </tr>
              }
            }
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
      <p class="faint more">Showing {{ limit() }} of {{ sorted().length }} rows. <button class="btn" type="button" (click)="limit.set(limit() + pageSize())">Show {{ pageSize() }} more</button></p>
    }
    <ng-template #linked let-c="c" let-r="r" let-text="text">
      @if (c.link && c.link(r)) { <a [routerLink]="c.link(r)" queryParamsHandling="preserve" [class]="dirClass(c, r)">{{ text }}</a> }
      @else { <span [class]="dirClass(c, r)">{{ text }}</span> }
    </ng-template>
  `,
  styles: `.sr-only { position: absolute; width: 1px; height: 1px; overflow: hidden; clip: rect(0 0 0 0); }
           .more { margin-top: 8px; }
           button.icon-only { padding: 4px; width: 28px; height: 28px; justify-content: center; border-radius: 8px; }
           button.icon-only v-icon { transition: transform .15s; }
           button.icon-only[aria-expanded] v-icon { transform: rotate(-90deg); }
           button.icon-only[aria-expanded="true"] v-icon { transform: rotate(0); }
           tr.expand-row td { background: var(--surface-2); padding: 0; }
           .expand-pane { position: sticky; left: 0; box-sizing: border-box; padding: 8px 12px 14px 28px; width: var(--expand-w, 100%); }`,
})
export class Table {
  rows = input<any[] | null | undefined>([]);
  columns = input.required<Col[]>();
  total = input<any>(null);
  caption = input<string>('');
  empty = input<string>('No rows.');
  /** Initial sort column; used until the user clicks a header. Numeric columns default descending. */
  sortBy = input<string | null>(null);
  /** For a list the server sorts and pages: the current sort. Header clicks only ask for a new one. */
  serverSort = input<{ key: string; desc: boolean } | null>(null);
  sortChange = output<{ key: string; desc: boolean }>();
  pageSize = input(100);
  sortKey = signal<string | null>(null);
  desc = signal(true);
  limit = signal(100);
  rowClick = output<any>();
  expandTpl = contentChild<TemplateRef<{ $implicit: any }>>('expand');
  private host = inject(ElementRef<HTMLElement>);
  activeSort = computed(() => this.serverSort()?.key ?? this.sortKey() ?? this.sortBy());
  activeDesc = computed(() => {
    const server = this.serverSort();
    if (server) return server.desc;
    if (this.sortKey() !== null) return this.desc();
    const col = this.columns().find(c => c.key === this.sortBy());
    return col ? this.isNum(col) : this.desc();
  });

  pinExpand(): void {
    requestAnimationFrame(() => {
      const el = (this.host.nativeElement as HTMLElement).querySelector('.table-wrap') as HTMLElement | null;
      if (!el) return;
      el.style.setProperty('--expand-w', `${el.clientWidth}px`);
      el.scrollTo({ left: 0 });
    });
  }

  sorted = computed(() => {
    const rows = [...(this.rows() ?? [])];
    const key = this.activeSort();
    if (!key || this.serverSort()) return rows;
    const col = this.columns().find(c => c.key === key)!;
    const numeric = this.isNum(col);
    rows.sort((a, b) => {
      const va = this.val(col, a), vb = this.val(col, b);
      const cmp = numeric ? compareDec(String(va ?? ''), String(vb ?? '')) : String(va ?? '').localeCompare(String(vb ?? ''));
      return this.activeDesc() ? -cmp : cmp;
    });
    return rows;
  });
  visible = computed(() => this.sorted().slice(0, this.limit()));

  rowExpanded(r: any): boolean { return this.columns().some(c => !!c.expanded?.(r)); }
  isNum(c: Col): boolean { return c.type === 'money' || c.type === 'num' || c.type === 'pct'; }
  val(c: Col, r: any): any { return c.value ? c.value(r) : r?.[c.key]; }
  blank(c: Col, r: any): boolean { return !!c.blankZero && !Number(this.val(c, r)); }
  dirClass(c: Col, r: any): string { return c.dir ? directionClass(String(this.val(c, r) ?? ''), c.dir) : ''; }
  dateText(v: unknown): string { return v ? String(v).slice(0, 10) : '—'; }
  sort(c: Col): void {
    if (this.serverSort()) {
      // First click: largest or newest first for numbers and dates, A to Z for text.
      this.sortChange.emit({ key: c.key, desc: this.activeSort() === c.key ? !this.activeDesc() : this.isNum(c) || c.type === 'date' });
      return;
    }
    if (this.activeSort() === c.key) {
      this.sortKey.set(c.key);
      this.desc.set(!this.activeDesc());
    } else {
      this.sortKey.set(c.key);
      this.desc.set(this.isNum(c));
    }
  }
}

/** Compact Type counts; click a tile to filter a procedure/package list. */
export function typeLabel(type: string): string {
  return type.replace(/_/g, ' ').toLowerCase().replace(/\b\w/g, c => c.toUpperCase());
}

@Component({
  selector: 'v-type-tiles',
  template: `
    <div class="type-tiles" role="group" aria-label="Filter by type">
      <button type="button" class="type-tile" [class.on]="!selected()" (click)="selected.set(null)">
        All ({{ total() }})
      </button>
      @for (t of tiles(); track t.type) {
        <button type="button" class="type-tile" [class.on]="selected() === t.type"
          [attr.aria-pressed]="selected() === t.type" (click)="toggle(t.type)">
          {{ t.label }} ({{ t.count }})
        </button>
      }
      @if (zeroPriceTile()) {
        <button type="button" class="type-tile" [class.on]="selected() === 'ZERO_PRICE'"
          [attr.aria-pressed]="selected() === 'ZERO_PRICE'" (click)="toggle('ZERO_PRICE')">
          Zero Price ({{ zeroPriceCount() }})
        </button>
      }
    </div>
  `,
  styles: `.type-tiles { display: flex; flex-wrap: wrap; gap: 8px; margin-bottom: 14px; }
           .type-tile { font: 600 13px var(--font); padding: 7px 12px; border-radius: var(--radius-sm); border: 1px solid var(--border);
                        background: var(--surface); color: var(--text); cursor: pointer; box-shadow: var(--shadow-sm);
                        transition: border-color .15s, background .15s, box-shadow .15s, transform .15s; }
           .type-tile:hover { transform: translateY(-1px); box-shadow: var(--shadow); border-color: var(--accent); }
           .type-tile.on { border-color: var(--accent); background: var(--accent-soft); color: var(--accent-strong); }`,
})
export class TypeTiles {
  rows = input<{ procType?: string | null; category?: string | null; listPrice?: string | number | null }[]>([]);
  /** Extra tiles keyed as `CAT:{category}` (e.g. MEDICINE SKUs that are not a procType). */
  categoryTiles = input<string[]>([]);
  /** Procedures/packages whose contracted list price is 0. */
  zeroPriceTile = input(false);
  selected = model<string | null>(null);
  tiles = computed(() => {
    const cats = new Set(this.categoryTiles().map(c => c.trim().toUpperCase()).filter(Boolean));
    const types = new Map<string, number>();
    const extra = new Map<string, number>();
    const skipZero = this.zeroPriceTile();
    for (const r of this.rows()) {
      if (skipZero && Number(r.listPrice) === 0) continue;
      const cat = (r.category ?? '').trim().toUpperCase();
      if (cats.has(cat)) {
        extra.set(cat, (extra.get(cat) ?? 0) + 1);
        continue;
      }
      const t = (r.procType ?? '').trim();
      if (t) types.set(t, (types.get(t) ?? 0) + 1);
    }
    const typeTiles = [...types.entries()].sort((a, b) => b[1] - a[1] || a[0].localeCompare(b[0]))
      .map(([type, count]) => ({ type, label: typeLabel(type), count }));
    const catTiles = [...cats].filter(c => extra.has(c))
      .map(c => ({ type: `CAT:${c}`, label: typeLabel(c), count: extra.get(c)! }));
    return [...typeTiles, ...catTiles];
  });
  zeroPriceCount = computed(() => this.rows().filter(r => Number(r.listPrice) === 0).length);
  total = computed(() => this.zeroPriceTile() ? this.rows().length - this.zeroPriceCount() : this.rows().length);
  toggle(type: string): void {
    this.selected.set(this.selected() === type ? null : type);
  }
}

/** Case-insensitive substring match on name or code (including a hospital-prefixed code). */
export function matchesNameOrCode(query: string, name: string | null | undefined, code: string | null | undefined): boolean {
  const q = query.trim().toLowerCase();
  if (!q) return true;
  return (name ?? '').toLowerCase().includes(q) || (code ?? '').toLowerCase().includes(q);
}

@Component({
  selector: 'v-list-search',
  template: `
    <input type="search" class="list-search" [placeholder]="placeholder()" [value]="query()"
      (input)="query.set($any($event.target).value)" [attr.aria-label]="placeholder()" />
  `,
  styles: `.list-search { display: block; width: min(28rem, 100%); margin: 0 0 14px; }`,
})
export class ListSearch {
  query = model('');
  placeholder = input('Search by name or code');
}

export const UI = [Page, Tile, Table, Chip, Bar, Degraded, State, TypeTiles, ListSearch, ...PIPES] as const;
