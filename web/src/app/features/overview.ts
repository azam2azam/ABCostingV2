import { Component, computed, effect, inject, input, signal } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { ActivatedRoute, RouterLink } from '@angular/router';
import { DashboardDto, ProfitDto } from '../core/api';
import { Auth } from '../core/auth';
import { RunContext, runInFlight } from '../core/context';
import { runResource } from '../core/resource';
import { BarRow, CHARTS, ChartSeries, DivergingRow, PieSlice, count, countTick, sar } from '../shared/charts';
import { Col, UI } from '../shared/ui';

const num = (v: unknown): number => Number(v ?? 0) || 0;
const REVENUE: ChartSeries = { name: 'Revenue', color: 'var(--chart-revenue)' };
const COST: ChartSeries = { name: 'Total cost', color: 'var(--chart-cost)' };
const MARGIN: ChartSeries = { name: 'Margin', color: 'var(--chart-3)' };
const shortPeriod = (code: string) => code.replace(/^20\d\d\s+/, '');

/** Multi-period Summary trend — OpenAPI types catch up on the next api:types run. */
interface TrendPoint {
  period: string; startDate: string; endDate: string; encounters: number;
  revenue: number; totalCost: number; margin: number; marginPct: number;
}
interface DashboardTrendDto { months: TrendPoint[]; }

/** The Patient Encounters filter behind a Dashboard tab, where its name differs. */
const ENCOUNTER_FILTER: Record<string, string> = { encounterType: 'encType', noBillable: 'noBillableType' };

