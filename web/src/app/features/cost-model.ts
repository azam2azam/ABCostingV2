import { Component, input } from '@angular/core';
import { RouterLink } from '@angular/router';
import {
  ActivityDetail as ActivityDetailDto, ActivityRow, AllocationDto, CostCentreDetail as CostCentreDetailDto, CostCentreRow,
  DriverDetail as DriverDetailDto, DriverRow, PoolDetail as PoolDetailDto, PoolRow, ResourceDetail as ResourceDetailDto, ResourceRow, RuleRow,
} from '../core/api';
import { runResource } from '../core/resource';
import { Col, UI } from '../shared/ui';

export const allocationCols: Col<AllocationDto>[] = [
  { key: 'stage', label: 'Stage', type: 'num', dp: 0 },
  { key: 'source', label: 'From' },
  { key: 'target', label: 'To', link: r => r.targetActivityId ? `/activities/${r.targetActivityId}` : r.targetCostCentreId ? `/cost-centres/${r.targetCostCentreId}` : null, sub: r => r.calcExpression },
  { key: 'rule', label: 'Rule' },
  { key: 'driver', label: 'Driver' },
  { key: 'driverShare', label: 'Share', type: 'num', dp: 6 },
  { key: 'amount', label: 'Amount', type: 'money' },
];

export const ruleCols: Col<RuleRow>[] = [
  { key: 'stage', label: 'Stage', type: 'num', dp: 0 },
  { key: 'name', label: 'Rule', sub: r => r.formula },
  { key: 'sourcePool', label: 'Source pool', link: r => `/cost-pools/${r.sourcePoolId}` },
  { key: 'methodology', label: 'Methodology' },
  { key: 'method', label: 'Method' },
  { key: 'driver', label: 'Driver', link: r => r.driverId ? `/cost-drivers/${r.driverId}` : null },
  { key: 'poolShare', label: 'Pool share', type: 'num', dp: 4 },
  { key: 'movedThisRun', label: 'Moved this run', type: 'money', dp: 0 },
  { key: 'status', label: 'Status', type: 'chip' },
];

// ── Cost centres ────────────────────────────────────────────────────────────────

@Component({
  selector: 'v-cost-centres',
  imports: [...UI],
  template: `
    <v-page title="Cost centres" purpose="Where ledger money lands and where it goes next. Support and overhead centres close at zero after step-down.">
      <v-state [loading]="res.isLoading()" [error]="res.error()">
        @if (res.value(); as r) { <v-table [rows]="r.data" [columns]="cols" caption="Cost centres" /> }
      </v-state>
    </v-page>
  `,
})
export class CostCentres {
  res = runResource<CostCentreRow[]>(() => 'cost-centres');
  cols: Col<CostCentreRow>[] = [
    { key: 'stepDownSeq', label: 'Seq', type: 'num', dp: 0 },
    { key: 'name', label: 'Cost centre', link: r => `/cost-centres/${r.id}`, sub: r => `${r.code} · ${r.department}` },
    { key: 'ccType', label: 'Type' },
    { key: 'glPosted', label: 'GL posted', type: 'money', dp: 0 },
    { key: 'direct', label: 'Allocation-pool direct', type: 'money', dp: 0 },
    { key: 'received', label: 'Received', type: 'money', dp: 0 },
    { key: 'allocatedOut', label: 'Allocated out', type: 'money', dp: 0 },
    { key: 'final', label: 'Closing balance', type: 'money', dp: 2 },
    { key: 'isClosed', label: 'Closed', value: r => r.isClosed ? 'Closed' : '' },
  ];
}

