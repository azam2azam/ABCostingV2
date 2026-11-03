import { HttpClient } from '@angular/common/http';
import { Component, computed, effect, inject, signal } from '@angular/core';
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
  pageSize = 100;
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

/**
 * Template download and raw-data upload (BRD 22.2). Validate runs the real load inside a
 * transaction and rolls it back, so a zip of templates is checked in load order exactly as it
 * would load. Bad rows come back one by one with the reason; the good rows load.
 */
@Component({
  selector: 'v-data-import',
  imports: [FormsModule, ...UI],
  template: `
    <v-page title="Data import" purpose="Use the HIS Extract Utility to extract data files from HIS, then upload them here to import. Confirm the SetupID/ProjectID mapping, then validate and proceed.">
      <section class="card">
        <h2>Upload</h2>
        <div class="row">
          <input type="file" accept=".csv,.zip" (change)="pick($event)" aria-label="File to upload" />
          <select [(ngModel)]="entity" name="entity" aria-label="Table for a CSV">
            <option value="">Detect from file name / zip</option>
            @for (e of visibleEntities(); track e.name) { <option [value]="e.name">{{ e.name }}</option> }
          </select>
          @if (busy()) { <span class="muted">Working…</span> }
        </div>
        @if (error()) { <div class="notice bad" role="alert" style="margin-top:10px">{{ error() }}</div> }
        @if (hisPreview(); as hp) {
          @if (!imported()) {
            <div class="his-preview">
              @if (hp.hisPack) {
                <p class="notice" style="margin-top:12px">
                  HIS pack {{ hp.fileName }}
                  · SetupID <strong>{{ hp.setupId }}</strong>
                  · ProjectID <strong>{{ hp.projectId }}</strong>
                  @if (hp.pack) { · {{ hp.pack }} }
                  @if (hp.from) { · {{ hp.from }} .. {{ hp.toInclusive }} }
                </p>
                <label class="field" style="margin-top:12px; max-width:420px">VERITA hospital
                  <select [ngModel]="hisHospital()" (ngModelChange)="hisHospital.set($event)" name="hisHospital" aria-label="VERITA hospital for this HIS pack">
                    <option value="">Select…</option>
                    @for (h of hp.hospitals; track h.code) {
                      <option [value]="h.code">{{ h.code }} · {{ h.name }}{{ h.match ? ' (HIS match)' : '' }}{{ h.hisSetupId ? ' · ' + h.hisSetupId + '/' + h.hisProjectId : '' }}</option>
                    }
                  </select>
                </label>
              } @else {
                <p class="muted" style="margin-top:12px">VERITA template {{ hp.fileName }}. Validate, then import.</p>
              }
              @for (n of hp.notices; track n) { <p class="notice" style="margin-top:8px">{{ n }}</p> }
              @for (w of hp.warnings; track w) { <p class="notice warn" style="margin-top:8px">{{ w }}</p> }
              @if (hp.tables.length) {
                <h3 style="margin-top:14px">Tables in this file</h3>
                <v-table [rows]="hp.tables" [columns]="previewTableCols" caption="Pack contents" />
              }
              <div class="row" style="margin-top:12px">
                <button class="btn" type="button" (click)="upload(true)" [disabled]="!canProceed() || busy()">Validate</button>
                <button class="btn primary" type="button" (click)="upload(false)" [disabled]="!canProceed() || busy()">Proceed</button>
              </div>
            </div>
          }
        }
        @if (results().length) {
          <p class="notice" style="margin-top:12px">
            {{ dryRunShown() ? 'Validation only — nothing was saved.' : 'Imported.' }}
            {{ fmt(totalCreated()) }} new, {{ fmt(totalUpdated()) }} updated.
            @if (dryRunShown() && totalRejected() === 0) { Ready to import. }
          </p>
          @if (totalRejected() > 0) {
            <div class="notice warn" role="status">
              <p>{{ rejectNote() }}</p>
              @for (s of mappingRejects(); track s.entity) {
                <p><strong>{{ s.entity }}</strong> — {{ fmt(s.rejected) }} {{ s.rejected === 1 ? 'row' : 'rows' }} not in VERITA yet. {{ s.reasons.join(' ') }} Add them in Data entry, then import this file again.</p>
              }
              @for (s of catalogueRejects(); track s.entity) {
                <p><strong>{{ s.entity }}</strong> — {{ fmt(s.rejected) }} {{ s.rejected === 1 ? 'row' : 'rows' }}. {{ s.reasons.join(' ') }} The HIS facts file has these codes; the catalogue extract did not. Re-extract catalogue, import it, then import this facts file again. Mapping will not create these.</p>
              }
              <button type="button" class="text-link" (click)="showRejects.set(!showRejects())">{{ showRejects() ? 'Hide list' : 'Show list' }}</button>
            </div>
          }
          <h3 style="margin-top:14px">{{ imported() ? 'Imported rows preview' : 'Validation results' }}</h3>
          <v-table [rows]="results()" [columns]="resultCols" [caption]="imported() ? 'Imported rows preview' : 'Validation results'" />
          @if (showRejects()) {
            @for (r of results(); track r.entity) {
              @if (shownRejects(r).length) {
                <h3 style="margin-top:14px">{{ r.entity }} — rows not imported</h3>
                @if (r.rejected > rejectPreviewLimit) {
                  <p class="muted">Showing {{ rejectPreviewLimit }} of {{ fmt(r.rejected) }}.</p>
                }
                <v-table [rows]="shownRejects(r)" [columns]="rejectCols" />
              }
            }
          }
        }
      </section>

      <section class="card">
        <details #hist (toggle)="historyToggle(hist)">
          <summary>
            <h2>Import history</h2>
          </summary>
          @if (historyOpen()) {
            <v-state [loading]="history.isLoading() && !history.value()" [error]="history.error()">
              <v-table [rows]="history.value() ?? []" [columns]="historyCols" empty="Nothing imported yet." />
              @if (rejectBatches().length) {
                <p class="muted" style="margin-top:8px">Rejected rows, ready to fix and re-upload:</p>
                @for (b of rejectBatches(); track b.id) {
                  <button class="btn" type="button" (click)="rejects(b)" style="margin:0 6px 6px 0">{{ b.entity }} · {{ b.loadedAt.slice(0, 16).replace('T', ' ') }}</button>
                }
              }
            </v-state>
          }
        </details>
      </section>
    </v-page>
  `,
  styles: `
    details > summary { cursor: pointer; list-style: none; display: flex; align-items: center; gap: 8px; }
    details > summary::-webkit-details-marker { display: none; }
    details > summary::before { content: '▸'; color: var(--text-3); font-size: 12px; line-height: 1; }
    details[open] > summary::before { content: '▾'; }
    details > summary h2 { margin: 0; }
    details[open] > summary { margin-bottom: 12px; }
    input[type="checkbox"]:disabled { opacity: .55; cursor: not-allowed; }
    .notice.warn p { margin: 0 0 6px; }
    button.text-link {
      all: unset; color: var(--accent); cursor: pointer; font-weight: 600;
      text-decoration: underline; text-underline-offset: 2px;
    }
    button.text-link:focus-visible { outline: 2px solid var(--focus); }
  `,
})
export class DataImport {
  private http = inject(HttpClient);
  private auth = inject(Auth);
  private ctx = inject(RunContext);
  entities = apiResource<EntityDescription[]>(() => 'data/entities');
  visibleEntities = computed(() => {
    const list = this.entities.value() ?? [];
    return this.ctx.hasDrgs() ? list : list.filter(e => e.name !== 'Drg');
  });
  historyOpen = signal(false);
  history = apiResource<ImportBatchDto[]>(() => this.historyOpen() ? 'data/imports' : null);
  entity = '';
  file = signal<File | null>(null);
  busy = signal(false);
  error = signal<string | null>(null);
  results = signal<ImportResult[]>([]);
  hisPreview = signal<ImportPreview | null>(null);
  hisHospital = signal('');
  dryRunShown = signal(false);
  showRejects = signal(false);
  canProceed = computed(() => {
    const f = this.file();
    const p = this.hisPreview();
    if (!f || !p) return false;
    return !p.hisPack || !!this.hisHospital();
  });
  imported = computed(() => this.results().length > 0 && !this.dryRunShown());
  totalCreated = computed(() => this.results().reduce((s, r) => s + r.created, 0));
  totalUpdated = computed(() => this.results().reduce((s, r) => s + r.updated, 0));
  totalRejected = computed(() => this.results().reduce((s, r) => s + r.rejected, 0));
  rejectSummaries = computed(() => this.results().filter(r => r.rejected > 0).map(r => {
    const reasons: string[] = [];
    for (const row of r.rejects) {
      for (const err of row.errors) {
        const reason = err.replace(/ '[^']*'/g, '').replace(/\s+/g, ' ').trim();
        if (reason && !reasons.includes(reason)) reasons.push(reason);
      }
    }
    const mapping = reasons.length > 0 && reasons.every(x => /^(activityCode|resourceCode) does not exist/i.test(x));
    return { entity: r.entity, rejected: r.rejected, reasons, mapping };
  }));
  mappingRejects = computed(() => this.rejectSummaries().filter(s => s.mapping));
  catalogueRejects = computed(() => this.rejectSummaries().filter(s => !s.mapping));
  mappingRejected = computed(() => this.mappingRejects().reduce((s, r) => s + r.rejected, 0));
  catalogueRejected = computed(() => this.catalogueRejects().reduce((s, r) => s + r.rejected, 0));
  rejectNote = computed(() => {
    const mapping = this.mappingRejected();
    const other = this.catalogueRejected();
    const saved = 'Rows already saved stay unchanged.';
    if (this.dryRunShown()) {
      if (mapping && !other) return `${this.rows(mapping)} would not be saved yet. HIS does not send activities and most resources — add them in Data entry, then import this file again.`;
      if (mapping) return `${this.rows(mapping)} would wait on activities and resources being added in Data entry. ${this.rows(other)} are on the HIS facts file but were left out of the catalogue extract.`;
      return `${this.rows(other)} would not be imported because the catalogue extract did not include their department, cost centre or patient. Re-extract catalogue and import it first.`;
    }
    if (mapping && !other) return `${this.rows(mapping)} ${mapping === 1 ? 'is' : 'are'} not in VERITA yet. HIS does not send activities and most resources. Add them in Data entry, then import this file again and those rows will be saved. ${saved}`;
    if (mapping) return `${this.rows(mapping)} ${mapping === 1 ? 'is' : 'are'} not in VERITA yet — add them in Data entry, then import this file again and they will be saved. ${this.rows(other)} ${other === 1 ? 'is' : 'are'} on the HIS facts file but ${other === 1 ? 'was' : 'were'} left out of the catalogue extract; re-extract catalogue, import it, then import this facts file again. Mapping will not create those. ${saved}`;
    return `${this.rows(other)} ${other === 1 ? 'was' : 'were'} not imported because the catalogue extract did not include their department, cost centre or patient. Re-run the HIS Extract Utility for catalogue, import that zip, then import this facts file again. ${saved}`;
  });
  readonly rejectPreviewLimit = 100;
  shownRejects(r: ImportResult): ImportResult['rejects'] {
    return r.rejects.slice(0, this.rejectPreviewLimit);
  }
  rejectBatches = computed(() => (this.history.value() ?? []).filter(b => b.hasRejects).slice(0, 12));
  hospitals = computed(() => this.ctx.context()?.hospitals ?? []);
  constructor() { void this.ctx.load(); }

