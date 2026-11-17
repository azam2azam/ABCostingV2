import { HttpClient } from '@angular/common/http';
import { Component, DestroyRef, computed, effect, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { firstValueFrom } from 'rxjs';
import type { components } from '../core/api-types';
import { Api } from '../core/api';
import { apiV1 } from '../core/api-url';
import { Auth, toApiError } from '../core/auth';
import { RunContext } from '../core/context';
import { formatDec } from '../core/decimal';
import { apiResource } from '../core/resource';
import { Col, UI } from '../shared/ui';

type S = components['schemas'];
export type EntityDescription = S['Verita.Infrastructure.DataHub.EntityDescription'];
export type ColumnSpec = S['Verita.Infrastructure.DataHub.ColumnSpec'];
export type RowPage = S['Verita.Infrastructure.DataHub.RowPage'];
export type ImportResult = S['Verita.Infrastructure.DataHub.ImportResult'];
export type ImportBatchDto = S['Verita.Infrastructure.DataHub.ImportBatchDto'];

/** Downloads go through HttpClient so the bearer token travels with them. */
async function download(http: HttpClient, url: string, fallbackName: string): Promise<void> {
  const res = await firstValueFrom(http.get(url, { responseType: 'blob', observe: 'response' }));
  const name = /filename="?([^";]+)"?/.exec(res.headers.get('content-disposition') ?? '')?.[1] ?? fallbackName;
  const href = URL.createObjectURL(res.body!);
  const a = document.createElement('a');
  a.href = href;
  a.download = name;
  a.click();
  URL.revokeObjectURL(href);
}

function entityLabel(e: EntityDescription): string {
  return e.name === 'Procedure' ? 'Procedure/Activity' : e.name;
}

function groupsOf(entities: EntityDescription[]): { group: string; items: EntityDescription[] }[] {
  const out: { group: string; items: EntityDescription[] }[] = [];
  for (const e of entities) {
    let g = out.find(x => x.group === e.group);
    if (!g) out.push(g = { group: e.group, items: [] });
    g.items.push(e);
  }
  return out;
}

// ── Data entry ──────────────────────────────────────────────────────────────────

interface RecipeSuggestion {
  entity: string; key: string; title: string; reason: string;
  fields: Record<string, string>; missing: string[]; alreadyExists: boolean; billedCount: number;
}
interface RecipeSuggestionList { hospital: string; costingVersionCode?: string | null; items: RecipeSuggestion[]; notices: string[]; }

/**
 * Add and edit any table of the costing model. The form is generated from the same declaration
 * that builds the templates and validates uploads, so a row saved here passes exactly the checks
 * an uploaded row does. Effective-dated rows (rates, capacity, mappings, prices) are never edited
 * in place: a change is saved as a new version from a later date.
 */
