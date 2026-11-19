import { Component, computed, inject, signal } from '@angular/core';
import { toSignal } from '@angular/core/rxjs-interop';
import { ActivatedRoute, Router, RouterLink } from '@angular/router';
import { PackageDetail as PackageDetailDto, PackageRow } from '../core/api';
import { catalogueResource } from '../core/resource';
import { formatDec } from '../core/decimal';
import {
  Bench, COMPARE_PAGE_STYLES, CompareGrid, CompareToggles, Figures, Stat, figuresOf, initials, money, n, pickColumns, tileTotals, withValues,
} from '../shared/compare';
import { TypeCounts, UI, hisProcedureId } from '../shared/ui';
import { packageTileInfo } from '../shared/tile-info';

interface CataloguePage<T> { rows: T[]; total: number; page: number; pageSize: number; counts: TypeCounts }
type DoctorRow = PackageDetailDto['doctors'][number];

function rawOf(d: DoctorRow, price: number, coveredLos: number): Figures {
  return {
    performed: n(d.quantity), encounters: n(d.encounters), price, feeBilled: d.feeBilled,
    consumablesBilled: d.consumablesBilled, consumablesCovered: d.consumablesCovered, medicineBilled: d.medicineBilled, medicineCovered: d.medicineCovered,
    claimsReceived: d.claimsReceived, rejected: d.rejectedClaims, wardDays: d.wardDays, coveredLos,
  };
}

/** One doctor's figures on the package, as the package page's doctor table counts them. */
function statOf(d: DoctorRow, price: number, coveredLos: number): Stat {
  return {
    ...figuresOf(rawOf(d, price, coveredLos)),
    key: d.id ?? `code:${d.code}`, name: d.name,
    sub: [d.specialty, d.department].filter(x => x && x.trim()).join(' · ') || d.code,
    link: d.id ? ['/doctors', d.id] : null, avatar: initials(d.name),
    spread: (d.encounterRows ?? []).map(e => ({
      value: n(e.consumablesBilled) + n(e.consumablesCovered),
      label: `${e.encounterNo} · ${money(n(e.consumablesBilled) + n(e.consumablesCovered))} consumables`,
    })).sort((a, b) => b.value - a.value),
  };
}

@Component({
  selector: 'v-compare-doctors',
  imports: [RouterLink, CompareGrid, CompareToggles, ...UI],
  template: `
    <v-page title="Compare doctors" purpose="Doctors who performed one package, side by side: volume, what they consumed and billed, what HIS claimed, and length of stay.">
      <a actions class="btn" routerLink="/packages" queryParamsHandling="preserve">Back to packages</a>
      <v-state [loading]="packagesRes.isLoading() && !packagesRes.value()" [error]="packagesRes.error()">
        @if (packagesRes.value()) {
          @if (!packages().length) {
            <p class="notice">No billed packages in this period, so there are no doctors to compare.</p>
          } @else {
            <section class="card controls">
              <label class="pick">
                <span class="lbl">Package</span>
                <select (change)="pickPackage($any($event.target).value)" aria-label="Package">
                  @for (p of packages(); track p.id) {
                    <option [value]="p.id" [selected]="p.id === packageId()">{{ hisProcedureId(p.code) }} - {{ p.name }} · {{ p.cases }} {{ p.cases === 1 ? 'case' : 'cases' }}</option>
                  }
                </select>
              </label>
              <v-compare-toggles [(perCase)]="perCase" [(bench)]="bench" />
            </section>

            <v-state [loading]="res.isLoading() && !res.value()" [error]="res.error()">
              @if (res.value(); as r) {
                <div class="grid tiles">
                  <v-tile label="Cases" [value]="r.data.package.cases" unit="" [info]="info.performed" />
                  <v-tile label="Resources charged" [value]="totals()?.resources ?? null" [info]="withValues(info.resources, totals()?.resourcesNote)" />
                  <v-tile label="Amount billed" [value]="totals()?.billed ?? null" [info]="withValues(info.billed, totals()?.billedNote)" />
                  <v-tile label="Claimed" [value]="totals()?.claimed ?? null" [info]="info.claims" />
                </div>
                <v-compare-grid [class.reloading]="res.isLoading()" [cols]="cols()" [options]="all()" [bench]="bench()" [perCase]="perCase()"
                  noun="Doctor" spreadCaption="Consumables per encounter" avgLabel="package average" (pick)="pickDoctor($event.index, $event.key)" />
                @if (!all().length) { <p class="muted center">No billed package lines in {{ r.meta.period }}.</p> }
              }
            </v-state>
          }
        }
      </v-state>
    </v-page>
  `,
  styles: COMPARE_PAGE_STYLES,
})
/** Side by side comparison of up to four doctors on one package, from the run-stored package lines. */
export class CompareDoctors {
  private route = inject(ActivatedRoute);
  private router = inject(Router);
  private qp = toSignal(this.route.queryParamMap, { initialValue: this.route.snapshot.queryParamMap });
  hisProcedureId = hisProcedureId;
  withValues = withValues;
  info = packageTileInfo('package');

  perCase = signal(true);
  bench = signal<Bench>('volume');

  packagesRes = catalogueResource<CataloguePage<PackageRow>>(() => 'packages', () => ({ sort: 'cases', desc: true, page: 1, pageSize: 500 }));
  packages = computed(() => (this.packagesRes.value()?.data.rows ?? []).filter(p => p.cases > 0));
  packageId = computed(() => {
    const want = this.qp().get('package');
    const list = this.packages();
    return list.find(p => p.id === want)?.id ?? list[0]?.id ?? null;
  });
  res = catalogueResource<PackageDetailDto>(() => this.packageId() ? `packages/${this.packageId()}` : null);
  private current = computed(() => {
    const d = this.res.value()?.data;
    return d && d.package.id === this.packageId() ? d : null;
  });

  /** Every doctor on the package, busiest first. */
  all = computed<Stat[]>(() => {
    const d = this.current();
    if (!d) return [];
    return d.doctors.map(x => statOf(x, n(d.package.price), n(d.package.coveredLOS))).sort((a, b) => b.performed - a.performed);
  });
  /** The four columns: the doctors in ?doctors=, else the four busiest. */
  cols = computed(() => pickColumns(this.all(), this.qp().get('doctors')));

  /** The package's tiles, summed over all its doctors the same way as the package page (not just the four shown). */
  totals = computed(() => {
    const d = this.current();
    if (!d || !d.doctors.length) return null;
    const cases = n(d.package.cases), price = n(d.package.price);
    return tileTotals(d.doctors.map(x => rawOf(x, price, 0)), cases, price * cases,
      `contracted price (${money(price)} × ${formatDec(String(cases), 0)} ${cases === 1 ? 'case' : 'cases'})`);
  });

  pickPackage(id: string): void {
    this.router.navigate([], { queryParams: { package: id, doctors: null }, queryParamsHandling: 'merge', replaceUrl: true });
  }
  pickDoctor(i: number, key: string): void {
    const keys = this.cols().map(c => c?.key ?? '');
    keys[i] = key;
    this.router.navigate([], { queryParams: { package: this.packageId(), doctors: keys.join(',') }, queryParamsHandling: 'merge', replaceUrl: true });
  }
}