  previewTableCols: Col<ImportPreviewTable>[] = [
    { key: 'entity', label: 'Table' }, { key: 'fileName', label: 'File' }, { key: 'rows', label: 'Rows', type: 'num', dp: 0 },
  ];
  resultCols: Col<ImportResult>[] = [
    { key: 'entity', label: 'Table' }, { key: 'rows', label: 'Rows', type: 'num', dp: 0 }, { key: 'created', label: 'New', type: 'num', dp: 0 },
    { key: 'updated', label: 'Updated', type: 'num', dp: 0 }, { key: 'unchanged', label: 'Unchanged', type: 'num', dp: 0 },
    { key: 'rejected', label: 'Rejected', type: 'num', dp: 0 },
  ];
  rejectCols: Col<ImportResult['rejects'][number]>[] = [
    { key: 'row', label: 'File row', type: 'num', dp: 0 }, { key: 'errors', label: 'Why', value: r => r.errors.join(' ') },
    { key: 'values', label: 'Values', value: r => Object.entries(r.values).filter(([, v]) => v).map(([k, v]) => `${k}=${v}`).join(', ') },
  ];
  historyCols: Col<ImportBatchDto>[] = [
    { key: 'loadedAt', label: 'When', value: r => r.loadedAt.slice(0, 16).replace('T', ' ') }, { key: 'entity', label: 'Table' },
    { key: 'sourceSystem', label: 'Source' }, { key: 'rowsAccepted', label: 'Accepted', type: 'num', dp: 0 },
    { key: 'rowsRejected', label: 'Rejected', type: 'num', dp: 0 }, { key: 'loadedBy', label: 'By' },
  ];

