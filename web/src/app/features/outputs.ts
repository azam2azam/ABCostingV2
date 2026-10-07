import { Component, input } from '@angular/core';
import { RouterLink } from '@angular/router';
import {
  ComponentDto, DrgDetail as DrgDetailDto, DrgRow, PackageDetail as PackageDetailDto, PackageRow, ProcedureDetail as ProcedureDetailDto, ProcedureRow,
} from '../core/api';
import { runResource } from '../core/resource';
import { Col, UI } from '../shared/ui';
import { allocationCols } from './cost-model';

export const componentCols: Col<ComponentDto>[] = [
  { key: 'label', label: 'Component', sub: r => r.calcExpression, link: r => r.resourceId ? `/resources/${r.resourceId}` : null },
  { key: 'costNature', label: 'Nature', type: 'nature' },
  { key: 'quantity', label: 'Quantity', type: 'num', dp: 4 },
  { key: 'uom', label: 'Unit' },
  { key: 'unitCost', label: 'Unit cost', type: 'num', dp: 6, value: r => r.unitCost ?? '••••' },
  { key: 'amount', label: 'Amount', type: 'money' },
  { key: 'share', label: 'Share', type: 'pct' },
];

const encounterCols: Col<any>[] = [
  { key: 'encounterNo', label: 'Encounter', link: r => `/patient-costing/${r.id}`, sub: r => `${r.patient} · ${r.mrn}` },
  { key: 'lengthOfStay', label: 'LOS', type: 'num', dp: 0 },
  { key: 'totalCost', label: 'Total cost', type: 'money' },
  { key: 'revenue', label: 'Revenue', type: 'money' },
  { key: 'operatingMargin', label: 'Margin', type: 'money', dir: 'higher' },
];

@Component({
  selector: 'v-procedures',
  imports: [...UI],
  template: `
    <v-page title="Procedures" purpose="Standard and actual cost per procedure. Open any one for the full cost explainer: why does this procedure cost what it does?">
      <v-state [loading]="res.isLoading()" [error]="res.error()">
        @if (res.value(); as r) { <v-table [rows]="r.data" [columns]="cols" caption="Procedures" /> }
      </v-state>
    </v-page>
  `,
})
export class Procedures {
  res = runResource<ProcedureRow[]>(() => 'procedures');
  cols: Col<ProcedureRow>[] = [
    { key: 'name', label: 'Procedure', link: r => `/procedures/${r.id}`, sub: r => r.state === 'COSTED' ? `${r.code} · ${r.department}` : r.state === 'NO_CASES' ? 'No cases this period.' : 'Not costed in this run: no costing definition under this version.' },
    { key: 'procType', label: 'Type' },
    { key: 'volume', label: 'Cases', type: 'num', dp: 0 },
    { key: 'standardUnit', label: 'Standard / case', type: 'money' },
    { key: 'actualUnit', label: 'Actual / case', type: 'money' },
    { key: 'variance', label: 'Variance', type: 'money', dp: 0, dir: 'lower' },
    { key: 'variancePct', label: 'Variance %', type: 'pct', dir: 'lower' },
    { key: 'revenue', label: 'Revenue', type: 'money', dp: 0 },
  ];
}

/**
 * The cost explainer (FR-11.7) — the primary acceptance test rendered (BRD 23.1): the total, every
 * component with its arithmetic, the chain down to source transactions, and the provenance.
 */
