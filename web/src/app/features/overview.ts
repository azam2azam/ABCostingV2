import { Component, computed, effect, inject, input, signal } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { ActivatedRoute, RouterLink } from '@angular/router';
import { DashboardDto, ProfitDto } from '../core/api';
import { Auth } from '../core/auth';
import { RunContext, runInFlight } from '../core/context';
import { runResource } from '../core/resource';
import { BarRow, CHARTS, ChartSeries, DivergingRow, PieSlice, count, countTick, sar } from '../shared/charts';
import { Col, TilePart, UI } from '../shared/ui';
import { dashboardTileInfo } from '../shared/tile-info';

const num = (v: unknown): number => Number(v ?? 0) || 0;
const REVENUE: ChartSeries = { name: 'Revenue', color: 'var(--chart-revenue)' };
const COST: ChartSeries = { name: 'Total cost', color: 'var(--chart-cost)' };
const MARGIN: ChartSeries = { name: 'Margin', color: 'var(--chart-3)' };
const AREA_IPD: ChartSeries = { name: 'IPD', color: 'var(--chart-1)' };
const AREA_OR: ChartSeries = { name: 'OR', color: 'var(--chart-4)' };
const AREA_OPD: ChartSeries = { name: 'OPD', color: 'var(--chart-2)' };
const AREA_ER: ChartSeries = { name: 'ER', color: 'var(--chart-3)' };
/** Each care area's own dashboard. */
const AREA_SCREEN: Record<string, string> = { IPD: '/ip-dashboard', OR: '/or-dashboard', OPD: '/op-dashboard', ER: '/er-dashboard' };
/** The formula notes moved into each tile's (i) popover; notes that are figures (e.g. "11.1% of revenue") stay. */
const FORMULA_NOTES = new Set(['encounters', 'admissionRequests', 'direct', 'indirect', 'overheads', 'total']);
const tileNote = (t: { key: string; note?: string | null }) => FORMULA_NOTES.has(t.key) ? null : t.note ?? null;
const shortPeriod = (code: string) => code.replace(/^20\d\d\s+/, '');

/** Multi-period Summary trend — OpenAPI types catch up on the next api:types run. */
interface TrendPoint {
  period: string; startDate: string; endDate: string; encounters: number;
  revenue: number; totalCost: number; margin: number; marginPct: number;
  ipdEncounters?: number; orEncounters?: number; opdEncounters?: number; erEncounters?: number;
}
interface DashboardTrendDto { months: TrendPoint[]; }
/** One run split into IPD, OR, OPD and ER; the four add up to the run. */
interface CareAreaRow { key: string; label: string; encounters: number; revenue: number; totalCost: number; margin: number; marginPct: number; revenueShare: number;
  direct: number; indirect: number; overhead: number; admissionRequests?: number; }
interface CareAreaSplitDto { areas: CareAreaRow[]; }

/** The Patient Episodes filter behind a Dashboard tab, where its name differs. */
const ENCOUNTER_FILTER: Record<string, string> = { encounterType: 'encType', noBillable: 'noBillableType' };
const AREA_DIMS = ['segment', 'encounterType', 'careArea'];

