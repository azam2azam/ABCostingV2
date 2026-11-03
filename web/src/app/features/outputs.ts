import { Component, computed, inject, input, signal } from '@angular/core';
import { RouterLink } from '@angular/router';
import { firstValueFrom } from 'rxjs';
import {
  Api, ComponentDto, DrgDetail as DrgDetailDto, DrgRow, PackageDetail as PackageDetailDto, PackageRow,
  ProcedureDetail as ProcedureDetailDto, ProcedureRow, RecipeLineDto,
} from '../core/api';
import { toApiError } from '../core/auth';
import { RunContext } from '../core/context';
import { catalogueResource, runResource } from '../core/resource';
import { Col, UI, matchesNameOrCode } from '../shared/ui';

export const componentCols: Col<ComponentDto>[] = [
  { key: 'label', label: 'Component', sub: r => r.calcExpression, link: r => r.resourceId ? `/resources/${r.resourceId}` : null },
  { key: 'costNature', label: 'Nature', type: 'nature' },
  { key: 'quantity', label: 'Quantity', type: 'num', dp: 4 },
  { key: 'uom', label: 'Unit' },
  { key: 'unitCost', label: 'Unit cost', type: 'num', dp: 6, value: r => r.unitCost ?? '••••' },
  { key: 'amount', label: 'Amount', type: 'money' },
  { key: 'share', label: 'Share', type: 'pct' },
];

export const activityResourceCols: Col<ComponentDto>[] = [
  { ...componentCols[0], label: 'Resource' },
  ...componentCols.slice(1),
];

/** Recipe resources scaled by activity occurrences (per procedure unit, or billed qty on an encounter). */
export function recipeToComponents(
  activityId: string,
  activityQty: number,
  resources: { resourceId: string; resource: string; resourceType: string; stdQuantity: string; uom: string; rate: string | null; lineCost: string | null }[],
): ComponentDto[] {
  const r4 = (n: number) => Math.round(n * 1e4) / 1e4;
  const rows = resources.map(x => {
    const q = r4(Number(x.stdQuantity) * activityQty);
    const amount = x.lineCost == null ? 0 : r4(Number(x.lineCost) * activityQty);
    return { x, q, amount };
  });
  const total = rows.reduce((s, r) => s + r.amount, 0);
  return rows.map(({ x, q, amount }) => ({
    id: x.resourceId, componentType: x.resourceType,
    costNature: x.resourceType === 'OVERHEAD' ? 'OVERHEAD'
      : ['EQUIPMENT', 'ROOM', 'UTILITY', 'IT_SERVICE', 'FACILITY'].includes(x.resourceType) ? 'INDIRECT' : 'DIRECT',
    label: x.resource, quantity: String(q), uom: x.uom, unitCost: x.rate, amount: String(amount),
    share: total === 0 ? '0' : String(amount / total * 100),
    calcExpression: `${q} ${x.uom} × ${x.rate ?? '••••'} = ${amount}`,
    sourceSystem: null, sourceRef: null, resourceId: x.resourceId, activityId,
  }));
}

const encounterCols: Col<any>[] = [
  { key: 'encounterNo', label: 'Encounter', link: r => `/patient-encounters/${r.id}`, sub: r => `${r.patient} · ${r.mrn}` },
  { key: 'lengthOfStay', label: 'LOS', type: 'num', dp: 0 },
  { key: 'totalCost', label: 'Actual cost', type: 'money' },
  { key: 'revenue', label: 'Revenue', type: 'money' },
  { key: 'operatingMargin', label: 'Margin', type: 'money', dir: 'higher' },
];