@Component({
  selector: 'v-procedure',
  imports: [RouterLink, ...UI],
  template: `
    <v-state [loading]="res.isLoading()" [error]="res.error()">
      @if (res.value(); as r) {
        <v-page [title]="'Why does ' + r.data.procedure.name + ' cost ' + (r.data.standardTotal | money) + '?'" [purpose]="r.data.procedure.code + ' · ' + r.data.procedure.department + ' · standard cost under ' + r.data.provenance.costingVersion">
          <div class="grid tiles">
            <v-tile label="Standard cost per case" [value]="r.data.standardTotal" />
            <v-tile label="Actual cost per case" [value]="r.data.procedure.actualUnit" />
            <v-tile label="Cases this period" [value]="r.data.procedure.volume" unit="" />
            <v-tile label="Variance" [value]="r.data.procedure.variance" [note]="(r.data.procedure.variancePct | pct) + ' against standard'" />
          </div>

          <section class="card" style="margin-top:14px">
            <h2>Components of the standard cost</h2>
            <v-table [rows]="r.data.standardComponents" [columns]="comp" caption="Standard cost components" />
          </section>

          <section class="card">
            <h2>Traceability chain</h2>
            <p class="muted">Procedure → the activities in its recipe → the resources each consumes and the overhead each received, with the rule and driver that sent it.</p>
            @for (a of r.data.chain; track a.activityId) {
              <details class="chain">
                <summary><a [routerLink]="['/activities', a.activityId]" queryParamsHandling="preserve">{{ a.activity }}</a> × {{ a.quantity | num:2 }}
                  <span class="faint">· {{ a.costCentre }} · overhead {{ a.overheadPerOccurrence | money }} per occurrence</span></summary>
                <h3>Resources</h3>
                <table class="data"><tbody>
                  @for (x of a.resources; track x.resourceId) {
                    <tr><td><a [routerLink]="['/resources', x.resourceId]" queryParamsHandling="preserve">{{ x.resource }}</a></td>
                        <td class="num">{{ x.stdQuantity | num:4 }} {{ x.uom }}</td><td class="num">× {{ x.rate ?? '••••' }}</td><td class="num">{{ x.lineCost | money }}</td></tr>
                  }
                </tbody></table>
                @if (a.overheadSources.length) {
                  <h3 style="margin-top:10px">Overhead received</h3>
                  <v-table [rows]="a.overheadSources" [columns]="alloc" />
                }
              </details>
            }
          </section>

          @if (r.data.variance; as v) {
            <section class="card">
              <h2>Actual against standard</h2>
              <p>{{ v.variance.summary }} <a [routerLink]="['/variance', v.variance.id]" queryParamsHandling="preserve">Decomposition →</a></p>
            </section>
          }

          <section class="card">
            <h2>Source transactions (latest)</h2>
            <v-table [rows]="r.data.sampleTransactions" [columns]="txnCols" empty="No transactions this period." />
          </section>

          <section class="card">
            <h2>Same procedure across the group</h2>
            <p class="muted">Joined on catalogue code; each hospital costs its own instance from its own rates and capacity (FR-11.9).</p>
            <v-table [rows]="r.data.acrossHospitals" [columns]="acrossCols" />
          </section>

          <section class="card">
            <h2>Provenance</h2>
            <dl class="kv">
              <dt>Run</dt><dd class="mono">{{ r.data.provenance.run }} ({{ r.data.provenance.runStatus }})</dd>
              <dt>Costing version</dt><dd>{{ r.data.provenance.costingVersion }}</dd>
              <dt>Hospital / period</dt><dd>{{ r.data.provenance.hospital }} · {{ r.data.provenance.period }}</dd>
              <dt>Triggered by</dt><dd>{{ r.data.provenance.triggeredBy }}</dd>
              <dt>Calculated</dt><dd>{{ r.data.provenance.calculatedAt ?? '—' }}</dd>
              <dt>Approved by</dt><dd>{{ r.data.provenance.approvedBy ?? 'Not approved' }}</dd>
              <dt>Published</dt><dd>{{ r.data.provenance.publishedAt ?? 'Not published' }}</dd>
            </dl>
          </section>
        </v-page>
      }
    </v-state>
  `,
  styles: `.chain { border-top: 1px solid var(--border); padding: 8px 0; } .chain summary { cursor: pointer; }`,
})
export class ProcedureDetail {
  id = input.required<string>();
  res = runResource<ProcedureDetailDto>(() => `procedures/${this.id()}`);
  comp = componentCols;
  alloc = allocationCols;
  txnCols: Col<ProcedureDetailDto['sampleTransactions'][number]>[] = [
    { key: 'encounterNo', label: 'Encounter' }, { key: 'activity', label: 'Activity' }, { key: 'resource', label: 'Resource' },
    { key: 'quantity', label: 'Quantity', type: 'num', dp: 2 }, { key: 'uom', label: 'Unit' }, { key: 'occurredAt', label: 'When', type: 'date' },
    { key: 'sourceSystem', label: 'Source system' }, { key: 'sourceRef', label: 'Source reference' },
  ];
  acrossCols: Col<ProcedureDetailDto['acrossHospitals'][number]>[] = [
    { key: 'hospitalName', label: 'Hospital' }, { key: 'run', label: 'Run', sub: r => r.runStatus },
    { key: 'standardUnit', label: 'Standard / case', type: 'money' }, { key: 'actualUnit', label: 'Actual / case', type: 'money' },
    { key: 'volume', label: 'Cases', type: 'num', dp: 0 },
  ];
}

