import { Component, computed, inject, signal } from '@angular/core';
import { AuditPage, QualityDto, UserDto } from '../core/api';
import { MetaService, RunContext } from '../core/context';
import { apiResource, runResource } from '../core/resource';
import { Col, UI } from '../shared/ui';

// ── Reports (FR-11.6) ───────────────────────────────────────────────────────────

@Component({
  selector: 'v-reports',
  imports: [...UI],
  template: `
    <v-page title="Reports" purpose="Standing reports for finance, executive, clinical operations, revenue cycle and supply chain, built from the selected run.">
      <div class="tabs">
        @for (r of reports; track r.key) { <button type="button" [class.active]="report() === r.key" (click)="report.set(r.key)">{{ r.title }} <span class="faint">· {{ r.audience }}</span></button> }
      </div>
      <v-state [loading]="res.isLoading()" [error]="res.error()">
        @if (res.value(); as r) {
          <p class="notice">{{ body().summary }}</p>
          @switch (report()) {
            @case ('cost-statement') {
              <div class="grid two">
                <section class="card"><h2>Ledger by natural class</h2><v-table [rows]="body().body.byNaturalClass" [columns]="ledger" /></section>
                <section class="card"><h2>Cost nature of patient cost</h2><v-table [rows]="body().body.costNature" [columns]="nature" /></section>
              </div>
              <section class="card"><h2>Reconciliation statement</h2><v-table [rows]="body().body.lines" [columns]="lines" /></section>
            }
            @case ('physician-variation') { <v-table [rows]="body().body" [columns]="physician" /> }
            @case ('supply-waste') { <v-table [rows]="body().body" [columns]="supply" /> }
            @case ('group-comparison') { <v-table [rows]="body().body" [columns]="group" /> }
            @default { <v-table [rows]="body().body.rows" [columns]="profit" [total]="body().body.total" /> }
          }
        }
      </v-state>
    </v-page>
  `,
})
export class Reports {
  reports = [
    { key: 'cost-statement', title: 'Hospital cost statement', audience: 'Finance' },
    { key: 'service-line', title: 'Service-line profitability', audience: 'Executive' },
    { key: 'physician-variation', title: 'Physician practice variation', audience: 'Clinical operations' },
    { key: 'payer-margin', title: 'Payer margin', audience: 'Revenue cycle' },
    { key: 'supply-waste', title: 'Supply cost and waste', audience: 'Supply chain' },
    { key: 'group-comparison', title: 'Group comparison', audience: 'Executive' },
  ];
  report = signal('cost-statement');
  res = runResource<any>(() => `reports/${this.report()}`);
  body = computed(() => this.res.value()?.data ?? { summary: '', body: {} });
  ledger: Col[] = [{ key: 'label', label: 'Item' }, { key: 'amount', label: 'Amount', type: 'money' }, { key: 'share', label: 'Share', type: 'pct' }];
  nature: Col[] = [{ key: 'label', label: 'Nature' }, { key: 'value', label: 'Amount', type: 'money' }, { key: 'share', label: 'Share', type: 'pct' }];
  lines: Col[] = [{ key: 'label', label: 'Line', sub: r => r.explanation }, { key: 'amount', label: 'Amount', type: 'money' }, { key: 'runningBalance', label: 'Running', type: 'money' }];
  profit: Col[] = [
    { key: 'label', label: 'Item' }, { key: 'encounters', label: 'Encounters', type: 'num', dp: 0 }, { key: 'total', label: 'Cost', type: 'money', dp: 0 },
    { key: 'revenue', label: 'Revenue', type: 'money', dp: 0 }, { key: 'operating', label: 'Margin', type: 'money', dp: 0, dir: 'higher' }, { key: 'marginPct', label: 'Margin %', type: 'pct', dir: 'higher' },
  ];
  physician: Col[] = [
    { key: 'physician', label: 'Physician' }, { key: 'specialty', label: 'Specialty' }, { key: 'encounters', label: 'Encounters', type: 'num', dp: 0 },
    { key: 'avgCost', label: 'Avg cost', type: 'money', dp: 0 }, { key: 'specialtyAvgCost', label: 'Specialty avg', type: 'money', dp: 0 },
    { key: 'vsSpecialtyPct', label: 'vs specialty', type: 'pct', dir: 'lower' }, { key: 'avgLOS', label: 'Avg LOS', type: 'num', dp: 1 },
    { key: 'costVsStandardPct', label: 'vs standard', type: 'pct', dir: 'lower' },
  ];
  supply: Col[] = [
    { key: 'name', label: 'Resource', link: r => `/resources/${r.resourceId}` }, { key: 'resourceType', label: 'Type' },
    { key: 'costOfUsed', label: 'Issued to patients', type: 'money', dp: 0 }, { key: 'costOfUnused', label: 'Not issued', type: 'money', dp: 0, dir: 'lower' },
    { key: 'utilisationPct', label: 'Issued %', type: 'pct' },
  ];
  group: Col[] = [
    { key: 'name', label: 'Hospital', sub: r => `${r.run} · ${r.runStatus}` }, { key: 'gl', label: 'Ledger', type: 'money', dp: 0 },
    { key: 'patientCost', label: 'Patient cost', type: 'money', dp: 0 }, { key: 'attributionPct', label: 'Attribution', type: 'pct' },
    { key: 'encounters', label: 'Encounters', type: 'num', dp: 0 }, { key: 'costPerEncounter', label: 'Cost / encounter', type: 'money', dp: 0 },
    { key: 'operatingMargin', label: 'Margin', type: 'money', dp: 0, dir: 'higher' }, { key: 'idleCapacity', label: 'Idle capacity', type: 'money', dp: 0 },
  ];
}