/** Margin by dimension, computed at read time over the run's episode costs (FR-5.16). */
@Component({
  selector: 'v-profit-tables',
  imports: [...CHARTS, ...UI],
  template: `
    @if (!lockedDim()) {
      <div class="tabs" role="tablist">
        @for (d of dims(); track d.key) {
          <button role="tab" type="button" [class.active]="dim() === d.key" (click)="dim.set(d.key)">{{ d.label }}</button>
        }
      </div>
    }
    <v-state [loading]="res.isLoading()" [error]="res.error()">
      @if (res.value(); as r) {
        @if (!canRevenue()) { <p class="notice warn">Revenue is masked for your role; margins are not shown.</p> }
        @if (dim() === 'resource') { <p class="muted" style="margin:0 0 10px">By the doctor on each service line. An episode can appear under several resources, so the Episodes column does not add up to the total.</p> }
        @if (dim() === 'noBillable' && !$any(r.data.total).services) { <p class="muted" style="margin:0 0 10px">This run holds no non-billable lines. Run the calculation again to fill this list.</p> }
        @if (!hideCharts() && r.data.rows.length) {
          <div class="grid two charts">
            @if (dim() === 'noBillable') {
              <section class="card chart">
                <h2>Billable vs non-billable services by procedure type</h2>
                <p class="faint sub">Invoice lines by procedure type, with the share invoiced at no price.</p>
                @if (billable.value(); as b) {
                  @if ($any(b.data.total).services) { <v-hbars [rows]="compare(r.data, b.data)" [series]="compareSeries" [format]="count" [tickFormat]="countTick" /> }
                  @else { <p class="faint">This run holds no billable lines by type. Run the calculation again to compare.</p> }
                }
              </section>
              <section class="card chart">
                <h2>Share by procedure type</h2>
                <p class="faint sub">Share of the cost of lines invoiced at no price.</p>
                <v-pie [slices]="pie()" caption="Non-billable cost by procedure type" centreLabel="total cost" />
              </section>
            } @else if (dim() === 'segment') {
              <section class="card chart">
                <h2>{{ (canRevenue() ? 'Revenue and cost' : 'Total cost') + ' by patient segment' }}</h2>
                <p class="faint sub">All {{ bars().length }} segments, largest first.</p>
                <v-columns [rows]="bars()" [series]="barSeries()" />
              </section>
            } @else {
              <section class="card chart">
                <h2>{{ (canRevenue() ? 'Revenue and cost' : 'Total cost') + ' by ' + dimLabel().toLowerCase() }}</h2>
                <p class="faint sub">{{ barNote() }}</p>
                <v-hbars [rows]="bars()" [series]="barSeries()" />
              </section>
            }
            @if (canRevenue() && dim() !== 'noBillable') {
              <section class="card chart">
                <h2>{{ pieDim() && marginPie().length ? 'Share by ' : 'Margin % by ' }}{{ dimLabel().toLowerCase() }}</h2>
                @if (pieDim() && marginPie().length) {
                  <p class="faint sub">Each {{ dimLabel().toLowerCase() }}'s share of the operating margin, with its margin %.</p>
                  <v-pie [slices]="marginPie()" [caption]="'Operating margin by ' + dimLabel().toLowerCase()" centreLabel="margin" />
                } @else {
                  <p class="faint sub">{{ marginNote() }}</p>
                  <v-divbars [rows]="margins().rows" [split]="margins().split" />
                }
              </section>
            }
          </div>
        }
        <v-table [rows]="r.data.rows" [columns]="cols()" [total]="r.data.total" [caption]="'Profitability by ' + dim()" />
      }
    </v-state>
  `,
})
export class ProfitTables {
  private auth = inject(Auth);
  /** When set (e.g. Departments screen), only that dimension is shown and the tab strip is hidden. */
  lockedDim = input<string | null>(null);
  /** Departments catalogue shows the table only. */
  hideCharts = input(false);
  /** IPD/OR/OPD/ER dashboards restrict profitability to one care area. */
  careArea = input<string | null>(null);
  /** Hide specific profitability tabs (e.g. ER drops Department and Resources). */
  omitDims = input<string[]>([]);
  canRevenue = computed(() => this.auth.can('revenue.view'));
  dims = computed(() => {
    const scoped = !!this.careArea();
    const omit = new Set(this.omitDims());
    const all = [
      ...(scoped ? [] : [{ key: 'careArea', label: 'Care area' }]),
      { key: 'department', label: 'Department' },
      { key: 'resource', label: 'Resources' }, { key: 'physician', label: 'Physician' }, { key: 'insurer', label: 'Insurer' },
      { key: 'segment', label: 'Patient segment' },
      ...(scoped ? [] : [{ key: 'noBillable', label: 'Non-billable' }]),
    ].filter(d => !omit.has(d.key));
    const lock = this.lockedDim();
    return lock ? all.filter(d => d.key === lock) : all;
  });
  dim = signal('careArea');
  pieDim = computed(() => AREA_DIMS.includes(this.dim()));

  constructor() {
    effect(() => {
      const lock = this.lockedDim();
      if (lock) this.dim.set(lock);
      else if (this.careArea()) this.dim.set(this.dims()[0]?.key ?? 'department');
    });
    // ?tab=noBillable opens a tab directly, e.g. from the assistant's links.
    inject(ActivatedRoute).queryParamMap.pipe(takeUntilDestroyed()).subscribe(m => {
      if (this.lockedDim()) return;
      const tab = m.get('tab');
      if (tab && this.dims().some(d => d.key === tab)) this.dim.set(tab);
    });
  }