@Component({
  selector: 'v-packages',
  imports: [...UI],
  template: `
    <v-page title="Packages" purpose="Bundled episodes at a contracted price. Does the price still cover the work?">
      <v-state [loading]="res.isLoading()" [error]="res.error()">
        @if (res.value(); as r) { <v-table [rows]="r.data" [columns]="cols" caption="Packages" /> }
      </v-state>
    </v-page>
  `,
})
export class Packages {
  res = runResource<PackageRow[]>(() => 'packages');
  cols: Col<PackageRow>[] = [
    { key: 'name', label: 'Package', link: r => `/packages/${r.id}`, sub: r => r.cases ? null : 'No cases this period.' },
    { key: 'price', label: 'Contracted price', type: 'money', dp: 0 },
    { key: 'cases', label: 'Cases', type: 'num', dp: 0 },
    { key: 'standardPerCase', label: 'Standard / case', type: 'money', dp: 0 },
    { key: 'actualPerCase', label: 'Actual / case', type: 'money', dp: 0 },
    { key: 'variance', label: 'Variance', type: 'money', dp: 0, dir: 'lower' },
    { key: 'operatingMargin', label: 'Margin', type: 'money', dp: 0, dir: 'higher' },
    { key: 'marginPct', label: 'Margin %', type: 'pct', dir: 'higher' },
  ];
}

@Component({
  selector: 'v-package',
  imports: [...UI],
  template: `
    <v-state [loading]="res.isLoading()" [error]="res.error()">
      @if (res.value(); as r) {
        <v-page [title]="r.data.package.name" [purpose]="r.data.package.description">
          <p class="notice">{{ r.data.explanation }}</p>
          <div class="grid tiles">
            <v-tile label="Contracted price" [value]="r.data.package.price" />
            <v-tile label="Standard per case" [value]="r.data.package.standardPerCase" />
            <v-tile label="Actual per case" [value]="r.data.package.actualPerCase" />
            <v-tile label="Operating margin" [value]="r.data.package.operatingMargin" direction="higher-better" />
          </div>
          <section class="card" style="margin-top:14px"><h2>Standard composition</h2><v-table [rows]="r.data.components" [columns]="compCols" /></section>
          <section class="card"><h2>Encounters</h2><v-table [rows]="r.data.encounters" [columns]="enc" empty="No cases this period." /></section>
        </v-page>
      }
    </v-state>
  `,
})
export class PackageDetail {
  id = input.required<string>();
  res = runResource<PackageDetailDto>(() => `packages/${this.id()}`);
  enc = encounterCols;
  compCols: Col<PackageDetailDto['components'][number]>[] = [
    { key: 'procedure', label: 'Procedure', link: r => `/procedures/${r.procedureId}` }, { key: 'quantity', label: 'Quantity', type: 'num', dp: 2 },
    { key: 'standardUnit', label: 'Standard unit', type: 'money' }, { key: 'standardLine', label: 'Line', type: 'money' },
  ];
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