@Component({
  selector: 'v-data-entry',
  imports: [FormsModule, ...UI],
  template: `
    <v-page title="Data entry" purpose="Add and edit the costing model, table by table. References are picked by code; rates and other effective-dated rows change by adding a new version. Recipe suggestions fill the form — they are not stored until you Save.">
      <div class="layout">
        <nav class="tables" aria-label="Tables">
          @for (g of groups(); track g.group) {
            <div class="group">{{ g.group }}</div>
            @for (e of g.items; track e.name) {
              <button type="button" [class.active]="selected()?.name === e.name" (click)="select(e)">{{ entityLabel(e) }}</button>
            }
          }
        </nav>
        <section class="content">
          @if (selected(); as e) {
            <div class="row head">
              <div>
                <h2>{{ entityLabel(e) }} @if (e.versioned) { <v-chip value="VERSIONED" label="Effective-dated" kind="info" /> }</h2>
                <p class="muted">{{ e.description }}</p>
              </div>
              <span class="spacer"></span>
              @if (canWrite(e)) { <button class="btn primary" type="button" (click)="startNew()">Add {{ entityLabel(e) }}</button> }
            </div>

            @if (suggestable.has(e.name)) {
              <section class="card suggestions">
                <div class="row head">
                  <div>
                    <h3>Suggestions</h3>
                    <p class="muted">Computed from billed HIS procedures and the recipe template. Picking one fills the form. Save writes the real row — suggestions are not stored.</p>
                  </div>
                  @if (hospitals().length > 1) {
                    <label class="field">Hospital
                      <select [ngModel]="hospital()" (ngModelChange)="hospital.set($event)" name="suggestHospital" aria-label="Hospital for suggestions">
                        @for (h of hospitals(); track h.code) { <option [value]="h.code">{{ h.code }}</option> }
                      </select>
                    </label>
                  }
                </div>
                @for (n of suggestions.value()?.notices ?? []; track n) { <p class="muted">{{ n }}</p> }
                <v-state [loading]="suggestions.isLoading() && !suggestions.value()" [error]="suggestions.error()">
                  @if (pendingSuggestions().length) {
                    <ul class="suggest-list">
                      @for (s of pendingSuggestions(); track s.key) {
                        <li>
                          <div>
                            <strong>{{ s.title }}</strong>
                            @if (s.billedCount) { <span class="muted"> · {{ s.billedCount }} billed</span> }
                            <p class="muted">{{ s.reason }}</p>
                            @if (s.missing.length) { <p class="muted">Needs {{ s.missing.join(', ') }} first.</p> }
                          </div>
                          @if (canWrite(e)) {
                            <button class="btn" type="button" (click)="useSuggestion(s)">Use</button>
                          }
                        </li>
                      }
                    </ul>
                    @if (pendingAll().length > pendingSuggestions().length) {
                      <p class="muted">Showing {{ pendingSuggestions().length }} of {{ pendingAll().length }} pending. Save some, then the next ones appear.</p>
                    }
                    @if (alreadySavedCount()) {
                      <p class="muted">{{ alreadySavedCount() }} already saved — omitted here.</p>
                    }
                  } @else if (suggestions.value()) {
                    <p class="muted">No pending suggestions for this table.</p>
                  }
                </v-state>
              </section>
            }

            @if (editing()) {
              <form class="card form" (ngSubmit)="save()">
                <h3>{{ mode() === 'new' ? 'New ' + entityLabel(e) : mode() === 'version' ? 'New version' : 'Edit ' + entityLabel(e) }}</h3>
                @if (mode() === 'version') { <p class="notice">The current version is kept and closed the day before the new effectiveFrom.</p> }
                <div class="fields">
                  @for (c of formColumns(); track c.name) {
                    <label class="field">
                      <span>{{ c.label || c.name }} @if (c.required) { <span class="bad" aria-hidden="true">*</span> }</span>
                      @switch (c.kind) {
                        @case ('ref') {
                          <select [name]="c.name" [attr.name]="c.name" [ngModel]="form()[c.name]" (ngModelChange)="set(c.name, $event)" [disabled]="locked(c)">
                            <option value="">{{ c.required ? '— choose —' : '— none —' }}</option>
                            @for (o of options()[c.refEntity ?? ''] ?? []; track o['code']) {
                              <option [value]="o['code']" [selected]="o['code'] === form()[c.name]">{{ o['code'] }} — {{ o['name'] }}</option>
                            }
                          </select>
                        }
                        @case ('bool') {
                          <select [name]="c.name" [attr.name]="c.name" [ngModel]="form()[c.name]" (ngModelChange)="set(c.name, $event)" [disabled]="locked(c)">
                            <option value=""></option><option value="true" [selected]="form()[c.name] === 'true'">true</option><option value="false" [selected]="form()[c.name] === 'false'">false</option>
                          </select>
                        }
                        @case ('date') { <input type="date" [name]="c.name" [attr.name]="c.name" [ngModel]="form()[c.name]" (ngModelChange)="set(c.name, $event)" [disabled]="locked(c)" /> }
                        @case ('password') { <input type="password" [name]="c.name" [attr.name]="c.name" autocomplete="new-password" [ngModel]="form()[c.name]" (ngModelChange)="set(c.name, $event)" /> }
                        @default {
                          @if (c.options) {
                            <select [name]="c.name" [attr.name]="c.name" [ngModel]="form()[c.name]" (ngModelChange)="set(c.name, $event)" [disabled]="locked(c)">
                              <option value=""></option>
                              @for (o of c.options; track o) { <option [value]="o" [selected]="o === form()[c.name]">{{ o }}</option> }
                            </select>
                          } @else {
                            <input [type]="c.kind === 'text' ? 'text' : 'text'" [attr.inputmode]="c.kind === 'decimal' || c.kind === 'int' ? 'decimal' : null"
                                   [name]="c.name" [attr.name]="c.name" [ngModel]="form()[c.name]" (ngModelChange)="set(c.name, $event)" [disabled]="locked(c)"
                                   [placeholder]="c.kind === 'datetime' ? 'yyyy-mm-dd hh:mm' : (c.description ?? '')" />
                          }
                        }
                      }
                    </label>
                  }
                </div>
                @if (errors().length) {
                  <div class="notice bad" role="alert"><strong>Not saved.</strong><ul>@for (x of errors(); track x) { <li>{{ x }}</li> }</ul></div>
                }
                <div class="row">
                  <button class="btn primary" type="submit" [disabled]="busy()">{{ busy() ? 'Saving…' : 'Save' }}</button>
                  <button class="btn" type="button" (click)="editing.set(false)">Cancel</button>
                </div>
              </form>
            }
            @if (message()) { <p class="notice">{{ message() }}</p> }

            <div class="row" style="margin-bottom:8px">
              <input type="search" placeholder="Search" [ngModel]="search()" (ngModelChange)="search.set($event); page.set(1)" name="search" aria-label="Search rows" />
              <span class="muted">{{ rows.value()?.total ?? 0 }} rows</span>
            </div>
            <v-state [loading]="rows.isLoading() && !rows.value()" [error]="rows.error()">
              <div class="table-wrap">
                <table class="data">
                  <thead><tr>
                    @if (canWrite(e)) { <th></th> }
                    @for (c of listColumns(); track c) { <th scope="col">{{ columnLabel(c) }}</th> }
                  </tr></thead>
                  <tbody>
                    @for (r of rows.value()?.rows ?? []; track $index) {
                      <tr>
                        @if (canWrite(e)) {
                          <td><button class="btn" type="button" (click)="startEdit(r)">{{ e.versioned ? 'New version' : 'Edit' }}</button></td>
                        }
                        @for (c of listColumns(); track c) { <td>{{ r[c] }}</td> }
                      </tr>
                    } @empty { <tr><td class="faint" [attr.colspan]="99">No rows yet.</td></tr> }
                  </tbody>
                </table>
              </div>
              @if ((rows.value()?.total ?? 0) > pageSize) {
                <div class="row" style="margin-top:8px">
                  <button class="btn" type="button" [disabled]="page() <= 1" (click)="page.set(page() - 1)">Previous</button>
                  <span class="muted">Page {{ page() }}</span>
                  <button class="btn" type="button" [disabled]="page() * pageSize >= (rows.value()?.total ?? 0)" (click)="page.set(page() + 1)">Next</button>
                </div>
              }
            </v-state>
          } @else { <p class="muted">Pick a table on the left.</p> }
        </section>
      </div>
    </v-page>
  `,
  styles: `
    .layout { display: grid; grid-template-columns: 200px 1fr; gap: 16px; align-items: start; }
    .tables { background: var(--surface); border: 1px solid var(--border); border-radius: var(--radius); padding: 8px; position: sticky; top: 70px; max-height: 80vh; overflow-y: auto; }
    .tables .group { font-size: 12px; text-transform: uppercase; letter-spacing: .5px; color: var(--text-3); padding: 10px 6px 4px; }
    .tables button { all: unset; display: block; padding: 4px 8px; border-radius: 4px; cursor: pointer; width: calc(100% - 16px); }
    .tables button.active { background: var(--accent-soft); color: var(--accent); font-weight: 600; }
    .tables button:focus-visible { outline: 2px solid var(--focus); }
    .content { min-width: 0; }
    .head { align-items: flex-start; }
    .fields { display: grid; grid-template-columns: repeat(auto-fill, minmax(220px, 1fr)); gap: 10px; margin: 10px 0; }
    .suggestions { margin-bottom: 12px; }
    .suggestions h3 { margin: 0 0 4px; }
    .suggest-list { list-style: none; margin: 8px 0 0; padding: 0; display: flex; flex-direction: column; gap: 8px; }
    .suggest-list li { display: flex; gap: 12px; align-items: flex-start; justify-content: space-between; padding: 8px 0; border-top: 1px solid var(--border); }
    .suggest-list li p { margin: 4px 0 0; }
    @media (max-width: 800px) { .layout { grid-template-columns: 1fr; } .tables { position: static; max-height: none; } }
  `,
})
export class DataEntry {
  private api = inject(Api);
  private auth = inject(Auth);
  private ctx = inject(RunContext);
  entityLabel = entityLabel;
  readonly suggestable = new Set(['Activity', 'ActivityResource', 'ProcedureActivityLine']);
  hospital = signal('');
  hospitals = computed(() => this.ctx.context()?.hospitals ?? []);
  entities = apiResource<EntityDescription[]>(() => 'data/entities');
  suggestions = apiResource<RecipeSuggestionList>(() => {
    const e = this.selected()?.name;
    const h = this.hospital();
    if (!e || !h || !this.suggestable.has(e)) return null;
    return 'data/recipe-suggestions';
  }, () => ({ hospital: this.hospital(), entity: this.selected()?.name ?? '' }));
  pendingAll = computed(() => (this.suggestions.value()?.items ?? []).filter(s => !s.alreadyExists));
  pendingSuggestions = computed(() => this.pendingAll().slice(0, 40));
  alreadySavedCount = computed(() => (this.suggestions.value()?.items ?? []).filter(s => s.alreadyExists).length);
  visibleEntities = computed(() => {
    const list = this.entities.value() ?? [];
    return this.ctx.hasDrgs() ? list : list.filter(e => e.name !== 'Drg');
  });
  groups = computed(() => groupsOf(this.visibleEntities()));
  selected = signal<EntityDescription | null>(null);
  search = signal('');
  page = signal(1);
  pageSize = 20;
  rows = apiResource<RowPage>(() => this.selected() ? `data/${this.selected()!.name}` : null,
    () => ({ search: this.search(), page: this.page(), pageSize: this.pageSize }));
  editing = signal(false);
  mode = signal<'new' | 'edit' | 'version'>('new');
  form = signal<Record<string, string>>({});
  errors = signal<string[]>([]);
  message = signal<string | null>(null);
  busy = signal(false);
  options = signal<Record<string, Record<string, string>[]>>({});
  formColumns = computed(() => {
    const cols = this.selected()?.columns ?? [];
    return this.ctx.hasDrgs() ? cols : cols.filter(c => c.name !== 'drgCode');
  });
  listColumns = computed(() => {
    const cols = this.rows.value()?.columns ?? [];
    return this.ctx.hasDrgs() ? cols : cols.filter(c => c !== 'drgCode');
  });
  columnLabel(name: string): string {
    return this.formColumns().find(c => c.name === name)?.label || name;
  }