  res = runResource<ProfitDto>(() => 'profitability', () => ({ dimension: this.dim(), careArea: this.careArea() }));
  /** Lines invoiced at a price, by the same procedure types, for the comparison on the Non-billable tab. */
  billable = runResource<ProfitDto>(() => this.dim() === 'noBillable' ? 'profitability' : null, () => ({ dimension: 'billable', careArea: this.careArea() }));
  count = count;
  countTick = countTick;
  compareSeries: ChartSeries[] = [{ name: 'Billable', color: 'var(--chart-billable)' }, { name: 'Non-billable', color: 'var(--chart-nonbillable)' }];
  /** Five slices at most, in a fixed colour order; the small rest fold into Other. */
  pie = computed<PieSlice[]>(() => {
    const colours = ['var(--chart-1)', 'var(--chart-2)', 'var(--chart-3)', 'var(--chart-4)', 'var(--chart-5)', 'var(--chart-6)'];
    const rows = this.rows().filter(r => num(r.total) > 0);
    const main = rows.slice(0, 5), rest = rows.slice(5);
    const enc = (n: number) => `${count(n)} episode${n === 1 ? '' : 's'}`;
    const slices = main.map((r, i) => ({ key: r.key, label: r.label, value: num(r.total), color: colours[i], note: enc(num(r.volume)) }));
    if (rest.length) slices.push({ key: 'other', label: rest.length === 1 ? rest[0].label : `Other (${rest.map(r => r.label).join(', ')})`,
      value: rest.reduce((t, r) => t + num(r.total), 0), color: colours[5], note: `${rest.map(r => count(num(r.volume))).join(' + ')} episodes` });
    return slices;
  });
  /** A pie needs every part above zero: with one at a loss this stays empty and the bars show instead. */
  marginPie = computed<PieSlice[]>(() => {
    const rows = [...this.rows()].sort((a, b) => String(a.label).localeCompare(String(b.label)));
    if (!rows.length || rows.some(r => num(r.operating) <= 0)) return [];
    const colours = ['var(--chart-1)', 'var(--chart-2)', 'var(--chart-3)', 'var(--chart-4)', 'var(--chart-5)', 'var(--chart-6)'];
    return rows.slice(0, 6).map((r, i) => ({ key: r.key || 'none', label: r.label, value: num(r.operating), color: colours[i],
      note: `${num(r.marginPct).toFixed(1)}% margin` }));
  });
  compare(nb: ProfitDto, b: ProfitDto): BarRow[] {
    const billed = new Map((b.rows as any[]).map(r => [r.key, num(r.services)]));
    const free = new Map((nb.rows as any[]).map(r => [r.key, num(r.services)]));
    const keys = [...new Set([...(nb.rows as any[]).map(r => r.key), ...(b.rows as any[]).map(r => r.key)])];
    return keys.map(k => {
      const label = ((nb.rows as any[]).find(r => r.key === k) ?? (b.rows as any[]).find(r => r.key === k)).label;
      const [x, y] = [billed.get(k) ?? 0, free.get(k) ?? 0];
      return { key: k, label, values: [x, y], note: x + y ? `${(y / (x + y) * 100).toFixed(1)}% non-billable` : null };
    }).filter(r => r.values[1] > 0);  // a type with no non-billable lines has nothing to compare
  }
  dimLabel = computed(() => this.dim() === 'noBillable' ? 'Procedure type' : this.dims().find(d => d.key === this.dim())?.label ?? 'Item');
  private rows = computed(() => (this.res.value()?.data.rows ?? []) as any[]);
  private byRevenue = computed(() => this.canRevenue() && this.dim() !== 'noBillable');
  barSeries = computed(() => this.byRevenue() ? [REVENUE, COST] : [COST]);
  /** The ten largest rows, by revenue where it can be shown, else by cost. */
  bars = computed<BarRow[]>(() => {
    const key = this.byRevenue() ? 'revenue' : 'total';
    const top = this.dim() === 'noBillable' ? this.rows() : [...this.rows()].sort((a, b) => num(b[key]) - num(a[key])).slice(0, 10);
    const encounters = (r: any) => `${num(r.volume).toLocaleString('en-US')} episode${num(r.volume) === 1 ? '' : 's'}`;
    return top.map(r => ({ key: r.key || 'none', label: r.label, values: this.byRevenue() ? [num(r.revenue), num(r.total)] : [num(r.total)],
      note: this.dim() === 'noBillable' ? encounters(r) : null }));
  });
  barNote = computed(() => this.dim() === 'noBillable' ? 'Each procedure type invoiced at no price.'
    : this.rows().length > 10 ? `The 10 largest of ${this.rows().length}, by ${this.byRevenue() ? 'revenue' : 'cost'}.` : `All ${this.rows().length}, largest first.`);
  /** Rows with revenue, best margin first; past 20 only the 10 best and 10 worst. */
  margins = computed<{ rows: DivergingRow[]; split: number | null }>(() => {
    const all = this.rows().filter(r => num(r.revenue) > 0).map(r => ({ key: r.key || 'none', label: r.label, value: num(r.marginPct) }))
      .sort((a, b) => b.value - a.value);
    return all.length > 20 ? { rows: [...all.slice(0, 10), ...all.slice(-10)], split: 10 } : { rows: all, split: null };
  });
  marginNote = computed(() => {
    const n = this.rows().filter(r => num(r.revenue) > 0).length;
    const capped = this.margins().rows.some(r => Math.abs(r.value) > 100) ? ' Bars stop at ±100%.' : '';
    return (n > 20 ? `The 10 highest and 10 lowest of ${n} with revenue.` : `All ${n} with revenue, highest first.`) + capped;
  });
  cols = computed<Col[]>(() => {
    const base: Col[] = [
      { key: 'label', label: this.dim() === 'noBillable' ? 'Procedure type' : this.dims().find(d => d.key === this.dim())?.label ?? 'Item', link: r => r.link ? '/' + r.link : null },
      {
        key: 'volume', label: 'Episodes', type: 'num', dp: 0, link: () => '/patient-encounters', totalLink: '/patient-encounters',
        linkParams: r => ({
          ...(this.careArea() ? { careArea: this.careArea()! } : {}),
          [ENCOUNTER_FILTER[this.dim()] ?? this.dim()]: r.key || 'none',
        }),
        totalLinkParams: (): Record<string, string> | null => {
          if (this.dim() === 'noBillable') return { noBillableType: 'ALL' };
          const area = this.careArea();
          return area ? { careArea: area } : null;
        },
      },
      { key: 'direct', label: 'Direct', type: 'money', dp: 0 },
      { key: 'indirect', label: 'Indirect', type: 'money', dp: 0 },
      { key: 'overhead', label: 'Overhead', type: 'money', dp: 0 },
      { key: 'total', label: 'Total cost', type: 'money', dp: 0 },
    ];
    if (this.dim() === 'noBillable') {
      const [type, encounters] = base;
      return [
        type, encounters,
        { key: 'services', label: 'Services', type: 'num', dp: 0 },
        { key: 'total', label: 'Total cost', type: 'money', dp: 0 },
      ];
    }
    if (!this.canRevenue()) return base;
    return [...base,
      { key: 'revenue', label: 'Revenue', type: 'money', dp: 0 },
      { key: 'operating', label: 'Operating margin', type: 'money', dp: 0, dir: 'higher' },
      { key: 'marginPct', label: 'Margin %', type: 'pct', dir: 'higher' },
    ];
  });
}

