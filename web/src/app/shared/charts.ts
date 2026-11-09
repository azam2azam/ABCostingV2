import { Component, ElementRef, computed, inject, input, signal } from '@angular/core';
import { RouterLink } from '@angular/router';
import { formatDec, formatMoney } from '../core/decimal';

/*
 * Small dashboard charts drawn in plain HTML and SVG (no chart library). Colours come from the
 * --chart-* tokens in styles.scss, validated for colour-blind separation on both themes. Every
 * value a chart shows on hover is also in the table or tiles beside it.
 */

export interface ChartSeries { name: string; color: string; }
export interface BarRow { key: string; label: string; values: (number | null)[]; link?: string | null; params?: Record<string, string> | null;
  /** A count in another unit, printed beside the bar rather than drawn on its scale. */
  note?: string | null; }

export const sar = (v: number | null | undefined): string => v === null || v === undefined ? '—' : formatMoney(String(v), { dp: 0 });
export const sarShort = (v: number): string => formatMoney(String(v), { compact: true });
/** An axis tick: compact and without trailing zeros, e.g. 2.5M, 10M, 750K. */
const tickLabel = (v: number): string => v === 0 ? '0' : sarShort(v).replace(/^SAR /, '').replace(/\.0+(?=[KMB])/, '').replace(/(\.\d*?)0+(?=[KMB])/, '$1');
export const pct = (v: number): string => `${formatDec(String(v), 1)}%`;

/** Round axis ticks: 0, step, 2·step… covering max, with a 1/2/2.5/5 × 10ⁿ step. */
export function ticks(max: number, count = 4): number[] {
  if (!(max > 0)) return [0];
  const raw = max / count;
  const power = Math.pow(10, Math.floor(Math.log10(raw)));
  const step = [1, 2, 2.5, 5, 10].map(m => m * power).find(s => s >= raw) ?? raw;
  const out: number[] = [];
  for (let v = 0; v < max + step * 0.001; v += step) out.push(Math.round(v * 1e6) / 1e6);
  if (out[out.length - 1] < max) out.push(out[out.length - 1] + step);
  return out;
}

interface Tip { x: number; y: number; title: string; rows: { name: string; value: string; color: string }[]; }

/** The one tooltip a chart uses: value first, series name after, keyed by a short line of its colour. */
@Component({
  selector: 'v-chart-tip',
  template: `
    @if (tip(); as t) {
      <div class="c-tip" role="status" [style.left.px]="t.x" [style.top.px]="t.y">
        <div class="c-tip-title">{{ t.title }}</div>
        @for (r of t.rows; track r.name) {
          <div class="c-tip-row"><span class="c-key" [style.background]="r.color"></span><strong>{{ r.value }}</strong><span class="c-name">{{ r.name }}</span></div>
        }
      </div>
    }
  `,
  styles: `.c-tip { position: absolute; z-index: 5; pointer-events: none; transform: translate(-50%, calc(-100% - 10px)); background: var(--surface-solid);
                  border: 1px solid var(--border-strong); border-radius: 8px; box-shadow: var(--shadow); padding: 8px 10px; font-size: 12.5px; white-space: nowrap; }
           .c-tip-title { color: var(--text-2); margin-bottom: 4px; }
           .c-tip-row { display: flex; align-items: center; gap: 6px; }
           .c-tip-row strong { color: var(--text); font-variant-numeric: tabular-nums; }
           .c-name { color: var(--text-2); }
           .c-key { width: 10px; height: 2px; border-radius: 1px; }`,
})
export class ChartTip { tip = input<Tip | null>(null); }

/** Where a tooltip goes: above the pointer, or above the focused element for the keyboard. */
function tipAt(host: HTMLElement, e: Event): { x: number; y: number } {
  const box = host.getBoundingClientRect();
  if (e instanceof PointerEvent) return { x: e.clientX - box.left, y: e.clientY - box.top };
  const r = (e.target as HTMLElement).getBoundingClientRect();
  return { x: r.left + r.width / 2 - box.left, y: r.top - box.top };
}

