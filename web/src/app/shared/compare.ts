import { Component, computed, input, model, output } from '@angular/core';
import { RouterLink } from '@angular/router';
import { formatDec, formatMoney } from '../core/decimal';
import { Icon } from './icon';
import { TileInfo } from './ui';

/**
 * The side-by-side comparison shared by Compare doctors and Compare packages: four columns, a gold benchmark badge for
 * the best of them, and the same Volume / Financial / Materials / Quality rows, worked out as the package pages do.
 */

/** What the gold benchmark badge goes to among the columns being compared. */
export type Bench = 'materials' | 'recovery' | 'los' | 'volume';
export const BENCH: { key: Bench; label: string; badge: string }[] = [
  { key: 'volume', label: 'Volume', badge: 'Highest volume' },
  { key: 'recovery', label: 'Recovery', badge: 'Best recovery' },
  { key: 'materials', label: 'Materials', badge: 'Best materials billing' },
  { key: 'los', label: 'Length of stay', badge: 'Shortest stay' },
];
export const COLUMNS = 4;

/** One column's figures: a doctor on a package, or a package in a department. */
export interface Stat {
  key: string;
  name: string;
  sub: string;
  /** Where the name links to, or null. */
  link: string[] | null;
  /** Initials in the circle; null shows the package icon. */
  avatar: string | null;
  performed: number;
  encounters: number;
  /** Resources consumed: contracted price × performed + consumables and medicine (billed and covered). */
  rc: number;
  billed: number;
  /** Consumables billed plus covered. */
  consumables: number;
  /** Materials (consumables + medicine) billed separately, and those the package price covered. */
  matBilled: number;
  matCovered: number;
  /** Medicine billed separately. */
  medicine: number;
  claimed: number | null;
  rejected: number;
  /** Ward days ÷ encounters. */
  los: number;
  /** The package's covered length of stay (0 when it has none). */
  coveredLos: number;
  /** The small bar chart under Performed, largest first. */
  spread: { value: number; label: string }[];
}

/** Raw figures as the run stores them (package doctor rows, department package rows). */
export interface Figures {
  performed: number; encounters: number; price: number; feeBilled: unknown;
  consumablesBilled: unknown; consumablesCovered: unknown; medicineBilled: unknown; medicineCovered: unknown;
  claimsReceived: unknown; rejected: unknown; wardDays: unknown; coveredLos: number;
}

export const n = (v: unknown) => Number(v ?? 0) || 0;
/** Rounded by the decimal formatter (half away from zero), not Math.round, which takes −645.5 to −645. */
export const money = (v: number) => formatMoney(v.toFixed(6), { dp: 0 });
const pct = (v: number) => `${formatDec(String(Math.abs(v) * 100), 0)}%`;

/** The figures every column shows, from the stored row. */
export function figuresOf(f: Figures): Omit<Stat, 'key' | 'name' | 'sub' | 'link' | 'avatar' | 'spread'> {
  const consumables = n(f.consumablesBilled) + n(f.consumablesCovered);
  const medicine = n(f.medicineBilled) + n(f.medicineCovered);
  return {
    performed: f.performed, encounters: f.encounters,
    rc: f.price * f.performed + consumables + medicine,
    billed: n(f.feeBilled), consumables,
    matBilled: n(f.consumablesBilled) + n(f.medicineBilled), matCovered: n(f.consumablesCovered) + n(f.medicineCovered),
    medicine: n(f.medicineBilled),
    claimed: f.claimsReceived == null ? null : n(f.claimsReceived),
    rejected: n(f.rejected),
    los: f.encounters ? n(f.wardDays) / f.encounters : 0,
    coveredLos: f.coveredLos,
  };
}

export function initials(name: string): string {
  const words = name.replace(/^(dr\.?|prof\.?)\s+/i, '').split(/\s+/).filter(Boolean);
  return ((words[0]?.[0] ?? '') + (words.length > 1 ? words[words.length - 1][0] : '')).toUpperCase() || '?';
}