  constructor() {
    void this.ctx.load();
    effect(() => {
      const list = this.visibleEntities();
      const current = this.selected();
      if (!list.length) { if (current) this.selected.set(null); return; }
      if (!current || !list.some(e => e.name === current.name)) this.selected.set(list[0]);
    });
    effect(() => {
      if (this.hospital()) return;
      const code = this.ctx.hospitalParam() ?? this.ctx.run()?.hospital ?? this.hospitals()[0]?.code ?? '';
      if (code) this.hospital.set(code);
    });
  }

  canWrite(e: EntityDescription): boolean { return this.auth.can(e.writePermission); }

  select(e: EntityDescription): void {
    this.selected.set(e);
    this.search.set('');
    this.page.set(1);
    this.editing.set(false);
    this.message.set(null);
  }

  /** Key columns cannot change on an existing row; on a new version only effectiveFrom moves. */
  locked(c: ColumnSpec): boolean {
    if (this.mode() === 'new') return false;
    if (this.mode() === 'version') return c.key && c.name !== 'effectiveFrom';
    return c.key;
  }

  set(name: string, value: string): void { this.form.set({ ...this.form(), [name]: value ?? '' }); }

  async useSuggestion(s: RecipeSuggestion): Promise<void> {
    this.mode.set('new');
    this.form.set({ ...s.fields });
    this.errors.set([]);
    this.editing.set(true);
    this.message.set(s.missing.length
      ? `Suggestion filled the form. ${s.missing.join(', ')} ${s.missing.length === 1 ? 'is' : 'are'} missing — create ${s.missing.length === 1 ? 'it' : 'them'} first, then Save.`
      : 'Suggestion filled the form. Review and Save to write the real row. Suggestions are not stored until you Save.');
    await this.loadOptions();
  }

  private async loadOptions(): Promise<void> {
    const needed = [...new Set(this.formColumns().filter(c => c.kind === 'ref' && c.refEntity).map(c => c.refEntity!))];
    const current = { ...this.options() };
    await Promise.all(needed.filter(n => !current[n]).map(async n => {
      current[n] = await firstValueFrom(this.api.get<Record<string, string>[]>(`data/${n}/lookup`));
    }));
    this.options.set(current);
  }

  async startNew(): Promise<void> {
    this.mode.set('new');
    this.form.set({});
    this.errors.set([]);
    this.message.set(null);
    this.editing.set(true);
    await this.loadOptions();
  }

  async startEdit(row: Record<string, string>): Promise<void> {
    const versioned = this.selected()!.versioned;
    this.mode.set(versioned ? 'version' : 'edit');
    const copy = { ...row };
    if (versioned) { copy['effectiveFrom'] = ''; copy['effectiveTo'] = ''; }
    this.form.set(copy);
    this.errors.set([]);
    this.message.set(null);
    this.editing.set(true);
    await this.loadOptions();
  }

