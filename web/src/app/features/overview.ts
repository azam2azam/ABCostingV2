import { Component, computed, inject, signal } from '@angular/core';
import { RouterLink } from '@angular/router';
import { DashboardDto, ProfitDto } from '../core/api';
import { Auth } from '../core/auth';
import { RunContext, runInFlight } from '../core/context';
import { runResource } from '../core/resource';
import { Col, UI } from '../shared/ui';

/** Margin by dimension, computed at read time over the run's encounter costs (FR-5.16). */
@Component({
  selector: 'v-profit-tables',
  imports: [...UI],
  template: `
    <div class="tabs" role="tablist">
      @for (d of dims(); track d.key) {
        <button role="tab" type="button" [class.active]="dim() === d.key" (click)="dim.set(d.key)">{{ d.label }}</button>
      }
    </div>
    <v-state [loading]="res.isLoading()" [error]="res.error()">
      @if (res.value(); as r) {
        @if (!canRevenue()) { <p class="notice warn">Revenue is masked for your role; margins are not shown.</p> }
        @if (dim() === 'resource') { <p class="muted" style="margin:0 0 10px">By the doctor on each service line. An encounter can appear under several resources, so the Encounters column does not add up to the total.</p> }
        <v-table [rows]="r.data.rows" [columns]="cols()" [total]="r.data.total" [caption]="'Profitability by ' + dim()" />
      }
    </v-state>
  `,
})
export class ProfitTables {
  private auth = inject(Auth);
  canRevenue = computed(() => this.auth.can('revenue.view'));
  dims = computed(() => [
    { key: 'hospital', label: 'Hospital' }, { key: 'serviceLine', label: 'Service line' }, { key: 'department', label: 'Department' },
    { key: 'resource', label: 'Resources' }, { key: 'physician', label: 'Physician' }, { key: 'payer', label: 'Payer' },
    { key: 'encounterType', label: 'Encounter type' }, { key: 'segment', label: 'Patient segment' },
  ]);
  dim = signal('hospital');
  res = runResource<ProfitDto>(() => 'profitability', () => ({ dimension: this.dim() }));
  cols = computed<Col[]>(() => {
    const base: Col[] = [
      { key: 'label', label: this.dims().find(d => d.key === this.dim())?.label ?? 'Item', link: r => r.link ? '/' + r.link : null },
      { key: 'volume', label: 'Encounters', type: 'num', dp: 0 },
      { key: 'direct', label: 'Direct', type: 'money', dp: 0 },
      { key: 'indirect', label: 'Indirect', type: 'money', dp: 0 },
      { key: 'overhead', label: 'Overhead', type: 'money', dp: 0 },
      { key: 'total', label: 'Total cost', type: 'money', dp: 0 },
    ];
    if (!this.canRevenue()) return base;
    return [...base,
      { key: 'revenue', label: 'Revenue', type: 'money', dp: 0 },
      { key: 'operating', label: 'Operating margin', type: 'money', dp: 0, dir: 'higher' },
      { key: 'marginPct', label: 'Margin %', type: 'pct', dir: 'higher' },
    ];
  });
}

@Component({
  selector: 'v-dashboard',
  imports: [RouterLink, ProfitTables, ...UI],
  template: `
    <v-page title="Dashboard" purpose="Direct cost, indirect cost, overheads, revenue and margin for one costed run. Every figure opens the screen that explains it.">
      @if (!calculated()) {
        <p class="notice warn">
          {{ pending() }}
          <a routerLink="/runs" queryParamsHandling="preserve">Go to Calculation Runs</a>
        </p>
      } @else {
        <v-state [loading]="res.isLoading()" [error]="res.error()">
          @if (res.value(); as r) {
            <div class="grid tiles">
              @for (t of r.data.headline; track t.key) {
                <v-tile [label]="t.label" [value]="t.value" [unit]="t.unit" [direction]="t.direction" [note]="t.note" [link]="t.link ? '/' + t.link : null" />
              }
            </div>
            @if (canProfitability()) { <div style="margin-top:14px"><v-profit-tables /></div> }
          }
        </v-state>
      }
    </v-page>
  `,
})
export class Dashboard {
  private auth = inject(Auth);
  private ctx = inject(RunContext);
  calculated = computed(() => ['RECONCILED', 'PUBLISHED'].includes(this.ctx.run()?.status ?? ''));
  pending = computed(() => {
    const code = this.ctx.periodParam();
    const selected = this.ctx.periodsForHospital().find(p => p.code === code);
    const period = selected ? `${selected.code} (${selected.startDate} – ${selected.endDate})` : code ?? 'this period';
    const run = this.ctx.run();
    if (!run) return `The calculation has not been run for ${period}.`;
    if (runInFlight(run.status)) return `The calculation for ${period} is still running.`;
    return run.status === 'FAILED'
      ? `The calculation for ${period} failed, so there are no figures to show.`
      : `The calculation has not been run for ${period}.`;
  });
  res = runResource<DashboardDto>(() => this.calculated() ? 'dashboard' : null);
  canProfitability = computed(() => this.auth.can('profitability.view'));
}