export function withValues(i: TileInfo, values: string | null | undefined): TileInfo { return values ? { ...i, values } : i; }

/** The four tiles: what the rows add up to, with the parts for the (i) popups. */
export function tileTotals(rows: Figures[], cases: number, priceTimesCases: number, priceNote: string) {
  const sum = (f: (r: Figures) => unknown) => rows.reduce((t, r) => t + n(f(r)), 0);
  const res: [number, string][] = [
    [priceTimesCases, priceNote],
    [sum(r => r.consumablesCovered), 'consumables covered'], [sum(r => r.medicineCovered), 'medicine covered'],
    [sum(r => r.consumablesBilled), 'consumables billed separately'], [sum(r => r.medicineBilled), 'medicine billed separately'],
  ];
  const bill: [number, string][] = [
    [sum(r => r.feeBilled), 'Invoice Billed'], [sum(r => r.consumablesBilled), 'consumables billed'], [sum(r => r.medicineBilled), 'medicine billed'],
  ];
  const total = (p: [number, string][]) => p.reduce((t, [v]) => t + v, 0);
  const note = (p: [number, string][]) => p.map(([v, l]) => `${money(v)} ${l}`).join(' + ');
  return {
    cases,
    resources: total(res), resourcesNote: note(res),
    billed: total(bill), billedNote: note(bill),
    claimed: rows.every(r => r.claimsReceived == null) ? null : sum(r => r.claimsReceived),
  };
}

/** Equal within rounding noise. */
const same = (x: number, y: number) => x === y || Math.abs(x - y) <= 1e-9 * Math.max(1, Math.abs(x), Math.abs(y));

/** A figure the benchmark ranks on, higher is better; null when the column has nothing to rank. */
interface Figure { label: string; score: (s: Stat) => number | null }
const FIG: Record<string, Figure> = {
  volume: { label: 'more cases', score: s => s.performed || null },
  encounters: { label: 'more encounters', score: s => s.encounters || null },
  recovery: { label: 'better recovery', score: s => s.performed && s.claimed != null && s.rc ? s.claimed / s.rc : null },
  materials: { label: 'better materials billing', score: s => s.performed ? s.matBilled - s.matCovered : null },
  resources: { label: 'lower resources per case', score: s => s.performed ? -s.rc / s.performed : null },
  los: { label: 'shorter stay', score: s => s.performed && s.encounters ? -s.los : null },
  rejected: { label: 'fewer rejected claims', score: s => s.performed ? -s.rejected : null },
};
/** Each benchmark's figure first, then the figures that settle a tie, in order. */
const FIGURES_BY_BENCH: Record<Bench, Figure[]> = {
  volume: [FIG['volume'], FIG['encounters'], FIG['recovery'], FIG['materials'], FIG['los']],
  recovery: [FIG['recovery'], FIG['rejected'], FIG['materials'], FIG['los'], FIG['volume']],
  materials: [FIG['materials'], FIG['recovery'], FIG['resources'], FIG['los'], FIG['volume']],
  los: [FIG['los'], FIG['materials'], FIG['recovery'], FIG['rejected'], FIG['volume']],
};

interface Cell { value: string; sub?: string; cls?: string; subCls?: string; }
interface Metric { section?: string; caption: string; info?: string; cells: (Cell | null)[]; big?: boolean; bars?: boolean; }