  async save(): Promise<void> {
    const e = this.selected()!;
    this.busy.set(true);
    this.errors.set([]);
    try {
      const res = await firstValueFrom(this.api.post<ImportResult>(`data/${e.name}`, this.form()));
      this.message.set(res.created ? `${entityLabel(e)} added.` : res.updated ? `${entityLabel(e)} updated.` : 'Nothing changed.');
      this.editing.set(false);
      this.rows.reload();
      this.suggestions.reload();
      this.options.set({});
    } catch (err) {
      const a = toApiError(err);
      this.errors.set(((a.detail as { errors?: string[] } | undefined)?.errors) ?? [a.message]);
    } finally {
      this.busy.set(false);
    }
  }
}

// ── Data import ─────────────────────────────────────────────────────────────────

interface ImportPreviewTable { entity: string; fileName: string; rows: number; }
interface ImportPreviewHospital { code: string; name: string; hisSetupId?: string | null; hisProjectId?: number | null; match: boolean; inScope: boolean; }
interface ImportPreview {
  hisPack: boolean; fileName: string; setupId?: string | null; projectId?: number | null;
  pack?: string | null; from?: string | null; toInclusive?: string | null; extractedAtUtc?: string | null;
  tables: ImportPreviewTable[]; skipped: string[]; hospitals: ImportPreviewHospital[];
  suggestedHospital?: string | null; notices: string[]; warnings: string[];
}

interface ImportFileDto {
  id: string; fileName: string; sizeBytes: number; uploadedAt: string; uploadedBy: string;
  isHisPack: boolean; hisSetupId?: string | null; hisProjectId?: number | null; pack?: string | null; factsFrom?: string | null; factsTo?: string | null;
  hospital?: string | null; totalRows: number; status: string; lastDryRun: boolean; tablesTotal: number; tablesDone: number; currentTable?: string | null;
  attempts: number; queuedAt?: string | null; startedAt?: string | null; finishedAt?: string | null; runBy?: string | null;
  rowsCreated: number; rowsUpdated: number; rowsUnchanged: number; rowsRejected: number; errorMessage?: string | null;
}
interface ImportFileDetail { file: ImportFileDto; preview: ImportPreview | null; results: ImportResult[]; }

const ACTIVE = ['QUEUED', 'RUNNING'];
const STATUS_TEXT: Record<string, string> = {
  UPLOADED: 'Uploaded · not imported', QUEUED: 'Queued', RUNNING: 'Running', VALIDATED: 'Validated · not imported',
  COMPLETED: 'Imported', COMPLETED_WITH_REJECTS: 'Imported with rejects', FAILED: 'Failed',
};

/**
 * Upload is immediate: the file is stored with its preview. Validate and import run in the background
 * and report progress on the file, so the page can be left and the file imported again from the list.
 */
