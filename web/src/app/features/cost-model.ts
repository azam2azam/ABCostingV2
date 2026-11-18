import { Component, computed, inject, input, signal } from '@angular/core';
import { RouterLink } from '@angular/router';
import {
  ActivityDetail as ActivityDetailDto, ActivityRow, AllocationDto, CostCentreDetail as CostCentreDetailDto, CostCentreRow,
  Envelope, ResourceDetail as ResourceDetailDto, ResourceRow,
} from '../core/api';
import { Auth } from '../core/auth';
import { RunContext } from '../core/context';
import { apiResource, catalogueResource } from '../core/resource';
import { Col, TypeCounts, UI, hisProcedureId, matchesNameOrCode } from '../shared/ui';

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
  doctors: number;
  cases: number;
  revenue: number;
  cost: number;
  /** Package cases on the department's encounters in the run. */
  packages?: number;
  /** Every counted line on the department's encounters, once, net plus VAT. */
  amountBilled?: number | string;
  claimed?: number | string;
}

@Component({
  selector: 'v-departments',
  imports: [...UI],
  template: `
    <v-page title="Departments" purpose="The hospital's departments from the catalogue. When a calculation has been run for the period, cases, revenue and cost are shown too.">
      <v-state [loading]="res.isLoading()" [error]="res.error()">
        @if (res.value()) {
          <v-type-tiles [counts]="serviceLineCounts()" [(selected)]="serviceLine" />
          <v-table paged [rows]="filtered()" [columns]="cols()" sortBy="cases" caption="Departments"
            [empty]="search().trim() ? 'No departments match this search.' : 'No departments in the catalogue for this hospital.'">
            <v-list-search toolbar [(query)]="search" placeholder="Search by name or code" />
          </v-table>
        }
      </v-state>
    </v-page>
  `,
})
export class Departments {
  private auth = inject(Auth);
  res = catalogueResource<DepartmentRow[]>(() => 'departments');
  search = signal('');
  /** Service line tile selected; null is All. Departments without one are under "Not assigned". */
  serviceLine = signal<string | null>(null);
  private static readonly NoServiceLine = 'Not assigned';
  private searched = computed(() => (this.res.value()?.data ?? []).filter(r => matchesNameOrCode(this.search(), r.name, r.code)));
  /** Service lines with fewer departments than this share one "Others" tile. */
  private static readonly OthersBelow = 2;
  private static readonly Others = 'Others';
  private lineOf(r: DepartmentRow): string { return r.serviceLine?.trim() || Departments.NoServiceLine; }
  /** Departments per service line among those matching the search. */
  private lineCounts = computed(() => {
    const by = new Map<string, number>();
    for (const r of this.searched()) { const k = this.lineOf(r); by.set(k, (by.get(k) ?? 0) + 1); }
    return by;
  });
  /** The tile a department falls under: its service line, or Others when that line has fewer than two departments. */
  private tileOf(r: DepartmentRow): string {
    const k = this.lineOf(r);
    return (this.lineCounts().get(k) ?? 0) < Departments.OthersBelow ? Departments.Others : k;
  }
  /** Tiles most first, with the small service lines together in Others at the end. */
  serviceLineCounts = computed<TypeCounts>(() => {
    const by = this.lineCounts();
    const big = [...by].filter(([, n]) => n >= Departments.OthersBelow).map(([type, count]) => ({ type, count }))
      .sort((a, b) => b.count - a.count || a.type.localeCompare(b.type));
    const others = [...by].filter(([, n]) => n < Departments.OthersBelow).reduce((t, [, n]) => t + n, 0);
    return { all: this.searched().length, zeroPrice: null, types: others ? [...big, { type: Departments.Others, count: others }] : big };
  });
  filtered = computed(() => {
    const tile = this.serviceLine();
    return this.searched().filter(r => !tile || this.tileOf(r) === tile);
  });
  cols = computed<Col<DepartmentRow>[]>(() => {
    const cols: Col<DepartmentRow>[] = [
      { key: 'name', label: 'Name', link: r => `/departments/${r.id}` },
      { key: 'specialty', label: 'Specialty' },
      { key: 'serviceLine', label: 'Service line' },
      { key: 'doctors', label: 'Doctors', type: 'num', dp: 0 },
      { key: 'cases', label: 'Cases', type: 'num', dp: 0, link: () => '/patient-encounters',
        linkParams: r => ({ department: r.id }) },
      { key: 'packages', label: 'Packages', type: 'num', dp: 0, blankZero: true },
    ];
    if (this.auth.can('revenue.view')) cols.push(
      { key: 'amountBilled', label: 'Amount Billed', type: 'money', dp: 0, blankZero: true },
      { key: 'claimed', label: 'Claimed', type: 'money', dp: 0, blankZero: true },
    );
    return cols;
  });
}