@Component({
  selector: 'v-chart-legend',
  template: `<ul class="c-legend">@for (s of series(); track s.name) { <li><span class="c-sw" [style.background]="s.color"></span>{{ s.name }}</li> }</ul>`,
  styles: `.c-legend { display: flex; flex-wrap: wrap; gap: 4px 14px; list-style: none; margin: 0 0 10px; padding: 0; font-size: 12.5px; color: var(--text-2); }
           li { display: flex; align-items: center; gap: 6px; }
           .c-sw { width: 10px; height: 10px; border-radius: 3px; }`,
})
export class ChartLegend { series = input.required<ChartSeries[]>(); }

/** Horizontal bars, one or more series per row, all on one scale from zero. */
@Component({
  selector: 'v-hbars',
  imports: [ChartTip, ChartLegend],
  template: `
    @if (series().length > 1) { <v-chart-legend [series]="series()" /> }
    @if (!rows().length) { <p class="faint">Nothing to show.</p> }
    @else {
      <div class="c-plot" [class.c-with-notes]="notes()">
        @for (r of rows(); track r.key) {
          <div class="c-row" tabindex="0" [attr.aria-label]="describe(r)"
               (pointermove)="show($event, r)" (pointerleave)="tip.set(null)" (focus)="show($event, r)" (blur)="tip.set(null)">
            <div class="c-label" [title]="r.label">{{ r.label }}</div>
            <div class="c-track">
              @for (t of grid(); track $index) { <span class="c-grid" [style.left.%]="t / scale() * 100"></span> }
              @for (v of r.values; track $index) {
                <span class="c-bar" [style.width.%]="width(v)" [style.background]="series()[$index].color"></span>
              }
            </div>
            @if (notes()) { <div class="c-note">{{ r.note }}</div> }
          </div>
        }
        <div class="c-row c-axis" aria-hidden="true">
          <div></div>
          <div class="c-track">@for (t of grid(); track $index) { <span class="c-tick" [style.left.%]="t / scale() * 100">{{ tickFormat()(t) }}</span> }</div>
          @if (notes()) { <div></div> }
        </div>
      </div>
      <v-chart-tip [tip]="tip()" />
    }
  `,
  styles: `:host { display: block; position: relative; }
           .c-plot { display: grid; gap: 6px; }
           .c-row { display: grid; grid-template-columns: minmax(90px, 32%) 1fr; gap: 10px; align-items: center; border-radius: 6px; outline: none; }
           .c-with-notes .c-row { grid-template-columns: minmax(90px, 28%) 1fr auto; }
           .c-note { font-size: 12px; color: var(--text-2); font-variant-numeric: tabular-nums; white-space: nowrap; min-width: 112px; }
           .c-row:hover .c-bar, .c-row:focus-visible .c-bar { filter: brightness(1.12); }
           .c-row:focus-visible { box-shadow: 0 0 0 2px var(--focus); }
           .c-label { font-size: 12.5px; color: var(--text-2); overflow: hidden; text-overflow: ellipsis; white-space: nowrap; text-align: right; }
           .c-track { position: relative; display: flex; flex-direction: column; gap: 2px; padding: 3px 0; }
           .c-grid { position: absolute; top: 0; bottom: 0; width: 1px; background: var(--border); }
           .c-bar { position: relative; display: block; height: 10px; min-width: 0; border-radius: 0 4px 4px 0; }
           .c-axis .c-track { height: 16px; padding: 0; }
           .c-tick { position: absolute; transform: translateX(-50%); font-size: 11px; color: var(--text-3); font-variant-numeric: tabular-nums; white-space: nowrap; }`,
})
export class HBars {
  private host = inject(ElementRef<HTMLElement>);
  rows = input.required<BarRow[]>();
  series = input.required<ChartSeries[]>();
  format = input<(v: number) => string>(sar);
  tickFormat = input<(v: number) => string>(tickLabel);
  tip = signal<Tip | null>(null);
  notes = computed(() => this.rows().some(r => !!r.note));
  grid = computed(() => ticks(Math.max(0, ...this.rows().flatMap(r => r.values.map(v => v ?? 0)))));
  scale = computed(() => this.grid()[this.grid().length - 1] || 1);
  width(v: number | null): number { return Math.max(0, (v ?? 0) / this.scale() * 100); }
  describe(r: BarRow): string { return `${r.label}: ` + this.series().map((s, i) => `${s.name} ${this.format()(r.values[i] ?? 0)}`).join(', ') + (r.note ? `, ${r.note}` : ''); }
  show(e: Event, r: BarRow): void {
    this.tip.set({ ...tipAt(this.host.nativeElement, e), title: r.note ? `${r.label} · ${r.note}` : r.label,
      rows: this.series().map((s, i) => ({ name: s.name, value: this.format()(r.values[i] ?? 0), color: s.color })) });
  }
}

