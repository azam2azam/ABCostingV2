import { Component, computed, inject, input, signal } from '@angular/core';
import { RouterLink } from '@angular/router';
import { CapacityDto, DashboardDto, ProfitDto, VarianceDetail as VarianceDetailDto, VarianceRow } from '../core/api';
import { Auth } from '../core/auth';
import { runResource } from '../core/resource';
import { Col, UI } from '../shared/ui';

type Bar = DashboardDto['serviceLines'][number];

@Component({
  selector: 'v-bars',
  imports: [RouterLink, ...UI],
  template: `
    <table class="data bars">
      <tbody>
        @for (b of bars(); track b.key) {
          <tr>
            <td>@if (b.link) { <a [routerLink]="'/' + linkPath(b.link)" [queryParams]="linkQuery(b.link)" queryParamsHandling="merge">{{ b.label }}</a> } @else { {{ b.label }} }
              @if (b.note) { <span class="expr">{{ b.note }}</span> }</td>
            <td class="num" [class]="'num ' + (b.value | dir: direction())">{{ unit() === '%' ? (b.value | pct) : (b.value | money:0) }}</td>
            <td class="barcell"><v-bar [share]="b.share" /></td>
          </tr>
        } @empty { <tr><td class="faint">Nothing to show for this run.</td></tr> }
      </tbody>
    </table>
  `,
  styles: `.bars td { border: 0; padding: 4px 6px; } .barcell { width: 35%; }`,
})
export class Bars {
  bars = input<Bar[]>([]);
  direction = input<'higher' | 'lower' | 'neutral'>('neutral');
  unit = input<'SAR' | '%'>('SAR');
  linkPath(l: string) { return l.split('?')[0]; }
  linkQuery(l: string) { const q = l.split('?')[1]; return q ? Object.fromEntries(new URLSearchParams(q)) : {}; }
}

/** FR-11.5: five audience views over one run. */
@Component({
  selector: 'v-dashboard',
  imports: [Bars, ...UI],
  template: `
    <v-page title="Dashboard" purpose="One costed run, seen five ways. Every figure opens the screen that explains it.">
      <v-state [loading]="res.isLoading()" [error]="res.error()">
        @if (res.value(); as r) {
          <div class="tabs" role="tablist">
            @for (v of views; track v.key) {
              <button role="tab" type="button" [class.active]="view() === v.key" [attr.aria-selected]="view() === v.key" (click)="view.set(v.key)">{{ v.label }}</button>
            }
          </div>
          <div class="grid tiles">
            @for (t of r.data.headline; track t.key) {
              <v-tile [label]="t.label" [value]="t.value" [unit]="t.unit" [direction]="t.direction" [note]="t.note" [link]="t.link ? '/' + t.link : null" />
            }
          </div>
          @if (r.data.narrative) { <p class="notice" style="margin-top:14px">{{ r.data.narrative }}</p> }
          <div class="grid two" style="margin-top:14px">
            @switch (view()) {
              @case ('executive') {
                <section class="card"><h2>{{ canRevenue() ? 'Service lines by operating margin' : 'Service lines by cost' }}</h2><v-bars [bars]="r.data.serviceLines" [direction]="canRevenue() ? 'higher' : 'neutral'" /></section>
                <section class="card"><h2>Where the ledger went</h2><v-bars [bars]="r.data.residuals" /></section>
              }
              @case ('finance') {
                <section class="card"><h2>Residual buckets</h2><v-bars [bars]="r.data.residuals" /></section>
                <section class="card"><h2>Cost nature</h2><v-bars [bars]="r.data.costNature" /></section>
              }
              @case ('clinical') {
                <section class="card"><h2>Largest variances against standard</h2><v-bars [bars]="r.data.variances" direction="lower" /></section>
                <section class="card"><h2>Utilisation of practical capacity</h2><v-bars [bars]="r.data.utilisation" unit="%" /></section>
              }
              @case ('rcm') {
                <section class="card"><h2>Payer margin</h2>
                  @if (canRevenue()) { <v-bars [bars]="r.data.payers" direction="higher" /> } @else { <p class="faint">Revenue is masked for your role.</p> }
                </section>
                <section class="card"><h2>Costliest procedures</h2><v-bars [bars]="r.data.procedures" /></section>
              }
              @case ('supply') {
                <section class="card"><h2>Supply cost reaching patients</h2><v-bars [bars]="r.data.supplyCost" /></section>
                <section class="card"><h2>Purchased but not issued</h2><v-bars [bars]="r.data.waste" unit="%" direction="lower" /></section>
              }
            }
          </div>
        }
      </v-state>
    </v-page>
  `,
})
export class Dashboard {
  private auth = inject(Auth);
  res = runResource<DashboardDto>(() => 'dashboard');
  canRevenue = computed(() => this.auth.can('revenue.view'));
  views = [
    { key: 'executive', label: 'Executive' }, { key: 'finance', label: 'Finance' }, { key: 'clinical', label: 'Clinical operations' },
    { key: 'rcm', label: 'Revenue cycle' }, { key: 'supply', label: 'Supply chain' },
  ];
  view = signal('executive');
}

