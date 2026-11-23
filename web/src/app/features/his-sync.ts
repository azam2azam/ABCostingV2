import { Component, DestroyRef, computed, effect, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { firstValueFrom } from 'rxjs';
import { Api } from '../core/api';
import { Auth, toApiError } from '../core/auth';
import { RunContext } from '../core/context';
import { apiResource } from '../core/resource';
import { Col, UI } from '../shared/ui';

// Contract of /his-sync (HisSyncService). Declared here until api-types.ts is regenerated.
interface HisSyncSettings {
  catalogueEnabled: boolean; catalogueTime: string;
  catalogueRequestedAt?: string | null; catalogueRequestedBy?: string | null; catalogueLastRunAt?: string | null;
  updatedAt?: string | null; updatedBy?: string | null;
}
interface HisSyncRange {
  id: string; fromDate: string; toDate: string; status: string; daysTotal: number; daysDone: number;
  requestedAt: string; requestedBy: string; startedAt?: string | null; finishedAt?: string | null; error?: string | null;
}
interface HisSyncWorker {
  kind: 'CATALOGUE' | 'FACTS'; status: string; alive: boolean; step?: string | null; rangeId?: string | null; currentDate?: string | null;
  batchesDone: number; batchesTotal?: number | null; recordsDone: number; recordsTotal?: number | null;
  startedAt?: string | null; heartbeatAt?: string | null; finishedAt?: string | null; lastError?: string | null;
}
interface HisSyncLog { id: string; at: string; kind: string; level: string; message: string; detail?: string | null; factDate?: string | null; actor: string; }
interface HisSyncDay {
  id: string; rangeId: string; date: string; status: string; encounters: number; batches: number; synced: number; failed: number;
  startedAt?: string | null; finishedAt?: string | null; error?: string | null;
}
interface HisSyncCatalogueTable { entity: string; rows: number; created: number; updated: number; unchanged: number; rejected: number; }
interface HisSyncCatalogueChange {
  id: string; detectedAt: string; status: string; created: number; updated: number; unchanged: number; rejected: number;
  tables: HisSyncCatalogueTable[]; decidedAt?: string | null; decidedBy?: string | null;
  importFileId?: string | null; importStatus?: string | null; importError?: string | null;
}
interface HisSyncDto {
  hospital: string; hospitalName: string; hisSetupId?: string | null; hisProjectId?: number | null; today: string;
  settings: HisSyncSettings; ranges: HisSyncRange[]; workers: HisSyncWorker[]; log: HisSyncLog[]; errors: number;
  days: HisSyncDay[]; catalogueChanges: HisSyncCatalogueChange[];
}

const STATUS_LABEL: Record<string, string> = {
  QUEUED: 'Queued', RUNNING: 'Syncing', DONE: 'Synced', DONE_WITH_ERRORS: 'Synced with errors', FAILED: 'Failed', CANCELLED: 'Cancelled',
  PENDING: 'Waiting', APPLYING: 'Applying (first catalogue)', APPLIED: 'Applied', DISCARDED: 'Discarded', SUPERSEDED: 'Replaced by a newer check',
  UPLOADED: 'Not imported', VALIDATED: 'Validated', COMPLETED: 'Imported', COMPLETED_WITH_REJECTS: 'Imported with rejects',
  IDLE: 'Idle', PAUSED: 'Paused', STALLED: 'Not responding', NOT_CONNECTED: 'Not connected',
};
const STATUS_KIND: Record<string, string> = {
  QUEUED: 'info', RUNNING: 'info live', DONE: 'good', DONE_WITH_ERRORS: 'warn', FAILED: 'bad', CANCELLED: '',
  PENDING: 'warn', APPLYING: 'info live', APPLIED: 'good', DISCARDED: '', SUPERSEDED: '',
  IDLE: 'good', PAUSED: 'warn', STALLED: 'bad', NOT_CONNECTED: '', INFO: 'info', WARN: 'warn', ERROR: 'bad',
};

function when(iso: string | null | undefined): string {
  if (!iso) return '—';
  const d = new Date(iso.endsWith('Z') || iso.includes('+') ? iso : iso + 'Z');
  return d.toLocaleString(undefined, { year: 'numeric', month: 'short', day: '2-digit', hour: '2-digit', minute: '2-digit' });
}

function addDays(ymd: string, n: number): string {
  const d = new Date(ymd + 'T00:00:00Z');
  d.setUTCDate(d.getUTCDate() + n);
  return d.toISOString().slice(0, 10);
}

/** Share of a / b as a percentage, or null when the total is unknown. */
function pct(a: number, b: number | null | undefined): number | null {
  return b ? Math.min(100, Math.round(a / b * 1000) / 10) : null;
}

/**
 * HIS Sync, per hospital: what the sync worker should do (daily catalogue check, fact dates to sync),
 * what it is doing now, and what it did. VERITA writes the requests; the worker reports back here.
 */
@Component({
  selector: 'v-his-sync',
  imports: [FormsModule, ...UI],
  template: `
    <v-page title="HIS Sync" purpose="Keep this hospital in step with the HIS. Catalogue changes are parked for review; facts sync automatically for the dates you add. Today is never synced.">
      <button actions class="btn" type="button" (click)="res.reload()" [disabled]="res.isLoading()">Refresh</button>
      <v-state [loading]="!data()" [error]="res.error()">
        @if (data(); as d) {
          @if (message()) { <p class="notice" [class.bad]="error()">{{ message() }}</p> }
          @if (!d.hisSetupId) {
            <p class="notice warn">{{ d.hospital }} is not linked to a HIS tenant yet. It is linked on its first HIS import (SetupID / ProjectID).</p>
          }

          <!-- ── Sync config ───────────────────────────────────────────────── -->
          <h2 class="section">Sync config</h2>
          <div class="grid two">
            <section class="card">
              <h3>Catalogue</h3>
              <p class="muted">New and changed catalogue rows from the HIS are parked for review. Nothing changes in VERITA until they are applied.</p>
              <label class="check">
                <input type="checkbox" [ngModel]="catEnabled()" (ngModelChange)="catEnabled.set($event)" [disabled]="!canEdit() || busy()" />
                Check the catalogue for changes every day
              </label>
              <div class="fields">
                <label class="field">Daily check at
                  <input type="time" [ngModel]="catTime()" (ngModelChange)="catTime.set($event)" [disabled]="!canEdit() || busy() || !catEnabled()" />
                </label>
              </div>
              <div class="row">
                @if (canEdit()) {
                  <button class="btn primary" type="button" (click)="saveSettings()" [disabled]="busy() || !settingsDirty()">Save</button>
                  <button class="btn" type="button" (click)="syncCatalogueNow()" [disabled]="busy() || !!d.settings.catalogueRequestedAt">
                    {{ d.settings.catalogueRequestedAt ? 'Requested' : 'Sync now' }}
                  </button>
                }
              </div>
              <dl class="facts">
                <dt>Last check</dt><dd>{{ when(d.settings.catalogueLastRunAt) }}</dd>
                @if (d.settings.catalogueRequestedAt) {
                  <dt>Requested</dt><dd>{{ when(d.settings.catalogueRequestedAt) }} by {{ d.settings.catalogueRequestedBy }} · runs when the worker picks it up</dd>
                }
                @if (d.settings.updatedAt) { <dt>Settings saved</dt><dd>{{ when(d.settings.updatedAt) }} by {{ d.settings.updatedBy }}</dd> }
              </dl>
            </section>

            <section class="card">
              <h3>Facts</h3>
              <p class="muted">Add the dates to sync. The worker syncs them in small batches of episodes, oldest date first. The last date you can add is yesterday ({{ yesterday() }}).</p>
              @if (canEdit()) {
                <div class="fields">
                  <label class="field">From
                    <input type="date" [ngModel]="from()" (ngModelChange)="from.set($event)" [max]="yesterday()" [disabled]="busy()" />
                  </label>
                  <label class="field">To
                    <input type="date" [ngModel]="to()" (ngModelChange)="to.set($event)" [min]="from()" [max]="yesterday()" [disabled]="busy()" />
                  </label>
                </div>
                <div class="row">
                  <button class="btn primary" type="button" (click)="addRange()" [disabled]="busy() || !from() || !to()">Add to sync</button>
                  @if (rangeDays(); as n) { <span class="muted">{{ n }} day{{ n === 1 ? '' : 's' }}</span> }
                </div>
              }
              <dl class="facts">
                <dt>Queued</dt><dd>{{ queuedDays() }} day{{ queuedDays() === 1 ? '' : 's' }}</dd>
                <dt>Synced through</dt><dd>{{ syncedThrough() ?? 'Nothing synced yet' }}</dd>
              </dl>
            </section>
          </div>

          <section class="card">
            <h3>Catalogue changes</h3>
            <p class="muted">A hospital's first catalogue is applied straight away, in batches. After that, changes the checks find in the HIS are parked here; applying imports them as a catalogue pack (the same checks as Data Import). Facts sync waits while new catalogue rows are parked or the first catalogue is still going in.</p>
            @if (pendingChange(); as c) {
              <div class="pending">
                <div class="row">
                  <strong>{{ c.created }} new · {{ c.updated }} changed</strong>
                  <span class="muted">found {{ when(c.detectedAt) }}{{ c.rejected ? ' · ' + c.rejected + ' row(s) fail VERITA\'s checks' : '' }}</span>
                </div>
                <v-table [rows]="changedTables(c)" [columns]="changeCols" caption="Catalogue changes by table" />
                @if (canEdit()) {
                  <div class="row">
                    <button class="btn primary" type="button" (click)="applyChange(c)" [disabled]="busy()">Apply changes</button>
                    <button class="btn" type="button" (click)="discardChange(c)" [disabled]="busy()">Discard</button>
                  </div>
                }
              </div>
            } @else {
              <p class="muted">No catalogue changes waiting.</p>
            }
            @if (pastChanges().length) {
              <v-table [rows]="pastChanges()" [columns]="pastChangeCols" caption="Earlier catalogue checks" [pageSize]="5" />
            }
          </section>

          <section class="card">
            <h3>Fact dates</h3>
            <v-table [rows]="d.ranges" [columns]="rangeCols()" caption="Fact dates added to sync" empty="No dates added yet." [pageSize]="10" />
          </section>

          <!-- ── Sync workers ──────────────────────────────────────────────── -->
          <h2 class="section">Sync workers</h2>
          @if (!anyConnected()) {
            <p class="notice">No sync worker has reported yet. Requests made here wait until the HIS sync service is running.</p>
          }
          <div class="grid two">
            @for (w of d.workers; track w.kind) {
              <section class="card worker">
                <div class="head">
                  <h3>{{ w.kind === 'CATALOGUE' ? 'Catalogue worker' : 'Facts worker' }}</h3>
                  <v-chip [value]="w.status" [label]="label(w.status)" [kind]="kindOf(w.status)" />
                </div>
                @if (w.status === 'NOT_CONNECTED') {
                  <p class="muted">Not connected.</p>
                } @else {
                  <p class="step">{{ w.step ?? (w.status === 'IDLE' ? 'Waiting for work' : '—') }}</p>
                  @if (w.kind === 'FACTS' && w.currentDate) { <p class="muted">Date {{ w.currentDate }}</p> }
                  @if (w.batchesTotal) {
                    <div class="progress"><span>{{ w.kind === 'CATALOGUE' ? 'Tables' : 'Batches' }}</span><v-bar [share]="pct(w.batchesDone, w.batchesTotal) ?? 0" /><span>{{ w.batchesDone }} / {{ w.batchesTotal }}</span></div>
                  }
                  @if (w.recordsTotal) {
                    <div class="progress"><span>Records</span><v-bar [share]="pct(w.recordsDone, w.recordsTotal) ?? 0" /><span>{{ w.recordsDone }} / {{ w.recordsTotal }}</span></div>
                  }
                  <dl class="facts">
                    @if (w.startedAt) { <dt>Started</dt><dd>{{ when(w.startedAt) }}</dd> }
                    <dt>Last heartbeat</dt><dd>{{ when(w.heartbeatAt) }}</dd>
                    @if (w.finishedAt) { <dt>Last finished</dt><dd>{{ when(w.finishedAt) }}</dd> }
                  </dl>
                  @if (w.lastError) { <p class="notice bad">{{ w.lastError }}</p> }
                }
              </section>
            }
          </div>

          @if (d.days.length) {
            <section class="card">
              <h3>Fact days</h3>
              <p class="muted">Each day is listed once, then synced in batches of episodes. An ER visit and the appointment it is billed through stay in one batch.</p>
              <v-table [rows]="d.days" [columns]="dayCols" caption="Fact days" [pageSize]="10" />
            </section>
          }

          <!-- ── Sync log ──────────────────────────────────────────────────── -->
          <h2 class="section">Sync log</h2>
          <section class="card">
            <div class="row log-head">
              <div class="seg" role="group" aria-label="Show">
                @for (o of levels; track o.value) {
                  <button type="button" class="btn" [class.primary]="level() === o.value" (click)="level.set(o.value)">{{ o.label }}</button>
                }
              </div>
              @if (d.errors) { <span class="bad-text">{{ d.errors }} error{{ d.errors === 1 ? '' : 's' }} in the last 7 days</span> }
            </div>
            <v-table [rows]="d.log" [columns]="logCols" caption="Sync log" [empty]="level() ? 'Nothing at this level.' : 'Nothing logged yet.'" [pageSize]="25" />
          </section>
        }
      </v-state>
    </v-page>
  `,
  styles: `
    h2.section { font-size: 15px; margin: 22px 0 10px; text-transform: uppercase; letter-spacing: .08em; color: var(--text-3); }
    h2.section:first-of-type { margin-top: 4px; }
    .card h3 { font-size: 15px; margin: 0 0 6px; }
    .card { margin-bottom: 14px; }
    .check { display: inline-flex; gap: 8px; align-items: center; margin: 8px 0 4px; font-weight: 500; }
    .fields { display: grid; grid-template-columns: repeat(auto-fill, minmax(180px, 1fr)); gap: 10px; margin: 10px 0; }
    .field { display: flex; flex-direction: column; gap: 5px; font-size: 12px; font-weight: 600; letter-spacing: .04em; text-transform: uppercase; color: var(--text-3); }
    .field input { font: 14px var(--font); text-transform: none; letter-spacing: normal; font-weight: 400; color: var(--text); padding: 6px 8px;
      border: 1px solid var(--border); border-radius: var(--radius-sm); background: var(--surface); }
    .facts { display: grid; grid-template-columns: max-content 1fr; gap: 4px 14px; margin: 14px 0 0; font-size: 13px; }
    .facts dt { color: var(--text-3); }
    .facts dd { margin: 0; }
    .worker .head { display: flex; justify-content: space-between; align-items: center; gap: 10px; }
    .worker .step { margin: 4px 0; font-weight: 500; }
    .progress { display: grid; grid-template-columns: 70px 1fr auto; gap: 10px; align-items: center; font-size: 13px; margin: 6px 0; }
    .progress span:first-child { color: var(--text-3); }
    .log-head { justify-content: space-between; margin-bottom: 10px; }
    .seg { display: inline-flex; gap: 6px; }
    .bad-text { color: var(--bad); font-size: 13px; font-weight: 600; }
    .notice.bad { color: var(--bad); }
    .pending { border: 1px solid var(--border); border-radius: var(--radius-sm); padding: 12px; margin: 8px 0 12px; display: grid; gap: 10px; }
  `,
})
export class HisSync {
  private api = inject(Api);
  private auth = inject(Auth);
  private ctx = inject(RunContext);

  level = signal<string>('');
  levels = [{ value: '', label: 'All' }, { value: 'ERROR', label: 'Errors' }, { value: 'WARN', label: 'Warnings' }];
  res = apiResource<HisSyncDto>(() => this.ctx.hospitalParam() ? 'his-sync' : null,
    () => ({ hospital: this.ctx.hospitalParam(), level: this.level() }));

  /**
   * The last loaded data for this hospital. Kept on screen while a filter change or refresh loads, so the
   * page does not collapse to "Loading…" and jump back to the top.
   */
  private last = signal<HisSyncDto | null>(null);
  data = computed(() => {
    const d = this.last();
    return d && d.hospital.toUpperCase() === (this.ctx.hospitalParam() ?? '').toUpperCase() ? d : null;
  });

  busy = signal(false);
  error = signal(false);
  message = signal<string | null>(null);
  canEdit = computed(() => this.auth.can('admin.manage'));

  catEnabled = signal(false);
  catTime = signal('01:00');
  from = signal('');
  to = signal('');

  when = when;
  pct = pct;
  label = (s: string) => STATUS_LABEL[s] ?? s;
  kindOf = (s: string) => STATUS_KIND[s] ?? '';

  yesterday = computed(() => {
    const today = this.data()?.today;
    return today ? addDays(today, -1) : '';
  });
  settingsDirty = computed(() => {
    const s = this.data()?.settings;
    return !!s && (s.catalogueEnabled !== this.catEnabled() || s.catalogueTime !== this.catTime());
  });
  rangeDays = computed(() => {
    const f = this.from(), t = this.to();
    if (!f || !t || t < f) return null;
    return Math.round((Date.parse(t) - Date.parse(f)) / 86400000) + 1;
  });
  queuedDays = computed(() => (this.data()?.ranges ?? [])
    .filter(r => r.status === 'QUEUED' || r.status === 'RUNNING').reduce((s, r) => s + r.daysTotal - r.daysDone, 0));
  syncedThrough = computed(() => {
    const done = (this.data()?.ranges ?? []).filter(r => r.status === 'DONE' || r.status === 'DONE_WITH_ERRORS').map(r => r.toDate).sort();
    return done.length ? done[done.length - 1] : null;
  });
  pendingChange = computed(() => (this.data()?.catalogueChanges ?? []).find(c => c.status === 'PENDING') ?? null);
  pastChanges = computed(() => (this.data()?.catalogueChanges ?? []).filter(c => c.status !== 'PENDING'));
  changedTables = (c: HisSyncCatalogueChange) => c.tables.filter(t => t.created || t.updated || t.rejected);

  changeCols: Col<HisSyncCatalogueTable>[] = [
    { key: 'entity', label: 'Table' },
    { key: 'created', label: 'New', type: 'num', dp: 0, blankZero: true },
    { key: 'updated', label: 'Changed', type: 'num', dp: 0, blankZero: true },
    { key: 'unchanged', label: 'Unchanged', type: 'num', dp: 0, blankZero: true },
    { key: 'rejected', label: 'Fail checks', type: 'num', dp: 0, blankZero: true, bad: r => r.rejected > 0 },
  ];
  pastChangeCols: Col<HisSyncCatalogueChange>[] = [
    { key: 'detectedAt', label: 'Found', value: r => when(r.detectedAt) },
    { key: 'created', label: 'New', type: 'num', dp: 0 },
    { key: 'updated', label: 'Changed', type: 'num', dp: 0 },
    { key: 'status', label: 'Decision', type: 'chip', value: r => this.label(r.status), chip: r => this.kindOf(r.status), sub: r => r.decidedBy ? `${r.decidedBy} · ${when(r.decidedAt)}` : null },
    { key: 'importStatus', label: 'Import', value: r => r.importStatus ? this.label(r.importStatus) : '—', sub: r => r.importError ?? null },
  ];
  dayCols: Col<HisSyncDay>[] = [
    { key: 'date', label: 'Date', sub: r => r.error ?? null, minWidth: '10rem' },
    { key: 'status', label: 'Status', type: 'chip', value: r => this.label(r.status), chip: r => this.kindOf(r.status) },
    { key: 'encounters', label: 'Episodes', type: 'num', dp: 0 },
    { key: 'batches', label: 'Batches', type: 'num', dp: 0 },
    { key: 'progress', label: 'Progress', type: 'bar', value: r => pct(r.synced + r.failed, r.encounters) ?? (r.status === 'DONE' ? 100 : 0), sortable: false },
    { key: 'synced', label: 'Synced', type: 'num', dp: 0 },
    { key: 'failed', label: 'Failed', type: 'num', dp: 0, blankZero: true, bad: r => r.failed > 0 },
    { key: 'finishedAt', label: 'Finished', value: r => when(r.finishedAt) },
  ];

  anyConnected = computed(() => (this.data()?.workers ?? []).some(w => w.status !== 'NOT_CONNECTED'));
  /** Poll while anything is waiting on the worker or running. */
  private active = computed(() => {
    const d = this.data();
    return !!d && (d.workers.some(w => w.status === 'RUNNING') || d.ranges.some(r => r.status === 'QUEUED' || r.status === 'RUNNING') || !!d.settings.catalogueRequestedAt
      || d.catalogueChanges.some(c => c.status === 'APPLYING' || c.importStatus === 'QUEUED' || c.importStatus === 'RUNNING'));
  });

  rangeCols = computed<Col<HisSyncRange>[]>(() => {
    const cols: Col<HisSyncRange>[] = [
      { key: 'fromDate', label: 'Dates', value: r => r.fromDate === r.toDate ? r.fromDate : `${r.fromDate} – ${r.toDate}`, minWidth: '13rem', sub: r => r.error ?? null },
      { key: 'daysTotal', label: 'Days', type: 'num', dp: 0 },
      { key: 'status', label: 'Status', type: 'chip', value: r => this.label(r.status), chip: r => this.kindOf(r.status) },
      { key: 'progress', label: 'Progress', type: 'bar', value: r => pct(r.daysDone, r.daysTotal) ?? 0, sortable: false },
      { key: 'daysDone', label: 'Days synced', type: 'num', dp: 0 },
      { key: 'requestedAt', label: 'Added', value: r => when(r.requestedAt), sub: r => r.requestedBy },
      { key: 'finishedAt', label: 'Finished', value: r => when(r.finishedAt) },
    ];
    if (this.canEdit()) {
      cols.push({ key: 'remove', label: '', type: 'button', sortable: false, button: r => r.status === 'QUEUED' && !r.startedAt && !r.daysDone ? 'Remove' : null, onButton: r => void this.removeRange(r) });
    }
    return cols;
  });

  logCols: Col<HisSyncLog>[] = [
    { key: 'at', label: 'When', value: r => when(r.at), minWidth: '11rem' },
    { key: 'level', label: 'Level', type: 'chip' },
    { key: 'kind', label: 'Area', value: r => r.kind === 'CONFIG' ? 'Settings' : r.kind === 'CATALOGUE' ? 'Catalogue' : 'Facts' },
    { key: 'message', label: 'Message', sub: r => r.detail ?? null, minWidth: '20rem', bad: r => r.level === 'ERROR' },
    { key: 'factDate', label: 'Fact date', value: r => r.factDate ?? '' },
    { key: 'actor', label: 'By' },
  ];

  constructor() {
    void this.ctx.load();
    effect(() => {
      const v = this.res.value();
      if (v) this.last.set(v);
    });
    // Form fields follow the saved settings when the hospital or the saved values change, not on every
    // refresh, so a poll never overwrites what someone is typing.
    let applied = '';
    effect(() => {
      const d = this.data();
      if (!d) return;
      const key = `${d.hospital}|${d.settings.catalogueEnabled}|${d.settings.catalogueTime}`;
      if (key === applied) return;
      applied = key;
      this.catEnabled.set(d.settings.catalogueEnabled);
      this.catTime.set(d.settings.catalogueTime);
    });
    const timer = setInterval(() => { if (this.active() && !this.busy() && !this.res.isLoading()) this.res.reload(); }, 10000);
    inject(DestroyRef).onDestroy(() => clearInterval(timer));
  }

  private async act(work: () => Promise<string>): Promise<void> {
    if (this.busy()) return;
    this.busy.set(true);
    this.error.set(false);
    this.message.set(null);
    try {
      this.message.set(await work());
      this.res.reload();
    } catch (err) {
      this.error.set(true);
      this.message.set(toApiError(err).message);
    } finally {
      this.busy.set(false);
    }
  }

  saveSettings(): Promise<void> {
    return this.act(async () => {
      await firstValueFrom(this.api.put(`his-sync/settings?hospital=${encodeURIComponent(this.ctx.hospitalParam()!)}`,
        { catalogueEnabled: this.catEnabled(), catalogueTime: this.catTime() || '01:00' }));
      return this.catEnabled() ? `Daily catalogue check saved for ${this.catTime()}.` : 'Daily catalogue check turned off.';
    });
  }

  syncCatalogueNow(): Promise<void> {
    return this.act(async () => {
      await firstValueFrom(this.api.post(`his-sync/catalogue?hospital=${encodeURIComponent(this.ctx.hospitalParam()!)}`, {}));
      return 'Catalogue check requested. Changes found are parked for review.';
    });
  }

  addRange(): Promise<void> {
    return this.act(async () => {
      const r = await firstValueFrom(this.api.post<HisSyncRange>(`his-sync/ranges?hospital=${encodeURIComponent(this.ctx.hospitalParam()!)}`,
        { fromDate: this.from(), toDate: this.to() }));
      this.from.set('');
      this.to.set('');
      return `Added ${r.fromDate} – ${r.toDate} (${r.daysTotal} day${r.daysTotal === 1 ? '' : 's'}) to sync.`;
    });
  }

  applyChange(c: HisSyncCatalogueChange): Promise<void> {
    if (!confirm(`Apply ${c.created} new and ${c.updated} changed catalogue row(s) to ${this.ctx.hospitalParam()}?`)) return Promise.resolve();
    return this.act(async () => {
      await firstValueFrom(this.api.post(`his-sync/catalogue-changes/${c.id}/apply?hospital=${encodeURIComponent(this.ctx.hospitalParam()!)}`, {}));
      return 'Catalogue import queued. Its progress shows under Earlier catalogue checks and on Data Import.';
    });
  }

  discardChange(c: HisSyncCatalogueChange): Promise<void> {
    if (!confirm('Discard these catalogue changes? The next check finds them again if they are still in the HIS.')) return Promise.resolve();
    return this.act(async () => {
      await firstValueFrom(this.api.post(`his-sync/catalogue-changes/${c.id}/discard?hospital=${encodeURIComponent(this.ctx.hospitalParam()!)}`, {}));
      return 'Catalogue changes discarded.';
    });
  }

  removeRange(r: HisSyncRange): Promise<void> {
    if (!confirm(`Remove ${r.fromDate} – ${r.toDate} from sync?`)) return Promise.resolve();
    return this.act(async () => {
      await firstValueFrom(this.api.delete(`his-sync/ranges/${r.id}?hospital=${encodeURIComponent(this.ctx.hospitalParam()!)}`));
      return `Removed ${r.fromDate} – ${r.toDate} from sync.`;
    });
  }
}