// ── Doctors ─────────────────────────────────────────────────────────────────────

/** OpenAPI types catch up on the next api:types run. */
export interface DoctorRow {
  id: string;
  code: string;
  name: string;
  specialty: string;
  grade: string;
  departmentId: string;
  department: string | null;
  departments: number;
  isDummy: boolean;
  cases: number;
  revenue: number;
  cost: number;
  staffMinutes?: number | string;
  materials?: number | string;
}

interface DoctorDetailDto {
  doctor: DoctorRow;
  departments: { id: string; code: string; name: string; hisClinicId: string | null; isHome: boolean; isDefault: boolean; cases: number }[];
  packages: DoctorWork[];
  procedures: DoctorWork[];
  packageWork?: PackageWork[] | null;
  departmentWork?: PackageWork[] | null;
  procedureWork?: PackageWork[] | null;
  departmentWorkAll?: PackageWork[] | null;
}

type Amount = number | string;
/** One encounter of a package-work row, with that encounter's own figures. */
interface WorkEncounter {
  encounterId: string; encounterNo: string; performed: number; feeBilled: Amount; consumablesBilled: Amount; consumablesCovered: Amount;
  medicineBilled: Amount; medicineCovered: Amount; claimed: Amount; consumablesClaimed: Amount; medicineClaimed: Amount;
  settled: Amount; rejectedClaims: Amount; wardDays: Amount; claimsReceived: Amount | null;
  /** Claims received on the row's own lines only (a package's without the encounter's shared consumable / medicine lines). */
  claimsReceivedLines?: Amount | null;
}
/** The doctor's package work by package or by department (the run's stored rows), as the package page's doctor table shows it. */
interface PackageWork extends Omit<WorkEncounter, 'encounterId' | 'encounterNo'> {
  id: string | null; code: string; name: string; encounters: number; encounterRows: WorkEncounter[];
}
/** A work row, or one of its encounters shown under it. */
type WorkTableRow = PackageWork & { kind: 'group' | 'encounter'; encounterId?: string; groupKey: string };

interface DoctorWork {
  id: string; code: string; name: string; category: string; encounters: number; quantity: number | string;
  revenue: number | string; unitCost: number | string; cost: number | string; margin: number | string; marginPct: number | string;
}