// ── Assurance ───────────────────────────────────────────────────────────────────

@Component({
  selector: 'v-data-quality',
  imports: [...UI],
  template: `
    <v-page title="Data quality" purpose="Nineteen checks gate every run. Passes are reported as well as failures, so a clean run is distinguishable from a check that stopped running (FR-9.4).">
      <v-state [loading]="res.isLoading()" [error]="res.error()">
        @if (res.value(); as r) {
          <div class="grid tiles" style="margin-bottom:14px">
            <v-tile label="Checks passed" [value]="r.data.passed" unit="" />
            <v-tile label="Checks with issues" [value]="r.data.failed" unit="" />
          </div>
          <section class="card"><h2>Check register</h2><v-table [rows]="r.data.register" [columns]="regCols" /></section>
          <section class="card"><h2>Issues</h2><v-table [rows]="r.data.issues" [columns]="issueCols" empty="No issues in this run." /></section>
        }
      </v-state>
    </v-page>
  `,
})
export class DataQuality {
  res = runResource<QualityDto>(() => 'data-quality');
  regCols: Col<QualityDto['register'][number]>[] = [
    { key: 'code', label: 'Check', sub: r => r.question }, { key: 'name', label: 'Name' }, { key: 'category', label: 'Category' },
    { key: 'severity', label: 'Severity', type: 'chip' }, { key: 'issues', label: 'Issues', type: 'num', dp: 0 }, { key: 'result', label: 'Result', type: 'chip' },
  ];
  issueCols: Col<QualityDto['issues'][number]>[] = [
    { key: 'checkCode', label: 'Check' }, { key: 'severity', label: 'Severity', type: 'chip' }, { key: 'entityLabel', label: 'Entity', sub: r => r.message },
    { key: 'value', label: 'Value' }, { key: 'status', label: 'Status' },
  ];
}

@Component({
  selector: 'v-audit',
  imports: [...UI],
  template: `
    <v-page title="Audit" purpose="Changes, approvals, publications, runs, PHI access and sign-ins — with actor, time and before-and-after state (FR-10.9).">
      <div class="row" style="margin-bottom:10px">
        <select (change)="action.set($any($event.target).value); page.set(1)" aria-label="Action">
          <option value="">All actions</option>
          @for (a of res.value()?.actions ?? []; track a) { <option [value]="a">{{ a }}</option> }
        </select>
        <select (change)="entityType.set($any($event.target).value); page.set(1)" aria-label="Entity type">
          <option value="">All entities</option>
          @for (t of res.value()?.entityTypes ?? []; track t) { <option [value]="t">{{ t }}</option> }
        </select>
        <input type="search" placeholder="Actor email" (change)="actor.set($any($event.target).value); page.set(1)" aria-label="Actor" />
      </div>
      <v-state [loading]="res.isLoading() && !res.value()" [error]="res.error()">
        @if (res.value(); as r) {
          <v-table [rows]="r.rows" [columns]="cols" />
          <div class="row" style="margin-top:10px">
            <button class="btn" type="button" [disabled]="page() <= 1" (click)="page.set(page() - 1)">Previous</button>
            <span class="muted">Page {{ r.page }} · {{ r.total }} entries</span>
            <button class="btn" type="button" [disabled]="page() * 50 >= r.total" (click)="page.set(page() + 1)">Next</button>
          </div>
        }
      </v-state>
    </v-page>
  `,
})
export class Audit {
  page = signal(1);
  action = signal('');
  entityType = signal('');
  actor = signal('');
  res = apiResource<AuditPage>(() => 'audit', () => ({ page: this.page(), pageSize: 50, action: this.action(), entityType: this.entityType(), actor: this.actor() }));
  cols: Col<AuditPage['rows'][number]>[] = [
    { key: 'at', label: 'When', value: r => String(r.at).replace('T', ' ').slice(0, 19), sortable: false },
    { key: 'actorEmail', label: 'Actor', sortable: false }, { key: 'action', label: 'Action', type: 'chip', sortable: false },
    { key: 'entityType', label: 'Entity', sub: r => r.entityLabel, sortable: false },
    { key: 'change', label: 'Change', value: r => [r.beforeJson && `before ${r.beforeJson}`, r.afterJson && `after ${r.afterJson}`, r.metadata].filter(Boolean).join(' · '), sortable: false },
    { key: 'ipAddress', label: 'IP', sortable: false },
  ];
}