/** One dimension's charts for Summary — no tabs, no table. */
@Component({
  selector: 'v-profit-dim-charts',
  imports: [...CHARTS, ...UI],
  template: `
    <v-state [loading]="res.isLoading()" [error]="res.error()">
      @if (res.value(); as r) {
        @if (dim() === 'resource') {
          <p class="muted" style="margin:0 0 10px">By the doctor on each service line. An episode can appear under several resources, so the Episodes column does not add up to the total.</p>
        }
        @if (dim() === 'noBillable' && !$any(r.data.total).services) {
          <p class="muted" style="margin:0 0 10px">This run holds no non-billable lines. Run the calculation again to fill this list.</p>
        }
        @if (r.data.rows.length) {
          <div class="grid two charts">
            @if (dim() === 'noBillable') {
              <section class="card chart">
                <h2>Billable vs non-billable services by procedure type</h2>
                <p class="faint sub">Invoice lines by procedure type, with the share invoiced at no price.</p>
                @if (billable.value(); as b) {
                  @if ($any(b.data.total).services) { <v-hbars [rows]="compare(r.data, b.data)" [series]="compareSeries" [format]="count" [tickFormat]="countTick" /> }
                  @else { <p class="faint">This run holds no billable lines by type. Run the calculation again to compare.</p> }
                }
              </section>
              <section class="card chart">
                <h2>Share by procedure type</h2>
                <p class="faint sub">Share of the cost of lines invoiced at no price.</p>
                <v-pie [slices]="pie()" caption="Non-billable cost by procedure type" centreLabel="total cost" />
              </section>
            } @else if (dim() === 'segment') {
              <section class="card chart">
                <h2>{{ (canRevenue() ? 'Revenue and cost' : 'Total cost') + ' by patient segment' }}</h2>
                <p class="faint sub">All {{ bars().length }} segments, largest first.</p>
                <v-columns [rows]="bars()" [series]="barSeries()" />
              </section>
            } @else {
              <section class="card chart">
                <h2>{{ (canRevenue() ? 'Revenue and cost' : 'Total cost') + ' by ' + label().toLowerCase() }}</h2>
                <p class="faint sub">{{ barNote() }}</p>
                <v-hbars [rows]="bars()" [series]="barSeries()" />
              </section>
            }
            @if (canRevenue() && dim() !== 'noBillable') {
              <section class="card chart">
                <h2>{{ pieDim() && marginPie().length ? 'Share by ' : 'Margin % by ' }}{{ label().toLowerCase() }}</h2>
                @if (pieDim() && marginPie().length) {
                  <p class="faint sub">Each {{ label().toLowerCase() }}'s share of the operating margin, with its margin %.</p>
                  <v-pie [slices]="marginPie()" [caption]="'Operating margin by ' + label().toLowerCase()" centreLabel="margin" />
                } @else {
                  <p class="faint sub">{{ marginNote() }}</p>
                  <v-divbars [rows]="margins().rows" [split]="margins().split" />
                }
              </section>
            }
          </div>
        }
      }
    </v-state>
  `,
})
export class ProfitDimCharts {
  private auth = inject(Auth);
  dim = input.required<string>();
  label = input.required<string>();
  pieDim = computed(() => AREA_DIMS.includes(this.dim()));
  canRevenue = computed(() => this.auth.can('revenue.view'));
  res = runResource<ProfitDto>(() => 'profitability', () => ({ dimension: this.dim() }));
  billable = runResource<ProfitDto>(() => this.dim() === 'noBillable' ? 'profitability' : null, () => ({ dimension: 'billable' }));
  count = count;
  countTick = countTick;
  compareSeries: ChartSeries[] = [{ name: 'Billable', color: 'var(--chart-billable)' }, { name: 'Non-billable', color: 'var(--chart-nonbillable)' }];
  private rows = computed(() => (this.res.value()?.data.rows ?? []) as any[]);
  private byRevenue = computed(() => this.canRevenue() && this.dim() !== 'noBillable');
  barSeries = computed(() => this.byRevenue() ? [REVENUE, COST] : [COST]);
  pie = computed<PieSlice[]>(() => {
    const colours = ['var(--chart-1)', 'var(--chart-2)', 'var(--chart-3)', 'var(--chart-4)', 'var(--chart-5)', 'var(--chart-6)'];
    const rows = this.rows().filter(r => num(r.total) > 0);
    const main = rows.slice(0, 5), rest = rows.slice(5);
    const enc = (n: number) => `${count(n)} episode${n === 1 ? '' : 's'}`;
    const slices = main.map((r, i) => ({ key: r.key, label: r.label, value: num(r.total), color: colours[i], note: enc(num(r.volume)) }));
    if (rest.length) slices.push({ key: 'other', label: rest.length === 1 ? rest[0].label : `Other (${rest.map(r => r.label).join(', ')})`,
      value: rest.reduce((t, r) => t + num(r.total), 0), color: colours[5], note: `${rest.map(r => count(num(r.volume))).join(' + ')} episodes` });
    return slices;
  });
  marginPie = computed<PieSlice[]>(() => {
    const rows = [...this.rows()].sort((a, b) => String(a.label).localeCompare(String(b.label)));
    if (!rows.length || rows.some(r => num(r.operating) <= 0)) return [];
    const colours = ['var(--chart-1)', 'var(--chart-2)', 'var(--chart-3)', 'var(--chart-4)', 'var(--chart-5)', 'var(--chart-6)'];
    return rows.slice(0, 6).map((r, i) => ({ key: r.key || 'none', label: r.label, value: num(r.operating), color: colours[i],
      note: `${num(r.marginPct).toFixed(1)}% margin` }));
  });
  compare(nb: ProfitDto, b: ProfitDto): BarRow[] {
    const billed = new Map((b.rows as any[]).map(r => [r.key, num(r.services)]));
    const free = new Map((nb.rows as any[]).map(r => [r.key, num(r.services)]));
    const keys = [...new Set([...(nb.rows as any[]).map(r => r.key), ...(b.rows as any[]).map(r => r.key)])];
    return keys.map(k => {
      const label = ((nb.rows as any[]).find(r => r.key === k) ?? (b.rows as any[]).find(r => r.key === k)).label;
      const [x, y] = [billed.get(k) ?? 0, free.get(k) ?? 0];
      return { key: k, label, values: [x, y], note: x + y ? `${(y / (x + y) * 100).toFixed(1)}% non-billable` : null };
    }).filter(r => r.values[1] > 0);
  }
  bars = computed<BarRow[]>(() => {
    const key = this.byRevenue() ? 'revenue' : 'total';
    const top = this.dim() === 'noBillable' ? this.rows() : [...this.rows()].sort((a, b) => num(b[key]) - num(a[key])).slice(0, 10);
    const encounters = (r: any) => `${num(r.volume).toLocaleString('en-US')} episode${num(r.volume) === 1 ? '' : 's'}`;
    return top.map(r => ({ key: r.key || 'none', label: r.label, values: this.byRevenue() ? [num(r.revenue), num(r.total)] : [num(r.total)],
      note: this.dim() === 'noBillable' ? encounters(r) : null }));
  });
  barNote = computed(() => this.dim() === 'noBillable' ? 'Each procedure type invoiced at no price.'
    : this.rows().length > 10 ? `The 10 largest of ${this.rows().length}, by ${this.byRevenue() ? 'revenue' : 'cost'}.` : `All ${this.rows().length}, largest first.`);
  margins = computed<{ rows: DivergingRow[]; split: number | null }>(() => {
    const all = this.rows().filter(r => num(r.revenue) > 0).map(r => ({ key: r.key || 'none', label: r.label, value: num(r.marginPct) }))
      .sort((a, b) => b.value - a.value);
    return all.length > 20 ? { rows: [...all.slice(0, 10), ...all.slice(-10)], split: 10 } : { rows: all, split: null };
  });
  marginNote = computed(() => {
    const n = this.rows().filter(r => num(r.revenue) > 0).length;
    const capped = this.margins().rows.some(r => Math.abs(r.value) > 100) ? ' Bars stop at ±100%.' : '';
    return (n > 20 ? `The 10 highest and 10 lowest of ${n} with revenue.` : `All ${n} with revenue, highest first.`) + capped;
  });
}