@Component({
  selector: 'v-data-import',
  imports: [FormsModule, ...UI],
  template: `
    <v-page title="Data import" purpose="Upload files extracted by the HIS Extract Utility (or filled templates). Upload stores the file; validate and import run in the background, and every file stays in the list to import again.">
      <section class="card">
        <h2>Upload</h2>
        <div class="row">
          <input #picker type="file" accept=".csv,.zip" multiple (change)="pick($event, picker)" aria-label="Files to upload" [disabled]="uploading()" />
        </div>
        <p class="muted" style="margin:6px 0 0">Choose one file to review it first, or several (e.g. a month of daily packs) to upload them all and queue them for import, oldest date first.</p>
        @if (uploading()) {
          <div class="import-loading" role="status"><span class="spinner" aria-hidden="true"></span><strong>{{ uploadText() }}</strong></div>
        }
        @if (error()) { <div class="notice bad" role="alert" style="margin-top:10px">{{ error() }}</div> }
        @if (batch(); as b) {
          <div class="notice" [class.warn]="b.failed.length > 0" role="status" style="margin-top:10px">
            <strong>{{ b.uploaded }} of {{ b.total }} file(s) uploaded, {{ b.queued }} queued for import{{ b.queued ? ' (oldest date first)' : '' }}.</strong>
            @if (b.failed.length) {
              <ul style="margin:6px 0 0; padding-left:20px">@for (f of b.failed; track f) { <li>{{ f }}</li> }</ul>
            }
          </div>
        }

        @if (selected(); as d) {
          <div class="his-preview">
            <div class="sel-head">
              <h3>{{ d.file.fileName }}</h3>
              <button class="btn" type="button" (click)="close()">Close</button>
            </div>
            <p class="muted">{{ fileFacts(d.file) }}</p>
            @if (d.preview?.hisPack) {
              <p class="notice">
                HIS pack · SetupID <strong>{{ d.file.hisSetupId }}</strong> · ProjectID <strong>{{ d.file.hisProjectId }}</strong>
                @if (d.file.pack) { · {{ d.file.pack }} }
                @if (d.file.factsFrom) { · {{ d.file.factsFrom }} .. {{ d.file.factsTo }} }
              </p>
              @if (mismatch(d.file); as m) {
                <p class="notice bad" role="alert" style="margin-top:8px">{{ m }}</p>
              } @else {
                <p class="muted" style="margin-top:8px">Imports into <strong>{{ target()?.code }}</strong> · {{ target()?.name }}, the hospital selected at the top.</p>
              }
            }
            @for (n of d.preview?.notices ?? []; track n) { <p class="notice" style="margin-top:8px">{{ n }}</p> }

            @if (live(d.file); as f) {
              @if (isActive(f)) {
                <div class="import-progress" role="status" aria-live="polite">
                  <div class="import-loading">
                    <span class="spinner" aria-hidden="true"></span>
                    <div>
                      <strong>{{ f.status === 'QUEUED' ? 'Queued' : (f.lastDryRun ? 'Validating' : 'Importing') }}{{ f.currentTable ? ' · ' + f.currentTable : '' }}</strong>
                      <p class="muted">Runs in the background. You can leave this page; the list below keeps the status.</p>
                    </div>
                    <span class="import-pct">{{ pct(f) }}%</span>
                  </div>
                  <div class="bar big" role="progressbar" [attr.aria-valuenow]="pct(f)" aria-valuemin="0" aria-valuemax="100" aria-label="Tables done"><span [style.width.%]="pct(f)"></span></div>
                  <p class="muted" style="margin:8px 0 0">{{ f.tablesDone }} of {{ f.tablesTotal }} tables done</p>
                </div>
              } @else {
                @if (f.status === 'FAILED') { <p class="notice bad" style="margin-top:12px">{{ f.errorMessage || 'Failed.' }}</p> }
                @if (f.status === 'VALIDATED') { <p class="notice" style="margin-top:12px">Validation only — nothing was saved. {{ fmt(f.rowsCreated) }} new, {{ fmt(f.rowsUpdated) }} updated, {{ fmt(f.rowsRejected) }} rejected.</p> }
                @if (f.status === 'COMPLETED' || f.status === 'COMPLETED_WITH_REJECTS') { <p class="notice" [class.warn]="f.rowsRejected > 0" style="margin-top:12px">Imported {{ when(f.finishedAt) }}. {{ fmt(f.rowsCreated) }} new, {{ fmt(f.rowsUpdated) }} updated, {{ fmt(f.rowsUnchanged) }} unchanged, {{ fmt(f.rowsRejected) }} rejected.</p> }
              }
              <div class="row" style="margin-top:12px">
                <button class="btn" type="button" (click)="run(f, true)" [disabled]="!canRun(f, d)">Validate</button>
                <button class="btn primary" type="button" (click)="run(f, false)" [disabled]="!canRun(f, d)">{{ imported(f) ? 'Import again' : 'Import' }}</button>
              </div>
            }

            @if (d.results.length && !isActive(live(d.file))) {
              <h3 style="margin-top:14px">{{ d.file.lastDryRun ? 'Validation results' : 'Import results' }}</h3>
              <v-table [rows]="d.results" [columns]="resultCols" [caption]="d.file.lastDryRun ? 'Validation results' : 'Import results'" />
            } @else if (d.preview?.tables?.length) {
              <h3 style="margin-top:14px">Tables in this file</h3>
              <v-table [rows]="d.preview!.tables" [columns]="previewTableCols" caption="File contents" />
            }
          </div>
        }
      </section>

      <section class="card">
        <h2>Uploaded files</h2>
        <v-state [loading]="files.isLoading() && !files.value()" [error]="files.error()">
          <v-table paged [rows]="files.value() ?? []" [columns]="fileCols()" caption="Uploaded files" empty="No files uploaded yet." />
        </v-state>
      </section>
    </v-page>
  `,
  styles: `
    .his-preview { margin-top: 16px; padding-top: 14px; border-top: 1px solid var(--border); }
    .sel-head { display: flex; align-items: center; justify-content: space-between; gap: 12px; }
    .sel-head h3 { margin: 0; overflow-wrap: anywhere; }
    .import-loading { display: flex; align-items: center; gap: 14px; margin-top: 14px; font-size: 16px; }
    .import-loading p { margin: 2px 0 0; font-size: 13px; }
    .spinner { flex: none; width: 36px; height: 36px; border: 4px solid var(--border); border-top-color: var(--accent); border-radius: 50%; animation: import-spin .9s linear infinite; }
    @keyframes import-spin { to { transform: rotate(360deg); } }
    @media (prefers-reduced-motion: reduce) { .spinner { animation-duration: 3s; } }
    .import-progress { margin-top: 14px; padding: 18px; border: 1px solid var(--border); border-radius: var(--radius-sm); background: var(--surface-2); }
    .import-progress .import-loading { margin-top: 0; }
    .import-pct { margin-left: auto; font-size: 26px; font-weight: 700; color: var(--accent-strong); font-variant-numeric: tabular-nums; }
    .bar { height: 6px; border-radius: 999px; background: var(--border); overflow: hidden; }
    .bar span { display: block; height: 100%; border-radius: inherit; background: var(--accent); transition: width .4s linear; }
    .bar.big { height: 12px; margin-top: 14px; }
  `,
})
export class DataImport {
  private http = inject(HttpClient);
  private api = inject(Api);
  private ctx = inject(RunContext);
  files = apiResource<ImportFileDto[]>(() => 'data/files');
  selected = signal<ImportFileDetail | null>(null);
  /** The hospital selected in the top bar: every import goes there. */
  target = computed(() => {
    const code = this.ctx.hospitalParam();
    return (this.ctx.context()?.hospitals ?? []).find(h => h.code === code) ?? null;
  });
  uploading = signal(false);
  uploadText = signal('');
  batch = signal<{ total: number; uploaded: number; queued: number; failed: string[] } | null>(null);
  busy = signal(false);
  error = signal<string | null>(null);
  private poll: ReturnType<typeof setInterval> | null = null;
  private wasActive = new Set<string>();

  constructor() {
    void this.ctx.load();
    // Poll while anything is queued or running; refresh the open file when its job finishes.
    effect(() => {
      const list = this.files.value() ?? [];
      const active = list.filter(f => ACTIVE.includes(f.status));
      const finished = [...this.wasActive].filter(id => !active.some(f => f.id === id));
      this.wasActive = new Set(active.map(f => f.id));
      const sel = this.selected();
      if (sel && finished.includes(sel.file.id)) void this.open(sel.file.id);
      if (finished.length) void this.ctx.load(true);
      if (active.length && !this.poll) this.poll = setInterval(() => this.files.reload(), 3000);
      if (!active.length && this.poll) { clearInterval(this.poll); this.poll = null; }
    });
    inject(DestroyRef).onDestroy(() => { if (this.poll) clearInterval(this.poll); });
  }

