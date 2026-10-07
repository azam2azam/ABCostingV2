import { Component, computed, inject, input, signal } from '@angular/core';
import { RouterLink } from '@angular/router';
import { firstValueFrom } from 'rxjs';
import { Api, CostingModelsDto, EncounterDetail as EncounterDetailDto, EncounterPage, ReconDto, RunDetail as RunDetailDto, RunListItem, RuleRow } from '../core/api';
import { Auth, toApiError } from '../core/auth';
import { RunContext } from '../core/context';
import { apiResource, runResource } from '../core/resource';
import { Col, UI } from '../shared/ui';
import { ruleCols } from './cost-model';
import { componentCols } from './outputs';

// ── Configuration ───────────────────────────────────────────────────────────────

@Component({
  selector: 'v-costing-models',
  imports: [...UI],
  template: `
    <v-page title="Costing models" purpose="Versioned costing definitions. A published version is never edited; a change is a new version, and the diff below is computed, not described (FR-10.6).">
      <v-state [loading]="res.isLoading()" [error]="res.error()">
        @if (res.value(); as r) {
          <v-table [rows]="r.versions" [columns]="cols" caption="Costing versions" />
          @for (d of r.diffs; track d.toVersion) {
            <section class="card" style="margin-top:14px">
              <h2>{{ d.fromVersion }} → {{ d.toVersion }}</h2>
              <v-table [rows]="d.changes" [columns]="diffCols" empty="No recipe changes." />
            </section>
          }
          <section class="card"><h2>Approval history</h2><v-table [rows]="r.approvals" [columns]="apprCols" /></section>
        }
      </v-state>
    </v-page>
  `,
})
export class CostingModels {
  res = apiResource<CostingModelsDto>(() => 'costing-models');
  cols: Col<CostingModelsDto['versions'][number]>[] = [
    { key: 'name', label: 'Version', sub: r => r.description }, { key: 'code', label: 'Code' },
    { key: 'effectiveFrom', label: 'From' }, { key: 'effectiveTo', label: 'To', value: r => r.effectiveTo ?? 'open' },
    { key: 'status', label: 'Status', type: 'chip' }, { key: 'definitions', label: 'Definitions', type: 'num', dp: 0 }, { key: 'runs', label: 'Runs', type: 'num', dp: 0 },
  ];
  diffCols: Col<CostingModelsDto['diffs'][number]['changes'][number]>[] = [
    { key: 'procedure', label: 'Procedure' }, { key: 'kind', label: 'Line' }, { key: 'item', label: 'Item' },
    { key: 'from', label: 'From', type: 'num', dp: 4 }, { key: 'to', label: 'To', type: 'num', dp: 4 },
  ];
  apprCols = approvalCols;
}

export const approvalCols: Col<any>[] = [
  { key: 'entityLabel', label: 'Item', sub: r => r.requestNote }, { key: 'entityType', label: 'Type' }, { key: 'stage', label: 'Stage', type: 'chip' },
  { key: 'requestedBy', label: 'Requested by' }, { key: 'reviewedBy', label: 'Reviewed by', sub: r => r.reviewNote },
  { key: 'requestedAt', label: 'Requested', type: 'date' },
];

@Component({
  selector: 'v-allocation-rules',
  imports: [...UI],
  template: `
    <v-page title="Allocation rules" purpose="How each pool is distributed: stage, methodology, driver, pool share, and the money each rule moved in this run.">
      <v-state [loading]="res.isLoading()" [error]="res.error()">
        @if (res.value(); as r) { <v-table [rows]="r.data" [columns]="cols" caption="Allocation rules" /> }
      </v-state>
    </v-page>
  `,
})
export class AllocationRules {
  res = runResource<RuleRow[]>(() => 'allocation-rules');
  cols = ruleCols;
}

// ── Runs ────────────────────────────────────────────────────────────────────────