export interface DivergingRow { key: string; label: string; value: number; }

/** Bars either side of zero: good to the right, bad to the left, the value at each tip. */
@Component({
  selector: 'v-divbars',
  imports: [ChartTip],
  template: `
    @if (!rows().length) { <p class="faint">Nothing to show.</p> }
    @else {
      <div class="c-plot">
        @for (r of rows(); track r.key; let i = $index) {
          @if (i === split()) { <div class="c-gap" aria-hidden="true">…</div> }
          <div class="c-row" tabindex="0" [attr.aria-label]="r.label + ': ' + format()(r.value)"
               (pointermove)="show($event, r)" (pointerleave)="tip.set(null)" (focus)="show($event, r)" (blur)="tip.set(null)">
            <div class="c-label" [title]="r.label">{{ r.label }}</div>
            <div class="c-track">
              <span class="c-zero" [style.left.%]="zero()"></span>
              <span class="c-bar" [class.c-neg]="r.value < 0" [class.c-capped]="capped(r.value)"
                    [style.left.%]="r.value < 0 ? zero() - width(r.value) : zero()" [style.width.%]="width(r.value)"></span>
              <span class="c-value" [class.c-neg]="r.value < 0"
                    [style.left.%]="r.value < 0 ? zero() - width(r.value) : zero() + width(r.value)">{{ format()(r.value) }}</span>
            </div>
          </div>
        }
      </div>
      <v-chart-tip [tip]="tip()" />
    }
  `,
  styles: `:host { display: block; position: relative; }
           .c-plot { display: grid; gap: 6px; }
           .c-row { display: grid; grid-template-columns: minmax(90px, 32%) 1fr; gap: 10px; align-items: center; border-radius: 6px; outline: none; }
           .c-row:hover .c-bar, .c-row:focus-visible .c-bar { filter: brightness(1.12); }
           .c-row:focus-visible { box-shadow: 0 0 0 2px var(--focus); }
           .c-label { font-size: 12.5px; color: var(--text-2); overflow: hidden; text-overflow: ellipsis; white-space: nowrap; text-align: right; }
           .c-track { position: relative; height: 18px; margin: 0 64px; }
           .c-zero { position: absolute; top: -3px; bottom: -3px; width: 1px; background: var(--border-strong); }
           .c-bar { position: absolute; top: 4px; height: 10px; background: var(--chart-good); border-radius: 0 4px 4px 0; }
           .c-bar.c-neg { background: var(--chart-bad); border-radius: 4px 0 0 4px; }
           .c-bar.c-capped { border-radius: 0; }
           .c-value { position: absolute; top: 1px; padding-left: 6px; font-size: 11.5px; color: var(--text-2); font-variant-numeric: tabular-nums; white-space: nowrap; }
           .c-value.c-neg { transform: translateX(-100%); padding: 0 6px 0 0; }
           .c-gap { text-align: center; color: var(--text-3); font-size: 12px; line-height: 1; }`,
})
export class DivBars {
  private host = inject(ElementRef<HTMLElement>);
  rows = input.required<DivergingRow[]>();
  /** Where the top rows end and the bottom rows start, when only the extremes are shown. */
  split = input<number | null>(null);
  format = input<(v: number) => string>(pct);
  /** Bars stop at ±limit so one extreme loss cannot flatten the rest; the label still shows the real value. */
  limit = input(100);
  tip = signal<Tip | null>(null);
  private clamp(v: number): number { return Math.max(-this.limit(), Math.min(this.limit(), v)); }
  private lo = computed(() => Math.min(0, ...this.rows().map(r => this.clamp(r.value))));
  private hi = computed(() => Math.max(0, ...this.rows().map(r => this.clamp(r.value))));
  zero = computed(() => this.hi() === this.lo() ? 0 : -this.lo() / (this.hi() - this.lo()) * 100);
  width(v: number): number { const span = this.hi() - this.lo(); return span ? Math.abs(this.clamp(v)) / span * 100 : 0; }
  capped(v: number): boolean { return Math.abs(v) > this.limit(); }
  show(e: Event, r: DivergingRow): void {
    this.tip.set({ ...tipAt(this.host.nativeElement, e), title: r.label, rows: [{ name: 'Margin', value: this.format()(r.value), color: r.value < 0 ? 'var(--chart-bad)' : 'var(--chart-good)' }] });
  }
}