@Component({
  selector: 'v-doctors',
  imports: [...UI],
  template: `
    <v-page title="Doctors" purpose="The hospital's doctors from the catalogue, with their home department and HIS clinics. When a calculation has been run for the period, cases, revenue and cost are shown too.">
      <v-state [loading]="res.isLoading()" [error]="res.error()">
        @if (res.value()) {
          <v-table paged [rows]="filtered()" [columns]="cols()" sortBy="cases" caption="Doctors"
            [empty]="search().trim() ? 'No doctors match this search.' : 'No doctors in the catalogue for this hospital.'">
            <v-list-search toolbar [(query)]="search" placeholder="Search by name or id" />
          </v-table>
        }
      </v-state>
    </v-page>
  `,
})
export class Doctors {
  private auth = inject(Auth);
  res = catalogueResource<DoctorRow[]>(() => 'doctors');
  search = signal('');
  filtered = computed(() => (this.res.value()?.data ?? []).filter(r => matchesNameOrCode(this.search(), r.name, r.code)));
  cols = computed<Col<DoctorRow>[]>(() => {
    const cols: Col<DoctorRow>[] = [
      { key: 'code', label: 'Id', value: r => localCode(r.code), minWidth: '9rem' },
      { key: 'name', label: 'Name', link: r => `/doctors/${r.id}`, sub: r => r.isDummy ? 'HIS placeholder' : null },
      { key: 'specialty', label: 'Specialty' },
      { key: 'grade', label: 'Grade' },
      { key: 'department', label: 'Home department', link: r => r.department ? `/departments/${r.departmentId}` : null },
      { key: 'departments', label: 'Departments', type: 'num', dp: 0 },
      { key: 'cases', label: 'Cases', type: 'num', dp: 0, link: () => '/patient-encounters',
        linkParams: r => ({ physician: r.id }) },
    ];
    if (this.auth.can('revenue.view')) cols.push({ key: 'revenue', label: 'Revenue', type: 'money', dp: 0 });
    // On the doctor's cases in the period: all staff minutes, and billed consumables, implants and medicine (charge, not cost).
    cols.push({ key: 'staffMinutes', label: 'Staff Min', type: 'num', dp: 0, blankZero: true });
    if (this.auth.can('revenue.view')) cols.push({ key: 'materials', label: 'Materials', type: 'money', dp: 0, blankZero: true });
    return cols;
  });
}

type DoctorWorkRow = DoctorDetailDto['packages'][number];