@Component({
  selector: 'v-profitability',
  imports: [...UI],
  template: `
    <v-page title="Profitability" purpose="Margin by dimension, computed at read time over this run's encounter costs (FR-5.16).">
      <div class="tabs" role="tablist">
        @for (d of dims; track d.key) {
          <button role="tab" type="button" [class.active]="dim() === d.key" (click)="dim.set(d.key)">{{ d.label }}</button>
        }
      </div>
      <v-state [loading]="res.isLoading()" [error]="res.error()">
        @if (res.value(); as r) {
          @if (canRevenue()) {
            <div class="grid tiles" style="margin-bottom:14px">
              <v-tile label="Operating margin" [value]="r.data.total.operating" direction="higher-better" />
              <v-tile label="Idle capacity" [value]="r.data.idleCapacity" note="Not charged to any patient" link="/capacity" />
              <v-tile label="Full-cost margin" [value]="r.data.fullCostMargin" direction="higher-better" note="Operating margin less idle capacity (FR-5.17)" />
            </div>
          } @else { <p class="notice warn">Revenue is masked for your role; margins are not shown.</p> }
          <v-table [rows]="r.data.rows" [columns]="cols()" [total]="r.data.total" [caption]="'Profitability by ' + dim()" />
        }
      </v-state>
    </v-page>
  `,
})
export class Profitability {
  private auth = inject(Auth);
  canRevenue = computed(() => this.auth.can('revenue.view'));
  dims = [
    { key: 'serviceLine', label: 'Service line' }, { key: 'department', label: 'Department' }, { key: 'physician', label: 'Physician' },
    { key: 'payer', label: 'Payer' }, { key: 'procedure', label: 'Procedure' }, { key: 'package', label: 'Package' }, { key: 'drg', label: 'DRG' },
    { key: 'encounterType', label: 'Encounter type' }, { key: 'segment', label: 'Patient segment' }, { key: 'hospital', label: 'Hospital' },
  ];
  dim = signal('serviceLine');
  res = runResource<ProfitDto>(() => 'profitability', () => ({ dimension: this.dim() }));
  cols = computed<Col[]>(() => {
    const base: Col[] = [
      { key: 'label', label: this.dims.find(d => d.key === this.dim())?.label ?? 'Item', link: r => r.link ? '/' + r.link : null },
      { key: 'volume', label: this.dim() === 'procedure' ? 'Cases' : 'Encounters', type: 'num', dp: 0 },
      { key: 'direct', label: 'Direct', type: 'money', dp: 0 },
      { key: 'indirect', label: 'Indirect', type: 'money', dp: 0 },
      { key: 'overhead', label: 'Overhead', type: 'money', dp: 0 },
      { key: 'total', label: 'Total cost', type: 'money', dp: 0 },
    ];
    if (!this.canRevenue()) return base;
    return [...base,
      { key: 'revenue', label: 'Revenue', type: 'money', dp: 0 },
      { key: 'contribution', label: 'Contribution', type: 'money', dp: 0, dir: 'higher' },
      { key: 'operating', label: 'Operating margin', type: 'money', dp: 0, dir: 'higher' },
      { key: 'marginPct', label: 'Margin %', type: 'pct', dir: 'higher' },
    ];
  });
}

@Component({
  selector: 'v-variance',
  imports: [...UI],
  template: `
    <v-page title="Variance" purpose="Actual against standard, split into rate, quantity and mix effects that sum exactly to the difference (FR-6).">
      <div class="notice">
        <strong>Reading the decomposition.</strong> Rate — the price of an input changed; procurement and payroll act on it.
        Quantity — more or less was consumed per case; practice and pathways act on it. Mix — rate and quantity moved together.
      </div>
      <v-state [loading]="res.isLoading()" [error]="res.error()">
        @if (res.value(); as r) { <v-table [rows]="r.data" [columns]="cols" caption="Procedure variances" /> }
      </v-state>
    </v-page>
  `,
})
export class Variance {
  res = runResource<VarianceRow[]>(() => 'variance');
  cols: Col<VarianceRow>[] = [
    { key: 'label', label: 'Procedure', link: r => `/variance/${r.id}`, sub: r => r.summary },
    { key: 'cases', label: 'Cases', type: 'num', dp: 0 },
    { key: 'baselineAmount', label: 'Standard', type: 'money', dp: 0 },
    { key: 'actual', label: 'Actual', type: 'money', dp: 0 },
    { key: 'variance', label: 'Variance', type: 'money', dp: 0, dir: 'lower' },
    { key: 'rateEffect', label: 'Rate', type: 'money', dp: 0, dir: 'lower' },
    { key: 'quantityEffect', label: 'Quantity', type: 'money', dp: 0, dir: 'lower' },
    { key: 'mixEffect', label: 'Mix', type: 'money', dp: 0, dir: 'lower' },
    { key: 'direction', label: 'Direction', type: 'chip' },
  ];
}