export interface PieSlice { key: string; label: string; value: number; color: string; note?: string | null; }

/** Parts of one whole as a ring, with a 2px gap between slices. The legend beside it carries every value. */
@Component({
  selector: 'v-pie',
  imports: [ChartTip],
  template: `
    @if (!total()) { <p class="faint">Nothing to show.</p> }
    @else {
      <div class="c-pie" [class.c-stacked]="inner()">
        <svg viewBox="-110 -110 220 220" width="200" height="200" role="img" [attr.aria-label]="caption()">
          @for (a of arcs(); track a.ring + a.slice.key) {
            <path class="c-slice" [attr.d]="a.d" [attr.fill]="a.slice.color" tabindex="0" [attr.aria-label]="a.name + ', ' + describe(a.slice, a.total)"
                  (pointermove)="show($event, a.slice, a.name, a.total)" (pointerleave)="tip.set(null)" (focus)="show($event, a.slice, a.name, a.total)" (blur)="tip.set(null)" />
          }
          @if (!inner()) {
            <text class="c-centre" y="-4" text-anchor="middle">{{ centre() }}</text>
            <text class="c-centre-sub" y="14" text-anchor="middle">{{ centreLabel() }}</text>
          }
        </svg>
        <ul class="c-legend" [class.c-two]="inner()">
          @if (inner()) {
            <li class="c-head"><span></span><span></span><span>{{ outerName() }}<br>outer ring</span><span>{{ innerName() }}<br>inner ring</span></li>
            @for (p of slices(); track p.key) {
              <li><span class="c-sw" [style.background]="p.color"></span>
                <span class="c-name">{{ p.label }}</span>
                <span class="c-pair"><strong>{{ short(p.value) }}</strong> <span class="c-share">{{ share(p) }}</span></span>
                <span class="c-pair"><strong>{{ short(innerOf(p).value) }}</strong> <span class="c-share">{{ share(innerOf(p), innerTotal()) }}</span></span>
                @if (p.note) { <span class="c-note">{{ p.note }}</span> }
              </li>
            }
          } @else {
          @for (p of slices(); track p.key) {
            <li><span class="c-sw" [style.background]="p.color"></span>
              <span class="c-name">{{ p.label }}</span>
              <strong>{{ format()(p.value) }}</strong>
              <span class="c-share">{{ share(p) }}</span>
              @if (p.note) { <span class="c-note">{{ p.note }}</span> }
            </li>
          }
          }
        </ul>
      </div>
      <v-chart-tip [tip]="tip()" />
    }
  `,
  styles: `:host { display: block; position: relative; }
           .c-pie { display: flex; flex-wrap: wrap; align-items: center; gap: 16px 24px; }
           svg { flex: none; }
           .c-slice { stroke: var(--surface-solid); stroke-width: 2; outline: none; }
           .c-slice:hover, .c-slice:focus-visible { filter: brightness(1.12); }
           .c-slice:focus-visible { stroke: var(--focus); }
           .c-centre { fill: var(--text); font-size: 18px; font-weight: 700; }
           .c-centre-sub { fill: var(--text-3); font-size: 11px; }
           .c-legend { list-style: none; margin: 0; padding: 0; display: grid; gap: 6px; flex: 1; min-width: 220px; font-size: 12.5px; }
           .c-legend li { display: grid; grid-template-columns: 10px minmax(70px, 1fr) auto 48px; align-items: center; gap: 2px 8px; }
           .c-sw { width: 10px; height: 10px; border-radius: 3px; }
           .c-name { color: var(--text-2); }
           .c-legend strong { color: var(--text); font-variant-numeric: tabular-nums; text-align: right; }
           .c-share { color: var(--text-3); font-variant-numeric: tabular-nums; text-align: right; }
           .c-note { grid-column: 2 / -1; color: var(--text-3); font-size: 11.5px; margin-top: -3px; }
           .c-stacked { flex-direction: column; align-items: stretch; }
           .c-stacked svg { align-self: center; }
           .c-stacked .c-legend { min-width: 0; }
           .c-two li { grid-template-columns: 10px minmax(70px, 1fr) auto auto; gap: 2px 14px; }
           .c-head { color: var(--text-3); font-size: 11.5px; }
           .c-head span:nth-child(n+3), .c-pair { text-align: right; }
           .c-pair { white-space: nowrap; }
           .c-pair strong { color: var(--text); font-variant-numeric: tabular-nums; }
           .c-pair .c-share { margin-left: 4px; }`,
})
export class Pie {
  private host = inject(ElementRef<HTMLElement>);
  slices = input.required<PieSlice[]>();
  caption = input('Share');
  format = input<(v: number) => string>(sar);
  centreLabel = input('total');
  /** A second measure of the same parts, drawn as an inner ring with the same colours. */
  inner = input<PieSlice[] | null>(null);
  outerName = input('Outer');
  innerName = input('Inner');
  tip = signal<Tip | null>(null);
  total = computed(() => this.slices().reduce((t, p) => t + Math.max(0, p.value), 0));
  innerTotal = computed(() => (this.inner() ?? []).reduce((t, p) => t + Math.max(0, p.value), 0));
  short(v: number): string { return sarShort(v); }
  innerOf(p: PieSlice): PieSlice { return this.inner()?.find(x => x.key === p.key) ?? { ...p, value: 0 }; }
  centre = computed(() => tickLabel(this.total()));
  share(p: PieSlice, of = this.total()): string { return pct(of ? p.value / of * 100 : 0); }
  arcs = computed(() => {
    const rings = this.inner()
      ? [{ ring: 'o', name: this.outerName(), slices: this.slices(), total: this.total(), r: 100, r0: 74 },
         { ring: 'i', name: this.innerName(), slices: this.inner()!, total: this.innerTotal(), r: 68, r0: 42 }]
      : [{ ring: 'o', name: '', slices: this.slices(), total: this.total(), r: 100, r0: 62 }];
    return rings.flatMap(g => {
      let start = -Math.PI / 2;
      return g.slices.filter(p => p.value > 0).map(p => {
        const sweep = p.value / g.total * Math.PI * 2;
        const d = ring(start, start + sweep, g.r, g.r0);
        start += sweep;
        return { ring: g.ring, name: g.name, total: g.total, slice: p, d };
      });
    });
  });
  describe(p: PieSlice, of = this.total()): string { return `${p.label}: ${this.format()(p.value)}, ${this.share(p, of)}${p.note ? ', ' + p.note : ''}`; }
  show(e: Event, p: PieSlice, ringName = '', of = this.total()): void {
    this.tip.set({ ...tipAt(this.host.nativeElement, e), title: `${p.label} · ${this.share(p, of)}${ringName ? ' of ' + ringName.toLowerCase() : ''}`,
      rows: [{ name: ringName || p.note || '', value: this.format()(p.value), color: p.color }] });
  }
}