@Component({
  selector: 'v-runs',
  imports: [RouterLink, ...UI],
  template: `
    <v-page title="Calculation runs" purpose="The only way costs are produced: one hospital, one period, one costing version, eleven observable stages.">
      @if (canRun()) {
        <div actions class="row">
          <select [value]="hospital()" (change)="hospital.set($any($event.target).value)" aria-label="Hospital">
            @for (h of ctx.context()?.hospitals ?? []; track h.code) { <option [value]="h.code">{{ h.name }}</option> }
          </select>
          <select [value]="period()" (change)="period.set($any($event.target).value)" aria-label="Period">
            @for (p of ctx.context()?.periods ?? []; track p.code) { <option [value]="p.code">{{ p.code }}</option> }
          </select>
          <button class="btn primary" type="button" (click)="create()" [disabled]="busy()">New run</button>
        </div>
      }
      @if (message()) { <p class="notice">{{ message() }}</p> }
      <v-table [rows]="ctx.runs()" [columns]="cols" caption="Runs" />
    </v-page>
  `,
})
export class Runs {
  ctx = inject(RunContext);
  private api = inject(Api);
  private auth = inject(Auth);
  canRun = computed(() => this.auth.can('run.execute'));
  hospital = signal('');
  period = signal('');
  busy = signal(false);
  message = signal<string | null>(null);
  cols: Col<RunListItem>[] = [
    { key: 'code', label: 'Run', link: r => `/runs/${r.code}`, sub: r => r.errorMessage ?? r.name },
    { key: 'hospital', label: 'Hospital' }, { key: 'period', label: 'Period' }, { key: 'costingVersion', label: 'Version' },
    { key: 'status', label: 'Status', type: 'chip' }, { key: 'forced', label: 'Forced', value: r => r.forced ? 'Forced — cannot publish' : '' },
    { key: 'durationMs', label: 'ms', type: 'num', dp: 0 }, { key: 'triggeredBy', label: 'Triggered by' },
  ];

  async create(): Promise<void> {
    const h = this.hospital() || this.ctx.context()?.hospitals[0]?.code;
    const p = this.period() || this.ctx.context()?.periods.at(-1)?.code;
    this.busy.set(true);
    try {
      const run = await firstValueFrom(this.api.post<RunListItem>('runs', { hospital: h, period: p }));
      await firstValueFrom(this.api.post(`runs/${run.code}/execute`, { force: false }));
      this.message.set(`${run.code} created and queued. Open it to watch its stages.`);
      await this.ctx.load(true);
    } catch (e) { this.message.set(toApiError(e).message); }
    finally { this.busy.set(false); }
  }
}

@Component({
  selector: 'v-run',
  imports: [RouterLink, ...UI],
  template: `
    <v-state [loading]="res.isLoading() && !res.value()" [error]="res.error()">
      @if (res.value(); as r) {
        <v-page [title]="r.data.run.code" [purpose]="r.data.run.name">
          <div actions class="row">
            <a class="btn" [routerLink]="['/reconciliation']" [queryParams]="{ run: r.data.run.code }">Reconciliation</a>
            @if (canRun() && r.data.run.status !== 'PUBLISHED') {
              <button class="btn" type="button" (click)="execute(false)" [disabled]="busy()">Re-run</button>
              <button class="btn" type="button" (click)="execute(true)" [disabled]="busy()" title="Proceed past ERROR issues; the run can then never be published (FR-9.8)">Re-run with force</button>
            }
            @if (canRequest() && r.data.run.status === 'RECONCILED' && !r.data.publishBlockers.length) { <button class="btn" type="button" (click)="request()" [disabled]="busy()">Request approval</button> }
            @if (canPublish() && r.data.run.status === 'APPROVED') { <button class="btn primary" type="button" (click)="publish()" [disabled]="busy()">Publish</button> }
          </div>
          @if (message()) { <p class="notice">{{ message() }}</p> }
          @if (r.data.publishBlockers.length) {
            <div class="notice warn"><strong>Cannot be published:</strong>
              <ul>@for (b of r.data.publishBlockers; track b) { <li>{{ b }}</li> }</ul></div>
          }
          <section class="card">
            <h2>Stages — money in and out (FR-2.4)</h2>
            <v-table [rows]="r.data.steps" [columns]="stepCols" caption="Run stages" />
          </section>
          <section class="card"><h2>Approvals</h2><v-table [rows]="r.data.approvals" [columns]="appr" empty="No approval requested." /></section>
        </v-page>
      }
    </v-state>
  `,
})
export class RunDetail {
  id = input.required<string>();
  private api = inject(Api);
  private auth = inject(Auth);
  private ctx = inject(RunContext);
  res = apiResource<{ meta: unknown; data: RunDetailDto }>(() => `runs/${this.id()}`);
  canRun = computed(() => this.auth.can('run.execute'));
  canRequest = computed(() => this.auth.can('approval.request'));
  canPublish = computed(() => this.auth.can('run.publish'));
  busy = signal(false);
  message = signal<string | null>(null);
  appr = approvalCols;
  stepCols: Col<RunDetailDto['steps'][number]>[] = [
    { key: 'stepNo', label: '#', type: 'num', dp: 0 }, { key: 'stepName', label: 'Stage', sub: r => r.message },
    { key: 'status', label: 'Status', type: 'chip' }, { key: 'moneyIn', label: 'Money in', type: 'money' },
    { key: 'moneyOut', label: 'Money out', type: 'money' }, { key: 'gap', label: 'Gap', type: 'money', dir: 'neutral' },
    { key: 'rowsWritten', label: 'Rows', type: 'num', dp: 0 }, { key: 'durationMs', label: 'ms', type: 'num', dp: 0 },
  ];