/** The Average / Total and Benchmark switches. */
@Component({
  selector: 'v-compare-toggles',
  imports: [Icon],
  template: `
    <div class="seg" role="group" aria-label="Figures">
      <button type="button" [class.on]="perCase()" (click)="perCase.set(true)">Average</button>
      <button type="button" [class.on]="!perCase()" (click)="perCase.set(false)">Total</button>
    </div>
    <span class="bench-lbl"><v-icon name="star" [size]="15" /> Benchmark on</span>
    <div class="seg gold" role="group" aria-label="Benchmark on">
      @for (b of modes; track b.key) {
        <button type="button" [class.on]="bench() === b.key" (click)="bench.set(b.key)">{{ b.label }}</button>
      }
    </div>
  `,
  styles: `
    :host { display: flex; gap: 10px; align-items: center; flex-wrap: wrap; }
    .seg { display: inline-flex; padding: 3px; border: 1px solid var(--border-strong); border-radius: 999px; background: var(--surface-solid); }
    .seg button { all: unset; cursor: pointer; padding: 5px 12px; border-radius: 999px; font-size: 13px; font-weight: 500; color: var(--text-2); white-space: nowrap; transition: background .2s, color .2s; }
    .seg button:hover { color: var(--text); }
    .seg button:focus-visible { outline: 2px solid var(--focus); }
    .seg button.on { background: linear-gradient(135deg, var(--accent), var(--accent-strong)); color: #fff; }
    .seg.gold button.on { background: linear-gradient(135deg, #d4ae2f, #b58e14); color: #fff; }
    .bench-lbl { display: inline-flex; align-items: center; gap: 6px; font-size: 13px; color: var(--text-2); margin-left: 6px; }
    .bench-lbl v-icon { color: #c9a227; }
  `,
})
export class CompareToggles {
  perCase = model(true);
  bench = model<Bench>('volume');
  modes = BENCH;
}

