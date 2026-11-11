import { Component, computed, inject, input, signal } from '@angular/core';
import { RouterLink } from '@angular/router';
import {
  ActivityDetail as ActivityDetailDto, ActivityRow, AllocationDto, CostCentreDetail as CostCentreDetailDto, CostCentreRow,
  Envelope, ResourceDetail as ResourceDetailDto, ResourceRow,
} from '../core/api';
import { Auth } from '../core/auth';
import { RunContext } from '../core/context';
import { apiResource, catalogueResource } from '../core/resource';
import { Col, UI, matchesNameOrCode } from '../shared/ui';

export const allocationCols: Col<AllocationDto>[] = [
  { key: 'stage', label: 'Stage', type: 'num', dp: 0 },
  { key: 'source', label: 'From' },
  { key: 'target', label: 'To', link: r => r.targetActivityId ? `/activities/${r.targetActivityId}` : r.targetCostCentreId ? `/cost-centres/${r.targetCostCentreId}` : null, sub: r => r.calcExpression },
  { key: 'rule', label: 'Rule' },
  { key: 'driver', label: 'Driver' },
  { key: 'driverShare', label: 'Share', type: 'num', dp: 6 },
  { key: 'amount', label: 'Amount', type: 'money' },
];

// ── Departments ─────────────────────────────────────────────────────────────────

/** OpenAPI types catch up on the next api:types run. */
export interface DepartmentRow {
  id: string;
  code: string;
  name: string;
  deptType: string;
  specialty: string | null;
  serviceLine: string | null;
  costCentres: number;
  cases: number;
  revenue: number;
  cost: number;
}

@Component({
  selector: 'v-departments',
  imports: [...UI],
  template: `
    <v-page title="Departments" purpose="The hospital's departments from the catalogue. When a calculation has been run for the period, cases, revenue and cost are shown too.">
      <v-state [loading]="res.isLoading()" [error]="res.error()">
        @if (res.value()) {
          <v-list-search [(query)]="search" placeholder="Search by name or code" />
          <v-table [rows]="filtered()" [columns]="cols()" sortBy="cases" caption="Departments"
            [empty]="search().trim() ? 'No departments match this search.' : 'No departments in the catalogue for this hospital.'" />
        }
      </v-state>
    </v-page>
  `,
})
export class Departments {
  private auth = inject(Auth);
  res = catalogueResource<DepartmentRow[]>(() => 'departments');
  search = signal('');
  filtered = computed(() => (this.res.value()?.data ?? []).filter(r => matchesNameOrCode(this.search(), r.name, r.code)));
  cols = computed<Col<DepartmentRow>[]>(() => {
    const cols: Col<DepartmentRow>[] = [
      { key: 'code', label: 'Code', value: r => localCode(r.code), minWidth: '9rem' },
      { key: 'name', label: 'Name' },
      { key: 'deptType', label: 'Type' },
      { key: 'specialty', label: 'Specialty' },
      { key: 'serviceLine', label: 'Service line' },
      { key: 'costCentres', label: 'Cost centres', type: 'num', dp: 0 },
      { key: 'cases', label: 'Cases', type: 'num', dp: 0, link: () => '/patient-encounters',
        linkParams: r => ({ department: r.id }) },
    ];
    if (this.auth.can('revenue.view')) {
      cols.push(
        { key: 'revenue', label: 'Revenue', type: 'money', dp: 0 },
        { key: 'cost', label: 'Cost', type: 'money', dp: 0 },
      );
    } else {
      cols.push({ key: 'cost', label: 'Cost', type: 'money', dp: 0 });
    }
    return cols;
  });
}

// ── Cost centres ────────────────────────────────────────────────────────────────