@Component({
  selector: 'v-cost-centre',
  imports: [RouterLink, ...UI],
  template: `
    <v-state [loading]="res.isLoading()" [error]="res.error()">
      @if (res.value(); as r) {
        <v-page [title]="r.data.costCentre.name" [purpose]="r.data.costCentre.code + ' · ' + r.data.costCentre.ccType + ' · step-down sequence ' + r.data.costCentre.stepDownSeq">
          <div class="grid tiles">
            <v-tile label="GL posted" [value]="r.data.costCentre.glPosted" />
            <v-tile label="Received" [value]="r.data.costCentre.received" />
            <v-tile label="Allocated out" [value]="r.data.costCentre.allocatedOut" />
            <v-tile label="Closing balance" [value]="r.data.costCentre.final" />
          </div>
          <section class="card" style="margin-top:14px"><h2>Received from</h2><v-table [rows]="r.data.inbound" [columns]="alloc" empty="Nothing allocated into this centre." /></section>
          <section class="card"><h2>Allocated to</h2><v-table [rows]="r.data.outbound" [columns]="alloc" empty="This centre allocated nothing onward." /></section>
          <section class="card"><h2>Pools</h2>
            @for (p of r.data.pools; track p.id) { <p><a [routerLink]="['/cost-pools', p.id]" queryParamsHandling="preserve">{{ p.name }}</a> <span class="faint">{{ p.stream }} · {{ p.allocMethod }}</span></p> }
          </section>
        </v-page>
      }
    </v-state>
  `,
})
export class CostCentreDetail {
  id = input.required<string>();
  res = runResource<CostCentreDetailDto>(() => `cost-centres/${this.id()}`);
  alloc = allocationCols;
}

// ── Cost pools ──────────────────────────────────────────────────────────────────

@Component({
  selector: 'v-cost-pools',
  imports: [...UI],
  template: `
    <v-page title="Cost pools" purpose="Homogeneous bundles of cost. A pool is consumption-stream when a resource draws its rate from it, otherwise allocation-stream — derived, never flagged by hand (CR-4).">
      <v-state [loading]="res.isLoading()" [error]="res.error()">
        @if (res.value(); as r) { <v-table [rows]="r.data" [columns]="cols" caption="Cost pools" /> }
      </v-state>
    </v-page>
  `,
})
export class CostPools {
  res = runResource<PoolRow[]>(() => 'cost-pools');
  cols: Col<PoolRow>[] = [
    { key: 'name', label: 'Pool', link: r => `/cost-pools/${r.id}`, sub: r => r.description },
    { key: 'costCentre', label: 'Cost centre' },
    { key: 'stream', label: 'Stream', type: 'chip', chip: r => r.stream === 'CONSUMPTION' ? 'info' : '' },
    { key: 'allocMethod', label: 'Method' },
    { key: 'primaryDriver', label: 'Driver' },
    { key: 'resources', label: 'Resources funded', type: 'num', dp: 0 },
    { key: 'directAmount', label: 'From the ledger', type: 'money', dp: 0 },
    { key: 'status', label: 'Status', type: 'chip' },
  ];
}

@Component({
  selector: 'v-cost-pool',
  imports: [...UI],
  template: `
    <v-state [loading]="res.isLoading()" [error]="res.error()">
      @if (res.value(); as r) {
        <v-page [title]="r.data.pool.name" [purpose]="r.data.pool.description">
          <dl class="kv card">
            <dt>Stream</dt><dd>{{ r.data.pool.stream }}</dd>
            <dt>Cost centre</dt><dd>{{ r.data.pool.costCentre }}</dd>
            <dt>Method</dt><dd>{{ r.data.pool.allocMethod }}</dd>
            <dt>Effective</dt><dd>{{ r.data.pool.effectiveFrom }} – {{ r.data.pool.effectiveTo ?? 'open' }} (v{{ r.data.pool.version }}, {{ r.data.pool.status }})</dd>
            <dt>From the ledger this run</dt><dd class="num" style="text-align:left">{{ r.data.pool.directAmount | money }}</dd>
          </dl>
          <section class="card"><h2>GL mappings</h2><v-table [rows]="r.data.mappings" [columns]="mapCols" /></section>
          @if (r.data.fundedResources.length) { <section class="card"><h2>Resources funded by this pool</h2><v-table [rows]="r.data.fundedResources" [columns]="resCols" /></section> }
          <section class="card"><h2>Allocation rules</h2><v-table [rows]="r.data.rules" [columns]="rules" empty="No rule allocates this pool." /></section>
          <section class="card"><h2>Money moved this run</h2><v-table [rows]="r.data.allocations" [columns]="alloc" empty="Nothing was allocated from this pool." /></section>
        </v-page>
      }
    </v-state>
  `,
})
export class CostPoolDetail {
  id = input.required<string>();
  res = runResource<PoolDetailDto>(() => `cost-pools/${this.id()}`);
  alloc = allocationCols;
  rules = ruleCols;
  mapCols: Col<PoolDetailDto['mappings'][number]>[] = [
    { key: 'account', label: 'Account' }, { key: 'naturalClass', label: 'Natural class' }, { key: 'costCentre', label: 'Cost centre' },
    { key: 'weight', label: 'Weight', type: 'num', dp: 4 }, { key: 'effectiveFrom', label: 'From' }, { key: 'effectiveTo', label: 'To', value: r => r.effectiveTo ?? 'open' },
  ];
  resCols: Col<ResourceRow>[] = [
    { key: 'name', label: 'Resource', link: r => `/resources/${r.id}` }, { key: 'resourceType', label: 'Type' },
    { key: 'costOfUsed', label: 'Consumed', type: 'money', dp: 0 }, { key: 'costOfUnused', label: 'Idle / not issued', type: 'money', dp: 0 },
  ];
}