  private async act(fn: () => Promise<unknown>, ok: string): Promise<void> {
    this.busy.set(true);
    try { await fn(); this.message.set(ok); } catch (e) {
      const err = toApiError(e);
      const reasons = (err.detail as { reasons?: string[] } | undefined)?.reasons;
      this.message.set(reasons ? `${err.message} ${reasons.join(' ')}` : err.message);
    } finally {
      this.busy.set(false);
      this.res.reload();
      await this.ctx.load(true);
    }
  }
  execute(force: boolean) { return this.act(() => firstValueFrom(this.api.post(`runs/${this.id()}/execute`, { force })), 'Queued. Stages appear below as they complete — refresh to update.'); }
  request() {
    const runId = this.res.value()!.data.run.id;
    return this.act(() => firstValueFrom(this.api.post('approvals', { entityType: 'CALCULATION_RUN', entityId: runId, note: 'Reconciled; requesting finance approval to publish.' })), 'Approval requested.');
  }
  publish() { return this.act(() => firstValueFrom(this.api.post(`runs/${this.id()}/transition`, { to: 'PUBLISHED' })), 'Published. This run is now immutable.'); }
}

// ── Reconciliation ──────────────────────────────────────────────────────────────

@Component({
  selector: 'v-reconciliation',
  imports: [...UI],
  template: `
    <v-page title="Reconciliation" purpose="Ledger = patient cost + named residuals + unexplained. Nothing is forced to balance (FR-7).">
      <v-state [loading]="res.isLoading()" [error]="res.error()">
        @if (res.value(); as r) {
          <p class="notice" [class.bad]="r.data.status === 'FAIL'">{{ r.data.narrative }}</p>
          <div class="grid tiles">
            <v-tile label="General ledger" [value]="r.data.glTotal" />
            <v-tile label="Attributed to patients" [value]="r.data.patientCost" />
            <v-tile label="Attribution" [value]="r.data.attributionPct" unit="%" />
            <v-tile label="Named residuals" [value]="r.data.residualTotal" />
            <v-tile label="Unexplained" [value]="r.data.unexplained" [note]="'Materiality ' + (r.data.materiality | money)" />
          </div>
          <section class="card" style="margin-top:14px"><h2>Statement</h2><v-table [rows]="r.data.lines" [columns]="lineCols" [caption]="'Reconciliation statement'" /></section>
          <section class="card"><h2>Residual buckets</h2><v-table [rows]="r.data.residuals" [columns]="resCols" /></section>
          <div class="grid two">
            <section class="card"><h2>Ledger by natural class</h2><v-table [rows]="r.data.byNaturalClass" [columns]="ledgerCols" /></section>
            <section class="card"><h2>Ledger by account</h2><v-table [rows]="r.data.byAccount" [columns]="ledgerCols" /></section>
          </div>
          <section class="card"><h2>Ledger by cost centre</h2><v-table [rows]="r.data.byCostCentre" [columns]="ledgerCols" /></section>
        }
      </v-state>
    </v-page>
  `,
})
export class Reconciliation {
  res = runResource<ReconDto>(() => 'reconciliation');
  lineCols: Col<ReconDto['lines'][number]>[] = [
    { key: 'label', label: 'Line', sub: r => r.explanation }, { key: 'lineType', label: 'Type' },
    { key: 'amount', label: 'Amount', type: 'money' }, { key: 'runningBalance', label: 'Running balance', type: 'money' },
  ];
  resCols: Col<ReconDto['residuals'][number]>[] = [
    { key: 'label', label: 'Bucket', sub: r => r.explanation }, { key: 'bucket', label: 'Code' }, { key: 'owner', label: 'Owner' },
    { key: 'amount', label: 'Amount', type: 'money' },
  ];
  ledgerCols: Col<ReconDto['byAccount'][number]>[] = [
    { key: 'label', label: 'Item' }, { key: 'amount', label: 'Amount', type: 'money' }, { key: 'share', label: 'Share', type: 'pct' },
  ];
}