  previewTableCols: Col<ImportPreviewTable>[] = [
    { key: 'entity', label: 'Table' }, { key: 'fileName', label: 'File' }, { key: 'rows', label: 'Rows', type: 'num', dp: 0 },
  ];
  resultCols: Col<ImportResult>[] = [
    { key: 'entity', label: 'Table' }, { key: 'rows', label: 'Rows', type: 'num', dp: 0 }, { key: 'created', label: 'New', type: 'num', dp: 0 },
    { key: 'updated', label: 'Updated', type: 'num', dp: 0 }, { key: 'unchanged', label: 'Unchanged', type: 'num', dp: 0 },
    { key: 'rejected', label: 'Rejected', type: 'num', dp: 0, bad: r => r.rejected > 0 },
    { key: 'rejects', label: '', type: 'button', sortable: false, button: r => r.batchId && r.rejected > 0 ? 'Download rejects' : null, onButton: r => void this.rejects(r) },
  ];
  fileCols = computed<Col<ImportFileDto>[]>(() => [
    { key: 'uploadedAt', label: 'Uploaded', value: r => this.when(r.uploadedAt), sub: r => r.uploadedBy },
    { key: 'fileName', label: 'File', sub: r => this.fileFacts(r) },
    { key: 'hospital', label: 'Hospital', value: r => r.hospital ?? '—' },
    { key: 'status', label: 'Status', type: 'chip', value: r => this.statusText(r), bad: r => r.status === 'FAILED' || r.rowsRejected > 0 && !ACTIVE.includes(r.status), sub: r => r.status === 'FAILED' ? r.errorMessage ?? null : this.mismatch(r) ? 'Not for the selected hospital' : null },
    { key: 'result', label: 'Result', sortable: false, value: r => this.resultText(r) },
    { key: 'open', label: '', type: 'button', sortable: false, button: () => 'Open', onButton: r => void this.open(r.id) },
    { key: 'sync', label: '', type: 'button', sortable: false, button: r => this.isActive(r) || this.mismatch(r) ? null : this.imported(r) ? 'Re-import' : 'Import', onButton: r => void this.quick(r) },
    { key: 'delete', label: '', type: 'button', sortable: false, button: r => this.isActive(r) ? null : 'Remove', onButton: r => void this.remove(r) },
  ]);

  isActive(f: ImportFileDto | null | undefined): boolean { return !!f && ACTIVE.includes(f.status); }
  imported(f: ImportFileDto): boolean { return f.status === 'COMPLETED' || f.status === 'COMPLETED_WITH_REJECTS' || (f.status === 'FAILED' && !f.lastDryRun); }
  /** The open file's latest status comes from the polled list. */
  live(f: ImportFileDto): ImportFileDto { return this.files.value()?.find(x => x.id === f.id) ?? f; }
  pct(f: ImportFileDto): number { return f.tablesTotal ? Math.floor(f.tablesDone / f.tablesTotal * 100) : 0; }
  canRun(f: ImportFileDto, _d: ImportFileDetail): boolean { return !this.busy() && !this.isActive(f) && !this.mismatch(f); }

  /** Why a HIS pack cannot load into the top-bar hospital: SetupID / ProjectID must match. Null when it can. */
  mismatch(f: ImportFileDto): string | null {
    if (!f.isHisPack) return null;
    const h = this.target();
    if (!h) return 'Select a hospital at the top of the screen.';
    const file = `HIS SetupID ${f.hisSetupId} / ProjectID ${f.hisProjectId}`;
    if (!h.hisSetupId || h.hisProjectId == null) return `This file is ${file}, but ${h.code} has no HIS SetupID / ProjectID. Set them on the hospital in Data Entry first.`;
    if (h.hisSetupId.trim().toLowerCase() !== (f.hisSetupId ?? '').trim().toLowerCase() || h.hisProjectId !== f.hisProjectId)
      return `This file is ${file}, but ${h.code} is HIS SetupID ${h.hisSetupId} / ProjectID ${h.hisProjectId}. Select the matching hospital at the top of the screen.`;
    return null;
  }

  statusText(f: ImportFileDto): string {
    if (f.status === 'RUNNING') return `${f.lastDryRun ? 'Validating' : 'Importing'} ${f.tablesDone}/${f.tablesTotal}${f.currentTable ? ' · ' + f.currentTable : ''}`;
    if (f.status === 'QUEUED') {
      const queue = (this.files.value() ?? []).filter(x => x.status === 'QUEUED').sort((a, b) => (a.queuedAt ?? '').localeCompare(b.queuedAt ?? ''));
      const pos = queue.findIndex(x => x.id === f.id) + 1;
      return `${f.lastDryRun ? 'Queued to validate' : 'Queued to import'}${pos > 0 ? ' · #' + pos : ''}`;
    }
    return STATUS_TEXT[f.status] ?? f.status;
  }
  resultText(f: ImportFileDto): string {
    if (this.isActive(f) || f.status === 'UPLOADED') return '—';
    if (f.status === 'FAILED') return f.attempts > 1 ? `${f.attempts} attempts` : '—';
    return `${this.fmt(f.rowsCreated)} new · ${this.fmt(f.rowsUpdated)} updated · ${this.fmt(f.rowsRejected)} rejected`;
  }
  fileFacts(f: ImportFileDto): string {
    return [formatBytes(f.sizeBytes), `${this.fmt(f.totalRows)} rows`, `${f.tablesTotal} table${f.tablesTotal === 1 ? '' : 's'}`,
      f.isHisPack ? `HIS ${f.hisSetupId}/${f.hisProjectId}` : 'Template',
      f.factsFrom ? `${f.factsFrom}${f.factsTo && f.factsTo !== f.factsFrom ? ' .. ' + f.factsTo : ''}` : null].filter(Boolean).join(' · ');
  }
  when(v?: string | null): string { return v ? new Date(v.endsWith('Z') ? v : v + 'Z').toLocaleString() : ''; }
  fmt(n: number): string { return formatDec(String(n), 0); }

  async pick(ev: Event, input: HTMLInputElement): Promise<void> {
    const picked = Array.from((ev.target as HTMLInputElement).files ?? []);
    if (!picked.length) return;
    this.error.set(null);
    this.batch.set(null);
    this.uploading.set(true);
    try {
      if (picked.length === 1) {
        this.uploadText.set(`Uploading and reading ${picked[0].name}…`);
        this.select(await this.uploadOne(picked[0]));
        this.files.reload();
        return;
      }
      await this.uploadMany(picked);
    } catch (e) {
      this.error.set(toApiError(e).message);
    } finally {
      this.uploading.set(false);
      this.uploadText.set('');
      input.value = '';
    }
  }

  private uploadOne(f: File): Promise<ImportFileDetail> {
    const body = new FormData();
    body.append('file', f);
    return firstValueFrom(this.http.post<ImportFileDetail>(`${apiV1}/data/files`, body));
  }

