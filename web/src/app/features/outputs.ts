import { Component, computed, effect, inject, input, linkedSignal, signal, untracked } from '@angular/core';
import { firstValueFrom } from 'rxjs';
import { RouterLink } from '@angular/router';
import { Icon } from '../shared/icon';
import {
  Api, ComponentDto, Envelope, PackageDetail as PackageDetailDto, PackageRow,
  ProcedureDetail as ProcedureDetailDto, ProcedureRow, RecipeLineDto,
} from '../core/api';
import { Auth, toApiError } from '../core/auth';
import { RunContext } from '../core/context';
import { catalogueResource } from '../core/resource';
import { formatDec, formatMoney } from '../core/decimal';
import { Col, TileInfo, TypeCounts, UI, hisProcedureId } from '../shared/ui';
import { packageTileInfo, procedureTileInfo } from '../shared/tile-info';
import { packageColumns } from '../shared/package-cols';

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

interface CataloguePage<T> { rows: T[]; total: number; page: number; pageSize: number; counts: TypeCounts }

/**
 * A catalogue list the server filters, sorts and pages: the browser only ever holds one page and the tile counts.
 * Any change of tile, search or sort goes back to page 1, and so does a change of hospital or period.
 */
class ServerList<T> {
  static readonly pageSize = 20;
  private ctx = inject(RunContext);
  readonly type = signal<string | null>(null);
  /** What the box shows; the request follows it once typing pauses. */
  readonly search = signal('');
  private query = signal('');
  readonly sort = signal('');
  readonly desc = signal(true);
  readonly page = signal(1);
  readonly res = catalogueResource<CataloguePage<T>>(() => this.path, () => ({
    search: this.query().trim(), type: this.type(), sort: this.sort(), desc: this.desc(), page: this.page(), pageSize: ServerList.pageSize,
  }));
  /** The last page loaded: kept on screen while the next tile, page, search or sort loads, so only the table dims. */
  readonly last = linkedSignal<Envelope<CataloguePage<T>> | undefined, Envelope<CataloguePage<T>> | undefined>({
    source: this.res.value,
    computation: (next, prev) => next ?? (this.res.error() ? undefined : prev?.value),
  });
  readonly pages = computed(() => Math.max(1, Math.ceil((this.last()?.data.total ?? 0) / ServerList.pageSize)));
  readonly tableSort = computed(() => ({ key: this.sort(), desc: this.desc() }));

  constructor(private readonly path: string, defaultSort: string) {
    this.sort.set(defaultSort);
    effect(onCleanup => {
      const text = this.search();
      const timer = setTimeout(() => untracked(() => { if (text !== this.query()) { this.query.set(text); this.page.set(1); } }), 250);
      onCleanup(() => clearTimeout(timer));
    });
    let scope: string | null = null;
    effect(() => {
      const next = `${this.ctx.hospitalParam()}|${this.ctx.periodParam()}`;
      if (scope !== null && scope !== next) untracked(() => this.page.set(1));
      scope = next;
    });
  }

  setType(type: string | null): void { this.type.set(type); this.page.set(1); }
  setSort(next: { key: string; desc: boolean }): void { this.sort.set(next.key); this.desc.set(next.desc); this.page.set(1); }
  /** Showing rows from, to and of, for the pager. */
  range(r: CataloguePage<T>): string {
    if (!r.total) return '0 rows';
    const from = (r.page - 1) * r.pageSize + 1;
    return `${from}–${from + r.rows.length - 1} of ${r.total}`;
  }
}

const LIST_BAR = `
  <div class="row list-bar">
  <v-list-search [query]="list.search()" (queryChange)="list.search.set($event)" placeholder="Search by name or code" />
  <div class="row pager">
    <button class="btn" type="button" [disabled]="list.page() <= 1" (click)="list.page.set(list.page() - 1)">Previous</button>
    <span class="muted">Page {{ r.data.page }} of {{ list.pages() }} · {{ list.range(r.data) }}</span>
    <button class="btn" type="button" [disabled]="r.data.page >= list.pages()" (click)="list.page.set(r.data.page + 1)">Next</button>
  </div>
  </div>`;