// ── Resources ───────────────────────────────────────────────────────────────────

@Component({
  selector: 'v-resources',
  imports: [...UI],
  template: `
    <v-page title="Resources" purpose="Everything that costs money and is consumed, with effective-dated rates and capacity models. Labour rates are masked without salary.view (FR-0.9).">
      <v-state [loading]="res.isLoading()" [error]="res.error()">
        @if (res.value(); as r) { <v-table [rows]="r.data" [columns]="cols" caption="Resources" /> }
      </v-state>
    </v-page>
  `,
})
export class Resources {
  res = runResource<ResourceRow[]>(() => 'resources');
  cols: Col<ResourceRow>[] = [
    { key: 'name', label: 'Resource', link: r => `/resources/${r.id}`, sub: r => r.code },
    { key: 'resourceType', label: 'Type' },
    { key: 'nature', label: 'Nature', type: 'nature' },
    { key: 'rate', label: 'Rate', type: 'num', dp: 6, value: r => r.rate ?? '••••' },
    { key: 'uom', label: 'Unit' },
    { key: 'costPool', label: 'Funding pool' },
    { key: 'utilisationPct', label: 'Utilisation', type: 'pct' },
    { key: 'costOfUsed', label: 'Consumed', type: 'money', dp: 0 },
    { key: 'costOfUnused', label: 'Idle / not issued', type: 'money', dp: 0, dir: 'lower' },
  ];
}

@Component({
  selector: 'v-resource',
  imports: [...UI],
  template: `
    <v-state [loading]="res.isLoading()" [error]="res.error()">
      @if (res.value(); as r) {
        <v-page [title]="r.data.resource.name" [purpose]="r.data.resource.code + ' · ' + r.data.resource.resourceType + ' · ' + r.data.resource.costCentre">
          @if (r.data.unitCost; as u) {
            <section class="card"><h2>This run</h2>
              <p class="mono">{{ u.calcExpression }}</p>
              <div class="grid tiles">
                <v-tile label="Utilisation" [value]="u.utilisationPct" unit="%" />
                <v-tile label="Cost of used capacity" [value]="u.costOfUsed" />
                <v-tile label="Cost of unused capacity" [value]="u.costOfUnused" />
              </div>
            </section>
          }
          <section class="card"><h2>Rates (effective-dated, never edited in place)</h2><v-table [rows]="r.data.rates" [columns]="rateCols" /></section>
          @if (r.data.capacity.length) { <section class="card"><h2>Capacity model</h2><v-table [rows]="r.data.capacity" [columns]="capCols" /></section> }
          @if (r.data.providers.length) { <section class="card"><h2>Providers sharing this role</h2> @for (p of r.data.providers; track p) { <p>{{ p }}</p> }</section> }
          <section class="card"><h2>Used in activity recipes</h2><v-table [rows]="r.data.usedBy" [columns]="useCols" empty="Not part of any standard recipe." /></section>
        </v-page>
      }
    </v-state>
  `,
})
export class ResourceDetail {
  id = input.required<string>();
  res = runResource<ResourceDetailDto>(() => `resources/${this.id()}`);
  rateCols: Col<ResourceDetailDto['rates'][number]>[] = [
    { key: 'unitCost', label: 'Unit cost', type: 'num', dp: 6, value: r => r.unitCost ?? '••••' }, { key: 'uom', label: 'Unit' },
    { key: 'basis', label: 'Basis' }, { key: 'sourceSystem', label: 'Source' }, { key: 'effectiveFrom', label: 'From' },
    { key: 'effectiveTo', label: 'To', value: r => r.effectiveTo ?? 'open' }, { key: 'version', label: 'Version', type: 'num', dp: 0 },
    { key: 'status', label: 'Status', type: 'chip' }, { key: 'effectiveInRun', label: 'In this run', value: r => r.effectiveInRun ? 'Effective' : '' },
  ];
  capCols: Col<ResourceDetailDto['capacity'][number]>[] = [
    { key: 'theoreticalMinutes', label: 'Theoretical', type: 'num', dp: 0 }, { key: 'practicalRatio', label: 'Practical ratio', type: 'num', dp: 4 },
    { key: 'practicalMinutes', label: 'Practical', type: 'num', dp: 0 }, { key: 'annualCost', label: 'Annual cost', type: 'money', dp: 0, value: r => r.annualCost ?? '••••' },
    { key: 'effectiveFrom', label: 'From' }, { key: 'effectiveTo', label: 'To', value: r => r.effectiveTo ?? 'open' },
  ];
  useCols: Col<ResourceDetailDto['usedBy'][number]>[] = [
    { key: 'activity', label: 'Activity', link: r => `/activities/${r.activityId}` }, { key: 'stdQuantity', label: 'Standard qty', type: 'num', dp: 4 }, { key: 'uom', label: 'Unit' },
  ];
}