// ── Patient costing ─────────────────────────────────────────────────────────────

@Component({
  selector: 'v-patient-costing',
  imports: [...UI],
  template: `
    <v-page title="Patient costing" purpose="Cost, revenue and margin per encounter, traceable to source transactions. Identifiers are masked without phi.view (FR-0.7).">
      <div class="row" style="margin-bottom:10px">
        <input type="search" placeholder="Encounter number" [value]="search()" (change)="search.set($any($event.target).value); page.set(1)" aria-label="Search encounter number" />
        <select (change)="encType.set($any($event.target).value); page.set(1)" aria-label="Encounter type">
          <option value="">All types</option><option value="IP">Inpatient</option><option value="OP">Outpatient</option><option value="ER">Emergency</option><option value="DAYCASE">Day case</option>
        </select>
        <select (change)="sort.set($any($event.target).value)" aria-label="Sort">
          <option value="total">Highest cost</option><option value="margin">Margin</option><option value="los">Length of stay</option><option value="admit">Admission</option>
        </select>
        <button class="btn" type="button" (click)="desc.set(!desc())">{{ desc() ? 'Descending' : 'Ascending' }}</button>
      </div>
      <v-state [loading]="res.isLoading() && !res.value()" [error]="res.error()">
        @if (res.value(); as r) {
          @if (r.data.phiMasked) { <p class="faint">Patient identifiers are masked for your role.</p> }
          <v-table [rows]="r.data.rows" [columns]="cols" caption="Encounters" />
          <div class="row" style="margin-top:10px">
            <button class="btn" type="button" [disabled]="page() <= 1" (click)="page.set(page() - 1)">Previous</button>
            <span class="muted">Page {{ r.data.page }} of {{ pages() }} · {{ r.data.total }} encounters</span>
            <button class="btn" type="button" [disabled]="page() >= pages()" (click)="page.set(page() + 1)">Next</button>
          </div>
        }
      </v-state>
    </v-page>
  `,
})
export class PatientCosting {
  page = signal(1);
  search = signal('');
  encType = signal('');
  sort = signal('total');
  desc = signal(true);
  res = runResource<EncounterPage>(() => 'encounters', () => ({ page: this.page(), pageSize: 50, search: this.search(), encType: this.encType(), sort: this.sort(), desc: this.desc() }));
  pages = computed(() => Math.max(1, Math.ceil((this.res.value()?.data.total ?? 0) / 50)));
  cols: Col<EncounterPage['rows'][number]>[] = [
    { key: 'encounterNo', label: 'Encounter', link: r => `/patient-costing/${r.id}`, sub: r => `${r.patient} · ${r.mrn}`, sortable: false },
    { key: 'encType', label: 'Type', sortable: false }, { key: 'department', label: 'Department', sortable: false },
    { key: 'drg', label: 'DRG', sortable: false }, { key: 'payer', label: 'Payer', sortable: false },
    { key: 'lengthOfStay', label: 'LOS', type: 'num', dp: 0, sortable: false },
    { key: 'directCost', label: 'Direct', type: 'money', dp: 0, sortable: false }, { key: 'indirectCost', label: 'Indirect', type: 'money', dp: 0, sortable: false },
    { key: 'overheadCost', label: 'Overhead', type: 'money', dp: 0, sortable: false }, { key: 'totalCost', label: 'Total', type: 'money', sortable: false },
    { key: 'revenue', label: 'Revenue', type: 'money', sortable: false }, { key: 'operatingMargin', label: 'Margin', type: 'money', dir: 'higher', sortable: false },
  ];
}

