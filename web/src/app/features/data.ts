import { HttpClient } from '@angular/common/http';
import { Component, computed, effect, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { firstValueFrom } from 'rxjs';
import type { components } from '../core/api-types';
import { Api } from '../core/api';
import { Auth, toApiError } from '../core/auth';
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
    <v-page title="Data entry" purpose="Add and edit the costing model, table by table. References are picked by code; rates and other effective-dated rows change by adding a new version.">
      <div class="layout">
        <nav class="tables" aria-label="Tables">
          @for (g of groups(); track g.group) {
            <div class="group">{{ g.group }}</div>
            @for (e of g.items; track e.name) {
              <button type="button" [class.active]="selected()?.name === e.name" (click)="select(e)">{{ e.name }}</button>
            }
          }
        </nav>
        <section class="content">
          @if (selected(); as e) {
            <div class="row head">
              <div>
                <h2>{{ e.name }} @if (e.versioned) { <v-chip value="VERSIONED" label="Effective-dated" kind="info" /> }</h2>
                <p class="muted">{{ e.description }}</p>
              </div>
              <span class="spacer"></span>
              @if (canWrite(e)) { <button class="btn primary" type="button" (click)="startNew()">Add {{ e.name }}</button> }
            </div>

            @if (editing()) {
              <form class="card form" (ngSubmit)="save()">
                <h3>{{ mode() === 'new' ? 'New ' + e.name : mode() === 'version' ? 'New version' : 'Edit ' + e.name }}</h3>
                @if (mode() === 'version') { <p class="notice">The current version is kept and closed the day before the new effectiveFrom.</p> }
                <div class="fields">
                  @for (c of formColumns(); track c.name) {
                    <label class="field">
                      <span>{{ c.name }} @if (c.required) { <span class="bad" aria-hidden="true">*</span> }</span>
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
                    @for (c of rows.value()?.columns ?? []; track c) { <th scope="col">{{ c }}</th> }
                  </tr></thead>
                  <tbody>
                    @for (r of rows.value()?.rows ?? []; track $index) {
                      <tr>
                        @if (canWrite(e)) {
                          <td><button class="btn" type="button" (click)="startEdit(r)">{{ e.versioned ? 'New version' : 'Edit' }}</button></td>
                        }
                        @for (c of rows.value()?.columns ?? []; track c) { <td>{{ r[c] }}</td> }
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
    @media (max-width: 800px) { .layout { grid-template-columns: 1fr; } .tables { position: static; max-height: none; } }
  `,
})
export class DataEntry {
  private api = inject(Api);
  private auth = inject(Auth);
  entities = apiResource<EntityDescription[]>(() => 'data/entities');
  groups = computed(() => groupsOf(this.entities.value() ?? []));
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
  formColumns = computed(() => this.selected()?.columns ?? []);

  constructor() {
    effect(() => {
      const list = this.entities.value();
      if (list?.length && !this.selected()) this.selected.set(list[0]);
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
      this.message.set(res.created ? `${e.name} added.` : res.updated ? `${e.name} updated.` : 'Nothing changed.');
      this.editing.set(false);
      this.rows.reload();
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

/**
 * Template download and raw-data upload (BRD 22.2). Validate runs the real load inside a
 * transaction and rolls it back, so a zip of templates is checked in load order exactly as it
 * would load. Bad rows come back one by one with the reason; the good rows load.
 */
@Component({
  selector: 'v-data-import',
  imports: [FormsModule, ...UI],
  template: `
    <v-page title="Data import" purpose="Download the templates, fill them, and upload one file or a zip of them. Validate first: it checks every row exactly as the import would, and saves nothing.">
      <section class="card">
        <h2>1. Download templates</h2>
        <p class="muted">One CSV per table, numbered in load order. Keep the header; the second row (starting with #) explains each column and is skipped on upload. Save as CSV UTF-8, dates as yyyy-mm-dd, numbers with a dot.</p>
        <div class="row">
          <button class="btn primary" type="button" (click)="zip()">Download all templates (zip)</button>
          <select [(ngModel)]="templateFor" name="templateFor" aria-label="Table">
            @for (e of entities.value() ?? []; track e.name) { <option [value]="e.name">{{ e.group }} · {{ e.name }}</option> }
          </select>
          <button class="btn" type="button" (click)="template()">Download template</button>
        </div>
      </section>

      <section class="card">
        <h2>2. Upload</h2>
        <div class="row">
          <input type="file" accept=".csv,.zip" (change)="pick($event)" aria-label="File to upload" />
          <select [(ngModel)]="entity" name="entity" aria-label="Table for a CSV">
            <option value="">Detect from file name / zip</option>
            @for (e of entities.value() ?? []; track e.name) { <option [value]="e.name">{{ e.name }}</option> }
          </select>
          <button class="btn" type="button" (click)="upload(true)" [disabled]="!file() || busy()">Validate</button>
          <button class="btn primary" type="button" (click)="upload(false)" [disabled]="!file() || busy()">Import</button>
          @if (busy()) { <span class="muted">Working…</span> }
        </div>
        @if (error()) { <div class="notice bad" role="alert" style="margin-top:10px">{{ error() }}</div> }
        @if (results().length) {
          <p class="notice" [class.warn]="totalRejected() > 0" style="margin-top:12px">
            {{ dryRunShown() ? 'Validation only — nothing was saved.' : 'Imported.' }}
            {{ totalCreated() }} new, {{ totalUpdated() }} updated, {{ totalRejected() }} rejected.
            @if (dryRunShown() && totalRejected() === 0) { Ready to import. }
          </p>
          <v-table [rows]="results()" [columns]="resultCols" caption="Import results" />
          @for (r of results(); track r.entity) {
            @if (r.rejects.length) {
              <h3 style="margin-top:14px">{{ r.entity }} — rejected rows</h3>
              <v-table [rows]="r.rejects" [columns]="rejectCols" />
            }
          }
        }
      </section>

      <section class="card">
        <h2>Import history</h2>
        <v-table [rows]="history.value() ?? []" [columns]="historyCols" empty="Nothing imported yet." />
        @if (rejectBatches().length) {
          <p class="muted" style="margin-top:8px">Rejected rows, ready to fix and re-upload:</p>
          @for (b of rejectBatches(); track b.id) {
            <button class="btn" type="button" (click)="rejects(b)" style="margin:0 6px 6px 0">{{ b.entity }} · {{ b.loadedAt.slice(0, 16).replace('T', ' ') }}</button>
          }
        }
      </section>
    </v-page>
  `,
})
export class DataImport {
  private http = inject(HttpClient);
  entities = apiResource<EntityDescription[]>(() => 'data/entities');
  history = apiResource<ImportBatchDto[]>(() => 'data/imports');
  templateFor = 'Hospital';
  entity = '';
  file = signal<File | null>(null);
  busy = signal(false);
  error = signal<string | null>(null);
  results = signal<ImportResult[]>([]);
  dryRunShown = signal(false);
  totalCreated = computed(() => this.results().reduce((s, r) => s + r.created, 0));
  totalUpdated = computed(() => this.results().reduce((s, r) => s + r.updated, 0));
  totalRejected = computed(() => this.results().reduce((s, r) => s + r.rejected, 0));
  rejectBatches = computed(() => (this.history.value() ?? []).filter(b => b.hasRejects).slice(0, 12));

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
    this.error.set(null);
  }

  async upload(dryRun: boolean): Promise<void> {
    const f = this.file();
    if (!f) return;
    const body = new FormData();
    body.append('file', f);
    body.append('dryRun', String(dryRun));
    if (this.entity) body.append('entity', this.entity);
    this.busy.set(true);
    this.error.set(null);
    try {
      this.results.set(await firstValueFrom(this.http.post<ImportResult[]>('/api/v1/data/import', body)));
      this.dryRunShown.set(dryRun);
      if (!dryRun) this.history.reload();
    } catch (e) {
      this.error.set(toApiError(e).message);
      this.results.set([]);
    } finally {
      this.busy.set(false);
    }
  }

  zip() { return download(this.http, '/api/v1/data/templates.zip', 'verita-templates.zip'); }
  template() { return download(this.http, `/api/v1/data/${this.templateFor}/template`, `${this.templateFor}.csv`); }
  rejects(b: ImportBatchDto) { return download(this.http, `/api/v1/data/imports/${b.id}/rejects`, `${b.entity}-rejects.csv`); }
}