const SUMMARY_PROFIT_DIMS = [
  { key: 'careArea', label: 'Care area' },
  { key: 'segment', label: 'Patient segment' },
  { key: 'noBillable', label: 'Non-billable' },
  { key: 'department', label: 'Department' },
  { key: 'resource', label: 'Resources' },
  { key: 'physician', label: 'Physician' },
  { key: 'insurer', label: 'Insurer' },
];

/** Summary profitability: every former tab as its own chart block, no list tables. */
@Component({
  selector: 'v-summary-profit-charts',
  imports: [ProfitDimCharts, ...UI],
  template: `
    @if (!canRevenue()) { <p class="notice warn">Revenue is masked for your role; margins are not shown.</p> }
    @for (d of dims; track d.key) {
      <section style="margin-top:18px">
        <h2 style="margin:0 0 8px;font-size:16px">{{ d.label }}</h2>
        <v-profit-dim-charts [dim]="d.key" [label]="d.label" />
      </section>
    }
  `,
})
export class SummaryProfitCharts {
  private auth = inject(Auth);
  dims = SUMMARY_PROFIT_DIMS;
  canRevenue = computed(() => this.auth.can('revenue.view'));
}

function dashboardPending(ctx: RunContext): string {
  const code = ctx.periodParam();
  const selected = ctx.periodsForHospital().find(p => p.code === code);
  const period = selected ? `${selected.code} (${selected.startDate} – ${selected.endDate})` : code ?? 'this period';
  const run = ctx.run();
  if (!run) return `The calculation has not been run for ${period}.`;
  if (runInFlight(run.status)) return `The calculation for ${period} is still running.`;
  return run.status === 'FAILED'
    ? `The calculation for ${period} failed, so there are no figures to show.`
    : `The calculation has not been run for ${period}.`;
}