  /** Upload each file, then queue the ones for the selected hospital: catalogue first, then facts by date. */
  private async uploadMany(picked: File[]): Promise<void> {
    const ordered = [...picked].sort((a, b) => a.name.localeCompare(b.name, undefined, { numeric: true }));
    const uploaded: ImportFileDto[] = [];
    const failed: string[] = [];
    for (const [i, f] of ordered.entries()) {
      this.uploadText.set(`Uploading ${i + 1} of ${ordered.length}: ${f.name}…`);
      try {
        uploaded.push((await this.uploadOne(f)).file);
      } catch (e) {
        failed.push(`${f.name}: ${toApiError(e).message}`);
      }
    }
    this.files.reload();
    const hospital = this.ctx.hospitalParam();
    const toQueue = uploaded.filter(f => {
      const m = this.mismatch(f);
      if (m) failed.push(`${f.fileName}: ${m}`);
      return !m;
    }).sort((a, b) => queueKey(a).localeCompare(queueKey(b), undefined, { numeric: true }));
    let queued = 0;
    for (const [i, f] of toQueue.entries()) {
      this.uploadText.set(`Queuing ${i + 1} of ${toQueue.length}: ${f.fileName}…`);
      try {
        await firstValueFrom(this.api.post<ImportFileDto>(`data/files/${f.id}/import`, { hospital: hospital || null, dryRun: false }));
        queued++;
      } catch (e) {
        failed.push(`${f.fileName}: ${toApiError(e).message}`);
      }
    }
    this.batch.set({ total: picked.length, uploaded: uploaded.length, queued, failed });
    this.files.reload();
  }

  async open(id: string): Promise<void> {
    this.error.set(null);
    try {
      this.select(await firstValueFrom(this.api.get<ImportFileDetail>(`data/files/${id}`)));
    } catch (e) {
      this.error.set(toApiError(e).message);
    }
  }

  private select(d: ImportFileDetail): void {
    this.selected.set(d);
  }

  close(): void { this.selected.set(null); }

  async run(f: ImportFileDto, dryRun: boolean): Promise<void> {
    if (this.busy()) return;
    this.busy.set(true);
    this.error.set(null);
    try {
      await firstValueFrom(this.api.post<ImportFileDto>(`data/files/${f.id}/import`, { hospital: this.ctx.hospitalParam() || null, dryRun }));
      this.files.reload();
    } catch (e) {
      this.error.set(toApiError(e).message);
    } finally {
      this.busy.set(false);
    }
  }

  /** From the list: import into the top-bar hospital. A HIS pack for another hospital is refused. */
  async quick(f: ImportFileDto): Promise<void> {
    const mismatch = this.mismatch(f);
    if (mismatch) { this.error.set(mismatch); return; }
    const hospital = this.ctx.hospitalParam();
    if (this.imported(f) && !confirm(`Import ${f.fileName} again${hospital ? ' into ' + hospital : ''}? Rows are matched on their keys, so existing rows are updated, not duplicated.`)) return;
    if (this.busy()) return;
    this.busy.set(true);
    this.error.set(null);
    try {
      await firstValueFrom(this.api.post<ImportFileDto>(`data/files/${f.id}/import`, { hospital: hospital || null, dryRun: false }));
      this.files.reload();
    } catch (e) {
      this.error.set(toApiError(e).message);
    } finally {
      this.busy.set(false);
    }
  }

  async remove(f: ImportFileDto): Promise<void> {
    if (!confirm(`Remove ${f.fileName} from the list? Data already imported stays.`)) return;
    try {
      await firstValueFrom(this.api.delete(`data/files/${f.id}`));
      if (this.selected()?.file.id === f.id) this.selected.set(null);
      this.files.reload();
    } catch (e) {
      this.error.set(toApiError(e).message);
    }
  }

  rejects(r: ImportResult) { return download(this.http, `${apiV1}/data/imports/${r.batchId}/rejects`, `${r.entity}-rejects.csv`); }
}

/** Queue order: catalogue packs first, then facts by their first day, then by name. */
function queueKey(f: ImportFileDto): string {
  return `${f.pack === 'catalogue' ? '0' : '1'}|${f.factsFrom ?? '9999-99-99'}|${f.fileName}`;
}

function formatBytes(n: number): string {
  if (!n) return '0 B';
  const units = ['B', 'KB', 'MB', 'GB'];
  const i = Math.min(units.length - 1, Math.floor(Math.log(n) / Math.log(1024)));
  return `${(n / 1024 ** i).toFixed(i ? 1 : 0)} ${units[i]}`;
}

// ── Storage ─────────────────────────────────────────────────────────────────────

interface StorageTable { table: string; group: string; rows: number; bytes: number; dataBytes: number; indexBytes: number; }
interface StoragePeriodEntity { entity: string; rows: number; estimatedBytes: number; }
interface StoragePeriod {
  id: string; code: string; startDate: string; endDate: string; status: string; run?: string | null; runStatus?: string | null;
  entities: StoragePeriodEntity[]; factRows: number; factBytes: number; resultRows: number; resultBytes: number;
}
interface StorageDto {
  hospital: string; databaseBytes: number; usedBytes: number; tables: StorageTable[];
  factEntities: { entity: string; label: string; group: string }[]; periods: StoragePeriod[];
}
interface StorageGroupRow { group: string; tables: number; rows: number; bytes: number; share: number; }

const GROUP_ORDER = ['Facts', 'Catalogue', 'Calculation results', 'Import files', 'History', 'System'];
const GROUP_NOTE: Record<string, string> = {
  Facts: 'Monthly HIS data: encounters, services, activity, revenue, refunds, consumption, staff time, ledger.',
  Catalogue: 'Hospitals, departments, cost centres, procedures, resources, rates and the rest of the costing model.',
  'Calculation results': 'What calculation runs wrote. Removed with the run.',
  'Import files': 'Uploaded files kept for re-import, and import batches.',
  History: 'Earlier versions of catalogue rows (temporal history).',
  System: 'Users, audit trail, assistant and migration tables.',
};