const LIST_STYLES = `.list-bar { justify-content: space-between; margin: 0 0 10px; }
  .pager { margin-left: auto; }
  .reloading { opacity: .6; transition: opacity .15s; }`;

/** The run's stored lines of a procedure billed outside a package; OpenAPI types catch up on the next api:types run. */
type ProcedureListRow = ProcedureRow & {
  performed?: number; feeBilled?: number | string; materialsBilled?: number | string; medicineBilled?: number | string;
  claimsReceived?: number | string | null; claimRejected?: number | string; encounters?: number; stayDays?: number | string;
};

@Component({
  selector: 'v-procedures',
  imports: [...UI],
  template: `
    <v-page title="Procedure/Activity" purpose="HIS procedure id, name, and what HIS billed and claimed on the procedure's lines billed outside a package in this run.">
      <v-state [loading]="list.res.isLoading() && !list.last()" [error]="list.res.error()">
        @if (list.last(); as r) {
          <v-type-tiles [counts]="r.data.counts" [selected]="list.type()" (selectedChange)="list.setType($event)" />
          ${LIST_BAR}
          <div [class.reloading]="list.res.isLoading()">
            <v-table [rows]="r.data.rows" [columns]="cols" [serverSort]="list.tableSort()" (sortChange)="list.setSort($event)" caption="Procedure/Activity"
              [empty]="list.search().trim() ? 'No procedures match this search.' : 'No rows.'" />
          </div>
        }
      </v-state>
    </v-page>
  `,
  styles: LIST_STYLES,
})
export class Procedures {
  list = new ServerList<ProcedureListRow>('procedures', 'performed');
  /** As the Packages list, from the procedure's lines billed outside a package (the procedure page's figures). */
  cols: Col<ProcedureListRow>[] = [
    { key: 'code', label: 'Id', value: r => hisProcedureId(r.code), link: r => `/procedures/${r.id}` },
    { key: 'name', label: 'Name', minWidth: '22rem' },
    { key: 'performed', label: 'Cases', type: 'num', dp: 0, blankZero: true },
    { key: 'materialsBilled', label: 'Consumables', type: 'money', dp: 0, blankZero: true },
    // Length of stay of the encounters with the lines: procedure lines carry no ward days.
    { key: 'avgLos', label: 'Avg. LOS', type: 'num', dp: 1, blankZero: true,
      value: r => Number(r.encounters) ? Number(r.stayDays ?? 0) / Number(r.encounters) : 0 },
    { key: 'feeBilled', label: 'Invoice Billed', type: 'money', dp: 0, blankZero: true },
    { key: 'medicineBilled', label: 'Medicine', type: 'money', dp: 0, blankZero: true },
    // Claimed: what HIS claimed on invoices it put on a claim (HIS_InvoiceMain.IsClaimed = 1).
    { key: 'claimsReceived', label: 'Claimed', type: 'money', dp: 0, blankZero: true, bad: r => Number(r.claimRejected) > 0 },
  ];
}

/** A doctor's lines of the procedure billed outside a package (the run's stored rows), with the encounters under it. */
interface ProcDoctorEncounter {
  encounterId: string; encounterNo: string; performed: number; feeBilled: number | string; consumablesBilled: number | string;
  medicineBilled: number | string; claimsReceived: number | string | null; rejectedClaims: number | string;
}
interface ProcDoctorRow extends Omit<ProcDoctorEncounter, 'encounterId' | 'encounterNo'> {
  id: string | null; code: string; name: string; encounters: number; encounterRows: ProcDoctorEncounter[];
}
type ProcDoctorTableRow = ProcDoctorRow & { kind: 'doctor' | 'encounter'; encounterId?: string };