@Component({
  selector: 'v-procedures',
  imports: [...UI],
  template: `
    <v-page title="Procedure/Activity" purpose="HIS procedure id, name, contracted price, and cases in this run.">
      <v-state [loading]="res.isLoading()" [error]="res.error()">
        @if (res.value(); as r) {
          <v-list-search [(query)]="search" placeholder="Search by name or code" />
          <v-type-tiles [rows]="r.data" [categoryTiles]="medicineCats" [zeroPriceTile]="true" [(selected)]="type" />
          @for (_ of [type()]; track _) {
            <v-table [rows]="filtered()" [columns]="cols" sortBy="volume" caption="Procedure/Activity"
              [empty]="search().trim() ? 'No procedures match this search.' : 'No rows.'" />
          }
        }
      </v-state>
    </v-page>
  `,
})
export class Procedures {
  medicineCats = ['MEDICINE'];
  type = signal<string | null>(null);
  search = signal('');
  res = catalogueResource<ProcedureRow[]>(() => 'procedures');
  filtered = computed(() => {
    const all = this.res.value()?.data ?? [];
    const t = this.type();
    let rows: ProcedureRow[];
    if (t === 'ZERO_PRICE') rows = all.filter(x => Number(x.listPrice) === 0);
    else {
      const priced = all.filter(x => Number(x.listPrice) !== 0);
      if (!t) rows = priced;
      else if (t.startsWith('CAT:')) {
        const cat = t.slice(4);
        rows = priced.filter(x => (x.category ?? '').toUpperCase() === cat);
      } else {
        rows = priced.filter(x => x.procType === t && (x.category ?? '').toUpperCase() !== 'MEDICINE');
      }
    }
    return rows.filter(x => matchesNameOrCode(this.search(), x.name, x.code));
  });
  cols: Col<ProcedureRow>[] = [
    { key: 'code', label: 'Id', value: r => hisProcedureId(r.code), link: r => `/procedures/${r.id}` },
    { key: 'name', label: 'Name' },
    { key: 'category', label: 'Category' },
    { key: 'procType', label: 'Type' },
    { key: 'listPrice', label: 'Contracted price', type: 'money' },
    { key: 'volume', label: 'Cases', type: 'num', dp: 0 },
    { key: 'billedRevenue', label: 'Revenue', type: 'money', value: r => Number(r.listPrice) * Number(r.volume) },
    { key: 'unitCost', label: 'Unit Cost', type: 'money' },
    { key: 'totalCost', label: 'Total Cost', type: 'money' },
  ];
}

@Component({
  selector: 'v-procedure',
  imports: [...UI],
  template: `
    <v-state [loading]="res.isLoading()" [error]="res.error()">
      @if (res.value(); as r) {
        <v-page [title]="hisProcedureId(r.data.procedure.code) + ' - ' + r.data.procedure.name">
          <div class="grid tiles">
            <v-tile label="Contracted Price" [value]="r.data.procedure.listPrice" />
            <v-tile label="Unit Cost" [value]="r.data.procedure.unitCost" />
            <v-tile label="Cases / Encounters" [value]="r.data.procedure.volume" unit="" />
            <v-tile label="Revenue" [value]="revenue()" />
            <v-tile label="Total Cost" [value]="r.data.procedure.totalCost" />
          </div>
          <section class="card" style="margin-top:14px">
            <h2>Standard recipe</h2>
            <v-table [rows]="r.data.recipe" [columns]="recipeCols" empty="No standard recipe." />
            @if (recipeError()) { <p class="bad">{{ recipeError() }}</p> }
          </section>
          <section class="card" style="margin-top:14px">
            <h2>Packages</h2>
            <v-table [rows]="pkgRows()" [columns]="pkgCols" sortBy="cases" [empty]="pkgEmpty()" />
            @if (!showAllPkgs() && pkgAll().length > pkgRows().length) {
              <p class="muted">Showing {{ pkgRows().length }} of {{ pkgAll().length }}. <button type="button" class="text-link" (click)="showAllPkgs.set(true)">Show all</button></p>
            }
          </section>
        </v-page>
      }
    </v-state>
  `,
  styles: `
    button.text-link {
      all: unset; color: var(--accent); cursor: pointer; font-weight: 600;
      text-decoration: underline; text-underline-offset: 2px;
    }
    button.text-link:focus-visible { outline: 2px solid var(--focus); }
  `,
})
export class ProcedureDetail {
  id = input.required<string>();
  hisProcedureId = hisProcedureId;
  private api = inject(Api);
  private ctx = inject(RunContext);
  res = catalogueResource<ProcedureDetailDto>(() => `procedures/${this.id()}`);
  busy = signal(false);
  recipeError = signal<string | null>(null);
  showAllPkgs = signal(false);
  recipeCols: Col<RecipeLineDto>[] = [
    { key: 'resource', label: 'Resource', link: r => `/resources/${r.resourceId}` },
    { key: 'resourceType', label: 'Type' },
    { key: 'stdQuantity', label: 'Std qty', type: 'num', dp: 4 },
    { key: 'uom', label: 'Unit' },
    { key: 'rate', label: 'Rate', type: 'num', dp: 6, value: r => r.rate ?? '••••' },
    { key: 'lineCost', label: 'Line cost', type: 'money' },
    {
      key: 'delete', label: '', type: 'button', sortable: false,
      button: () => this.busy() ? null : 'Remove from recipe',
      icon: () => 'trash',
      onButton: r => void this.remove(r),
    },
  ];
  async remove(row: RecipeLineDto): Promise<void> {
    this.busy.set(true);
    this.recipeError.set(null);
    try {
      const q = this.ctx.scopeQuery();
      await firstValueFrom(this.api.delete(`procedures/${this.id()}/recipe/${row.resourceId}${q}`));
      this.res.reload();
    } catch (err) {
      this.recipeError.set(toApiError(err).message);
    } finally {
      this.busy.set(false);
    }
  }
  revenue = computed(() => {
    const p = this.res.value()?.data.procedure;
    if (!p) return null;
    return Number(p.listPrice) * Number(p.volume);
  });
  pkgAll = computed(() => this.res.value()?.data.packages ?? []);
  pkgRows = computed(() => {
    const all = this.pkgAll();
    if (this.showAllPkgs()) return all;
    return [...all]
      .filter(p => Number(p.cases) > 0)
      .sort((a, b) => Number(b.cases) - Number(a.cases))
      .slice(0, 10);
  });
  pkgEmpty = computed(() => this.pkgAll().length ? 'No billed packages this run.' : 'Not added to any package.');
  pkgCols: Col<NonNullable<ProcedureDetailDto['packages']>[number]>[] = [
    { key: 'code', label: 'Id', value: r => hisProcedureId(r.code), link: r => `/packages/${r.id}` },
    { key: 'name', label: 'Name' },
    { key: 'price', label: 'Contracted price', type: 'money' },
    { key: 'cases', label: 'Cases', type: 'num', dp: 0 },
    { key: 'billedRevenue', label: 'Revenue', type: 'money', value: r => Number(r.price) * Number(r.cases) },
  ];
}