@Component({
  selector: 'v-cost-centres',
  imports: [...UI],
  template: `
    <v-page title="Cost centres" purpose="Where ledger money lands and where it goes next. Support and overhead centres close at zero after step-down.">
      <v-state [loading]="res.isLoading()" [error]="res.error()">
        @if (res.value()) {
          <v-list-search [(query)]="search" placeholder="Search by name or code" />
          <v-table [rows]="filtered()" [columns]="cols" sortBy="cases" caption="Cost centres" [empty]="search().trim() ? 'No cost centres match this search.' : 'No rows.'" />
        }
      </v-state>
    </v-page>
  `,
})
export class CostCentres {
  res = catalogueResource<CostCentreRow[]>(() => 'cost-centres');
  search = signal('');
  filtered = computed(() => (this.res.value()?.data ?? []).filter(r => matchesNameOrCode(this.search(), r.name, r.code)));
  cols: Col<CostCentreRow>[] = [
    { key: 'stepDownSeq', label: 'Seq', type: 'num', dp: 0 },
    { key: 'code', label: 'Code', value: r => localCode(r.code) },
    { key: 'name', label: 'Name', link: r => `/cost-centres/${r.id}` },
    { key: 'ccType', label: 'Type' },
    { key: 'cases', label: 'Cases', type: 'num', dp: 0 },
    { key: 'cost', label: 'Cost', type: 'money', dp: 0 },
    { key: 'revenue', label: 'Revenue', type: 'money', dp: 0 },
  ];
}

@Component({
  selector: 'v-cost-centre',
  imports: [...UI],
  template: `
    <v-state [loading]="res.isLoading()" [error]="res.error()">
      @if (res.value(); as r) {
        <v-page [title]="r.data.costCentre.name" [purpose]="localCode(r.data.costCentre.code) + ' · ' + r.data.costCentre.ccType + ' · step-down sequence ' + r.data.costCentre.stepDownSeq">
          <div class="grid tiles">
            <v-tile label="Cases" [value]="r.data.costCentre.cases" unit="" />
            <v-tile label="Cost" [value]="r.data.costCentre.cost" />
            <v-tile label="Revenue" [value]="r.data.costCentre.revenue" />
            <v-tile label="Margin" [value]="margin(r.data.costCentre)" direction="higher-better" />
            <v-tile label="Margin %" [value]="marginPct(r.data.costCentre)" unit="%" direction="higher-better" />
          </div>
          <section class="card" style="margin-top:14px">
            <h2>Packages</h2>
            <v-table [rows]="r.data.packages" [columns]="pkgCols" sortBy="cases" empty="No packages billed at this centre." />
          </section>
          <section class="card">
            <h2>Procedures</h2>
            <v-table [rows]="r.data.procedures" [columns]="procCols" sortBy="cases" empty="No procedures billed at this centre." />
          </section>
        </v-page>
      }
    </v-state>
  `,
})
export class CostCentreDetail {
  id = input.required<string>();
  localCode = localCode;
  res = catalogueResource<CostCentreDetailDto>(() => `cost-centres/${this.id()}`);
  pkgCols: Col<CostCentreDetailDto['packages'][number]>[] = [
    { key: 'code', label: 'Code', value: r => localCode(r.code), link: r => `/packages/${r.id}` },
    { key: 'name', label: 'Name' },
    { key: 'unitPrice', label: 'Unit Price', type: 'money' },
    { key: 'cases', label: 'Cases', type: 'num', dp: 0 },
    { key: 'revenue', label: 'Revenue', type: 'money' },
    { key: 'unitCost', label: 'Unit Cost', type: 'money' },
    { key: 'totalCost', label: 'Total Cost', type: 'money' },
  ];
  procCols: Col<CostCentreDetailDto['procedures'][number]>[] = [
    { key: 'code', label: 'Code', value: r => localCode(r.code), link: r => `/procedures/${r.id}` },
    { key: 'name', label: 'Name' },
    { key: 'unitPrice', label: 'Unit Price', type: 'money' },
    { key: 'cases', label: 'Cases', type: 'num', dp: 0 },
    { key: 'revenue', label: 'Revenue', type: 'money' },
    { key: 'unitCost', label: 'Unit Cost', type: 'money' },
    { key: 'totalCost', label: 'Total Cost', type: 'money' },
  ];
  margin(c: CostCentreRow): number {
    return Number(c.revenue) - Number(c.cost);
  }
  marginPct(c: CostCentreRow): number {
    const rev = Number(c.revenue);
    return rev === 0 ? 0 : this.margin(c) / rev * 100;
  }
}