@Component({
  selector: 'v-doctor',
  imports: [...UI],
  template: `
    <v-state [loading]="res.isLoading()" [error]="res.error()">
      @if (res.value(); as r) {
        <v-page [title]="r.data.doctor.name" [purpose]="purpose(r.data.doctor)">
          <label class="check" style="display:inline-flex;gap:6px;align-items:center;margin:0 0 10px">
            <input type="checkbox" [checked]="includeAll()" (change)="includeAll.set($any($event.target).checked)" /> Include non-package items
          </label>
          <div class="grid tiles">
            <v-tile label="Cases" [value]="casesTile()" unit="" />
            <v-tile label="Amount billed" [value]="billedTile()" />
            <v-tile label="Claims Received" [value]="claimsReceivedTile()" />
          </div>
          <section class="card" style="margin-top:14px">
            <h2>Departments</h2>
            <v-table [rows]="deptRows()" [columns]="deptWorkCols" [total]="workSum(includeAll() ? r.data.departmentWorkAll : r.data.departmentWork)" caption="Departments"
              empty="No packages billed under this doctor in this period." />
          </section>
          <section class="card">
            <h2>Packages performed</h2>
            <v-table [rows]="packageRows()" [columns]="packageWorkCols" [total]="workSum(r.data.packageWork)" caption="Packages performed"
              empty="No packages billed under this doctor in this period." />
          </section>
          @if (includeAll()) {
          <section class="card">
            <h2>Procedures performed (not packaged)</h2>
            <v-table paged [rows]="procedureRows()" [columns]="procedureWorkCols" [total]="workSum(r.data.procedureWork)" caption="Procedures performed (not packaged)"
              empty="No procedures billed under this doctor in this period." />
          </section>
          }
        </v-page>
      }
    </v-state>
  `,
})
export class DoctorDetail {
  id = input.required<string>();
  private auth = inject(Auth);
  res = catalogueResource<DoctorDetailDto>(() => `doctors/${this.id()}`);
  canRevenue = computed(() => this.auth.can('revenue.view'));
  /** Adds the doctor's non-package procedures to the tiles and the Departments list, and shows the procedures list. */
  includeAll = signal(false);
  /**
   * The Performed totals of the lists: package cases (one admission can bill several packages), plus, with non-package
   * items, the procedures performed (not packaged).
   */
  casesTile = computed(() => {
    const d = this.res.value()?.data;
    const sum = (rows: PackageWork[] | null | undefined) => (rows ?? []).reduce((t, r) => t + Number(r.performed), 0);
    return sum(d?.packageWork) + (this.includeAll() ? sum(d?.procedureWork) : 0);
  });
  /**
   * Package lines billed plus consumables and medicine billed, as on the package page's Amount billed tile; with non-package
   * items, every line under the doctor once (package lines plus procedure lines).
   */
  billedTile = computed(() => {
    const d = this.res.value()?.data;
    const pkgs = d?.packageWork ?? [], procs = this.includeAll() ? d?.procedureWork ?? [] : [];
    if (!pkgs.length && !procs.length) return null;
    // With non-package items, lines only: the package's shared consumables and medicine are the admission's general lines,
    // which are procedure lines already, so adding both would count them twice.
    if (this.includeAll()) return [...pkgs, ...procs].reduce((t, r) => t + Number(r.feeBilled), 0);
    return pkgs.reduce((t, r) => t + Number(r.feeBilled) + Number(r.consumablesBilled) + Number(r.medicineBilled), 0);
  });
  /**
   * Claims on invoices HIS put on a claim (IsClaimed); blank until the facts carry the flag. With non-package items, lines
   * only: the package's own lines plus the procedure lines, since the shared consumable / medicine lines are procedure lines.
   */
  claimsReceivedTile = computed(() => {
    const d = this.res.value()?.data;
    const pkgs = d?.packageWork ?? [], procs = this.includeAll() ? d?.procedureWork ?? [] : [];
    const rows = [...pkgs, ...procs];
    if (!rows.length || rows.every(r => r.claimsReceived == null)) return null;
    if (!this.includeAll()) return pkgs.reduce((t, r) => t + Number(r.claimsReceived ?? 0), 0);
    return pkgs.reduce((t, r) => t + Number(r.claimsReceivedLines ?? 0), 0) + procs.reduce((t, r) => t + Number(r.claimsReceived ?? 0), 0);
  });
  /** Rows whose encounters are shown under them, per table. */
  openRows = signal<ReadonlySet<string>>(new Set());
  private toggleRow(r: WorkTableRow): void {
    this.openRows.update(open => { const next = new Set(open); if (next.has(r.groupKey)) next.delete(r.groupKey); else next.add(r.groupKey); return next; });
  }
  private flatten(rows: PackageWork[] | null | undefined, table: string): WorkTableRow[] {
    const open = this.openRows();
    return (rows ?? []).flatMap(g => {
      const groupKey = `${table}:${g.id ?? 'none'}`;
      const row: WorkTableRow = { ...g, kind: 'group', groupKey };
      if (!open.has(groupKey)) return [row];
      return [row, ...g.encounterRows.map(e => ({ ...g, ...e, kind: 'encounter' as const, groupKey, name: e.encounterNo, encounters: 1, performed: 0 }))];
    });
  }
  deptRows = computed(() => {
    const d = this.res.value()?.data;
    return this.flatten(this.includeAll() ? d?.departmentWorkAll : d?.departmentWork, 'dept');
  });
  packageRows = computed(() => this.flatten(this.res.value()?.data.packageWork, 'pkg'));
  procedureRows = computed(() => this.flatten(this.res.value()?.data.procedureWork, 'proc'));
  /** Totals of the group rows (encounter rows are not added again). */
  workSum(rows: PackageWork[] | null | undefined): Record<string, number> | null {
    if (!rows?.length) return null;
    const sum = (f: (r: PackageWork) => Amount | null) => rows.reduce((t, r) => t + Number(f(r) ?? 0), 0);
    return {
      performed: sum(r => r.performed), feeBilled: sum(r => r.feeBilled), consumablesBilled: sum(r => r.consumablesBilled), consumablesCovered: sum(r => r.consumablesCovered),
      medicineBilled: sum(r => r.medicineBilled), medicineCovered: sum(r => r.medicineCovered), wardDays: sum(r => r.wardDays), encounters: sum(r => r.encounters),
      claimed: sum(r => r.claimed), consumablesClaimed: sum(r => r.consumablesClaimed), medicineClaimed: sum(r => r.medicineClaimed), settled: sum(r => r.settled),
      claimsReceived: sum(r => r.claimsReceived),
    };
  }
  /** The package page's doctor table columns, with the group (department or package) in the first column. */
  private workTableCols(label: string, link: (r: WorkTableRow) => string | null): Col<WorkTableRow>[] {
    return [
      { key: 'toggle', label: '', type: 'button', sortable: false, icon: () => 'chevron',
        button: r => r.kind === 'group' && r.encounterRows.length ? (this.openRows().has(r.groupKey) ? 'Hide encounters' : 'Show encounters') : null,
        expanded: r => r.kind === 'group' && this.openRows().has(r.groupKey), onButton: r => this.toggleRow(r) },
      { key: 'name', label, minWidth: '16rem', sortable: false,
        value: r => r.kind === 'encounter' ? `\u00a0\u00a0\u00a0${r.name}` : r.kind === 'group' && r.code && label !== 'Department' ? `${hisProcedureId(r.code)} - ${r.name}` : r.name,
        link: r => r.kind === 'encounter' ? `/patient-encounters/${r.encounterId}` : link(r) },
      { key: 'performed', label: 'Performed', type: 'num', dp: 0, sortable: false, blankZero: true },
      { key: 'feeBilled', label: 'Amount billed', type: 'money', dp: 0, blankZero: true, sortable: false },
      { key: 'consumablesAll', label: 'Consumables', type: 'money', dp: 0, blankZero: true, sortable: false,
        value: r => Number(r.consumablesBilled ?? 0) + Number(r.consumablesCovered ?? 0) },
      { key: 'medicineAll', label: 'Medicine', type: 'money', dp: 0, blankZero: true, sortable: false,
        value: r => Number(r.medicineBilled ?? 0) + Number(r.medicineCovered ?? 0) },
      { key: 'avgLos', label: 'Avg. LOS', type: 'num', dp: 1, blankZero: true, sortable: false,
        value: r => Number(r.encounters) ? Number(r.wardDays ?? 0) / Number(r.encounters) : 0 },
      { key: 'allClaimed', label: 'Claimed', type: 'money', dp: 0, blankZero: true, sortable: false, bad: r => Number(r.rejectedClaims) > 0,
        value: r => Number(r.claimed) + Number(r.consumablesClaimed ?? 0) + Number(r.medicineClaimed ?? 0) },
      { key: 'claimsReceived', label: 'Claims received', type: 'money', dp: 0, blankZero: true, sortable: false },
    ];
  }
  deptWorkCols = this.workTableCols('Department', r => r.id ? `/departments/${r.id}` : null);
  packageWorkCols = this.workTableCols('Package', r => r.id ? `/packages/${r.id}` : null);
  procedureWorkCols = this.workTableCols('Procedure', r => r.id ? `/procedures/${r.id}` : null);
  deptCols: Col<DoctorDetailDto['departments'][number]>[] = [
    { key: 'code', label: 'Code', value: r => localCode(r.code) },
    { key: 'name', label: 'Department', link: r => `/departments/${r.id}` },
    { key: 'hisClinicId', label: 'HIS clinic' },
    { key: 'isHome', label: 'Link', value: r => r.isHome ? 'Home department' : r.isDefault ? 'Default clinic' : r.hisClinicId ? 'Clinic' : 'Encounters only' },
    { key: 'cases', label: 'Cases', type: 'num', dp: 0 },
  ];
  workCols(kind: 'packages' | 'procedures'): Col<DoctorWorkRow>[] {
    const cols: Col<DoctorWorkRow>[] = [
      { key: 'code', label: 'Id', value: r => hisProcedureId(r.code), link: r => `/${kind}/${r.id}` },
      { key: 'name', label: kind === 'packages' ? 'Package' : 'Procedure', link: r => `/${kind}/${r.id}` },
      { key: 'category', label: kind === 'packages' ? 'Type' : 'Category', value: r => r.category || '—' },
      { key: 'encounters', label: 'Encounters', type: 'num', dp: 0 },
      { key: 'quantity', label: 'Quantity', type: 'num', dp: 0 },
    ];
    if (this.canRevenue()) cols.push({ key: 'revenue', label: 'Revenue', type: 'money', dp: 0 });
    cols.push({ key: 'unitCost', label: 'Unit cost', type: 'money', dp: 0 }, { key: 'cost', label: 'Cost', type: 'money', dp: 0, bad: r => this.canRevenue() && Number(r.cost) > Number(r.revenue) });
    if (this.canRevenue()) cols.push(
      { key: 'margin', label: 'Margin', type: 'money', dp: 0, bad: r => Number(r.margin) < 0 },
      { key: 'marginPct', label: 'Margin %', value: r => Number(r.revenue) ? `${Number(r.marginPct).toFixed(1)}%` : '—', bad: r => Number(r.margin) < 0 },
    );
    return cols;
  }
  workTotal(rows: DoctorWorkRow[]): Record<string, number> | null {
    if (!rows.length) return null;
    const sum = (k: keyof DoctorWorkRow) => rows.reduce((t, r) => t + Number(r[k]), 0);
    return { quantity: sum('quantity'), revenue: sum('revenue'), cost: sum('cost'), margin: sum('margin') };
  }
  purpose(d: DoctorRow): string {
    return [localCode(d.code), d.specialty, d.grade, d.department, d.isDummy ? 'HIS placeholder' : null].filter(Boolean).join(' · ');
  }
  margin(d: DoctorRow): number { return Number(d.revenue) - Number(d.cost); }
  marginPct(d: DoctorRow): number {
    const rev = Number(d.revenue);
    return rev === 0 ? 0 : this.margin(d) / rev * 100;
  }
}