  pick(ev: Event): void {
    this.file.set((ev.target as HTMLInputElement).files?.[0] ?? null);
    this.results.set([]);
    this.showRejects.set(false);
    this.error.set(null);
    this.hisPreview.set(null);
    this.hisHospital.set('');
    void this.loadHisPreview();
  }

  async loadHisPreview(): Promise<void> {
    const f = this.file();
    if (!f) return;
    const body = new FormData();
    body.append('file', f);
    if (this.entity) body.append('entity', this.entity);
    this.busy.set(true);
    this.error.set(null);
    try {
      const p = await firstValueFrom(this.http.post<ImportPreview>(`${apiV1}/data/import/preview`, body));
      this.hisPreview.set(p);
      this.hisHospital.set(p.suggestedHospital ?? '');
    } catch (e) {
      this.error.set(toApiError(e).message);
      this.hisPreview.set(null);
    } finally {
      this.busy.set(false);
    }
  }

  async upload(dryRun: boolean): Promise<void> {
    const f = this.file();
    if (!f || !this.canProceed()) return;
    const body = new FormData();
    body.append('file', f);
    body.append('dryRun', String(dryRun));
    if (this.entity) body.append('entity', this.entity);
    if (this.hisHospital()) body.append('hospital', this.hisHospital());
    this.busy.set(true);
    this.error.set(null);
    this.showRejects.set(false);
    try {
      this.results.set(await firstValueFrom(this.http.post<ImportResult[]>(`${apiV1}/data/import`, body)));
      this.dryRunShown.set(dryRun);
      if (!dryRun) {
        if (this.historyOpen()) this.history.reload();
        void this.ctx.load(true);
      }
    } catch (e) {
      this.error.set(toApiError(e).message);
      this.results.set([]);
    } finally {
      this.busy.set(false);
    }
  }

  historyToggle(el: HTMLDetailsElement): void {
    if (el.open) this.historyOpen.set(true);
  }

  fmt(n: number): string { return formatDec(String(n), 0); }
  rows(n: number): string { return `${this.fmt(n)} ${n === 1 ? 'row' : 'rows'}`; }

  rejects(b: ImportBatchDto) { return download(this.http, `${apiV1}/data/imports/${b.id}/rejects`, `${b.entity}-rejects.csv`); }

}