@Component({
  selector: 'v-packages',
  imports: [...UI],
  template: `
    <v-page title="Packages" purpose="HIS package id, name, contracted price, and cases in this run.">
      <v-state [loading]="res.isLoading()" [error]="res.error()">
        @if (res.value(); as r) {
          <v-list-search [(query)]="search" placeholder="Search by name or code" />
          <v-type-tiles [rows]="r.data" [(selected)]="type" />
          @for (_ of [type()]; track _) {
            <v-table [rows]="filtered()" [columns]="cols" sortBy="cases" caption="Packages"
              [empty]="search().trim() ? 'No packages match this search.' : 'No rows.'" />
          }
        }
      </v-state>
    </v-page>
  `,
})
export class Packages {
  type = signal<string | null>(null);
  search = signal('');
  res = catalogueResource<PackageRow[]>(() => 'packages');
  filtered = computed(() => {
    const all = this.res.value()?.data ?? [];
    const t = this.type();
    const rows = t ? all.filter(x => x.procType === t) : all;
    return rows.filter(x => matchesNameOrCode(this.search(), x.name, x.code));
  });
  cols: Col<PackageRow>[] = [
    { key: 'code', label: 'Id', value: r => hisProcedureId(r.code), link: r => `/packages/${r.id}` },
    { key: 'name', label: 'Name' },
    { key: 'procType', label: 'Type' },
    { key: 'compositionPrice', label: 'Composition Price', type: 'money' },
    { key: 'price', label: 'Contracted price', type: 'money' },
    { key: 'cases', label: 'Cases', type: 'num', dp: 0 },
    { key: 'billedRevenue', label: 'Revenue', type: 'money', value: r => Number(r.price) * Number(r.cases) },
    { key: 'unitCost', label: 'Unit Cost', type: 'money', bad: r => Number(r.unitCost) > Number(r.price) },
    { key: 'totalCost', label: 'Total Cost', type: 'money' },
  ];
}

/** VERITA code is `{hospital}.{HIS ProcedureID}`; the list shows the HIS id only. */
function hisProcedureId(code: string): string {
  const i = code.indexOf('.');
  return i >= 0 ? code.slice(i + 1) : code;
}