/** OpenAPI types catch up on the next api:types run. */
type CcRow =CostCentreRow & { departmentId?: string };

interface DepartmentDetailDto {
  department: DepartmentRow;
  costCentres: CcRow[];
  doctors: { id: string; code: string; name: string; specialty: string; grade: string; isHome: boolean; isDefault: boolean; isDummy: boolean; cases: number }[];
}

@Component({
  selector: 'v-department',
  imports: [...UI],
  template: `
    <v-state [loading]="res.isLoading()" [error]="res.error()">
      @if (res.value(); as r) {
        <v-page [title]="r.data.department.name" [purpose]="purpose(r.data.department)">
          <div class="grid tiles">
            <v-tile label="Cases" [value]="r.data.department.cases" unit="" />
            @if (canRevenue()) { <v-tile label="Revenue" [value]="r.data.department.revenue" /> }
            <v-tile label="Cost" [value]="r.data.department.cost" />
            @if (canRevenue()) {
              <v-tile label="Margin" [value]="margin(r.data.department)" direction="higher-better" />
              <v-tile label="Margin %" [value]="marginPct(r.data.department)" unit="%" direction="higher-better" />
            }
            <v-tile label="Cost centres" [value]="r.data.costCentres.length" unit="" />
            <v-tile label="Doctors" [value]="realDoctors(r.data)" unit="" />
          </div>
          <section class="card" style="margin-top:14px">
            <h2>Cost centres</h2>
            <v-table [rows]="r.data.costCentres" [columns]="ccCols()" sortBy="cases" caption="Cost centres in this department"
              empty="No cost centre is linked to this department." />
          </section>
          <section class="card">
            <h2>Doctors</h2>
            <v-table paged [rows]="r.data.doctors" [columns]="doctorCols" sortBy="cases" caption="Doctors in this department"
              empty="No doctors work in this department." />
          </section>
        </v-page>
      }
    </v-state>
  `,
})
export class DepartmentDetail {
  id = input.required<string>();
  private auth = inject(Auth);
  res = catalogueResource<DepartmentDetailDto>(() => `departments/${this.id()}`);
  canRevenue = computed(() => this.auth.can('revenue.view'));
  ccCols = computed<Col<CcRow>[]>(() => {
    const cols: Col<CcRow>[] = [
      { key: 'code', label: 'Code', value: r => localCode(r.code) },
      { key: 'name', label: 'Name', link: r => `/cost-centres/${r.id}` },
      { key: 'ccType', label: 'Type' },
      { key: 'cases', label: 'Cases', type: 'num', dp: 0 },
      { key: 'cost', label: 'Cost', type: 'money', dp: 0 },
    ];
    if (this.canRevenue()) cols.push({ key: 'revenue', label: 'Revenue', type: 'money', dp: 0 });
    return cols;
  });
  doctorCols: Col<DepartmentDetailDto['doctors'][number]>[] = [
    { key: 'code', label: 'Id', value: r => localCode(r.code) },
    { key: 'name', label: 'Doctor', sub: r => r.isDummy ? 'HIS placeholder' : null },
    { key: 'specialty', label: 'Specialty' },
    { key: 'grade', label: 'Grade' },
    { key: 'isHome', label: 'Home department', value: r => r.isHome ? 'Yes' : r.isDefault ? 'Default clinic' : 'Clinic' },
    { key: 'cases', label: 'Cases', type: 'num', dp: 0 },
  ];
  purpose(d: DepartmentRow): string {
    return [localCode(d.code), d.deptType, d.specialty, d.serviceLine].filter(Boolean).join(' · ');
  }
  realDoctors(d: DepartmentDetailDto): number { return d.doctors.filter(x => !x.isDummy).length; }
  margin(d: DepartmentRow): number { return Number(d.revenue) - Number(d.cost); }
  marginPct(d: DepartmentRow): number {
    const rev = Number(d.revenue);
    return rev === 0 ? 0 : this.margin(d) / rev * 100;
  }
}