/** Summary only — multi-month trend charts live here and must not be reused by IPD/OR/OPD/ER. */
@Component({
  selector: 'v-dashboard',
  imports: [RouterLink, SummaryProfitCharts, ...CHARTS, ...UI],
  template: `
    <v-page title="Summary" purpose="Direct cost, indirect cost, overheads, revenue and margin for one costed run — each figure opens the screen that explains it.">
      @if (!calculated()) {
        <p class="notice warn">
          {{ pending() }}
          <a routerLink="/runs" queryParamsHandling="preserve">Go to Calculation Runs</a>
        </p>
      } @else {
        <v-state [loading]="res.isLoading()" [error]="res.error()">
          @if (res.value(); as r) {
            <div class="grid tiles four">
              @for (t of r.data.headline; track t.key) {
                <v-tile [label]="t.label" [value]="t.value" [unit]="t.unit" [direction]="t.direction" [note]="tileNote(t)"
                  [link]="t.link ? '/' + t.link : null" [parts]="areaParts()[t.key] ?? null" [info]="info[t.key] ?? null" />
              }
            </div>
            <v-state [loading]="trend.isLoading()" [error]="trend.error()">
              @if (last6Bars().length) {
                <section style="margin-top:18px">
                  <h2 style="margin:0 0 8px;font-size:16px">Trend (period vs 6 months)</h2>
                  <div class="grid two charts">
                    <section class="card chart">
                      <p class="faint sub">{{ canRevenue() ? 'Revenue, total cost and margin' : 'Total cost' }} for the last 6 costed periods, including the current one.</p>
                      <v-line [rows]="last6Bars()" [series]="moneySeries()" [format]="sar" [activeKey]="period()" />
                    </section>
                    <section class="card chart">
                      <p class="faint sub">IPD, OR, OPD and ER episode counts for the last 6 costed periods, including the current one.</p>
                      <v-line [rows]="last6EncBars()" [series]="encSeries" [format]="count" [tickFormat]="countTick" [activeKey]="period()" />
                    </section>
                  </div>
                </section>
              }
            </v-state>
            @if (canProfitability()) {
              <div style="margin-top:14px"><v-summary-profit-charts /></div>
            }
          }
        </v-state>
      }
    </v-page>
  `,
})
export class Dashboard {
  private auth = inject(Auth);
  private ctx = inject(RunContext);
  calculated = computed(() => ['RECONCILED', 'PUBLISHED'].includes(this.ctx.run()?.status ?? ''));
  pending = computed(() => dashboardPending(this.ctx));
  res = runResource<DashboardDto>(() => this.calculated() ? 'dashboard' : null);
  trend = runResource<DashboardTrendDto>(() => this.calculated() ? 'dashboard/trend' : null);
  areas = runResource<CareAreaSplitDto>(() => this.calculated() ? 'dashboard/care-areas' : null);
  info = dashboardTileInfo(null);
  tileNote = tileNote;
  /** Each headline tile split into IPD, OR, OPD and ER; each part opens that care area's dashboard. */
  areaParts = computed<Record<string, TilePart[]>>(() => {
    const areas = this.areas.value()?.data.areas ?? [];
    if (!areas.length) return {};
    const field: Record<string, (x: CareAreaRow) => number> = {
      encounters: x => num(x.encounters), admissionRequests: x => num(x.admissionRequests), direct: x => num(x.direct), indirect: x => num(x.indirect), overheads: x => num(x.overhead),
      revenue: x => num(x.revenue), total: x => num(x.totalCost), margin: x => num(x.margin),
    };
    return Object.fromEntries(Object.entries(field).map(([tile, get]) =>
      [tile, areas.map(x => ({ key: x.key, label: x.key, value: get(x), link: AREA_SCREEN[x.key] }))]));
  });
  canProfitability = computed(() => this.auth.can('profitability.view'));
  canRevenue = computed(() => this.auth.can('revenue.view'));
  period = computed(() => this.ctx.periodParam());
  sar = sar;
  count = count;
  countTick = countTick;
  encSeries = [AREA_IPD, AREA_OR, AREA_OPD, AREA_ER];
  private months = computed(() => this.trend.value()?.data.months ?? []);
  moneySeries = computed(() => this.canRevenue() ? [REVENUE, COST, MARGIN] : [COST]);
  private toBars = (rows: TrendPoint[]): BarRow[] => rows.map(m => ({
    key: m.period,
    label: shortPeriod(m.period),
    values: this.canRevenue() ? [num(m.revenue), num(m.totalCost), num(m.margin)] : [num(m.totalCost)],
    note: `${num(m.encounters).toLocaleString('en-US')} enc.`,
  }));
  last6Bars = computed(() => this.toBars(this.months().slice(-6)));
  last6EncBars = computed<BarRow[]>(() => this.months().slice(-6).map(m => ({
    key: m.period,
    label: shortPeriod(m.period),
    values: [num(m.ipdEncounters), num(m.orEncounters), num(m.opdEncounters), num(m.erEncounters)],
    note: `${num(m.encounters).toLocaleString('en-US')} total`,
  })));
}