@Component({
  selector: 'v-procedure',
  imports: [...UI],
  template: `
    <v-state [loading]="res.isLoading()" [error]="res.error()">
      @if (res.value(); as r) {
        <v-page [title]="hisProcedureId(r.data.procedure.code) + ' - ' + r.data.procedure.name">
          <div class="grid tiles">
            <v-tile label="Performed (cases)" [value]="total()?.performed ?? 0" unit="" [info]="info.performed" />
            <v-tile label="Resources Consumed" [value]="resourcesTile()" [info]="info.resources" />
            <v-tile label="Amount billed" [value]="total() ? total()!.feeBilled : null" [info]="info.billed" />
            <v-tile label="Claimed" [value]="claimsReceivedTile()" [info]="info.claims" />
          </div>
          <section class="card" style="margin-top:14px">
            <h2>Doctors who performed this procedure</h2>
            <p class="muted">Doctor on each line of this procedure billed outside a package in {{ r.meta.period }}; lines billed as or under a package are on the package's page.</p>
            <v-table [rows]="doctorRows()" [columns]="doctorCols" [total]="total()" caption="Doctors who performed this procedure"
              empty="No lines of this procedure billed outside a package in this period." />
          </section>
          <section class="card">
            <h2>Packages included in</h2>
            <p class="muted">Packages whose standard composition has this procedure.</p>
            <v-table [rows]="pkgRows()" [columns]="pkgCols" sortBy="cases" [empty]="pkgEmpty()" caption="Packages included in" />
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
  res = catalogueResource<ProcedureDetailDto & { doctors?: ProcDoctorRow[] }>(() => `procedures/${this.id()}`);
  info = procedureTileInfo();
  /** Doctors whose encounters are shown under them. */
  openDoctors = signal<ReadonlySet<string>>(new Set());
  private doctorKey(r: ProcDoctorRow): string { return r.id ?? 'none'; }
  private toggleDoctor(r: ProcDoctorTableRow): void {
    const key = this.doctorKey(r);
    this.openDoctors.update(open => { const next = new Set(open); if (next.has(key)) next.delete(key); else next.add(key); return next; });
  }
  /** Doctor rows, each open one followed by its encounters (encounter number under Doctor, that encounter's figures). */
  doctorRows = computed<ProcDoctorTableRow[]>(() => {
    const open = this.openDoctors();
    return (this.res.value()?.data.doctors ?? []).flatMap(d => {
      const row: ProcDoctorTableRow = { ...d, kind: 'doctor' };
      if (!open.has(this.doctorKey(d))) return [row];
      return [row, ...(d.encounterRows ?? []).map(e => ({ ...d, ...e, kind: 'encounter' as const, name: e.encounterNo, encounters: 1 }))];
    });
  });
  doctorCols: Col<ProcDoctorTableRow>[] = [
    { key: 'toggle', label: '', type: 'button', sortable: false, icon: () => 'chevron',
      button: r => r.kind === 'doctor' && r.encounterRows?.length ? (this.openDoctors().has(this.doctorKey(r)) ? 'Hide encounters' : 'Show encounters') : null,
      expanded: r => r.kind === 'doctor' && this.openDoctors().has(this.doctorKey(r)), onButton: r => this.toggleDoctor(r) },
    { key: 'name', label: 'Doctor', minWidth: '16rem', sortable: false,
      link: r => r.kind === 'encounter' ? `/patient-encounters/${r.encounterId}` : r.id && r.code ? `/doctors/${r.id}` : null },
    { key: 'performed', label: 'Performed', type: 'num', dp: 0, sortable: false, blankZero: true },
    { key: 'consumablesBilled', label: 'Consumables', type: 'money', dp: 0, blankZero: true, sortable: false },
    { key: 'feeBilled', label: 'Invoice Billed', type: 'money', dp: 0, blankZero: true, sortable: false },
    { key: 'medicineBilled', label: 'Medicine', type: 'money', dp: 0, blankZero: true, sortable: false },
    // Claimed: what HIS claimed on invoices it put on a claim (HIS_InvoiceMain.IsClaimed = 1).
    { key: 'claimsReceived', label: 'Claimed', type: 'money', dp: 0, blankZero: true, sortable: false, bad: r => Number(r.rejectedClaims) > 0 },
  ];
  /** Sum of the doctor rows: the tiles and the table's total row. */
  total = computed(() => {
    const rows = this.res.value()?.data.doctors ?? [];
    if (!rows.length) return null;
    const sum = (f: (r: ProcDoctorRow) => unknown) => rows.reduce((t, r) => t + Number(f(r) ?? 0), 0);
    return { performed: sum(r => r.performed), feeBilled: sum(r => r.feeBilled), consumablesBilled: sum(r => r.consumablesBilled),
      medicineBilled: sum(r => r.medicineBilled), claimsReceived: sum(r => r.claimsReceived) };
  });
  /** Fee billed plus the consumables and medicine tied to the lines. */
  resourcesTile = computed(() => { const t = this.total(); return t ? t.feeBilled + t.consumablesBilled + t.medicineBilled : null; });
  /** Blank until the facts carry the IsClaimed flag. */
  claimsReceivedTile = computed(() => {
    const rows = this.res.value()?.data.doctors ?? [];
    return !rows.length || rows.every(r => r.claimsReceived == null) ? null : this.total()!.claimsReceived;
  });
  showAllPkgs = signal(false);
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
    { key: 'name', label: 'Name', minWidth: '16rem' },
    { key: 'quantity', label: 'Qty in package', type: 'num', dp: 0 },
    { key: 'price', label: 'Contracted price', type: 'money' },
    { key: 'cases', label: 'Cases', type: 'num', dp: 0 },
    { key: 'billedRevenue', label: 'Revenue', type: 'money', value: r => Number(r.price) * Number(r.cases) },
  ];
}

@Component({
  selector: 'v-packages',
  imports: [RouterLink, Icon, ...UI],
  template: `
    <v-page title="Packages" purpose="HIS package id, name, contracted price, cases, and what HIS billed and claimed on the package and its child lines in this run.">
      <a actions class="btn primary" routerLink="/packages/compare" queryParamsHandling="preserve"><v-icon name="compare" [size]="16" /> Compare Doctors</a>
      <v-state [loading]="list.res.isLoading() && !list.last()" [error]="list.res.error()">
        @if (list.last(); as r) {
          @if (!r.meta.run && r.meta.costingVersion) {
            <p class="notice">No calculation run for {{ r.meta.period }}. Costs are estimated from costing model {{ r.meta.costingVersion }} ({{ r.meta.costingVersionStatus }}): Package Costing where set, otherwise the package's composition.</p>
          }
          <v-type-tiles [counts]="r.data.counts" [selected]="list.type()" (selectedChange)="list.setType($event)" />
          ${LIST_BAR}
          <div [class.reloading]="list.res.isLoading()">
            <v-table [rows]="r.data.rows" [columns]="cols" [serverSort]="list.tableSort()" (sortChange)="list.setSort($event)" caption="Packages"
              [empty]="list.search().trim() ? 'No packages match this search.' : 'No rows.'" />
          </div>
        }
      </v-state>
    </v-page>
  `,
  styles: LIST_STYLES,
})
/** The packages list. Figures beyond the price are what the run stored on the package's own and child lines. */
export class Packages {
  list = new ServerList<PackageRow>('packages', 'cases');
  cols = packageColumns();
}


type DoctorRow = PackageDetailDto['doctors'][number];
/** A doctor row, or one of its encounters shown under it. */
type DoctorTableRow = DoctorRow & { kind: 'doctor' | 'encounter'; encounterId?: string; encounterNo?: string };

@Component({
  selector: 'v-package',
  imports: [RouterLink, Icon, ...UI],
  template: `
    <v-state [loading]="res.isLoading()" [error]="res.error()">
      @if (res.value(); as r) {
        <v-page [title]="hisProcedureId(r.data.package.code) + ' - ' + r.data.package.name">
          <a actions class="btn" routerLink="/packages/compare" [queryParams]="{ package: r.data.package.id }" queryParamsHandling="merge"><v-icon name="compare" [size]="16" /> Compare Doctors</a>
          <div class="grid tiles">
            <v-tile label="Performed (cases)" [value]="r.data.package.cases" unit="" [info]="info.performed" />
            <v-tile label="Resources Consumed" [value]="resourcesTile()?.total ?? null" [info]="withValues(info.resources, resourcesTile()?.note)" />
            <v-tile label="Amount billed" [value]="billedTile()?.total ?? null" [info]="withValues(info.billed, billedValues())" />
            <v-tile label="Claimed" [value]="claimsReceivedTile()" [info]="info.claims" />
          </div>
          <section class="card" style="margin-top:14px">
            <h2>Doctors who performed this package</h2>
            <p class="muted">Doctor on each billed package line in {{ r.meta.period }}, counted the same way as the encounter count.</p>
            <v-table [rows]="doctorRows()" [columns]="doctorCols" [total]="doctorTotal()" caption="Doctors who performed this package" empty="No billed package lines in this period." />
          </section>
          <section class="card"><h2>Standard composition</h2><v-table [rows]="r.data.components" [columns]="compCols" [total]="compTotal()" /></section>
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
  ];
  /** Money columns are what HIS billed and claimed (not cost). Share/Fee is "—" for facts extracted before the share was. */
  /** Doctors whose encounters are shown under them. */
  openDoctors = signal<ReadonlySet<string>>(new Set());
  private doctorKey(r: DoctorRow): string { return r.id ?? 'none'; }
  toggleDoctor(r: DoctorRow): void {
    const key = this.doctorKey(r);
    this.openDoctors.update(open => { const next = new Set(open); if (next.has(key)) next.delete(key); else next.add(key); return next; });
  }
  /** Doctor rows, each open one followed by its encounters (encounter number under Doctor, Performed blank, that encounter's figures). */
  doctorRows = computed<DoctorTableRow[]>(() => {
    const open = this.openDoctors();
    return (this.res.value()?.data.doctors ?? []).flatMap(d => {
      const row: DoctorTableRow = { ...d, kind: 'doctor' };
      if (!open.has(this.doctorKey(d))) return [row];
      return [row, ...(d.encounterRows ?? []).map(e => ({
        ...d, ...e, kind: 'encounter' as const, id: d.id, name: e.encounterNo, encounters: 1, quantity: '0', encounterRows: null,
      }))];
    });
  });
  doctorCols: Col<DoctorTableRow>[] = [
    { key: 'toggle', label: '', type: 'button', sortable: false, icon: () => 'chevron',
      button: r => r.kind === 'doctor' && r.encounterRows?.length ? (this.openDoctors().has(this.doctorKey(r)) ? 'Hide encounters' : 'Show encounters') : null,
      expanded: r => r.kind === 'doctor' && this.openDoctors().has(this.doctorKey(r)), onButton: r => this.toggleDoctor(r) },
    { key: 'name', label: 'Doctor', minWidth: '16rem', sortable: false,
      value: r => r.name,
      link: r => r.kind === 'encounter' ? `/patient-encounters/${r.encounterId}` : r.id ? `/doctors/${r.id}` : null },
    { key: 'quantity', label: 'Performed', type: 'num', dp: 0, sortable: false, blankZero: true },
    { key: 'consumablesAll', label: 'Consumables', type: 'money', dp: 0, blankZero: true, sortable: false,
      value: r => Number(r.consumablesBilled ?? 0) + Number(r.consumablesCovered ?? 0) },
    { key: 'avgLos', label: 'Avg. LOS', type: 'num', dp: 1, blankZero: true, sortable: false,
      value: r => Number(r.encounters) ? Number(r.wardDays ?? 0) / Number(r.encounters) : 0 },
    { key: 'feeBilled', label: 'Invoice Billed', type: 'money', dp: 0, blankZero: true, sortable: false },
    { key: 'medicineBilled', label: 'Medicine', type: 'money', dp: 0, blankZero: true, sortable: false },
    // Claimed: what HIS claimed on invoices it put on a claim (HIS_InvoiceMain.IsClaimed = 1): package, consumable and medicine lines.
    { key: 'claimsReceived', label: 'Claimed', type: 'money', dp: 0, blankZero: true, sortable: false, bad: r => Number(r.rejectedClaims) > 0 },
  ];
  /** Contracted price × cases, the consumables and medicine the package price covered, and those billed separately. */
  resourcesTile = computed(() => {
    const d = this.res.value()?.data;
    if (!d) return null;
    const t = (this.doctorTotal() ?? {}) as Record<string, number>;
    const cases = Number(d.package.cases ?? 0);
    const parts: [number, string][] = [
      [Number(d.package.price ?? 0) * cases, `contracted price (${formatMoney(String(d.package.price ?? 0), { dp: 0 })} × ${formatDec(String(cases), 0)} ${cases === 1 ? 'case' : 'cases'})`],
      [Number(t['consumablesCovered'] ?? 0), 'consumables covered'],
      [Number(t['medicineCovered'] ?? 0), 'medicine covered'],
      [Number(t['consumablesBilled'] ?? 0), 'consumables billed separately'],
      [Number(t['medicineBilled'] ?? 0), 'medicine billed separately'],
    ];
    return {
      total: parts.reduce((s, [v]) => s + v, 0),
      note: parts.map(([v, label]) => `${formatMoney(String(v), { dp: 0 })} ${label}`).join(' + '),
    };
  });
  /** Package lines billed plus medicine, both including VAT: the doctor table's totals, so the tile matches it. */
  info = packageTileInfo('package');
  /** A tile's info with this package's figures filled in. */
  withValues(i: TileInfo, values: string | null | undefined): TileInfo { return values ? { ...i, values } : i; }
  billedTile = computed(() => this.tile(['feeBilled', 'Invoice Billed'], ['consumablesBilled', 'consumables billed'], ['medicineBilled', 'medicine billed']));
  /** Claims on invoices HIS put on a claim (HIS_InvoiceMain.IsClaimed = 1): package, consumable and medicine lines. Blank until the facts carry the flag. */
  claimsReceivedTile = computed(() => {
    const rows = this.res.value()?.data.doctors ?? [];
    if (!rows.length || rows.every(r => r.claimsReceived == null)) return null;
    return rows.reduce((t, r) => t + Number(r.claimsReceived ?? 0), 0);
  });
  /** The billed parts, and how the doctor table's Consumables and Medicine columns differ from them (they add what the package covered). */
  billedValues = computed(() => {
    const b = this.billedTile(), t = this.doctorTotal() as Record<string, number> | null;
    if (!b || !t) return null;
    const m = (v: number) => formatMoney(String(v), { dp: 0 });
    const col = (label: string, billed: number, covered: number) =>
      `${label} column ${m(billed + covered)} = ${m(billed)} billed + ${m(covered)} covered (not billed)`;
    return `${b.note}. ${col('Consumables', Number(t['consumablesBilled'] ?? 0), Number(t['consumablesCovered'] ?? 0))}; `
      + `${col('Medicine', Number(t['medicineBilled'] ?? 0), Number(t['medicineCovered'] ?? 0))}.`;
  });
  private tile(...parts: [string, string][]): { total: number; note: string } | null {
    const t = this.doctorTotal() as Record<string, number> | null;
    if (!t) return null;
    const values = parts.map(([key, label]) => [Number(t[key] ?? 0), label] as const);
    return {
      total: values.reduce((s, [v]) => s + v, 0),
      note: values.map(([v, label]) => `${formatMoney(String(v), { dp: 0 })} ${label}`).join(' + '),
    };
  }
  /** Sum of the doctor rows; they add up to the package's figures on Packages (Performed can exceed Cases when an encounter has several doctors). */
  doctorTotal = computed(() => {
    const rows = this.res.value()?.data.doctors ?? [];
    if (!rows.length) return null;
    const sum = (f: (r: (typeof rows)[number]) => unknown) => rows.reduce((t, r) => t + Number(f(r) ?? 0), 0);
    return {
      quantity: sum(r => r.quantity), feeBilled: sum(r => r.feeBilled),
      consumablesBilled: sum(r => r.consumablesBilled), consumablesCovered: sum(r => r.consumablesCovered), medicineCovered: sum(r => r.medicineCovered), medicineBilled: sum(r => r.medicineBilled), claimed: sum(r => r.claimed), settled: sum(r => r.settled), claimsReceived: sum(r => r.claimsReceived), medicineClaimed: sum(r => r.medicineClaimed), consumablesClaimed: sum(r => r.consumablesClaimed),
      wardDays: sum(r => r.wardDays), encounters: sum(r => r.encounters),
    };
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