@Component({
  selector: 'v-encounter',
  imports: [RouterLink, ...UI],
  template: `
    <v-state [loading]="res.isLoading()" [error]="res.error()">
      @if (res.value(); as r) {
        <v-page [title]="'Encounter ' + r.data.encounter.encounterNo" [purpose]="r.data.encounter.patient + ' · ' + r.data.encounter.mrn + ' · ' + r.data.encounter.department + ' · ' + r.data.encounter.payer">
          <div class="grid tiles">
            <v-tile label="Total cost" [value]="r.data.encounter.totalCost" />
            <v-tile label="Standard cost" [value]="r.data.encounter.standardCost" />
            <v-tile label="Revenue" [value]="r.data.encounter.revenue" />
            <v-tile label="Contribution margin" [value]="r.data.encounter.contributionMargin" direction="higher-better" />
            <v-tile label="Operating margin" [value]="r.data.encounter.operatingMargin" direction="higher-better" />
            <v-tile label="Weighted activity units" [value]="r.data.weightedActivityUnits" unit="" />
          </div>
          <section class="card" style="margin-top:14px"><h2>Cost components</h2><v-table [rows]="r.data.components" [columns]="comp" /></section>
          <section class="card"><h2>Services</h2><v-table [rows]="r.data.services" [columns]="svcCols" /></section>
          <section class="card"><h2>Source transactions ({{ r.data.transactions.length }})</h2><v-table [rows]="r.data.transactions" [columns]="txnCols" /></section>
          @if (r.data.revenue.length) { <section class="card"><h2>Revenue</h2><v-table [rows]="r.data.revenue" [columns]="revCols" /></section> }
          <p class="faint">Run {{ r.data.provenance.run }} ({{ r.data.provenance.runStatus }}) · {{ r.data.provenance.costingVersion }} · triggered by {{ r.data.provenance.triggeredBy }}</p>
        </v-page>
      }
    </v-state>
  `,
})
export class EncounterDetail {
  id = input.required<string>();
  res = runResource<EncounterDetailDto>(() => `encounters/${this.id()}`);
  comp = componentCols;
  svcCols: Col<EncounterDetailDto['services'][number]>[] = [
    { key: 'procedure', label: 'Procedure', link: r => `/procedures/${r.procedureId}` }, { key: 'quantity', label: 'Qty', type: 'num', dp: 0 },
    { key: 'costCentre', label: 'Cost centre' }, { key: 'physician', label: 'Physician' }, { key: 'standardUnit', label: 'Standard / unit', type: 'money' },
    { key: 'sourceSystem', label: 'Source' }, { key: 'sourceRef', label: 'Reference' },
  ];
  txnCols: Col<EncounterDetailDto['transactions'][number]>[] = [
    { key: 'activity', label: 'Activity' }, { key: 'resource', label: 'Resource' }, { key: 'quantity', label: 'Quantity', type: 'num', dp: 2 },
    { key: 'uom', label: 'Unit' }, { key: 'occurredAt', label: 'When', type: 'date' }, { key: 'sourceSystem', label: 'Source system' }, { key: 'sourceRef', label: 'Source reference' },
  ];
  revCols: Col<EncounterDetailDto['revenue'][number]>[] = [
    { key: 'procedure', label: 'Procedure' }, { key: 'payer', label: 'Payer' }, { key: 'gross', label: 'Gross', type: 'money' },
    { key: 'discount', label: 'Discount', type: 'money' }, { key: 'net', label: 'Net', type: 'money' }, { key: 'sourceRef', label: 'Reference' },
  ];
}