/** The four columns: a picker and header each, then the comparison rows. */
@Component({
  selector: 'v-compare-grid',
  imports: [RouterLink, Icon],
  template: `
    <div class="scroll">
      <div class="cmp">
        <div class="heads">
          @for (c of cols(); track $index; let i = $index) {
            <div class="head">
              <select (change)="pick.emit({ index: i, key: $any($event.target).value })" [attr.aria-label]="noun() + ' ' + (i + 1)">
                <option value="" [selected]="!c">Add {{ noun().toLowerCase() }}…</option>
                @for (o of options(); track o.key) {
                  <option [value]="o.key" [selected]="c?.key === o.key" [disabled]="o.key !== c?.key && taken().has(o.key)">{{ o.name }} · {{ o.performed }}</option>
                }
              </select>
              @if (c) {
                <div class="avatar" [class.gold]="winner()?.key === c.key">
                  @if (c.avatar) { {{ c.avatar }} } @else { <v-icon name="package-costing" [size]="28" /> }
                </div>
                <div class="name">
                  @if (c.link) { <a [routerLink]="c.link" queryParamsHandling="preserve">{{ c.name }}</a> } @else { {{ c.name }} }
                </div>
                <div class="sub">{{ c.sub }}</div>
                @if (winner()?.key === c.key) {
                  <span class="badge"><v-icon name="star" [size]="11" /> {{ badge() }}</span>
                  @if (winner()?.note) { <div class="tie">{{ winner()?.note }}</div> }
                }
              } @else {
                <div class="avatar empty"><v-icon name="plus" [size]="22" /></div>
                <div class="sub">Pick a {{ noun().toLowerCase() }} to compare</div>
              }
            </div>
          }
        </div>

        @for (m of metrics(); track m.caption) {
          @if (m.section) { <h2 class="section">{{ m.section }}</h2> }
          <div class="row" [class.first]="!!m.section">
            @if (m.bars) {
              @for (c of cols(); track $index) {
                <div class="cell">
                  @if (c) {
                    <div class="bars" role="img" [attr.aria-label]="m.caption + ' for ' + c.name">
                      @for (b of bars(c); track $index) { <span [style.height.px]="b.h" [title]="b.label" [class.zero]="!b.value"></span> }
                    </div>
                    <div class="cap">{{ m.caption }}{{ c.spread.length > maxBars ? ' · top ' + maxBars + ' of ' + c.spread.length : '' }}</div>
                  }
                </div>
              }
            } @else {
              @for (cell of m.cells; track $index) {
                <div class="cell">
                  @if (cell) {
                    <div [class]="'v num ' + (m.big ? 'big ' : '') + (cell.cls ?? '')">{{ cell.value }}</div>
                    <div class="cap" [title]="m.info ?? ''">{{ m.caption }}</div>
                    @if (cell.sub) { <div [class]="'vs ' + (cell.subCls ?? '')">{{ cell.sub }}</div> }
                  }
                </div>
              }
            }
          </div>
        }
      </div>
    </div>
  `,
  styles: `
    .scroll { overflow-x: auto; -webkit-overflow-scrolling: touch; }
    .cmp { min-width: 760px; background: var(--surface); border: 1px solid var(--border); border-radius: var(--radius); box-shadow: var(--shadow-sm); padding: 6px 22px 28px; }
    .heads, .row { display: grid; grid-template-columns: repeat(${COLUMNS}, minmax(0, 1fr)); gap: 22px; }
    .heads { position: sticky; top: 0; z-index: 2; padding: 16px 22px 20px; background: var(--surface-solid); border-bottom: 1px solid var(--border); margin: 0 -22px; border-radius: var(--radius) var(--radius) 0 0; }
    .head { min-width: 0; text-align: center; }
    .head select { width: 100%; }
    .avatar { width: 68px; height: 68px; margin: 16px auto 0; border-radius: 50%; display: grid; place-items: center; font-size: 22px; font-weight: 650; letter-spacing: .02em;
              color: var(--accent-strong); background: var(--accent-soft); border: 1.5px solid color-mix(in srgb, var(--accent) 40%, transparent); }
    .avatar.empty { color: var(--text-3); background: var(--surface-2); border: 1.5px dashed var(--border-strong); }
    .avatar.gold { color: #8a6a00; background: rgba(240, 201, 74, 0.22); border-color: rgba(201, 162, 39, 0.85);
                   box-shadow: 0 0 0 3px rgba(201, 162, 39, 0.35), 0 0 22px rgba(201, 162, 39, 0.35); animation: goldPulse 2.6s ease-in-out infinite; }
    @keyframes goldPulse { 50% { box-shadow: 0 0 0 5px rgba(201, 162, 39, 0.20), 0 0 30px rgba(201, 162, 39, 0.45); } }
    @media (prefers-reduced-motion: reduce) { .avatar.gold { animation: none; } }
    .name { font-size: 16px; font-weight: 650; margin-top: 10px; overflow-wrap: anywhere; }
    .name a { color: var(--text); }
    .sub { font-size: 12.5px; color: var(--text-3); margin-top: 3px; }
    .badge { display: inline-flex; align-items: center; gap: 4px; margin-top: 8px; font-size: 11px; font-weight: 700; letter-spacing: .04em; text-transform: uppercase;
             color: #7a5c00; background: linear-gradient(135deg, rgba(240, 201, 74, 0.35), rgba(201, 162, 39, 0.22)); border: 1px solid rgba(201, 162, 39, 0.55); border-radius: 999px; padding: 3px 9px; }
    :host-context([data-theme="dark"]) .badge, :host-context([data-theme="dark"]) .avatar.gold { color: #f5d36a; }
    @media (prefers-color-scheme: dark) { :host-context(:root:not([data-theme="light"])) .badge, :host-context(:root:not([data-theme="light"])) .avatar.gold { color: #f5d36a; } }
    .tie { font-size: 11.5px; color: var(--text-3); margin-top: 4px; }
    .section { display: flex; align-items: center; gap: 8px; font-size: 18px; margin: 30px 0 0; }
    .row { padding-top: 16px; }
    .cell { min-width: 0; text-align: center; border-top: 1px solid var(--border); padding-top: 14px; }
    .row.first .cell { border-top-color: var(--border-strong); }
    .v { text-align: center; font-size: 21px; font-weight: 650; letter-spacing: -0.01em; line-height: 1.15; }
    .v.big { font-size: 28px; }
    .cap { font-size: 12px; color: var(--text-3); margin-top: 6px; }
    .vs { font-size: 12.5px; font-weight: 500; margin-top: 4px; color: var(--text-2); font-variant-numeric: tabular-nums; }
    .bars { display: flex; align-items: flex-end; justify-content: center; gap: 3px; height: 48px; }
    .bars span { width: 8px; border-radius: 3px 3px 0 0; background: linear-gradient(180deg, var(--accent), var(--accent-strong)); transition: height .35s cubic-bezier(.22, 1, .36, 1); }
    .bars span.zero { background: var(--border-strong); }
  `,
})
export class CompareGrid {
  /** The four columns (null is an empty one). */
  cols = input.required<(Stat | null)[]>();
  /** Everything a column can pick, which is also what the averages are over. */
  options = input.required<Stat[]>();
  bench = input.required<Bench>();
  perCase = input.required<boolean>();
  /** "Doctor" or "Package". */
  noun = input.required<string>();
  /** Caption of the bar chart under Performed. */
  spreadCaption = input.required<string>();
  /** What the consumables average is over, e.g. "package average". */
  avgLabel = input.required<string>();
  pick = output<{ index: number; key: string }>();
  readonly maxBars = 24;