/** How much the database holds: by table and group (all hospitals), and the selected hospital's facts per period. */
@Component({
  selector: 'v-storage',
  imports: [...UI],
  template: `
    <v-page title="Storage" purpose="How much the database holds. Storage by group covers every hospital; the period summary is for the selected hospital and is estimated from each table's average row size.">
      <v-state [loading]="res.isLoading() && !res.value()" [error]="res.error()">
        @if (res.value(); as d) {
          <div class="grid tiles">
            <v-tile label="Database size" [value]="size(d.databaseBytes)" unit="id" [note]="size(d.usedBytes) + ' used'" />
            @for (g of groupTiles(); track g.group) {
              <v-tile [label]="g.group" [value]="size(g.bytes)" unit="id" [note]="fmt(g.rows) + ' rows in ' + g.tables + ' tables'" />
            }
          </div>

          <section class="card" style="margin-top:14px">
            <h2>By group</h2>
            <v-table [rows]="groups()" [columns]="groupCols" caption="Storage by group" />
          </section>

          <section class="card">
            <h2>Facts by period · {{ d.hospital }}</h2>
            <p class="muted">Facts belong to a period by encounter admit date, as on each period's detail screen. Calculation results are the rows the period's runs wrote.</p>
            <v-table [rows]="d.periods" [columns]="periodCols()" [total]="periodTotal()" caption="Facts by period" empty="This hospital has no periods." />
          </section>

        }
      </v-state>
    </v-page>
  `,
})
export class Storage {
  private ctx = inject(RunContext);
  res = apiResource<StorageDto>(() => this.ctx.hospitalParam() ? 'storage' : null, () => ({ hospital: this.ctx.hospitalParam() }));
  size = formatBytes;
  fmt(n: number): string { return formatDec(String(n), 0); }

  constructor() { void this.ctx.load(); }

  groups = computed<StorageGroupRow[]>(() => {
    const t = this.res.value()?.tables ?? [];
    const total = t.reduce((s, x) => s + x.bytes, 0) || 1;
    const names = [...new Set(t.map(x => x.group))].sort((a, b) => GROUP_ORDER.indexOf(a) - GROUP_ORDER.indexOf(b));
    return names.map(g => {
      const rows = t.filter(x => x.group === g);
      const bytes = rows.reduce((s, x) => s + x.bytes, 0);
      return { group: g, tables: rows.length, rows: rows.reduce((s, x) => s + x.rows, 0), bytes, share: bytes / total * 100 };
    });
  });
  groupTiles = computed(() => this.groups().filter(g => ['Facts', 'Catalogue', 'Calculation results'].includes(g.group)));

  groupCols: Col<StorageGroupRow>[] = [
    { key: 'group', label: 'Group', sub: r => GROUP_NOTE[r.group] ?? null },
    { key: 'tables', label: 'Tables', type: 'num', dp: 0 },
    { key: 'rows', label: 'Rows', type: 'num', dp: 0 },
    { key: 'bytes', label: 'Storage', value: r => formatBytes(r.bytes) },
    { key: 'share', label: 'Share', type: 'bar' },
  ];

  private rowsOf(p: StoragePeriod, ...entities: string[]): number {
    return (p.entities ?? []).filter(e => entities.includes(e.entity)).reduce((s, e) => s + e.rows, 0);
  }
  /** Period columns also render the totals row, whose object has the column key instead of entities. */
  private count(key: string, ...entities: string[]): (r: StoragePeriod) => number {
    return r => r.entities ? this.rowsOf(r, ...entities) : Number((r as unknown as Record<string, unknown>)[key] ?? 0);
  }
  periodCols = computed<Col<StoragePeriod>[]>(() => [
    { key: 'code', label: 'Period', link: r => `/periods/${r.id}`, sub: r => `${r.startDate} – ${r.endDate}`, minWidth: '12rem' },
    { key: 'run', label: 'Calculation run', value: r => r.run ?? 'Not run', link: r => r.run ? `/runs/${r.run}` : null, sub: r => r.runStatus ?? null },
    { key: 'enc', label: 'Encounters', type: 'num', dp: 0, value: this.count('enc', 'Encounter') },
    { key: 'svc', label: 'Services', type: 'num', dp: 0, value: this.count('svc', 'EncounterService') },
    { key: 'act', label: 'Activity', type: 'num', dp: 0, value: this.count('act', 'ActivityTransaction') },
    { key: 'rev', label: 'Revenue', type: 'num', dp: 0, value: this.count('rev', 'RevenueTransaction') },
    { key: 'cons', label: 'Consumption', type: 'num', dp: 0, value: this.count('cons', 'ConsumptionItem') },
    { key: 'staff', label: 'Staff time', type: 'num', dp: 0, value: this.count('staff', 'StaffTime') },
    { key: 'other', label: 'Other', type: 'num', dp: 0, value: this.count('other', 'Patient', 'RefundTransaction', 'InvoicePackageItem', 'GLPosting', 'DriverVolume') },
    { key: 'factRows', label: 'Fact records', type: 'num', dp: 0 },
    { key: 'factBytes', label: 'Fact storage', value: r => r.factRows ? formatBytes(r.factBytes) : '—' },
    { key: 'resultRows', label: 'Result records', type: 'num', dp: 0 },
    { key: 'resultBytes', label: 'Result storage', value: r => r.resultRows ? formatBytes(r.resultBytes) : '—' },
  ]);
  periodTotal = computed(() => {
    const ps = this.res.value()?.periods ?? [];
    if (!ps.length) return null;
    const sum = (f: (p: StoragePeriod) => number) => ps.reduce((s, p) => s + f(p), 0);
    return {
      enc: sum(p => this.rowsOf(p, 'Encounter')), svc: sum(p => this.rowsOf(p, 'EncounterService')), act: sum(p => this.rowsOf(p, 'ActivityTransaction')),
      rev: sum(p => this.rowsOf(p, 'RevenueTransaction')), cons: sum(p => this.rowsOf(p, 'ConsumptionItem')), staff: sum(p => this.rowsOf(p, 'StaffTime')),
      other: sum(p => this.rowsOf(p, 'Patient', 'RefundTransaction', 'InvoicePackageItem', 'GLPosting', 'DriverVolume')),
      factRows: sum(p => p.factRows), factBytes: formatBytes(sum(p => p.factBytes)), resultRows: sum(p => p.resultRows), resultBytes: formatBytes(sum(p => p.resultBytes)),
    };
  });
}