// ── Cost centres ────────────────────────────────────────────────────────────────

@Component({
  selector: 'v-cost-centres',
  imports: [...UI],
  template: `
    <v-page title="Cost centres" purpose="Where ledger money lands and where it goes next. Support and overhead centres close at zero after step-down.">
      <v-state [loading]="res.isLoading()" [error]="res.error()">
        @if (res.value()) {
          <v-table paged [rows]="filtered()" [columns]="cols" sortBy="cases" caption="Cost centres" [empty]="search().trim() ? 'No cost centres match this search.' : 'No rows.'">
            <v-list-search toolbar [(query)]="search" placeholder="Search by name or code" />
          </v-table>
        }
      </v-state>
    </v-page>
  `,
})
export class CostCentres {
  res = catalogueResource<CcRow[]>(() => 'cost-centres');
  search = signal('');
  filtered = computed(() => (this.res.value()?.data ?? []).filter(r => matchesNameOrCode(this.search(), r.name, r.code)));
  cols: Col<CcRow>[] = [
    { key: 'stepDownSeq', label: 'Seq', type: 'num', dp: 0 },
    { key: 'code', label: 'Code', value: r => localCode(r.code) },
    { key: 'name', label: 'Name', link: r => `/cost-centres/${r.id}` },
    { key: 'department', label: 'Department', link: r => r.departmentId ? `/departments/${r.departmentId}` : null },
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