  taken = computed(() => new Set(this.cols().filter((c): c is Stat => !!c).map(c => c.key)));

  /**
   * The one column that gets the badge. A tie on the benchmark's figure is settled by the next figure in its chain
   * (all higher-is-better; a missing figure ranks last); level on every figure, the leftmost column wins.
   */
  winner = computed<{ key: string; note: string } | null>(() => {
    const chain = FIGURES_BY_BENCH[this.bench()];
    let tied = this.cols().filter((c): c is Stat => !!c && chain[0].score(c) != null);
    if (!tied.length) return null;
    for (let i = 0; i < chain.length; i++) {
      const f = chain[i];
      const top = Math.max(...tied.map(c => f.score(c) ?? -Infinity));
      tied = tied.filter(c => same(f.score(c) ?? -Infinity, top));
      if (tied.length === 1) return { key: tied[0].key, note: i ? `Tied, decided on ${f.label}` : '' };
    }
    return { key: tied[0].key, note: 'Level on every figure' };
  });
  badge = computed(() => BENCH.find(b => b.key === this.bench())!.badge);

  /** Every option together, for the "vs average" captions. */
  private avg = computed(() => {
    const all = this.options();
    const sum = (f: (s: Stat) => number) => all.reduce((t, s) => t + f(s), 0);
    const performed = sum(s => s.performed), encounters = sum(s => s.encounters);
    return {
      consumablesPerCase: performed ? sum(s => s.consumables) / performed : 0,
      los: encounters ? sum(s => s.los * s.encounters) / encounters : 0,
    };
  });