/** IPD / OR / OPD / ER only — separate from Summary so that screen can evolve independently. */
@Component({
  selector: 'v-enc-dashboard',
  imports: [RouterLink, ProfitTables, ...UI],
  template: `
    <v-page [title]="title()" [purpose]="purpose()">
      @if (!calculated()) {
        <p class="notice warn">
          {{ pending() }}
          <a routerLink="/runs" queryParamsHandling="preserve">Go to Calculation Runs</a>
        </p>
      } @else {
        <v-state [loading]="res.isLoading()" [error]="res.error()">
          @if (res.value(); as r) {
            <div class="grid tiles four">
              @for (t of r.data.headline; track t.key) {
                <v-tile [label]="t.label" [value]="t.value" [unit]="t.unit" [direction]="t.direction" [note]="tileNote(t)"
                  [link]="t.link ? '/' + t.link : null" [info]="info()[t.key] ?? null"
                  [linkParams]="t.link === 'patient-encounters' ? { careArea: careArea() } : null" />
              }
            </div>
            @if (canProfitability()) {
              <div style="margin-top:14px"><v-profit-tables [careArea]="careArea()" [omitDims]="omitDims()" /></div>
            }
          }
        </v-state>
      }
    </v-page>
  `,
})
export class EncDashboard {
  title = input.required<string>();
  purpose = input.required<string>();
  careArea = input.required<string>();
  omitDims = input<string[]>([]);
  private auth = inject(Auth);
  private ctx = inject(RunContext);
  calculated = computed(() => ['RECONCILED', 'PUBLISHED'].includes(this.ctx.run()?.status ?? ''));
  pending = computed(() => dashboardPending(this.ctx));
  res = runResource<DashboardDto>(() => this.calculated() ? 'dashboard' : null, () => ({ careArea: this.careArea() }));
  info = computed(() => dashboardTileInfo(this.careArea()));
  tileNote = tileNote;
  canProfitability = computed(() => this.auth.can('profitability.view'));
}