// ── Cost drivers ────────────────────────────────────────────────────────────────

@Component({
  selector: 'v-drivers',
  imports: [...UI],
  template: `
    <v-page title="Cost drivers" purpose="The measurable things that cause cost. The source system, field and formula are what let a volume be checked at source when a number is challenged (FR-1.12).">
      <v-state [loading]="res.isLoading()" [error]="res.error()">
        @if (res.value(); as r) { <v-table [rows]="r.data" [columns]="cols" caption="Cost drivers" /> }
      </v-state>
    </v-page>
  `,
})
export class Drivers {
  res = runResource<DriverRow[]>(() => 'cost-drivers');
  cols: Col<DriverRow>[] = [
    { key: 'name', label: 'Driver', link: r => `/cost-drivers/${r.id}`, sub: r => r.formula },
    { key: 'driverType', label: 'Type' },
    { key: 'unit', label: 'Unit' },
    { key: 'sourceSystem', label: 'Source', sub: r => r.sourceField },
    { key: 'allocMethodology', label: 'Methodology' },
    { key: 'volume', label: 'Volume this period', type: 'num', dp: 2 },
    { key: 'status', label: 'Status', type: 'chip' },
  ];
}

@Component({
  selector: 'v-driver',
  imports: [...UI],
  template: `
    <v-state [loading]="res.isLoading()" [error]="res.error()">
      @if (res.value(); as r) {
        <v-page [title]="r.data.driver.name" [purpose]="r.data.driver.description">
          <dl class="kv card">
            <dt>Formula</dt><dd class="mono">{{ r.data.driver.formula }}</dd>
            <dt>Source</dt><dd>{{ r.data.driver.sourceSystem }} · {{ r.data.driver.sourceField }}</dd>
            <dt>Type / unit</dt><dd>{{ r.data.driver.driverType }} · {{ r.data.driver.unit }} · {{ r.data.driver.frequency }}</dd>
            <dt>Effective</dt><dd>{{ r.data.driver.effectiveFrom }} – {{ r.data.driver.effectiveTo ?? 'open' }}</dd>
          </dl>
          <section class="card"><h2>Measured volume this period</h2><v-table [rows]="r.data.volumes" [columns]="volCols" empty="No volume was measured — a rule using this driver routes its pool to NO_DRIVER_VOLUME (FR-3.13)." /></section>
          <section class="card"><h2>Rules using this driver</h2><v-table [rows]="r.data.rules" [columns]="rules" empty="No allocation rule uses this driver." /></section>
        </v-page>
      }
    </v-state>
  `,
})
export class DriverDetail {
  id = input.required<string>();
  res = runResource<DriverDetailDto>(() => `cost-drivers/${this.id()}`);
  rules = ruleCols;
  volCols: Col<DriverDetailDto['volumes'][number]>[] = [
    { key: 'costCentre', label: 'Cost centre' }, { key: 'quantity', label: 'Quantity', type: 'num', dp: 2 },
    { key: 'share', label: 'Share', type: 'pct', dp: 2 }, { key: 'sourceSystem', label: 'Source' },
  ];
}