@Component({
  selector: 'v-package',
  imports: [RouterLink, ...UI],
  template: `
    <v-state [loading]="res.isLoading()" [error]="res.error()">
      @if (res.value(); as r) {
        <v-page [title]="hisProcedureId(r.data.package.code) + ' - ' + r.data.package.name">
          <div class="grid tiles">
            <v-tile label="Contracted price" [value]="r.data.package.price" />
            <v-tile label="Encounter count" [value]="encCount()" unit="" />
            <v-tile label="Package Revenue" [value]="packageRevenue()" />
            <v-tile label="Average Encounter Invoice" [value]="r.data.averageEncounterFee" />
          </div>
          <section class="card" style="margin-top:14px"><h2>Standard composition</h2><v-table [rows]="r.data.components" [columns]="compCols" [total]="compTotal()" /></section>
          <section class="card">
            <h2>Encounters</h2>
            <p><a [routerLink]="['/patient-encounters']" [queryParams]="{ billedPackageId: id() }" queryParamsHandling="merge">View list</a></p>
          </section>
        </v-page>
      }
    </v-state>
  `,
})
export class PackageDetail {
  id = input.required<string>();
  hisProcedureId = hisProcedureId;
  res = catalogueResource<PackageDetailDto>(() => `packages/${this.id()}`);
  compCols: Col<PackageDetailDto['components'][number]>[] = [
    { key: 'code', label: 'Id', value: r => hisProcedureId(r.code), link: r => `/procedures/${r.procedureId}` },
    { key: 'procedure', label: 'Procedure' },
    { key: 'category', label: 'Category' },
    { key: 'procType', label: 'Type', value: r => r.isMedicine ? 'Medicine' : r.procType },
    { key: 'price', label: 'Contracted price', type: 'money' },
    { key: 'cost', label: 'Cost', type: 'money' },
  ];
  encCount = computed(() => this.res.value()?.data.encounterCount ?? 0);
  /** Same as the packages list: contracted list price × billed quantity in this run. */
  packageRevenue = computed(() => {
    const pkg = this.res.value()?.data.package;
    if (!pkg) return null;
    const cases = Number(pkg.cases);
    return cases > 0 ? Number(pkg.price) * cases : null;
  });
  /** Sum of child contracted prices × quantity — same as Composition Price on the list. */
  compTotal = computed(() => {
    const rows = this.res.value()?.data.components ?? [];
    if (!rows.length) return null;
    return {
      price: rows.reduce((s, r) => s + Number(r.price) * Number(r.quantity), 0),
      cost: rows.reduce((s, r) => s + Number(r.cost), 0),
    };
  });
}

@Component({
  selector: 'v-drgs',
  imports: [...UI],
  template: `
    <v-page title="DRGs" purpose="Cost distribution by case-mix group. Outliers use Tukey's rule (cost above P75 + 1.5 × IQR), not a standard-deviation threshold (FR-5.13).">
      <v-state [loading]="res.isLoading()" [error]="res.error()">
        @if (res.value(); as r) { <v-table [rows]="r.data" [columns]="cols" caption="DRGs" /> }
      </v-state>
    </v-page>
  `,
})
export class Drgs {
  res = runResource<DrgRow[]>(() => 'drgs');
  cols: Col<DrgRow>[] = [
    { key: 'name', label: 'DRG', link: r => `/drgs/${r.id}`, sub: r => r.code },
    { key: 'cases', label: 'Cases', type: 'num', dp: 0 },
    { key: 'mean', label: 'Mean', type: 'money', dp: 0 },
    { key: 'median', label: 'Median', type: 'money', dp: 0 },
    { key: 'p25', label: 'P25', type: 'money', dp: 0 },
    { key: 'p75', label: 'P75', type: 'money', dp: 0 },
    { key: 'p90', label: 'P90', type: 'money', dp: 0 },
    { key: 'cv', label: 'CV', type: 'num', dp: 3 },
    { key: 'avgLOS', label: 'Avg LOS', type: 'num', dp: 1 },
    { key: 'avgMargin', label: 'Avg margin', type: 'money', dp: 0, dir: 'higher' },
    { key: 'outliers', label: 'Outliers', type: 'num', dp: 0 },
  ];
}

@Component({
  selector: 'v-drg',
  imports: [...UI],
  template: `
    <v-state [loading]="res.isLoading()" [error]="res.error()">
      @if (res.value(); as r) {
        <v-page [title]="r.data.drg.code + ' ' + r.data.drg.name" [purpose]="r.data.explanation">
          <div class="grid tiles">
            <v-tile label="Cases" [value]="r.data.drg.cases" unit="" />
            <v-tile label="Median cost" [value]="r.data.drg.median" />
            <v-tile label="Outlier fence" [value]="r.data.outlierFence" note="P75 + 1.5 × IQR" />
            <v-tile label="Outliers" [value]="r.data.drg.outliers" unit="" />
          </div>
          <section class="card" style="margin-top:14px"><h2>Why the expensive cases are expensive</h2>
            <p class="muted">Average cost composition of the outliers against the rest, by component (FR-5.14).</p>
            <v-table [rows]="r.data.whyExpensive" [columns]="whyCols" />
          </section>
          <section class="card"><h2>Outlier encounters</h2><v-table [rows]="r.data.outliers" [columns]="enc" empty="No outliers this period." /></section>
        </v-page>
      }
    </v-state>
  `,
})
export class DrgDetail {
  id = input.required<string>();
  res = runResource<DrgDetailDto>(() => `drgs/${this.id()}`);
  enc = encounterCols;
  whyCols: Col<DrgDetailDto['whyExpensive'][number]>[] = [
    { key: 'label', label: 'Component' }, { key: 'outlierAverage', label: 'Outliers (avg)', type: 'money' },
    { key: 'othersAverage', label: 'Others (avg)', type: 'money' }, { key: 'difference', label: 'Difference', type: 'money', dir: 'lower' },
    { key: 'shareOfGap', label: 'Share of gap', type: 'pct' },
  ];
}