@Component({
  selector: 'v-ip-dashboard',
  imports: [EncDashboard],
  template: `<v-enc-dashboard title="IPD Dashboard" purpose="Inpatient (IPD) cost, revenue and margin for one costed run — admissions without an operation (OR has its own dashboard)." careArea="IPD" />`,
})
export class IpDashboard {}

@Component({
  selector: 'v-or-dashboard',
  imports: [EncDashboard],
  template: `<v-enc-dashboard title="OR Dashboard" purpose="Operating room (OR) cost, revenue and margin for one costed run — admissions with a theatre booking that went ahead." careArea="OR" />`,
})
export class OrDashboard {}

@Component({
  selector: 'v-op-dashboard',
  imports: [EncDashboard],
  template: `<v-enc-dashboard title="OPD Dashboard" purpose="Outpatient (OPD) cost, revenue and margin for one costed run — OP episodes only (ER has its own dashboard)." careArea="OPD" />`,
})
export class OpDashboard {}

@Component({
  selector: 'v-er-dashboard',
  imports: [EncDashboard],
  template: `<v-enc-dashboard title="ER Dashboard" purpose="Emergency (ER) cost, revenue and margin for one costed run — limited to ER episodes." careArea="ER" [omitDims]="omit" />`,
})
export class ErDashboard {
  omit = ['department', 'resource'];
}