// ── System ──────────────────────────────────────────────────────────────────────

@Component({
  selector: 'v-administration',
  imports: [...UI],
  template: `
    <v-page title="Administration" purpose="Users, roles, scopes and the permission matrix — one declaration, read by the API and this interface alike.">
      <v-state [loading]="res.isLoading()" [error]="res.error()">
        @if (res.value(); as users) { <section class="card"><h2>Users</h2><v-table [rows]="users" [columns]="cols" /></section> }
      </v-state>
      <section class="card">
        <h2>Permission matrix</h2>
        <div class="table-wrap"><table class="data">
          <thead><tr><th>Permission</th>@for (r of roles(); track r.code) { <th [title]="r.description">{{ r.code }}</th> }</tr></thead>
          <tbody>
            @for (p of permissions(); track p) {
              <tr><td class="mono">{{ p }}</td>@for (r of roles(); track r.code) { <td>{{ r.permissions.includes(p) ? '●' : '' }}</td> }</tr>
            }
          </tbody>
        </table></div>
      </section>
    </v-page>
  `,
})
export class Administration {
  private meta = inject(MetaService);
  res = apiResource<UserDto[]>(() => 'admin/users');
  roles = computed(() => (this.meta.meta()?.['roles'] ?? []) as { code: string; description: string; permissions: string[] }[]);
  permissions = computed(() => {
    const p = this.meta.meta()?.['permissions'] ?? {};
    return [...(p.read ?? []), ...(p.sensitive ?? []), ...(p.write ?? []), ...(p.governance ?? [])] as string[];
  });
  cols: Col<UserDto>[] = [
    { key: 'name', label: 'Name', sub: r => r.email }, { key: 'roleName', label: 'Role' }, { key: 'title', label: 'Title' },
    { key: 'hospitalScope', label: 'Hospitals' }, { key: 'deptScope', label: 'Departments' }, { key: 'lastLoginAt', label: 'Last sign-in', type: 'date' },
  ];
}

@Component({
  selector: 'v-help',
  imports: [...UI],
  template: `
    <v-page title="Help & method" purpose="How the costing model works and what each number means (FR-11.15).">
      <section class="card"><h2>The costing chain</h2>
        <p>Money enters once, from the general ledger, into cost pools. It reaches a patient by one of two streams. <strong>Consumption</strong>: measured quantity × effective rate, bounded by the pools that fund the resources. <strong>Allocation</strong>: pool ÷ driver volume × driver quantity, bounded by the ledger. Whatever does not reach a patient sits in one of six named residual buckets.</p>
        <p class="mono">Σ GL postings = Σ patient cost + Σ named residuals + unexplained</p>
      </section>
      <section class="card"><h2>Time-driven ABC</h2>
        <p>Capacity cost rate = annual cost ÷ practical minutes (theoretical × practical ratio). Unused capacity is priced and carved out before allocation; it never reaches a patient. Over-capacity is reported separately and never netted against idle time.</p>
      </section>
      <section class="card"><h2>Allocation</h2>
        <p>Three stages in order: reciprocal among support centres that serve each other (repeated redistribution, never to self), step-down to operating centres in service order, then cost centre to activity by driver. A zero driver volume routes the balance to NO_DRIVER_VOLUME — never to a substitute denominator.</p>
      </section>
      <section class="card"><h2>Variance</h2>
        <p class="mono">rate = (actual rate − standard rate) × standard qty · quantity = (actual qty − standard qty) × standard rate · mix = (actual rate − standard rate) × (actual qty − standard qty)</p>
        <p>The three sum exactly to actual minus standard. Rate belongs to procurement and payroll; quantity to clinical practice.</p>
      </section>
      <section class="card"><h2>Residual buckets</h2>
        <v-table [rows]="residuals()" [columns]="resCols" />
      </section>
      <section class="card"><h2>Margins</h2>
        <p><strong>Contribution margin</strong> = revenue − direct − indirect. <strong>Operating margin</strong> = revenue − total cost. <strong>Full-cost margin</strong> = operating margin − idle capacity: the number the income statement sees.</p>
      </section>
      <section class="card"><h2>Modules</h2>
        @for (g of groups(); track g.label) {
          <h3>{{ g.label }}</h3>
          @for (i of g.items; track i.path) { <p><strong>{{ i.label }}</strong> — {{ i.purpose }}</p> }
        }
      </section>
    </v-page>
  `,
})
export class Help {
  meta = inject(MetaService);
  private ctx = inject(RunContext);
  residuals = computed(() => (this.meta.meta()?.['residuals'] ?? []) as any[]);
  groups = computed(() => this.meta.navigation()
    .map(g => ({ ...g, items: g.items.filter(i => i.path !== 'activities' && (i.path !== 'drgs' || this.ctx.hasDrgs())) }))
    .filter(g => g.items.length > 0));
  resCols: Col[] = [{ key: 'bucket', label: 'Bucket' }, { key: 'label', label: 'Means' }, { key: 'owner', label: 'Owner' }];
}