// ── Activities ──────────────────────────────────────────────────────────────────

@Component({
  selector: 'v-activities',
  imports: [...UI],
  template: `
    <v-page title="Activities" purpose="Units of clinical and operational work: their standard recipes and what one occurrence cost in this run.">
      <v-state [loading]="res.isLoading()" [error]="res.error()">
        @if (res.value(); as r) { <v-table [rows]="r.data" [columns]="cols" caption="Activities" /> }
      </v-state>
    </v-page>
  `,
})
export class Activities {
  res = runResource<ActivityRow[]>(() => 'activities');
  cols: Col<ActivityRow>[] = [
    { key: 'name', label: 'Activity', link: r => `/activities/${r.id}`, sub: r => r.calcExpression ?? this.stateText(r.state) },
    { key: 'costCentre', label: 'Cost centre' },
    { key: 'stdDuration', label: 'Std min', type: 'num', dp: 0 },
    { key: 'volume', label: 'Occurrences', type: 'num', dp: 0 },
    { key: 'directCost', label: 'Direct', type: 'money', dp: 0 },
    { key: 'overheadCost', label: 'Overhead', type: 'money', dp: 0 },
    { key: 'unitCost', label: 'Per occurrence', type: 'money' },
  ];
  stateText(s: string): string {
    return s === 'FULLY_ALLOCATED_SUPPORT' ? 'Fully allocated support centre: its cost appears where it was allocated.'
      : s === 'NO_CASES' ? 'No cases this period.' : '';
  }
}

@Component({
  selector: 'v-activity',
  imports: [RouterLink, ...UI],
  template: `
    <v-state [loading]="res.isLoading()" [error]="res.error()">
      @if (res.value(); as r) {
        <v-page [title]="r.data.activity.name" [purpose]="r.data.activity.code + ' · ' + r.data.activity.costCentre + ' · ' + r.data.activity.department">
          @if (r.data.activity.calcExpression) { <p class="notice mono">{{ r.data.activity.calcExpression }}</p> }
          <section class="card"><h2>Standard recipe</h2><v-table [rows]="r.data.recipe" [columns]="recipeCols" empty="No standard recipe (DQ-ACT-NO-RECIPE)." /></section>
          <section class="card"><h2>Overhead allocated into this activity</h2><v-table [rows]="r.data.overheadIn" [columns]="alloc" empty="No overhead reached this activity." /></section>
          <section class="card"><h2>Procedures whose recipe uses it</h2>
            @for (u of r.data.usedBy; track u.procedureId) { <p><a [routerLink]="['/procedures', u.procedureId]" queryParamsHandling="preserve">{{ u.procedure }}</a> <span class="faint">× {{ u.quantity | num:2 }}</span></p> }
            @empty { <p class="faint">No procedure under this version uses it.</p> }
          </section>
        </v-page>
      }
    </v-state>
  `,
})
export class ActivityDetail {
  id = input.required<string>();
  res = runResource<ActivityDetailDto>(() => `activities/${this.id()}`);
  alloc = allocationCols;
  recipeCols: Col<ActivityDetailDto['recipe'][number]>[] = [
    { key: 'resource', label: 'Resource', link: r => `/resources/${r.resourceId}` }, { key: 'resourceType', label: 'Type' },
    { key: 'stdQuantity', label: 'Std qty', type: 'num', dp: 4 }, { key: 'uom', label: 'Unit' },
    { key: 'rate', label: 'Rate', type: 'num', dp: 6, value: r => r.rate ?? '••••' }, { key: 'lineCost', label: 'Line cost', type: 'money' },
  ];
}