/** Margin by dimension, computed at read time over the run's encounter costs (FR-5.16). */
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
        @if (dim() === 'resource') { <p class="muted" style="margin:0 0 10px">By the doctor on each service line. An encounter can appear under several resources, so the Encounters column does not add up to the total.</p> }
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
                <h2>{{ (dim() === 'segment' || dim() === 'encounterType') && marginPie().length ? 'Share by ' : 'Margin % by ' }}{{ dimLabel().toLowerCase() }}</h2>
                @if ((dim() === 'segment' || dim() === 'encounterType') && marginPie().length) {
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
  /** IP/OP/ER dashboards restrict profitability to one encounter type. */
  encType = input<string | null>(null);
  /** Hide specific profitability tabs (e.g. ER drops Department and Resources). */
  omitDims = input<string[]>([]);
  canRevenue = computed(() => this.auth.can('revenue.view'));
  dims = computed(() => {
    const scoped = !!this.encType();
    const omit = new Set(this.omitDims());
    const all = [
      ...(scoped ? [] : [{ key: 'encounterType', label: 'Encounter type' }]),
      { key: 'department', label: 'Department' },
      { key: 'resource', label: 'Resources' }, { key: 'physician', label: 'Physician' }, { key: 'insurer', label: 'Insurer' },
      { key: 'segment', label: 'Patient segment' },
      ...(scoped ? [] : [{ key: 'noBillable', label: 'Non-billable' }]),
    ].filter(d => !omit.has(d.key));
    const lock = this.lockedDim();
    return lock ? all.filter(d => d.key === lock) : all;
  });
  dim = signal('encounterType');

  constructor() {
    effect(() => {
      const lock = this.lockedDim();
      if (lock) this.dim.set(lock);
      else if (this.encType()) this.dim.set(this.dims()[0]?.key ?? 'department');
    });
    // ?tab=noBillable opens a tab directly, e.g. from the assistant's links.
    inject(ActivatedRoute).queryParamMap.pipe(takeUntilDestroyed()).subscribe(m => {
      if (this.lockedDim()) return;
      const tab = m.get('tab');
      if (tab && this.dims().some(d => d.key === tab)) this.dim.set(tab);
    });
  }

  res = runResource<ProfitDto>(() => 'profitability', () => ({ dimension: this.dim(), encType: this.encType() }));
  /** Lines invoiced at a price, by the same procedure types, for the comparison on the Non-billable tab. */
  billable = runResource<ProfitDto>(() => this.dim() === 'noBillable' ? 'profitability' : null, () => ({ dimension: 'billable', encType: this.encType() }));
  count = count;
  countTick = countTick;
  compareSeries: ChartSeries[] = [{ name: 'Billable', color: 'var(--chart-billable)' }, { name: 'Non-billable', color: 'var(--chart-nonbillable)' }];
  /** Five slices at most, in a fixed colour order; the small rest fold into Other. */
  pie = computed<PieSlice[]>(() => {
    const colours = ['var(--chart-1)', 'var(--chart-2)', 'var(--chart-3)', 'var(--chart-4)', 'var(--chart-5)', 'var(--chart-6)'];
    const rows = this.rows().filter(r => num(r.total) > 0);
    const main = rows.slice(0, 5), rest = rows.slice(5);
    const enc = (n: number) => `${count(n)} encounter${n === 1 ? '' : 's'}`;
    const slices = main.map((r, i) => ({ key: r.key, label: r.label, value: num(r.total), color: colours[i], note: enc(num(r.volume)) }));
    if (rest.length) slices.push({ key: 'other', label: rest.length === 1 ? rest[0].label : `Other (${rest.map(r => r.label).join(', ')})`,
      value: rest.reduce((t, r) => t + num(r.total), 0), color: colours[5], note: `${rest.map(r => count(num(r.volume))).join(' + ')} encounters` });
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
    const encounters = (r: any) => `${num(r.volume).toLocaleString('en-US')} encounter${num(r.volume) === 1 ? '' : 's'}`;
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
        key: 'volume', label: 'Encounters', type: 'num', dp: 0, link: () => '/patient-encounters', totalLink: '/patient-encounters',
        linkParams: r => ({
          ...(this.encType() ? { encType: this.encType()! } : {}),
          [ENCOUNTER_FILTER[this.dim()] ?? this.dim()]: r.key || 'none',
        }),
        totalLinkParams: (): Record<string, string> | null => {
          if (this.dim() === 'noBillable') return { noBillableType: 'ALL' };
          const enc = this.encType();
          return enc ? { encType: enc } : null;
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
          <p class="muted" style="margin:0 0 10px">By the doctor on each service line. An encounter can appear under several resources, so the Encounters column does not add up to the total.</p>
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
                <h2>{{ (dim() === 'segment' || dim() === 'encounterType') && marginPie().length ? 'Share by ' : 'Margin % by ' }}{{ label().toLowerCase() }}</h2>
                @if ((dim() === 'segment' || dim() === 'encounterType') && marginPie().length) {
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
    const enc = (n: number) => `${count(n)} encounter${n === 1 ? '' : 's'}`;
    const slices = main.map((r, i) => ({ key: r.key, label: r.label, value: num(r.total), color: colours[i], note: enc(num(r.volume)) }));
    if (rest.length) slices.push({ key: 'other', label: rest.length === 1 ? rest[0].label : `Other (${rest.map(r => r.label).join(', ')})`,
      value: rest.reduce((t, r) => t + num(r.total), 0), color: colours[5], note: `${rest.map(r => count(num(r.volume))).join(' + ')} encounters` });
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
    const encounters = (r: any) => `${num(r.volume).toLocaleString('en-US')} encounter${num(r.volume) === 1 ? '' : 's'}`;
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
  { key: 'encounterType', label: 'Encounter type' },
  { key: 'department', label: 'Department' },
  { key: 'resource', label: 'Resources' },
  { key: 'physician', label: 'Physician' },
  { key: 'insurer', label: 'Insurer' },
  { key: 'segment', label: 'Patient segment' },
  { key: 'noBillable', label: 'Non-billable' },
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

/** Summary only — multi-month trend charts live here and must not be reused by IP/OP/ER. */
@Component({
  selector: 'v-dashboard',
  imports: [RouterLink, SummaryProfitCharts, ...CHARTS, ...UI],
  template: `
    <v-page title="Summary" purpose="Direct cost, indirect cost, overheads, revenue and margin for one costed run. Every figure opens the screen that explains it.">
      @if (!calculated()) {
        <p class="notice warn">
          {{ pending() }}
          <a routerLink="/runs" queryParamsHandling="preserve">Go to Calculation Runs</a>
        </p>
      } @else {
        <v-state [loading]="res.isLoading()" [error]="res.error()">
          @if (res.value(); as r) {
            <div class="grid tiles">
              @for (t of r.data.headline; track t.key) {
                <v-tile [label]="t.label" [value]="t.value" [unit]="t.unit" [direction]="t.direction" [note]="t.note"
                  [link]="t.link ? '/' + t.link : null" />
              }
            </div>
            <v-state [loading]="trend.isLoading()" [error]="trend.error()">
              @if (last6Bars().length) {
                <div class="grid two charts" style="margin-top:14px">
                  <section class="card chart">
                    <h2>6 month trend</h2>
                    <p class="faint sub">{{ canRevenue() ? 'Revenue, total cost and margin' : 'Total cost' }} for the last 6 costed periods, including the current one.</p>
                    <v-line [rows]="last6Bars()" [series]="moneySeries()" [format]="sar" />
                  </section>
                  @if (canRevenue()) {
                    <section class="card chart">
                      <h2>Margin share by month</h2>
                      <p class="faint sub">Each month's share of operating margin over the same six periods.</p>
                      @if (marginShare().length) {
                        <v-pie [slices]="marginShare()" caption="Margin share by month" centreLabel="margin" />
                      } @else {
                        <p class="faint">No positive margin in these periods to share.</p>
                      }
                    </section>
                  }
                </div>
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
  canProfitability = computed(() => this.auth.can('profitability.view'));
  canRevenue = computed(() => this.auth.can('revenue.view'));
  sar = sar;
  private months = computed(() => this.trend.value()?.data.months ?? []);
  moneySeries = computed(() => this.canRevenue() ? [REVENUE, COST, MARGIN] : [COST]);
  private toBars = (rows: TrendPoint[]): BarRow[] => rows.map(m => ({
    key: m.period,
    label: shortPeriod(m.period),
    values: this.canRevenue() ? [num(m.revenue), num(m.totalCost), num(m.margin)] : [num(m.totalCost)],
    note: `${num(m.encounters).toLocaleString('en-US')} enc.`,
  }));
  last6Bars = computed(() => this.toBars(this.months().slice(-6)));
  marginShare = computed<PieSlice[]>(() => {
    const colours = ['var(--chart-1)', 'var(--chart-2)', 'var(--chart-3)', 'var(--chart-4)', 'var(--chart-5)', 'var(--chart-6)'];
    return this.months().slice(-6)
      .filter(m => num(m.margin) > 0)
      .map((m, i) => ({
        key: m.period,
        label: shortPeriod(m.period),
        value: num(m.margin),
        color: colours[i % colours.length],
        note: `${num(m.encounters).toLocaleString('en-US')} encounters`,
      }));
  });
}

/** IP / OP / ER only — separate from Summary so that screen can evolve independently. */
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
            <div class="grid tiles">
              @for (t of r.data.headline; track t.key) {
                <v-tile [label]="t.label" [value]="t.value" [unit]="t.unit" [direction]="t.direction" [note]="t.note"
                  [link]="t.link ? '/' + t.link : null"
                  [linkParams]="t.link === 'patient-encounters' ? { encType: encType() } : null" />
              }
            </div>
            @if (canProfitability()) {
              <div style="margin-top:14px"><v-profit-tables [encType]="encType()" [omitDims]="omitDims()" /></div>
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
  encType = input.required<string>();
  omitDims = input<string[]>([]);
  private auth = inject(Auth);
  private ctx = inject(RunContext);
  calculated = computed(() => ['RECONCILED', 'PUBLISHED'].includes(this.ctx.run()?.status ?? ''));
  pending = computed(() => dashboardPending(this.ctx));
  res = runResource<DashboardDto>(() => this.calculated() ? 'dashboard' : null, () => ({ encType: this.encType() }));
  canProfitability = computed(() => this.auth.can('profitability.view'));
}

@Component({
  selector: 'v-ip-dashboard',
  imports: [EncDashboard],
  template: `<v-enc-dashboard title="IP Dashboard" purpose="Inpatient (IP) cost, revenue and margin for one costed run — limited to IP encounters." encType="IP" />`,
})
export class IpDashboard {}

@Component({
  selector: 'v-op-dashboard',
  imports: [EncDashboard],
  template: `<v-enc-dashboard title="OP Dashboard" purpose="Outpatient (OP) cost, revenue and margin for one costed run — OP encounters only (ER has its own dashboard)." encType="OP" />`,
})
export class OpDashboard {}

@Component({
  selector: 'v-er-dashboard',
  imports: [EncDashboard],
  template: `<v-enc-dashboard title="ER Dashboard" purpose="Emergency (ER) cost, revenue and margin for one costed run — limited to ER encounters." encType="ER" [omitDims]="omit" />`,
})
export class ErDashboard {
  omit = ['department', 'resource'];
}

/** Catalogue screen: the Dashboard department tab as its own module. */
@Component({
  selector: 'v-departments',
  imports: [RouterLink, ProfitTables, ...UI],
  template: `
    <v-page title="Departments" purpose="Cost, revenue and margin by department for one costed run — the same figures as the Summary department tab. Encounter counts open Patient Encounters for that department.">
      @if (!calculated()) {
        <p class="notice warn">
          {{ pending() }}
          <a routerLink="/runs" queryParamsHandling="preserve">Go to Calculation Runs</a>
        </p>
      } @else if (!canProfitability()) {
        <p class="notice warn">Department profitability is not included in your role.</p>
      } @else {
        <v-profit-tables lockedDim="department" [hideCharts]="true" />
      }
    </v-page>
  `,
})
export class Departments {
  private auth = inject(Auth);
  private ctx = inject(RunContext);
  calculated = computed(() => ['RECONCILED', 'PUBLISHED'].includes(this.ctx.run()?.status ?? ''));
  canProfitability = computed(() => this.auth.can('profitability.view'));
  pending = computed(() => {
    const code = this.ctx.periodParam();
    const selected = this.ctx.periodsForHospital().find(p => p.code === code);
    const period = selected ? `${selected.code} (${selected.startDate} – ${selected.endDate})` : code ?? 'this period';
    const run = this.ctx.run();
    if (!run) return `The calculation has not been run for ${period}.`;
    if (runInFlight(run.status)) return `The calculation for ${period} is still running.`;
    return run.status === 'FAILED'
      ? `The calculation for ${period} failed, so there are no figures to show.`
      : `The calculation has not been run for ${period}.`;
  });
}