  metrics = computed<Metric[]>(() => {
    const cols = this.cols(), per = this.perCase(), avg = this.avg(), avgLabel = this.avgLabel();
    const amt = (s: Stat, v: number) => money(per ? (s.performed ? v / s.performed : 0) : v);
    const each = (f: (s: Stat) => Cell): (Cell | null)[] => cols.map(s => s ? f(s) : null);
    const suffix = per ? ' per case' : '';
    const days = (d: number) => `${d} ${d === 1 ? 'day' : 'days'}`;
    return [
      { section: 'Volume', caption: 'Performed', big: true,
        cells: each(s => ({ value: formatDec(String(s.performed), 0), sub: `${s.encounters} ${s.encounters === 1 ? 'encounter' : 'encounters'}` })) },
      { caption: this.spreadCaption(), bars: true, cells: [] },
      { section: 'Financial metrics', caption: 'Resources consumed' + suffix, big: true,
        info: 'Contracted price × performed + consumables and medicine, billed and covered by the package price.',
        cells: each(s => ({ value: amt(s, s.rc) })) },
      { caption: 'Invoice billed' + suffix, cells: each(s => ({ value: amt(s, s.billed) })) },
      { caption: 'Claimed' + suffix, info: 'What HIS claimed on invoices it put on a claim (IsClaimed = 1).',
        cells: each(s => s.claimed == null ? { value: '—' } : { value: amt(s, s.claimed) }) },
      { caption: 'Resources consumed − Claimed', info: '(Resources consumed − Claimed); the share is of resources consumed.',
        cells: each(s => {
          if (s.claimed == null) return { value: '—' };
          const gap = s.rc - s.claimed;
          return { value: amt(s, gap), cls: gap > 0 ? 'bad' : 'good', sub: s.rc ? `${pct(gap / s.rc)} of resources consumed` : '' };
        }) },
      { section: 'Materials', caption: 'Materials billed − covered', big: true,
        info: 'Σ (consumables + medicine) billed separately − Σ (consumables + medicine) the package price covered. Higher is better; the Materials benchmark ranks on it.',
        cells: each(s => {
          const v = s.matBilled - s.matCovered;
          return { value: money(v), cls: v < 0 ? 'bad' : '', sub: `${money(s.matBilled)} billed − ${money(s.matCovered)} covered` };
        }) },
      { caption: 'Consumables' + suffix, info: 'Consumables billed plus those the package price covered.',
        cells: each(s => {
          const perCase = s.performed ? s.consumables / s.performed : 0;
          const d = avg.consumablesPerCase ? (perCase - avg.consumablesPerCase) / avg.consumablesPerCase : 0;
          return { value: amt(s, s.consumables),
            sub: avg.consumablesPerCase ? `${pct(d)} ${d >= 0 ? 'above' : 'below'} ${avgLabel}` : '',
            subCls: d > 0.1 ? 'warn' : d < -0.05 ? 'good' : '' };
        }) },
      { caption: 'Medicine billed' + suffix, cells: each(s => ({ value: amt(s, s.medicine) })) },
      { section: 'Quality metrics', caption: 'Average length of stay', big: true,
        cells: each(s => ({
          value: s.encounters ? `${formatDec(String(s.los), 1)} days` : '—',
          cls: s.coveredLos && s.los > s.coveredLos ? 'bad' : '',
          sub: s.coveredLos ? `Package covers ${days(s.coveredLos)}` : avg.los ? `${avgLabel[0].toUpperCase() + avgLabel.slice(1)} ${formatDec(String(avg.los), 1)} days` : '',
        })) },
      { caption: 'Rejected claims', cells: each(s => ({ value: s.rejected ? money(s.rejected) : 'None', cls: s.rejected ? 'bad' : '' })) },
    ];
  });

  /** The bars under Performed, on one scale across the columns. */
  private barMax = computed(() => Math.max(1, ...this.cols().flatMap(c => c?.spread.slice(0, this.maxBars).map(b => b.value) ?? [])));
  bars(c: Stat): { h: number; value: number; label: string }[] {
    const max = this.barMax();
    return c.spread.slice(0, this.maxBars).map(b => ({ ...b, h: b.value ? 4 + Math.round(42 * b.value / max) : 2 }));
  }
}

/** Styles for the page around the grid: the picker card and the tiles. */
export const COMPARE_PAGE_STYLES = `
  .controls { display: flex; gap: 14px 24px; align-items: flex-end; justify-content: space-between; flex-wrap: wrap; }
  .pick { display: flex; flex-direction: column; gap: 6px; flex: 1 1 340px; min-width: 0; }
  .pick select { width: 100%; }
  .lbl { color: var(--text-3); font-size: 12px; font-weight: 600; letter-spacing: .04em; text-transform: uppercase; }
  .tiles { grid-template-columns: repeat(auto-fit, minmax(170px, 1fr)); margin-bottom: 16px; }
  .center { text-align: center; margin-top: 12px; }
  .reloading { opacity: .6; transition: opacity .15s; }
`;

/** Columns from ?<param>=a,b,c, else the first four options. */
export function pickColumns(all: Stat[], raw: string | null): (Stat | null)[] {
  const keys = raw != null ? raw.split(',') : all.slice(0, COLUMNS).map(s => s.key);
  return Array.from({ length: COLUMNS }, (_, i) => all.find(s => s.key === keys[i]) ?? null);
}