/** One ring slice from angle a to b (radians), outer radius r, inner radius r0. */
function ring(a: number, b: number, r: number, r0: number): string {
  if (b - a >= Math.PI * 2 - 1e-6) b = a + Math.PI * 2 - 1e-4;
  const p = (ang: number, rad: number) => `${(Math.cos(ang) * rad).toFixed(2)},${(Math.sin(ang) * rad).toFixed(2)}`;
  const large = b - a > Math.PI ? 1 : 0;
  return `M${p(a, r)}A${r},${r} 0 ${large} 1 ${p(b, r)}L${p(b, r0)}A${r0},${r0} 0 ${large} 0 ${p(a, r0)}Z`;
}

export const count = (v: number): string => Math.round(v).toLocaleString('en-US');
/** Count axis ticks: 0, 500, 1K, 1.5K… */
export const countTick = (v: number): string => v >= 1000 ? `${formatDec(String(v / 1000), 1).replace(/\.0$/, '')}K` : String(v);

/**
 * Vertical columns, one or more series side by side per category. Values below zero hang from the zero
 * line; with signColors each column takes good or bad by its sign instead of its series colour. With
 * limit, columns stop at ±limit so one extreme cannot flatten the rest (the tooltip keeps the real value).
 */
@Component({
  selector: 'v-columns',
  imports: [ChartTip, ChartLegend],
  template: `
    @if (series().length > 1) { <v-chart-legend [series]="series()" /> }
    @if (!rows().length) { <p class="faint">Nothing to show.</p> }
    @else {
      <div class="c-cols" [class.c-many]="many()">
        <div class="c-y" aria-hidden="true">
          @for (t of grid(); track $index) { <span class="c-ytick" [style.bottom.%]="pos(t)">{{ tickFormat()(t) }}</span> }
        </div>
        <div class="c-area">
          @for (t of grid(); track $index) { <span class="c-hline" [class.c-zero]="t === 0" [style.bottom.%]="pos(t)"></span> }
          @for (r of rows(); track r.key; let i = $index) {
            @if (i === split()) { <span class="c-split" aria-hidden="true">…</span> }
            <div class="c-group" tabindex="0" [attr.aria-label]="describe(r)"
                 (pointermove)="show($event, r)" (pointerleave)="tip.set(null)" (focus)="show($event, r)" (blur)="tip.set(null)">
              @for (v of r.values; track $index) {
                <span class="c-slot">
                  <span class="c-col" [class.c-neg]="(v ?? 0) < 0"
                        [style.bottom.%]="(v ?? 0) < 0 ? null : zero()" [style.top.%]="(v ?? 0) < 0 ? 100 - zero() : null"
                        [style.height.%]="size(v)" [style.background]="colour(v, $index)"></span>
                </span>
              }
            </div>
          }
        </div>
        <div></div>
        <div class="c-x">
          @for (r of rows(); track r.key; let i = $index) {
            @if (i === split()) { <span class="c-split"></span> }
            <span class="c-xl" [title]="r.label"><span class="c-xt">{{ r.label }}</span>@if (r.note) { <span class="c-xn">{{ r.note }}</span> }</span>
          }
        </div>
      </div>
      <v-chart-tip [tip]="tip()" />
    }
  `,
  styles: `:host { display: block; position: relative; }
           .c-cols { display: grid; grid-template-columns: 44px 1fr; grid-template-rows: 200px auto; column-gap: 8px; }
           .c-y { position: relative; }
           .c-ytick { position: absolute; right: 0; transform: translateY(50%); font-size: 11px; color: var(--text-3); font-variant-numeric: tabular-nums; white-space: nowrap; }
           .c-area { position: relative; display: flex; align-items: stretch; }
           .c-hline { position: absolute; left: 0; right: 0; height: 1px; background: var(--border); }
           .c-hline.c-zero { background: var(--border-strong); }
           .c-group { position: relative; flex: 1 1 0; min-width: 0; display: flex; justify-content: center; gap: 2px; border-radius: 6px; outline: none; }
           .c-group:hover .c-col, .c-group:focus-visible .c-col { filter: brightness(1.12); }
           .c-group:focus-visible { box-shadow: 0 0 0 2px var(--focus); }
           .c-slot { position: relative; width: 24px; max-width: 40%; }
           .c-many .c-slot { max-width: 70%; }
           .c-col { position: absolute; left: 0; right: 0; border-radius: 4px 4px 0 0; }
           .c-col.c-neg { border-radius: 0 0 4px 4px; }
           .c-split { flex: 0 0 14px; align-self: center; text-align: center; color: var(--text-3); font-size: 12px; }
           .c-x { display: flex; padding-top: 6px; }
           .c-xl { flex: 1 1 0; min-width: 0; display: flex; flex-direction: column; align-items: center; padding: 0 2px; }
           .c-xt { max-width: 100%; font-size: 12px; color: var(--text-2); overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
           .c-xn { font-size: 11px; color: var(--text-3); white-space: nowrap; }
           .c-many .c-x { height: 86px; }
           .c-many .c-xn { display: none; }
           .c-many .c-xl { position: relative; }
           .c-many .c-xt { position: absolute; top: 0; right: 50%; max-width: 110px; transform-origin: right top; transform: rotate(-50deg); font-size: 11px; }`,
})
export class Columns {
  private host = inject(ElementRef<HTMLElement>);
  rows = input.required<BarRow[]>();
  series = input.required<ChartSeries[]>();
  format = input<(v: number) => string>(sar);
  tickFormat = input<(v: number) => string>(tickLabel);
  /** Colour each column by its sign: good above zero, bad below. */
  signColors = input(false);
  limit = input<number | null>(null);
  /** Where the top rows end and the bottom rows start, when only the extremes are shown. */
  split = input<number | null>(null);
  tip = signal<Tip | null>(null);
  many = computed(() => this.rows().length > 6);
  private clamp(v: number): number { const l = this.limit(); return l === null ? v : Math.max(-l, Math.min(l, v)); }
  private values = computed(() => this.rows().flatMap(r => r.values.map(v => this.clamp(v ?? 0))));
  private up = computed(() => ticks(Math.max(0, ...this.values())));
  private down = computed(() => Math.min(0, ...this.values()) < 0 ? ticks(-Math.min(0, ...this.values())).map(t => -t).filter(t => t !== 0).reverse() : []);
  grid = computed(() => [...this.down(), ...this.up()]);
  private hi = computed(() => this.up()[this.up().length - 1] || 1);
  private lo = computed(() => this.down()[0] ?? 0);
  pos(v: number): number { return (v - this.lo()) / (this.hi() - this.lo()) * 100; }
  zero = computed(() => this.pos(0));
  size(v: number | null): number { return Math.abs(this.clamp(v ?? 0)) / (this.hi() - this.lo()) * 100; }
  colour(v: number | null, i: number): string {
    return this.signColors() ? ((v ?? 0) < 0 ? 'var(--chart-bad)' : 'var(--chart-good)') : this.series()[i].color;
  }
  describe(r: BarRow): string { return `${r.label}: ` + this.series().map((s, i) => `${s.name} ${this.format()(r.values[i] ?? 0)}`).join(', ') + (r.note ? `, ${r.note}` : ''); }
  show(e: Event, r: BarRow): void {
    this.tip.set({ ...tipAt(this.host.nativeElement, e), title: r.note ? `${r.label} · ${r.note}` : r.label,
      rows: this.series().map((s, i) => ({ name: s.name, value: this.format()(r.values[i] ?? 0), color: this.colour(r.values[i], i) })) });
  }
}

export const CHARTS = [HBars, DivBars, Pie, Columns] as const;