// ── Resources ───────────────────────────────────────────────────────────────────

const isNotAssigned = (r: { id: string }) => /^0{8}-/.test(r.id);

@Component({
  selector: 'v-resources',
  imports: [...UI],
  template: `
    <v-page title="Resources" purpose="Everything that costs money and is consumed, with effective-dated rates and capacity models. Labour rates are masked without salary.view (FR-0.9).">
      <v-state [loading]="res.isLoading()" [error]="res.error()">
        @if (res.value(); as r) { <v-table [rows]="r.data" [columns]="cols()" caption="Resources" /> }
      </v-state>
    </v-page>
  `,
})
export class Resources {
  private auth = inject(Auth);
  res = catalogueResource<ResourceRow[]>(() => 'resources');
  cols = computed<Col<ResourceRow>[]>(() => {
    const cols: Col<ResourceRow>[] = [
      { key: 'code', label: 'Code', value: r => localCode(r.code) },
      { key: 'name', label: 'Name', link: r => `/resources/${r.id}` },
      { key: 'physicians', label: 'Physicians', type: 'num', dp: 0 },
      { key: 'cases', label: 'Cases', type: 'num', dp: 0 },
    ];
    if (this.auth.can('revenue.view')) cols.push({ key: 'revenue', label: 'Revenue', type: 'money' });
    return cols;
  });
}

@Component({
  selector: 'v-resource',
  imports: [...UI],
  template: `
    <v-state [loading]="res.isLoading()" [error]="res.error()">
      @if (res.value(); as r) {
        <v-page [title]="unmapped(r.data.resource) ? 'Not assigned' : localCode(r.data.resource.code) + ' - ' + r.data.resource.name"
          [purpose]="unmapped(r.data.resource) ? 'Doctors not mapped to any resource.' : r.data.resource.resourceType + ' · ' + r.data.resource.costCentre">
          <section class="card"><h2>{{ unmapped(r.data.resource) ? 'Providers not mapped to a role' : 'Providers sharing this role' }}</h2>
            <v-table [rows]="r.data.providers" [columns]="providerCols" [caption]="unmapped(r.data.resource) ? 'Providers not mapped to a role' : 'Providers sharing this role'"
              [empty]="unmapped(r.data.resource) ? 'Every doctor is mapped to a resource.' : 'No doctors mapped to this role.'" />
          </section>
        </v-page>
      }
    </v-state>
  `,
})
export class ResourceDetail {
  id = input.required<string>();
  unmapped = isNotAssigned;
  localCode = localCode;
  private ctx = inject(RunContext);
  res = apiResource<Envelope<ResourceDetailDto>>(() => this.ctx.hospitalParam() ? `resources/${this.id()}` : null, () => ({ hospital: this.ctx.hospitalParam() }));
  providerCols: Col<NonNullable<ResourceDetailDto['providers']>[number]>[] = [
    { key: 'code', label: 'Id', value: r => localCode(r.code) },
    { key: 'name', label: 'Doctor' },
    { key: 'department', label: 'Department' },
    { key: 'specialty', label: 'Specialty' },
    { key: 'grade', label: 'Grade' },
    { key: 'billedLines', label: 'Invoice lines', type: 'num', dp: 0 },
  ];
}

export function localCode(code: string): string {
  const i = code.indexOf('.');
  return i >= 0 ? code.slice(i + 1) : code;
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
  res = catalogueResource<ActivityRow[]>(() => 'activities');
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
  res = catalogueResource<ActivityDetailDto>(() => `activities/${this.id()}`);
  alloc = allocationCols;
  recipeCols: Col<ActivityDetailDto['recipe'][number]>[] = [
    { key: 'resource', label: 'Resource', link: r => `/resources/${r.resourceId}` }, { key: 'resourceType', label: 'Type' },
    { key: 'stdQuantity', label: 'Std qty', type: 'num', dp: 4 }, { key: 'uom', label: 'Unit' },
    { key: 'rate', label: 'Rate', type: 'num', dp: 6, value: r => r.rate ?? '••••' }, { key: 'lineCost', label: 'Line cost', type: 'money' },
  ];
}
