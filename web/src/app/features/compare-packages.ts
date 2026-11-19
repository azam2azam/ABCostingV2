import { Component, computed, inject, signal } from '@angular/core';
import { toSignal } from '@angular/core/rxjs-interop';
import { ActivatedRoute, Router, RouterLink } from '@angular/router';
import { PackageRow } from '../core/api';
import { catalogueResource } from '../core/resource';
import {
  Bench, COMPARE_PAGE_STYLES, CompareGrid, CompareToggles, Figures, Stat, figuresOf, money, n, pickColumns, tileTotals, withValues,
} from '../shared/compare';
import { UI, hisProcedureId } from '../shared/ui';
import { packageTileInfo } from '../shared/tile-info';

/** The Departments list row, as far as this screen needs it. */
interface DepartmentRow { id: string; code: string; name: string; packages?: number }
/** A department's package row: the Packages list's figures for the department's encounters only, with what the package price covered. */
type DeptPackage = PackageRow & { consumablesCovered?: string | number | null; medicineCovered?: string | number | null };
interface DepartmentDetail {
  department: DepartmentRow;
  packages?: DeptPackage[] | null;
  /** Each package's figures split by the encounter doctor (id, code and name are the doctor's), by package id. */
  packageDoctors?: Record<string, PackageRow[]> | null;
}

function rawOf(p: DeptPackage): Figures {
  return {
    performed: n(p.cases), encounters: n(p.encounters), price: n(p.price), feeBilled: p.feeBilled,
    // On a department's rows Materials billed is the consumables billed separately (as the run stores them).
    consumablesBilled: p.materialsBilled, consumablesCovered: p.consumablesCovered, medicineBilled: p.medicineBilled, medicineCovered: p.medicineCovered,
    claimsReceived: p.claimsReceived, rejected: p.claimRejected, wardDays: p.wardDays, coveredLos: n(p.coveredLOS),
  };
}

/** One package's figures on the department's encounters. */
function statOf(p: DeptPackage, doctors: PackageRow[]): Stat {
  return {
    ...figuresOf(rawOf(p)),
    key: p.id, name: p.name, sub: `${hisProcedureId(p.code)} · ${money(n(p.price))}`,
    link: ['/packages', p.id], avatar: null,
    spread: doctors.map(d => ({ value: n(d.cases), label: `${d.name} · ${n(d.cases)} ${n(d.cases) === 1 ? 'case' : 'cases'}` }))
      .sort((a, b) => b.value - a.value),
  };
}

@Component({
  selector: 'v-compare-packages',
  imports: [RouterLink, CompareGrid, CompareToggles, ...UI],
  template: `
    <v-page title="Compare packages" purpose="Packages billed on one department's encounters, side by side: volume, what they consumed and billed, what HIS claimed, and length of stay.">
      <a actions class="btn" [routerLink]="departmentId() ? ['/departments', departmentId()] : ['/departments']" [queryParams]="{ department: null, packages: null }" queryParamsHandling="merge">Back to {{ departmentId() ? 'department' : 'departments' }}</a>
      <v-state [loading]="deptsRes.isLoading() && !deptsRes.value()" [error]="deptsRes.error()">
        @if (deptsRes.value()) {
          @if (!departments().length) {
            <p class="notice">No department billed a package in this period, so there are no packages to compare.</p>
          } @else {
            <section class="card controls">
              <label class="pick">
                <span class="lbl">Department</span>
                <select (change)="pickDepartment($any($event.target).value)" aria-label="Department">
                  @for (d of departments(); track d.id) {
                    <option [value]="d.id" [selected]="d.id === departmentId()">{{ d.name }} · {{ d.packages }} package {{ d.packages === 1 ? 'case' : 'cases' }}</option>
                  }
                </select>
              </label>
              <v-compare-toggles [(perCase)]="perCase" [(bench)]="bench" />
            </section>

            <v-state [loading]="res.isLoading() && !res.value()" [error]="res.error()">
              @if (res.value(); as r) {
                <div class="grid tiles">
                  <v-tile label="Cases" [value]="totals()?.cases ?? 0" unit="" [info]="info.performed" />
                  <v-tile label="Resources charged" [value]="totals()?.resources ?? null" [info]="withValues(info.resources, totals()?.resourcesNote)" />
                  <v-tile label="Amount billed" [value]="totals()?.billed ?? null" [info]="withValues(info.billed, totals()?.billedNote)" />
                  <v-tile label="Claimed" [value]="totals()?.claimed ?? null" [info]="info.claims" />
                </div>
                <v-compare-grid [class.reloading]="res.isLoading()" [cols]="cols()" [options]="all()" [bench]="bench()" [perCase]="perCase()"
                  noun="Package" spreadCaption="Cases per doctor" avgLabel="department average" (pick)="pickPackage($event.index, $event.key)" />
                @if (!all().length) { <p class="muted center">No packages billed on this department's encounters in {{ r.meta.period }}.</p> }
              }
            </v-state>
          }
        }
      </v-state>
    </v-page>
  `,
  styles: COMPARE_PAGE_STYLES,
})
/** Side by side comparison of up to four packages on one department's encounters, from the run-stored package lines. */
export class ComparePackages {
  private route = inject(ActivatedRoute);
  private router = inject(Router);
  private qp = toSignal(this.route.queryParamMap, { initialValue: this.route.snapshot.queryParamMap });
  withValues = withValues;
  info = packageTileInfo('department');

  perCase = signal(true);
  bench = signal<Bench>('volume');

  deptsRes = catalogueResource<DepartmentRow[]>(() => 'departments');
  /** Departments that billed a package this period, most package cases first. */
  departments = computed(() => (this.deptsRes.value()?.data ?? []).filter(d => n(d.packages) > 0).sort((a, b) => n(b.packages) - n(a.packages)));
  departmentId = computed(() => {
    const want = this.qp().get('department');
    const list = this.departments();
    return list.find(d => d.id === want)?.id ?? list[0]?.id ?? null;
  });
  res = catalogueResource<DepartmentDetail>(() => this.departmentId() ? `departments/${this.departmentId()}` : null);
  private current = computed(() => {
    const d = this.res.value()?.data;
    return d && d.department.id === this.departmentId() ? d : null;
  });

  /** Every package on the department's encounters, most cases first. */
  all = computed<Stat[]>(() => {
    const d = this.current();
    if (!d) return [];
    return (d.packages ?? []).map(p => statOf(p, d.packageDoctors?.[p.id] ?? [])).sort((a, b) => b.performed - a.performed);
  });
  /** The four columns: the packages in ?packages=, else the four with most cases. */
  cols = computed(() => pickColumns(this.all(), this.qp().get('packages')));

  /** The department's package tiles over all its packages (not just the four shown), as the department page adds them up. */
  totals = computed(() => {
    const rows = this.current()?.packages ?? [];
    if (!rows.length) return null;
    const cases = rows.reduce((t, p) => t + n(p.cases), 0);
    const priced = rows.reduce((t, p) => t + n(p.price) * n(p.cases), 0);
    return tileTotals(rows.map(rawOf), cases, priced, 'Σ (contracted price × cases)');
  });

  pickDepartment(id: string): void {
    this.router.navigate([], { queryParams: { department: id, packages: null }, queryParamsHandling: 'merge', replaceUrl: true });
  }
  pickPackage(i: number, key: string): void {
    const keys = this.cols().map(c => c?.key ?? '');
    keys[i] = key;
    this.router.navigate([], { queryParams: { department: this.departmentId(), packages: keys.join(',') }, queryParamsHandling: 'merge', replaceUrl: true });
  }
}