@Component({
  selector: 'v-variance-detail',
  imports: [RouterLink, ...UI],
  template: `
    <v-state [loading]="res.isLoading()" [error]="res.error()">
      @if (res.value(); as r) {
        <v-page [title]="r.data.variance.label" [purpose]="r.data.variance.summary">
          <div class="grid tiles">
            <v-tile label="Standard" [value]="r.data.variance.baselineAmount" />
            <v-tile label="Actual" [value]="r.data.variance.actual" />
            <v-tile label="Variance" [value]="r.data.variance.variance" [note]="r.data.variance.direction" />
            <v-tile label="Cases" [value]="r.data.variance.cases" unit="" />
          </div>
          <p class="notice" [class.bad]="!r.data.identityHolds" style="margin-top:12px">
            {{ r.data.identityHolds ? 'Rate + quantity + mix equals actual minus standard on every line (AC-2).' : 'The decomposition identity does not hold on every line — this is a defect.' }}
          </p>
          <v-table [rows]="r.data.components" [columns]="cols" caption="Variance by component" />
          <p style="margin-top:10px"><a [routerLink]="['/procedures', r.data.variance.scopeId]" queryParamsHandling="preserve">Open the procedure's cost explainer →</a></p>
        </v-page>
      }
    </v-state>
  `,
})
export class VarianceDetail {
  id = input.required<string>();
  res = runResource<VarianceDetailDto>(() => `variance/${this.id()}`);
  cols: Col<VarianceDetailDto['components'][number]>[] = [
    { key: 'label', label: 'Component', sub: r => r.explanation },
    { key: 'standardQty', label: 'Std qty / case', type: 'num', dp: 4 },
    { key: 'actualQty', label: 'Actual qty / case', type: 'num', dp: 4 },
    { key: 'standardRate', label: 'Std rate', type: 'num', dp: 4 },
    { key: 'actualRate', label: 'Actual rate', type: 'num', dp: 4 },
    { key: 'rateEffect', label: 'Rate effect', type: 'money', dir: 'lower' },
    { key: 'quantityEffect', label: 'Quantity effect', type: 'money', dir: 'lower' },
    { key: 'mixEffect', label: 'Mix effect', type: 'money', dir: 'lower' },
    { key: 'totalEffect', label: 'Total', type: 'money', dir: 'lower' },
  ];
}

@Component({
  selector: 'v-capacity',
  imports: [...UI],
  template: `
    <v-page title="Capacity" purpose="Practical capacity, utilisation and the cost of capacity paid for but not used — carved out before allocation, never charged to a patient (CR-12).">
      <v-state [loading]="res.isLoading()" [error]="res.error()">
        @if (res.value(); as r) {
          <div class="grid tiles" style="margin-bottom:14px">
            <v-tile label="Capacity paid for" [value]="r.data.capacityPaid" />
            <v-tile label="Capacity consumed" [value]="r.data.capacityUsed" />
            <v-tile label="Idle capacity" [value]="r.data.idle" note="UNUSED_CAPACITY — operations owns it" />
            <v-tile label="Over capacity" [value]="r.data.overCapacity" note="Overtime and extended lists, never netted" />
            <v-tile label="Supply not issued" [value]="r.data.supplyWaste" note="Wastage, expiry, stock movement" />
          </div>
          <div class="tabs">
            <button type="button" [class.active]="kind() === 'capacity'" (click)="kind.set('capacity')">Labour, equipment, rooms</button>
            <button type="button" [class.active]="kind() === 'supply'" (click)="kind.set('supply')">Supply</button>
          </div>
          <v-table [rows]="rows()" [columns]="cols" caption="Resource capacity" />
        }
      </v-state>
    </v-page>
  `,
})
export class Capacity {
  res = runResource<CapacityDto>(() => 'capacity');
  kind = signal<'capacity' | 'supply'>('capacity');
  rows = computed(() => (this.res.value()?.data.rows ?? []).filter(r => r.isCapacityDriven === (this.kind() === 'capacity')));
  cols: Col<CapacityDto['rows'][number]>[] = [
    { key: 'name', label: 'Resource', link: r => `/resources/${r.resourceId}`, sub: r => r.calcExpression },
    { key: 'resourceType', label: 'Type' },
    { key: 'unitCost', label: 'Rate', type: 'num', dp: 6, value: r => r.unitCost ?? '••••' },
    { key: 'availableMinutes', label: 'Available', type: 'num', dp: 0 },
    { key: 'usedMinutes', label: 'Used', type: 'num', dp: 0 },
    { key: 'utilisationPct', label: 'Utilisation', type: 'pct' },
    { key: 'costOfUsed', label: 'Cost of used', type: 'money', dp: 0 },
    { key: 'costOfUnused', label: 'Cost of unused', type: 'money', dp: 0, dir: 'lower' },
  ];
}
