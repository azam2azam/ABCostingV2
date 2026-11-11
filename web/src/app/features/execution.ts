import { NgTemplateOutlet } from '@angular/common';
import { Component, Injector, afterNextRender, computed, effect, inject, input, linkedSignal, signal, untracked } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { FormsModule } from '@angular/forms';
import { ActivatedRoute, ParamMap, Params, Router, RouterLink } from '@angular/router';
import { firstValueFrom } from 'rxjs';
import { Api, CostingModelsDto, Envelope, RunDetail as RunDetailDto, RunListItem } from '../core/api';
import { Auth, toApiError } from '../core/auth';
import { RunContext, runInFlight, runLiveStatus } from '../core/context';
import { formatMoney } from '../core/decimal';
import { apiResource, runResource, catalogueResource } from '../core/resource';
import { Icon } from '../shared/icon';
import { Col, UI, matchesNameOrCode, hisProcedureId } from '../shared/ui';

// ── Configuration ───────────────────────────────────────────────────────────────

type Pct = string | number | null | undefined;
interface CompareTypeRow { code: string; name: string; procedures: number; fromPct?: Pct; toPct?: Pct; changed: number; }
interface CompareItem { id: string; code: string; name: string; listPrice: string | number; fromPct?: Pct; toPct?: Pct; }
interface CompareItemsPage { items: CompareItem[]; total: number; }
interface CompareOverheadRow {
  costCenterCode: string; costCenterName: string; description: string;
  fromPct?: Pct; toPct?: Pct; fromAmount?: Pct; toAmount?: Pct;
}
interface CompareTypeDraft extends CompareTypeRow { expanded: boolean; items: CompareItem[]; itemTotal: number; loadingItems: boolean; }

/**
 * Direct or Indirect costing for two versions side by side, laid out like the Direct and
 * Indirect costing screens: types first, each expandable to its procedures with the difference.
 */
@Component({
  selector: 'v-compare-costing',
  imports: [NgTemplateOutlet, FormsModule, RouterLink, Icon, ...UI],
  template: `
    @if (stream() === 'indirect') {
      <section class="card overhead-block">
        <div class="head">
          <h2>{{ title() }} · Encounter level overheads (Cost will be considered for every encounter)</h2>
        </div>
        @if (!ready()) {
          <ng-container [ngTemplateOutlet]="notReady" />
        } @else {
          <v-state [loading]="overheads.isLoading()" [error]="overheads.error()">
            <div class="table-wrap">
              <table class="data">
                <caption class="sr-only">Encounter level overheads, {{ from() }} to {{ to() }}</caption>
                <thead>
                  <tr>
                    <th scope="col">Cost Center Code</th>
                    <th scope="col">Description</th>
                    <th class="num" scope="col">Overhead %<span class="ver">{{ from() }}</span></th>
                    <th class="num" scope="col">Overhead %<span class="ver">{{ to() }}</span></th>
                    <th class="num" scope="col" title="If Overhead (%) is not given, it will be considered as Fixed.">Max Cap (Fixed)<span class="ver">{{ from() }}</span></th>
                    <th class="num" scope="col" title="If Overhead (%) is not given, it will be considered as Fixed.">Max Cap (Fixed)<span class="ver">{{ to() }}</span></th>
                  </tr>
                </thead>
                <tbody>
                  @for (o of overheadRows(); track o.costCenterCode + o.description) {
                    <tr [class.changed]="highlight() && (pctText(o.fromPct) !== pctText(o.toPct) || pctText(o.fromAmount) !== pctText(o.toAmount))">
                      <td>{{ local(o.costCenterCode) }}</td>
                      <td>{{ o.description || '—' }}</td>
                      <td class="num">{{ fmtPct(o.fromPct) }}</td>
                      <td class="num">{{ fmtPct(o.toPct) }}</td>
                      <td class="num">{{ o.fromAmount | money }}</td>
                      <td class="num">{{ o.toAmount | money }}</td>
                    </tr>
                  } @empty {
                    <tr><td class="faint" colspan="6">No encounter-level overheads.</td></tr>
                  }
                </tbody>
              </table>
            </div>
          </v-state>
        }
      </section>
    }
    <section class="card overhead-block">
      <h2>{{ stream() === 'indirect' ? title() + ' · Procedure/activity overheads' : title() }}</h2>
      @if (!ready()) {
        <ng-container [ngTemplateOutlet]="notReady" />
      } @else {
        @if (message()) { <p class="notice bad">{{ message() }}</p> }
        @if (hint()) { <p class="muted margin-hint">{{ hint() }}</p> }
        <div class="tools">
          <input type="search" class="item-search" placeholder="Search procedures by name or code"
            [ngModel]="search()" (ngModelChange)="setSearch($event)" [attr.aria-label]="'Search procedures in ' + title()" />
        </div>
        @if (types.isLoading() && rows().length) { <p class="faint search-status">Searching…</p> }
        @else if ((query() || changedOnly()) && visibleRows().length <= 1) { <p class="faint search-status">No procedures match this search.</p> }
        <v-state [loading]="types.isLoading() && !rows().length" [error]="types.error()">
          <div class="table-wrap">
            <table class="data">
              <caption class="sr-only">{{ title() }} by procedure/activity type, {{ from() }} to {{ to() }}</caption>
              <thead>
                <tr>
                  <th class="exp" scope="col"></th>
                  <th scope="col">Procedure/Activity type</th>
                  <th class="num" scope="col">Procedure/Activity</th>
                  <th class="num" scope="col">Changed</th>
                  <th scope="col">{{ pctLabel() }}<span class="ver">{{ from() }}</span></th>
                  <th scope="col">{{ pctLabel() }}<span class="ver">{{ to() }}</span></th>
                </tr>
              </thead>
              <tbody>
                @for (row of visibleRows(); track row.code) {
                  <tr [class.changed]="highlight() && row.changed > 0">
                    <td class="exp">
                      @if (row.code !== 'ALL') {
                        <button class="btn icon-only" type="button"
                          [attr.aria-expanded]="row.expanded" [attr.aria-label]="'Expand ' + row.name"
                          (click)="toggle(row.code)">
                          <v-icon name="chevron" />
                        </button>
                      }
                    </td>
                    <td>{{ row.name }}</td>
                    <td class="num">{{ row.procedures }}</td>
                    <td class="num">{{ row.changed }}</td>
                    <td><span class="cell-value">{{ pctText(row.fromPct) }}</span></td>
                    <td><span class="cell-value">{{ pctText(row.toPct) }}</span></td>
                  </tr>
                  @if (row.expanded) {
                    <tr class="expand-row">
                      <td colspan="6">
                        <div class="expand-pane">
                          <table class="data nested">
                            <thead>
                              <tr>
                                <th scope="col">Id</th>
                                <th scope="col">Name</th>
                                <th class="num" scope="col">Contracted price</th>
                                <th scope="col">{{ pctLabel() }}<span class="ver">{{ from() }}</span></th>
                                <th scope="col">{{ pctLabel() }}<span class="ver">{{ to() }}</span></th>
                                <th class="num" scope="col">{{ amountLabel() }}<span class="ver">{{ from() }}</span></th>
                                <th class="num" scope="col">{{ amountLabel() }}<span class="ver">{{ to() }}</span></th>
                                <th class="num" scope="col">Difference</th>
                              </tr>
                            </thead>
                            <tbody>
                              @for (item of row.items; track item.id) {
                                <tr [class.changed]="highlight() && pctText(item.fromPct) !== pctText(item.toPct)">
                                  <td><a [routerLink]="['/procedures', item.id]" queryParamsHandling="preserve">{{ local(item.code) }}</a></td>
                                  <td>{{ item.name }}</td>
                                  <td class="num">{{ item.listPrice | money }}</td>
                                  <td><span class="cell-value" [class.faint]="!pctText(item.fromPct)">{{ pctText(item.fromPct) || '—' }}</span></td>
                                  <td><span class="cell-value" [class.faint]="!pctText(item.toPct)">{{ pctText(item.toPct) || '—' }}</span></td>
                                  <td class="num"><span class="cell-value">{{ amountText(item.listPrice, item.fromPct) }}</span></td>
                                  <td class="num"><span class="cell-value">{{ amountText(item.listPrice, item.toPct) }}</span></td>
                                  <td class="num">{{ diffText(diff(item)) }}</td>
                                </tr>
                              } @empty {
                                <tr><td class="faint" colspan="8">{{ search().trim() || changedOnly() ? 'No procedures match this search.' : 'No procedures in this type.' }}</td></tr>
                              }
                            </tbody>
                          </table>
                          @if (row.loadingItems) { <p class="faint more">Loading…</p> }
                          @else if (row.itemTotal > row.items.length) {
                            <p class="faint more">Showing {{ row.items.length }} of {{ row.itemTotal }}.
                              <button class="btn" type="button" (click)="loadItems(row.code, false)">Show 100 more</button>
                            </p>
                          }
                        </div>
                      </td>
                    </tr>
                  }
                } @empty {
                  <tr><td class="faint" colspan="6">{{ search().trim() ? 'No procedures match this search.' : 'No procedures in this hospital.' }}</td></tr>
                }
              </tbody>
            </table>
          </div>
        </v-state>
      }
    </section>
    <ng-template #notReady>
      <p class="faint">{{ !from() || !to() ? 'No version pair to compare.' : 'Choose a hospital and period to compare.' }}</p>
    </ng-template>
  `,
  styles: `
    .sr-only { position: absolute; width: 1px; height: 1px; overflow: hidden; clip: rect(0 0 0 0); }
    .cell-value {
      display: inline-block; width: 8rem; box-sizing: border-box; text-align: right; cursor: default;
      font: 14px var(--font); padding: 6px 8px;
      border: 1px solid var(--border); border-radius: var(--radius-sm);
      background: var(--surface); color: var(--text);
    }
    .notice { margin: 0 0 12px; }
    .margin-hint { margin: 0 0 12px; max-width: 72ch; }
    td.exp, th.exp { width: 40px; padding-right: 0; }
    button.icon-only { padding: 4px; width: 28px; height: 28px; justify-content: center; border-radius: 8px; }
    button.icon-only v-icon { transition: transform .15s; }
    button.icon-only[aria-expanded] v-icon { transform: rotate(-90deg); }
    button.icon-only[aria-expanded="true"] v-icon { transform: rotate(0); }
    tr.expand-row td { background: var(--surface-2); padding: 0; }
    .expand-pane { padding: 8px 12px 14px 40px; }
    table.nested { background: var(--surface-solid); }
    .more { margin-top: 8px; }
    .tools { display: flex; gap: 14px; align-items: center; flex-wrap: wrap; margin: 0 0 10px; }
    .item-search {
      width: min(28rem, 100%); font: 14px var(--font); padding: 6px 8px;
      border: 1px solid var(--border); border-radius: var(--radius-sm);
      background: var(--surface); color: var(--text);
    }
    tr.changed > td { background: var(--warn-soft); }
    .search-status { margin: 0 0 10px; }
    .ver { display: block; }
    .overhead-block { margin-top: 14px; position: relative; z-index: 0; }
    .overhead-block h2 { margin: 0 0 12px; }
    .head { display: flex; gap: 12px; align-items: flex-end; justify-content: space-between; flex-wrap: wrap; margin-bottom: 12px; }
    .head h2 { margin: 0; max-width: 72ch; }
    .overhead-block > .faint { margin: 0; }
  `,
})
export class CompareCosting {
  stream = input.required<'direct' | 'indirect'>();
  title = input.required<string>();
  from = input.required<string>();
  to = input.required<string>();
  pctLabel = input('Margin Cost (%)');
  amountLabel = input('Evaluated cost');
  ctx = inject(RunContext);
  private api = inject(Api);
  search = signal('');
  query = signal('');
  changedOnly = input(false);
  highlight = input(false);
  rows = signal<CompareTypeDraft[]>([]);
  message = signal<string | null>(null);
  private searchTimer: ReturnType<typeof setTimeout> | null = null;
  local = hisProcedureId;
  fmtPct = formatOverheadPct;
  types = apiResource<Envelope<CompareTypeRow[]>>(
    () => this.ready() ? `costing-models/compare/${this.stream()}` : null,
    () => ({ hospital: this.ctx.hospitalParam(), from: this.from(), to: this.to(), search: this.query() }));
  overheads = apiResource<Envelope<CompareOverheadRow[]>>(
    () => this.stream() === 'indirect' && this.ready() ? 'costing-models/compare/encounter-overheads' : null,
    () => ({ hospital: this.ctx.hospitalParam(), from: this.from(), to: this.to() }));
  overheadRows = computed(() => this.overheads.value()?.data ?? []);
  visibleRows = computed(() => {
    const filtered = !!this.query() || this.changedOnly();
    return this.rows().filter(r => !filtered || r.code === 'ALL' || (this.changedOnly() ? r.changed > 0 : r.procedures > 0));
  });
  ready = computed(() => !!this.from() && !!this.to() && !!this.ctx.hospitalParam());
  /** Same hint as the Direct costing screen; Indirect shows none. */
  hint = computed(() => this.stream() === 'direct' ? 'If Margin Cost (%) is 0 or blank, it will follow recipe costing.' : '');

  constructor() {
    effect(() => {
      this.changedOnly();
      untracked(() => {
        for (const r of this.rows()) if (r.expanded) void this.loadItems(r.code, true);
      });
    });
    effect(() => {
      const data = this.types.value()?.data;
      if (!data) return;
      untracked(() => {
        const prev = new Map(this.rows().map(r => [r.code, r]));
        this.rows.set(data.map(x => {
          const old = prev.get(x.code);
          return { ...x, expanded: !!old?.expanded, items: [], itemTotal: 0, loadingItems: false };
        }));
        for (const r of this.rows()) if (r.expanded) void this.loadItems(r.code, true);
      });
    });
  }

  setSearch(value: string): void {
    this.search.set(value);
    if (this.searchTimer) clearTimeout(this.searchTimer);
    this.searchTimer = setTimeout(() => this.query.set(value.trim()), 250);
  }
  toggle(code: string): void {
    const row = this.rows().find(r => r.code === code);
    if (!row) return;
    const opening = !row.expanded;
    this.rows.update(rows => rows.map(r => r.code === code ? { ...r, expanded: opening } : r));
    if (opening && row.items.length === 0) void this.loadItems(code, true);
  }
  async loadItems(code: string, reset: boolean): Promise<void> {
    const row = this.rows().find(r => r.code === code);
    if (!row || (row.loadingItems && !reset)) return;
    this.rows.update(rows => rows.map(r => r.code === code ? { ...r, loadingItems: true } : r));
    try {
      const page = await firstValueFrom(this.api.run<CompareItemsPage>(
        `costing-models/compare/${this.stream()}/${encodeURIComponent(code)}`, { hospital: this.ctx.hospitalParam() },
        { from: this.from(), to: this.to(), skip: reset ? 0 : row.items.length, take: 100, search: this.query() || undefined, changedOnly: this.changedOnly() || undefined }));
      this.rows.update(rows => rows.map(r => {
        if (r.code !== code) return r;
        const items = reset ? page.data.items : [...r.items, ...page.data.items.filter(n => !r.items.some(o => o.id === n.id))];
        return { ...r, items, itemTotal: page.data.total, loadingItems: false };
      }));
    } catch (err) {
      this.rows.update(rows => rows.map(r => r.code === code ? { ...r, loadingItems: false } : r));
      this.message.set(toApiError(err).message);
    }
  }

  pctText(v: Pct): string { return marginText(v); }
  /** Contracted price × % / 100; null when there is no % (Direct follows recipe; Indirect is 0). */
  amount(price: string | number, pct: Pct): number | null {
    const m = Number(pct);
    if (pct == null || pct === '' || !Number.isFinite(m) || m === 0) return this.stream() === 'indirect' ? 0 : null;
    const p = Number(price);
    return Number.isFinite(p) ? Math.round(p * (m / 100) * 1e4) / 1e4 : null;
  }
  amountText(price: string | number, pct: Pct): string {
    const a = this.amount(price, pct);
    return a === null ? '—' : formatMoney(String(a), { dp: 2 });
  }
  diff(item: CompareItem): number | null {
    const a = this.amount(item.listPrice, item.fromPct);
    const b = this.amount(item.listPrice, item.toPct);
    if (a === null || b === null) return null;
    return Math.round((b - a) * 1e4) / 1e4;
  }
  diffText(d: number | null): string {
    if (d === null) return '—';
    if (d === 0) return '0.00';
    return (d > 0 ? '+' : '') + formatMoney(String(d), { dp: 2 });
  }
}

@Component({
  selector: 'v-costing-models',
  imports: [FormsModule, CompareCosting, ...UI],
  template: `
    <v-page title="Costing models" purpose="Versioned costing definitions. A published version is never edited; a change is a new version that copies recipes and Direct/Indirect costing. Direct and Indirect diffs below are computed, not described (FR-10.6).">
      <button actions class="btn primary" type="button" (click)="openForm()" [disabled]="busy() || !!form()">New version</button>
      @if (message()) { <p class="notice" [class.bad]="!!error()">{{ message() }}</p> }
      @if (form(); as f) {
        <form class="card form" (ngSubmit)="create()">
          <h2>New costing version</h2>
          @if (!versions().length) {
            <p class="muted">This is the hospital's first version, so it starts blank. Enter its percentages on Direct Costing and Indirect Costing.</p>
          } @else {
          <p class="muted">Copies recipes, Direct costing, Indirect costing and encounter overheads from the source version. The new version starts as Draft and can be edited.</p>
          }
          <div class="fields">
            @if (versions().length) {
            <label class="field">Source version
              <select name="fromVersionId" [ngModel]="f.fromVersionId" (ngModelChange)="patch({ fromVersionId: $event })" required>
                @for (v of versions(); track v.id) {
                  <option [value]="v.id">{{ v.code }} · {{ v.status }}</option>
                }
              </select>
            </label>
            }
            <label class="field">Code
              <input name="code" [ngModel]="f.code" (ngModelChange)="patch({ code: $event })" required />
            </label>
            <label class="field">Name
              <input name="name" [ngModel]="f.name" (ngModelChange)="patch({ name: $event })" />
            </label>
            <label class="field">Effective from
              <input type="date" name="effectiveFrom" [ngModel]="f.effectiveFrom" (ngModelChange)="patch({ effectiveFrom: $event })" required />
            </label>
            <label class="field">Effective to
              <input type="date" name="effectiveTo" [ngModel]="f.effectiveTo" (ngModelChange)="patch({ effectiveTo: $event })" />
            </label>
            <label class="field wide">Description
              <input name="description" [ngModel]="f.description" (ngModelChange)="patch({ description: $event })" />
            </label>
          </div>
          <div class="row">
            <button class="btn primary" type="submit" [disabled]="busy()">{{ busy() ? 'Creating…' : 'Create version' }}</button>
            <button class="btn" type="button" (click)="cancelForm()" [disabled]="busy()">Cancel</button>
          </div>
        </form>
      }
      @if (decision(); as d) {
        <form class="card form" (ngSubmit)="confirmDecision()">
          <h2>{{ decisionTitle() }}</h2>
          <p class="muted">{{ d.version.code }} · {{ d.version.effectiveFrom }} to {{ d.version.effectiveTo ?? 'open' }}</p>
          <div class="fields">
            <label class="field wide">{{ d.kind === 'request' ? 'What changed and why' : 'Note (optional)' }}
              <input name="decisionNote" [ngModel]="decisionNote()" (ngModelChange)="decisionNote.set($event)" [required]="d.kind === 'request'" />
            </label>
          </div>
          <div class="row">
            <button class="btn primary" type="submit" [disabled]="busy()">{{ busy() ? 'Saving…' : decisionTitle() }}</button>
            <button class="btn" type="button" (click)="closeDecision()" [disabled]="busy()">Cancel</button>
          </div>
        </form>
      }
      <v-state [loading]="res.isLoading()" [error]="res.error()">
        @if (res.value(); as r) {
          <v-table [rows]="r.versions" [columns]="cols" caption="Costing versions" />
          <section class="card compare-pick">
            <h2>Compare versions</h2>
            @if (fromOptions().length && fromCode()) {
              <div class="compare-fields">
                <label class="field">From
                  <select name="compareFrom" [ngModel]="fromCode()" (ngModelChange)="pickFrom($event)" aria-label="From costing version">
                    @for (v of fromOptions(); track v.id) {
                      <option [value]="v.code">{{ v.code }} · {{ v.status }}</option>
                    }
                  </select>
                </label>
                <label class="field">To
                  <select name="compareTo" [ngModel]="toCode()" (ngModelChange)="pickTo($event)" aria-label="To costing version">
                    @for (v of toOptions(); track v.id) {
                      <option [value]="v.code">{{ v.code }} · {{ v.status }}</option>
                    }
                  </select>
                </label>
              </div>
            } @else {
              <p class="faint">Need at least two costing versions to compare.</p>
            }
            <div class="compare-options">
              <label class="check"><input type="checkbox" name="showChangedOnly" [ngModel]="showChangedOnly()" (ngModelChange)="showChangedOnly.set($event)" /> Show Changed only</label>
              <label class="check"><input type="checkbox" name="highlightChanged" [ngModel]="highlightChanged()" (ngModelChange)="highlightChanged.set($event)" /> Highlight Changed</label>
            </div>
          </section>
          <v-compare-costing stream="direct" title="Direct Costing" [from]="fromCode()" [to]="toCode()" [changedOnly]="showChangedOnly()" [highlight]="highlightChanged()" />
          <v-compare-costing stream="indirect" title="Indirect Costing" pctLabel="Overhead (%)" amountLabel="Overhead (SAR)"
            [from]="fromCode()" [to]="toCode()" [changedOnly]="showChangedOnly()" [highlight]="highlightChanged()" />
        }
      </v-state>
    </v-page>
  `,
  styles: `
    .form { margin-bottom: 16px; }
    .form h2 { margin: 0 0 8px; }
    .fields { display: grid; grid-template-columns: repeat(auto-fill, minmax(220px, 1fr)); gap: 10px; margin: 10px 0; }
    .field { display: flex; flex-direction: column; gap: 5px; font-size: 12px; font-weight: 600; letter-spacing: .04em; text-transform: uppercase; color: var(--text-3); }
    .field.wide { grid-column: 1 / -1; }
    .field input, .field select { font: 14px var(--font); text-transform: none; letter-spacing: normal; font-weight: 400; color: var(--text); padding: 6px 8px; border: 1px solid var(--border); border-radius: var(--radius-sm); background: var(--surface); }
    .row { display: flex; gap: 8px; }
    .notice { margin: 0 0 12px; }
    .compare-options { display: flex; gap: 18px; flex-wrap: wrap; margin-top: 14px; }
    .check { display: inline-flex; gap: 6px; align-items: center; font-size: 14px; }
    .compare-pick { margin-top: 14px; }
    .compare-pick h2 { margin: 0 0 12px; }
    .compare-pick .faint { margin: 0; }
    .compare-fields { display: grid; grid-template-columns: repeat(auto-fill, minmax(220px, 1fr)); gap: 10px; }
  `,
})
export class CostingModels {
  private api = inject(Api);
  private ctx = inject(RunContext);
  private auth = inject(Auth);
  showChangedOnly = signal(false);
  highlightChanged = signal(false);
  res = apiResource<CostingModelsDto>(() => 'costing-models', () => ({ hospital: this.ctx.hospitalParam() }));
  versions = computed(() => this.res.value()?.versions ?? []);
  fromCode = signal('');
  private toPick = signal('');
  fromOptions = computed(() => this.versions().slice(0, -1));
  /** Any later version; defaults to the one right after From. */
  toOptions = computed(() => {
    const all = this.versions();
    const i = all.findIndex(v => v.code === this.fromCode());
    return i >= 0 ? all.slice(i + 1) : [];
  });
  toCode = computed(() => {
    const opts = this.toOptions();
    return opts.find(v => v.code === this.toPick())?.code ?? opts[0]?.code ?? '';
  });
  private seedCompare = effect(() => {
    const opts = this.fromOptions();
    if (!opts.length) return;
    if (opts.some(v => v.code === this.fromCode())) return;
    this.fromCode.set(opts.at(-1)!.code);
  });
  pickFrom(code: string): void {
    if (!code || code === this.fromCode()) return;
    this.fromCode.set(code);
  }
  pickTo(code: string): void { this.toPick.set(code); }
  form = signal<NewVersionForm | null>(null);
  busy = signal(false);
  error = signal(false);
  message = signal<string | null>(null);
  cols: Col<CostingModelsDto['versions'][number]>[] = [
    { key: 'name', label: 'Version', sub: r => r.description }, { key: 'code', label: 'Code' },
    { key: 'effectiveFrom', label: 'From' }, { key: 'effectiveTo', label: 'To', value: r => r.effectiveTo ?? 'open' },
    { key: 'status', label: 'Status', type: 'chip' }, { key: 'definitions', label: 'Definitions', type: 'num', dp: 0 }, { key: 'runs', label: 'Runs', type: 'num', dp: 0 },
    { key: 'approval', label: 'Approval', sortable: false, value: r => this.approvalText(r), sub: r => this.approvalDetail(r) },
    { key: 'decide', label: '', type: 'button', sortable: false, button: r => this.decideLabel(r), onButton: r => this.openDecision(r, this.openRequest(r) ? 'approve' : 'request') },
    { key: 'reject', label: '', type: 'button', sortable: false, button: r => this.canDecide(r) ? 'Reject' : null, onButton: r => this.openDecision(r, 'reject') },
  ];
  decision = signal<VersionDecision | null>(null);
  decisionNote = signal('');
  decisionTitle = computed(() => ({ request: 'Request approval', approve: 'Approve', reject: 'Reject' })[this.decision()?.kind ?? 'request']);
  private approvalsOf(r: VersionRow) { return (this.res.value()?.approvals ?? []).filter(a => a.entityId === r.id); }
  private latestRequest(r: VersionRow) {
    return [...this.approvalsOf(r)].sort((a, b) => b.requestedAt.localeCompare(a.requestedAt))[0] ?? null;
  }
  private openRequest(r: VersionRow) {
    const latest = this.latestRequest(r);
    return latest && !['PUBLISHED', 'REJECTED', 'RETIRED'].includes(latest.stage) ? latest : null;
  }
  /** The API refuses the same cases; this only decides which buttons to show. */
  private canDecide(r: VersionRow): boolean {
    const open = this.openRequest(r);
    if (!open || open.requestedBy === this.auth.user()?.email) return false;
    return this.auth.can(open.stage === 'CLINICAL_APPROVAL' ? 'approval.clinical' : 'approval.finance');
  }
  private decideLabel(r: VersionRow): string | null {
    if (this.canDecide(r)) return 'Approve';
    return r.status === 'DRAFT' && !this.openRequest(r) && this.auth.can('approval.request') ? 'Request approval' : null;
  }
  private approvalText(r: VersionRow): string {
    const stage = this.latestRequest(r)?.stage;
    if (stage === 'FINANCE_APPROVAL') return 'Waiting on Finance';
    if (stage === 'CLINICAL_APPROVAL') return 'Waiting on Clinical';
    if (stage === 'REJECTED') return 'Rejected';
    return stage === 'PUBLISHED' ? 'Approved' : '—';
  }
  private approvalDetail(r: VersionRow): string | null {
    const latest = this.latestRequest(r);
    if (!latest) return null;
    if (latest.stage === 'REJECTED') return [`By ${latest.reviewedBy}`, latest.reviewNote].filter(Boolean).join(': ');
    return `Requested by ${latest.requestedBy}: ${latest.requestNote}`;
  }
  openDecision(version: VersionRow, kind: VersionDecision['kind']): void {
    this.form.set(null);
    this.decisionNote.set('');
    this.decision.set({ kind, version, requestId: this.openRequest(version)?.id ?? null });
    this.message.set(null);
    this.error.set(false);
  }
  closeDecision(): void { this.decision.set(null); }
  async confirmDecision(): Promise<void> {
    const d = this.decision();
    if (!d) return;
    const note = this.decisionNote().trim();
    if (d.kind === 'request' && !note) { this.error.set(true); this.message.set('Say what changed and why.'); return; }
    this.busy.set(true);
    this.message.set(null);
    this.error.set(false);
    try {
      const result = d.kind === 'request'
        ? await firstValueFrom(this.api.post<{ stage: string }>('approvals', { entityType: 'COSTING_VERSION', entityId: d.version.id, note }))
        : await firstValueFrom(this.api.post<{ stage: string }>(`approvals/${d.requestId}/decide`, { approve: d.kind === 'approve', note: note || null }));
      this.decision.set(null);
      this.res.reload();
      await this.ctx.load(true);
      this.message.set(result.stage === 'PUBLISHED' ? `${d.version.code} is published.`
        : result.stage === 'REJECTED' ? `${d.version.code} was rejected and is back in Draft.`
        : `${d.version.code} is waiting on ${result.stage === 'CLINICAL_APPROVAL' ? 'Clinical' : 'Finance'} approval.`);
    } catch (err) {
      this.error.set(true);
      this.message.set(toApiError(err).message);
    } finally {
      this.busy.set(false);
    }
  }
  openForm(): void {
    this.decision.set(null);
    const from = this.versions().at(-1);
    this.form.set({
      fromVersionId: from?.id ?? '', code: '', name: '', description: '',
      effectiveFrom: new Date().toISOString().slice(0, 10), effectiveTo: '',
    });
    this.message.set(null);
    this.error.set(false);
  }
  cancelForm(): void { this.form.set(null); this.message.set(null); this.error.set(false); }
  patch(p: Partial<NewVersionForm>): void {
    const cur = this.form();
    if (cur) this.form.set({ ...cur, ...p });
  }
  async create(): Promise<void> {
    const f = this.form();
    if (!f) return;
    if (this.versions().length && !f.fromVersionId) { this.error.set(true); this.message.set('Choose a source version.'); return; }
    if (!f.code.trim()) { this.error.set(true); this.message.set('Version code is required.'); return; }
    if (!f.effectiveFrom) { this.error.set(true); this.message.set('Effective from is required.'); return; }
    this.busy.set(true);
    this.message.set(null);
    this.error.set(false);
    try {
      await firstValueFrom(this.api.post<CostingModelsDto>('costing-models', {
        fromVersionId: f.fromVersionId || null, hospital: this.ctx.hospitalParam(), code: f.code.trim(), name: f.name.trim() || f.code.trim(),
        description: f.description.trim(), effectiveFrom: f.effectiveFrom, effectiveTo: f.effectiveTo || null,
      }));
      this.form.set(null);
      this.res.reload();
      this.message.set('Draft version created. Direct and Indirect costing now edit this draft until it is published.');
    } catch (err) {
      this.error.set(true);
      this.message.set(toApiError(err).message);
    } finally {
      this.busy.set(false);
    }
  }
}

type VersionRow = CostingModelsDto['versions'][number];
interface VersionDecision { kind: 'request' | 'approve' | 'reject'; version: VersionRow; requestId: string | null; }

interface NewVersionForm {
  fromVersionId: string; code: string; name: string; description: string; effectiveFrom: string; effectiveTo: string;
}

export const approvalCols: Col<any>[] = [
  { key: 'entityLabel', label: 'Item', sub: r => r.requestNote }, { key: 'entityType', label: 'Type' }, { key: 'stage', label: 'Stage', type: 'chip' },
  { key: 'requestedBy', label: 'Requested by' }, { key: 'reviewedBy', label: 'Reviewed by', sub: r => r.reviewNote },
  { key: 'requestedAt', label: 'Requested', type: 'date' },
];

interface ProcedureCostingItem {
  id: string; code: string; name: string; marginPct?: string | number | null;
  listPrice?: string | number | null; packageId?: string | null; compositionCost?: string | number | null;
}
interface ProcedureTypeItemsPage { items: ProcedureCostingItem[]; total: number; }
interface ProcedureTypeRow {
  code: string; name: string; procedures: number | string; marginPct?: string | number | null;
  items?: ProcedureCostingItem[];
}
interface ItemDraft { id: string; code: string; name: string; marginPct: string; savedMargin: string; listPrice: string | number; packageId?: string | null; compositionCost?: string | number | null; }
interface ProcedureTypeDraft {
  code: string; name: string; procedures: number | string; marginPct: string;
  items: ItemDraft[]; stash: ItemDraft[]; expanded: boolean; itemTotal: number; loadingItems: boolean;
}

@Component({
  selector: 'v-version-pick',
  imports: [FormsModule],
  template: `
    @if (res.value(); as r) {
      <label class="pick">Costing model
        <select name="costingVersion" [ngModel]="r.current" (ngModelChange)="pick($event)" aria-label="Costing model">
          @for (v of r.options; track v.code) { <option [value]="v.code">{{ v.code }} · {{ v.status }}</option> }
        </select>
      </label>
    }
  `,
  styles: `
    .pick { display: inline-flex; flex-direction: column; gap: 5px; margin: 0 0 14px; font-size: 12px; font-weight: 600; letter-spacing: .04em; text-transform: uppercase; color: var(--text-3); }
    select { font: 14px var(--font); text-transform: none; letter-spacing: normal; font-weight: 400; color: var(--text); padding: 6px 8px; border: 1px solid var(--border); border-radius: var(--radius-sm); background: var(--surface); }
  `,
})
export class VersionPick {
  private ctx = inject(RunContext);
  private router = inject(Router);
  res = apiResource<{ current: string; options: { code: string; status: string }[] }>(
    () => this.ctx.hospitalParam() ? 'costing-models/versions' : null, () => this.ctx.configParams());
  pick(version: string): void {
    void this.router.navigate([], { queryParams: { version }, queryParamsHandling: 'merge' });
  }
}

@Component({
  selector: 'v-type-costing',
  imports: [NgTemplateOutlet, FormsModule, RouterLink, Icon, VersionPick, ...UI],
  template: `
    @if (linked()) {
      <section class="linked-list">
        <h2>{{ title() }}</h2>
        <ng-container [ngTemplateOutlet]="body" />
      </section>
    } @else if (embedded()) {
      <section class="card overhead-block">
        <h2>{{ title() }}</h2>
        <ng-container [ngTemplateOutlet]="body" />
      </section>
    } @else {
      <v-page [title]="title()" [purpose]="purpose()">
      <button actions class="btn primary" type="button" (click)="save()" [disabled]="busy() || !dirty() || locked()">{{ busy() ? 'Saving…' : 'Save' }}</button>
      <div class="version-line">
        <v-version-pick />
        @if (versionCode() && !locked()) { <span class="muted version-note">Bound to costing version {{ versionCode() }} ({{ versionStatus() }}).</span> }
      </div>
      <ng-container [ngTemplateOutlet]="body" />
      </v-page>
    }
    <ng-template #body>
      <v-state [loading]="res.isLoading()" [error]="res.error()">
        @if (res.value()) {
          @if (!embedded()) {
            @if (locked()) {
              <p class="notice">{{ versionCode() }} is {{ versionStatus() }}. Direct and Indirect costing on a published version are never edited.
                <a routerLink="/costing-models" queryParamsHandling="preserve">Create a new version</a>.</p>
            }
          }
          @if (message()) { <p class="notice" [class.bad]="!!error()">{{ message() }}</p> }
          @if (hint()) { <p class="muted margin-hint">{{ hint() }}</p> }
          @if (!linked()) {
          <input type="search" class="item-search" [placeholder]="'Search ' + noun() + 's by name or code'"
            [ngModel]="search()" (ngModelChange)="setSearch($event)" [attr.aria-label]="'Search ' + noun() + 's'" />
          }
          @if (searching()) { <p class="faint search-status">Searching…</p> }
          @else if (search().trim() && visibleRows().length === 0) { <p class="faint search-status">No {{ noun() }}s match this search.</p> }
          @if (listTitle()) { <h2 class="list-title">{{ listTitle() }}</h2> }
          <div class="table-wrap">
            <table class="data">
              <caption class="sr-only">{{ typeLabel() }}s</caption>
              <thead>
                <tr>
                  <th class="exp" scope="col"></th>
                  <th scope="col">{{ typeLabel() }}</th>
                  <th class="num" scope="col">{{ countLabel() }}</th>
                  <th scope="col">{{ pctLabel() }}</th>
                </tr>
              </thead>
              <tbody>
                @for (row of visibleRows(); track row.code) {
                  <tr>
                    <td class="exp">
                      @if (row.code !== 'ALL') {
                        <button class="btn icon-only" type="button"
                          [attr.aria-expanded]="row.expanded" [attr.aria-label]="'Expand ' + row.name"
                          (click)="toggle(row.code)">
                          <v-icon name="chevron" />
                        </button>
                      }
                    </td>
                    <td>{{ row.name }}</td>
                    <td class="num">{{ search().trim() ? row.itemTotal : row.procedures }}</td>
                    <td>
                      <input class="margin-input" name="margin-{{ row.code }}" type="text" inputmode="decimal" [attr.aria-label]="pctLabel() + ' for ' + row.name"
                        [ngModel]="row.marginPct" (ngModelChange)="setMargin(row.code, $event)" [disabled]="locked()" />
                    </td>
                  </tr>
                  @if (row.expanded) {
                    <tr class="expand-row">
                      <td colspan="4">
                        <div class="expand-pane">
                          <table class="data nested">
                            <thead>
                              <tr>
                                <th scope="col">Id</th>
                                <th scope="col">Name</th>
                                @if (!fixedCost()) { <th class="num" [class.composition]="!compositionColumn()" scope="col">Contracted price</th> }
                                @if (compositionColumn()) { <th class="num composition" scope="col">Composition Cost</th> }
                                <th scope="col">{{ pctLabel() }}</th>
                                @if (!fixedCost()) { <th class="num" scope="col">{{ amountLabel() }}</th> }
                              </tr>
                            </thead>
                            <tbody>
                              @for (item of row.items; track item.id) {
                                <tr>
                                  <td><a [routerLink]="item.packageId ? ['/packages', item.packageId] : ['/procedures', item.id]" queryParamsHandling="preserve">{{ hisId(item.code) }}</a></td>
                                  <td>{{ item.name }}</td>
                                  @if (!fixedCost()) { <td class="num" [class.composition]="!compositionColumn()">{{ item.listPrice | money }}</td> }
                                  @if (compositionColumn()) {
                                    <td class="num composition" [class.bad]="+(item.compositionCost ?? 0) > +item.listPrice">{{ item.compositionCost | money }}</td>
                                  }
                                  <td>
                                    @if (editing()?.id === item.id && editing()?.field === 'margin') {
                                      <input class="cell-input" name="item-margin-{{ item.id }}" type="text" inputmode="decimal"
                                        [ngModel]="editValue()" (ngModelChange)="editValue.set($event)"
                                        (blur)="commitItemEdit()" (keydown.enter)="$event.preventDefault(); commitItemEdit()"
                                        (keydown.escape)="$event.preventDefault(); cancelItemEdit()" />
                                    } @else {
                                      <span class="cell-value" [class.faint]="!item.marginPct" [class.locked]="locked()"
                                        (click)="startItemEdit(row.code, item.id, 'margin')">{{ item.marginPct || '—' }}</span>
                                    }
                                  </td>
                                  @if (!fixedCost()) {
                                  <td class="num">
                                    @if (editing()?.id === item.id && editing()?.field === 'cost') {
                                      <input class="cell-input" name="item-cost-{{ item.id }}" type="text" inputmode="decimal"
                                        [ngModel]="editValue()" (ngModelChange)="editValue.set($event)"
                                        (blur)="commitItemEdit()" (keydown.enter)="$event.preventDefault(); commitItemEdit()"
                                        (keydown.escape)="$event.preventDefault(); cancelItemEdit()" />
                                    } @else {
                                      <span class="cell-value" [class.locked]="locked()"
                                        (click)="startItemEdit(row.code, item.id, 'cost')">{{ evaluatedCost(item) | money }}</span>
                                    }
                                  </td>
                                  }
                                </tr>
                              } @empty {
                                <tr><td class="faint" [attr.colspan]="fixedCost() ? 3 : compositionColumn() ? 6 : 5">{{ search().trim() ? 'No ' + noun() + 's match this search.' : 'No ' + noun() + 's in this type.' }}</td></tr>
                              }
                            </tbody>
                          </table>
                          @if (row.loadingItems) { <p class="faint more">Loading…</p> }
                          @else if (row.itemTotal > row.items.length) {
                            <p class="faint more">Showing {{ row.items.length }} of {{ row.itemTotal }}.
                              <button class="btn" type="button" (click)="showMore(row.code)">Show 100 more</button>
                            </p>
                          }
                        </div>
                      </td>
                    </tr>
                  }
                } @empty {
                  <tr><td class="faint" colspan="4">{{ search().trim() ? 'No ' + noun() + 's match this search.' : 'No ' + noun() + 's in this hospital.' }}</td></tr>
                }
              </tbody>
            </table>
          </div>
          @if (!linked() && !companion()) {
            <div class="save-bar">
              <button class="btn primary" type="button" (click)="save()" [disabled]="busy() || !dirty() || locked()">{{ busy() ? 'Saving…' : 'Save' }}</button>
            </div>
          }
        }
      </v-state>
    </ng-template>
  `,
  styles: `
    .sr-only { position: absolute; width: 1px; height: 1px; overflow: hidden; clip: rect(0 0 0 0); }
    .margin-input, .cell-input, .cell-value {
      width: 8rem; font: 14px var(--font); padding: 6px 8px;
      border: 1px solid var(--border); border-radius: var(--radius-sm);
      background: var(--surface); color: var(--text);
    }
    .cell-value {
      display: inline-block; text-align: right; cursor: text; box-sizing: border-box;
    }
    .cell-value:hover { border-color: var(--text); }
    .cell-value.locked, .cell-value.locked:hover { cursor: default; border-color: var(--border); }
    .save-bar { display: flex; justify-content: flex-end; margin-top: 14px; }
    .version-line { display: flex; flex-wrap: wrap; align-items: flex-end; column-gap: 14px; }
    .version-note { margin-bottom: 21px; }
    .notice { margin: 0 0 12px; }
    .margin-hint { margin: 0 0 12px; }
    td.exp, th.exp { width: 40px; padding-right: 0; }
    button.icon-only { padding: 4px; width: 28px; height: 28px; justify-content: center; border-radius: 8px; }
    button.icon-only v-icon { transition: transform .15s; }
    button.icon-only[aria-expanded] v-icon { transform: rotate(-90deg); }
    button.icon-only[aria-expanded="true"] v-icon { transform: rotate(0); }
    tr.expand-row td { background: var(--surface-2); padding: 0; }
    .expand-pane { padding: 8px 12px 14px 40px; }
    table.nested { background: var(--surface-solid); }
    table.nested th.composition, table.nested td.composition { padding-right: 32px; }
    .more { margin-top: 8px; }
    .item-search {
      width: min(28rem, 100%); margin: 0 0 10px; font: 14px var(--font); padding: 6px 8px;
      border: 1px solid var(--border); border-radius: var(--radius-sm);
      background: var(--surface); color: var(--text);
    }
    .search-status { margin: 0 0 10px; }
    .overhead-block { margin-top: 14px; position: relative; z-index: 0; }
    .overhead-block h2 { margin: 0 0 12px; }
    .linked-list { margin-top: 24px; }
    .linked-list h2 { margin: 0 0 12px; }
    .list-title { margin: 10px 0 12px; }
  `,
})
export class TypeCosting {
  title = input.required<string>();
  purpose = input.required<string>();
  apiPath = input.required<string>();
  pctLabel = input('Margin Cost (%)');
  amountLabel = input('Evaluated cost');
  hint = input('');
  embedded = input(false);
  noun = input('procedure');
  typeLabel = input('Procedure/Activity type');
  countLabel = input('Procedure/Activity');
  /** The field holds a fixed cost in SAR: no contracted price or evaluated cost beside it. */
  fixedCost = input(false);
  /** A heading right above the grid, for a page with more than one list. */
  listTitle = input('');
  /** A second list on the same page: this list's search box and Save buttons also drive it. */
  companion = input<TypeCosting | null>(null);
  /** This list is driven by another's search box and Save buttons, so it shows neither. */
  linked = input(false);
  compositionColumn = input(false);
  private api = inject(Api);
  private ctx = inject(RunContext);
  private injector = inject(Injector);
  res = apiResource<Envelope<ProcedureTypeRow[]>>(() => this.ctx.hospitalParam() ? this.apiPath() : null, () => this.ctx.configParams());
  rows = signal<ProcedureTypeDraft[]>([]);
  search = signal('');
  searching = signal(false);
  private searchTimer: ReturnType<typeof setTimeout> | null = null;
  private searchGen = 0;
  busy = signal(false);
  error = signal(false);
  message = signal<string | null>(null);
  editing = signal<{ typeCode: string; id: string; field: 'margin' | 'cost' } | null>(null);
  editValue = signal('');
  versionCode = computed(() => this.res.value()?.meta.costingVersion ?? '');
  versionStatus = computed(() => this.res.value()?.meta.costingVersionStatus ?? '');
  locked = computed(() => versionFrozen(this.versionStatus()));
  visibleRows = computed(() => {
    const q = this.search().trim();
    const rows = this.rows();
    if (!q) return rows;
    return rows.filter(r => r.code !== 'ALL' && r.itemTotal > 0);
  });
  hisId = hisProcedureId;
  evaluatedCost = evaluatedCost;
  private parseValue(raw: string) {
    if (!this.fixedCost()) return parseMargin(raw, this.pctLabel());
    const parsed = parseCost(raw, this.pctLabel());
    return parsed.ok && Number(parsed.value) === 0 ? { ok: true as const, value: null } : parsed;
  }
  startItemEdit(typeCode: string, id: string, field: 'margin' | 'cost'): void {
    if (this.locked()) return;
    if (this.editing()) {
      this.commitItemEdit();
      if (this.editing()) return;
    }
    const item = this.rows().find(r => r.code === typeCode)?.items.find(i => i.id === id);
    if (!item) return;
    this.editing.set({ typeCode, id, field });
    this.editValue.set(field === 'margin' ? item.marginPct : evaluatedCost(item) ?? '');
    this.message.set(null);
    this.error.set(false);
    afterNextRender(() => {
      const el = document.querySelector<HTMLInputElement>('input.cell-input');
      el?.focus();
      el?.select();
    }, { injector: this.injector });
  }
  commitItemEdit(): void {
    const ed = this.editing();
    if (!ed) return;
    const raw = this.editValue();
    if (ed.field === 'margin') {
      const parsed = this.parseValue(raw);
      if (!parsed.ok) {
        this.error.set(true);
        this.message.set(parsed.message);
        return;
      }
      this.setItemMargin(ed.typeCode, ed.id, parsed.value ?? '');
    } else {
      const parsed = parseCost(raw, this.amountLabel());
      if (!parsed.ok) {
        this.error.set(true);
        this.message.set(parsed.message);
        return;
      }
      const item = this.rows().find(r => r.code === ed.typeCode)?.items.find(i => i.id === ed.id);
      if (!item) { this.editing.set(null); return; }
      const margin = marginFromEvaluated(item.listPrice, parsed.value, this.pctLabel(), this.amountLabel());
      if (!margin.ok) {
        this.error.set(true);
        this.message.set(margin.message);
        return;
      }
      this.setItemMargin(ed.typeCode, ed.id, margin.value ?? '');
    }
    this.editing.set(null);
    this.editValue.set('');
  }
  cancelItemEdit(): void {
    this.editing.set(null);
    this.editValue.set('');
    this.message.set(null);
    this.error.set(false);
  }
  dirty = computed(() => this.ownDirty() || !!this.companion()?.ownDirty());
  ownDirty = computed(() => {
    const cur = this.rows();
    const src = this.res.value()?.data ?? [];
    if (cur.length !== src.length) return cur.length > 0;
    const savedType = new Map(src.map(x => [x.code, marginText(x.marginPct)]));
    return cur.some(row => (savedType.get(row.code) ?? '') !== row.marginPct
      || row.items.some(i => i.marginPct !== i.savedMargin)
      || row.stash.some(i => i.marginPct !== i.savedMargin));
  });
  constructor() {
    effect(() => {
      const data = this.res.value()?.data;
      if (!data) return;
      untracked(() => {
        const prev = this.rows();
        const expanded = new Set(prev.filter(r => r.expanded).map(r => r.code));
        const kept = new Map(prev.map(r => [r.code, r]));
        this.rows.set(data.map(x => {
          const parent = marginText(x.marginPct);
          const old = kept.get(x.code);
          return {
            code: x.code, name: x.name, procedures: x.procedures, marginPct: parent,
            expanded: expanded.has(x.code), itemTotal: old?.itemTotal ?? 0, loadingItems: false,
            stash: old?.stash ?? [],
            items: (old?.items ?? []).map(i => ({
              ...i, marginPct: marginText(i.marginPct) === marginText(old?.marginPct) ? parent : i.marginPct,
            })),
          };
        }));
      });
    });
  }
  setSearch(value: string): void {
    this.companion()?.setSearch(value);
    this.search.set(value);
    if (this.searchTimer) clearTimeout(this.searchTimer);
    this.searchTimer = setTimeout(() => void this.loadSearch(), 250);
  }
  private async loadSearch(): Promise<void> {
    const q = this.search().trim();
    const gen = ++this.searchGen;
    if (!q) {
      this.searching.set(false);
      this.rows.update(rows => rows.map(r => {
        const stash = [...r.stash, ...r.items].filter(i => i.marginPct !== i.savedMargin);
        return { ...r, expanded: false, items: [], stash, itemTotal: 0, loadingItems: false };
      }));
      return;
    }
    this.searching.set(true);
    try {
      const page = await firstValueFrom(this.api.run<ProcedureTypeRow[]>(this.apiPath(), this.ctx.configParams(), { search: q }));
      if (gen !== this.searchGen) return;
      const hits = new Map((page.data ?? []).map(x => [x.code, x]));
      this.rows.update(rows => rows.map(r => {
        const hit = hits.get(r.code);
        if (!hit) {
          const stash = [...r.stash, ...r.items].filter(i => i.marginPct !== i.savedMargin);
          return { ...r, expanded: false, items: [], stash, itemTotal: 0, loadingItems: false };
        }
        const parent = marginText(this.rows().find(x => x.code === r.code)?.marginPct) || marginText(hit.marginPct);
        const incoming = (hit.items ?? []).map(i => toItemDraft(i, parent));
        const merged = mergeDrafts(r, incoming, true);
        return { ...r, ...merged, expanded: true, itemTotal: Number(hit.procedures), loadingItems: false };
      }));
    } catch (err) {
      if (gen !== this.searchGen) return;
      this.error.set(true);
      this.message.set(toApiError(err).message);
    } finally {
      if (gen === this.searchGen) this.searching.set(false);
    }
  }
  toggle(code: string): void {
    const row = this.rows().find(r => r.code === code);
    if (!row) return;
    const opening = !row.expanded;
    this.rows.update(rows => rows.map(r => r.code === code ? { ...r, expanded: opening } : r));
    if (opening && row.items.length === 0) void this.loadItems(code, true);
  }
  showMore(code: string): void { void this.loadItems(code, false); }
  private async loadItems(code: string, reset: boolean): Promise<void> {
    const row = this.rows().find(r => r.code === code);
    if (!row || row.loadingItems) return;
    this.rows.update(rows => rows.map(r => r.code === code ? { ...r, loadingItems: true } : r));
    try {
      const skip = reset ? 0 : row.items.length;
      const search = this.search().trim() || undefined;
      const page = await firstValueFrom(this.api.run<ProcedureTypeItemsPage>(
        `${this.apiPath()}/${encodeURIComponent(code)}`, this.ctx.configParams(), { skip, take: 100, search }));
      const parent = marginText(this.rows().find(r => r.code === code)?.marginPct);
      const incoming = (page.data.items ?? []).map(i => toItemDraft(i, parent));
      this.rows.update(rows => rows.map(r => {
        if (r.code !== code) return r;
        const merged = mergeDrafts(r, incoming, reset);
        return { ...r, ...merged, itemTotal: page.data.total, loadingItems: false };
      }));
    } catch (err) {
      this.rows.update(rows => rows.map(r => r.code === code ? { ...r, loadingItems: false } : r));
      this.error.set(true);
      this.message.set(toApiError(err).message);
    }
  }
  setMargin(code: string, value: string): void {
    this.rows.update(rows => {
      if (code !== 'ALL') {
        return rows.map(r => {
          if (r.code !== code) return r;
          return { ...r, marginPct: value, items: followParent(r.items, r.marginPct, value), stash: followParent(r.stash, r.marginPct, value) };
        });
      }
      const prev = marginText(rows.find(r => r.code === 'ALL')?.marginPct);
      return rows.map(r => {
        if (r.code === 'ALL') return { ...r, marginPct: value };
        const typePrev = marginText(r.marginPct);
        if (typePrev !== '' && typePrev !== prev) return r;
        return { ...r, marginPct: value, items: followParent(r.items, typePrev, value), stash: followParent(r.stash, typePrev, value) };
      });
    });
    this.message.set(null);
    this.error.set(false);
  }
  setItemMargin(typeCode: string, id: string, value: string): void {
    this.rows.update(rows => rows.map(r => r.code !== typeCode ? r
      : { ...r, items: r.items.map(i => i.id === id ? { ...i, marginPct: value } : i) }));
    this.message.set(null);
    this.error.set(false);
  }
  async save(): Promise<void> {
    if (this.locked()) return;
    const companion = this.companion();
    // Nothing is sent until both lists are valid.
    const mine = this.ownDirty() ? this.payload() : undefined;
    const theirs = companion?.ownDirty() ? companion.payload() : undefined;
    if (mine === null || theirs === null) return;
    this.busy.set(true);
    this.message.set(null);
    this.error.set(false);
    companion?.message.set(null);
    companion?.error.set(false);
    try {
      if (mine) await this.send(mine);
      if (theirs) await companion!.send(theirs);
      this.message.set('Saved.');
    } catch (err) {
      this.error.set(true);
      this.message.set(toApiError(err).message);
    } finally {
      this.busy.set(false);
    }
  }
  /** What to send, or null with the reason shown when a value is not valid. */
  payload(): { items: { code: string; marginPct: string | null }[]; procedures: { id: string; marginPct: string | null }[] } | null {
    this.error.set(false);
    this.message.set(null);
    const items: { code: string; marginPct: string | null }[] = [];
    const procedures: { id: string; marginPct: string | null }[] = [];
    for (const row of this.rows()) {
      const parsed = this.parseValue(row.marginPct);
      if (!parsed.ok) {
        this.error.set(true);
        this.message.set(parsed.message);
        return null;
      }
      items.push({ code: row.code, marginPct: parsed.value });
      const seen = new Set<string>();
      for (const item of [...row.stash, ...row.items]) {
        if (seen.has(item.id)) continue;
        seen.add(item.id);
        const ip = this.parseValue(item.marginPct);
        if (!ip.ok) {
          this.error.set(true);
          this.message.set(`${item.name}: ${ip.message}`);
          return null;
        }
        procedures.push({ id: item.id, marginPct: ip.value });
      }
    }
    return { items, procedures };
  }
  async send(body: { items: { code: string; marginPct: string | null }[]; procedures: { id: string; marginPct: string | null }[] }): Promise<void> {
    await firstValueFrom(this.api.put<Envelope<ProcedureTypeRow[]>>(`${this.apiPath()}${this.ctx.configQuery()}`, body));
    this.rows.update(rs => rs.map(r => ({
      ...r,
      items: r.items.map(i => ({ ...i, savedMargin: i.marginPct })),
      stash: r.stash.map(i => ({ ...i, savedMargin: i.marginPct })),
    })));
    this.res.reload();
  }
}

@Component({
  selector: 'v-procedure-costing',
  imports: [TypeCosting],
  template: `
    <v-type-costing #main title="Procedure costing" purpose="Procedure/activity types in this hospital, and how many sit under each." apiPath="procedure-costing" listTitle="Billable Procedures" [companion]="zero" />
    <v-type-costing #zero title="Zero price procedures" purpose="" apiPath="zero-price-costing" pctLabel="Fixed Cost (SAR)" hint="" [fixedCost]="true" [linked]="true" />
    <div class="save-bar">
      <button class="btn primary" type="button" (click)="main.save()" [disabled]="main.busy() || !main.dirty() || main.locked()">{{ main.busy() ? 'Saving…' : 'Save' }}</button>
    </div>
  `,
  styles: `.save-bar { display: flex; justify-content: flex-end; margin-top: 14px; }`,
})
export class ProcedureCosting {}

@Component({
  selector: 'v-package-costing',
  imports: [TypeCosting],
  template: `<v-type-costing title="Package costing" purpose="Package types in this hospital, and the direct cost of each package as a percentage of its contracted price." apiPath="package-costing"
    noun="package" typeLabel="Package type" countLabel="Packages" [compositionColumn]="true" hint="If Margin Cost (%) is 0 or blank, the package follows its composition costing (sum of included procedure's cost)" />`,
})
export class PackageCosting {}

interface EncounterOverheadRow {
  id: string; costCenterId: string; costCenterCode: string; costCenterName: string;
  description: string; overheadPct?: string | number | null; overheadAmount?: string | number | null;
}
interface CostCentrePick { id: string; code: string; name: string; }
interface EncounterOverheadPage { items: EncounterOverheadRow[]; costCentres: CostCentrePick[]; }
interface OverheadDraft {
  id: string | null; costCenterId: string; ccQuery: string; description: string; overheadPct: string; overheadAmount: string;
}

@Component({
  selector: 'v-encounter-overheads',
  imports: [FormsModule, RouterLink, Icon, ...UI],
  template: `
    <section class="card" [class.pick-open]="ccOpen()">
      <div class="head">
        <h2>Encounter level overheads (Cost will be considered for every encounter)</h2>
        <button class="btn primary" type="button" (click)="startAdd()" [disabled]="!!draft() || busy() || locked()">Add</button>
      </div>
      @if (locked()) {
        <p class="notice">{{ versionCode() }} is {{ versionStatus() }}. Direct and Indirect costing on a published version are never edited.
          <a routerLink="/costing-models" queryParamsHandling="preserve">Create a new version</a>.</p>
      } @else if (versionCode()) {
        <p class="muted hint">Bound to costing version {{ versionCode() }} ({{ versionStatus() }}).</p>
      }
      @if (message()) { <p class="notice" [class.bad]="!!error()">{{ message() }}</p> }
      @if (draft(); as d) {
        <div class="draft-form">
          <div class="draft-fields">
            <label class="field cc-field">
              <span>Cost Center Code</span>
              <div class="cc-pick">
                <input type="search" class="cc-input" placeholder="Search cost centre"
                  [ngModel]="d.ccQuery" (ngModelChange)="setCcQuery($event)"
                  (focus)="ccOpen.set(true)" (blur)="ccOpen.set(false)" aria-label="Cost Center Code" />
                @if (ccOpen()) {
                  <ul class="cc-list" role="listbox">
                    @for (c of filteredCentres(); track c.id) {
                      <li>
                        <button type="button" (mousedown)="$event.preventDefault(); pickCentre(c)">
                          <strong>{{ local(c.code) }}</strong>
                          <span class="muted">{{ c.name }}</span>
                        </button>
                      </li>
                    } @empty { <li class="faint">No cost centres match.</li> }
                  </ul>
                }
              </div>
            </label>
            <label class="field">
              <span>Description</span>
              <input class="desc-input" [ngModel]="d.description" (ngModelChange)="patchDraft({ description: $event })" aria-label="Description" />
            </label>
            <label class="field">
              <span>Overhead %</span>
              <input class="num-input" inputmode="decimal" [ngModel]="d.overheadPct" (ngModelChange)="patchDraft({ overheadPct: $event })" aria-label="Overhead %" />
            </label>
            <label class="field">
              <span class="with-hint">Max Cap (Fixed)
                <span class="hint-icon" title="If Overhead (%) is not given, it will be considered as Fixed." aria-label="If Overhead (%) is not given, it will be considered as Fixed.">
                  <v-icon name="help" [size]="14" />
                </span>
              </span>
              <input class="num-input" inputmode="decimal" [ngModel]="d.overheadAmount" (ngModelChange)="patchDraft({ overheadAmount: $event })" aria-label="Max Cap (Fixed)" />
            </label>
          </div>
          <p class="muted hint">If Overhead (%) is not given, it will be considered as Fixed.</p>
          <div class="draft-actions">
            <button class="btn primary" type="button" (click)="saveDraft()" [disabled]="busy()">Save</button>
            <button class="btn" type="button" (click)="cancelDraft()" [disabled]="busy()">Cancel</button>
          </div>
        </div>
      }
      <v-state [loading]="res.isLoading()" [error]="res.error()">
        @if (res.value()) {
          <div class="table-wrap">
            <table class="data">
              <caption class="sr-only">Encounter level overheads</caption>
              <thead>
                <tr>
                  <th scope="col">Cost Center Code</th>
                  <th scope="col">Description</th>
                  <th class="num" scope="col">Overhead %</th>
                  <th class="num" scope="col" title="If Overhead (%) is not given, it will be considered as Fixed.">Max Cap (Fixed)</th>
                  <th class="acts" scope="col"></th>
                </tr>
              </thead>
              <tbody>
                @for (row of items(); track row.id) {
                  <tr [class.editing]="draft()?.id === row.id">
                    <td>{{ local(row.costCenterCode) }}</td>
                    <td>{{ row.description || '—' }}</td>
                    <td class="num">{{ fmtPct(row.overheadPct) }}</td>
                    <td class="num">{{ row.overheadAmount | money }}</td>
                    <td class="acts">
                      <div class="acts-row">
                        <button class="btn" type="button" (click)="startEdit(row)" [disabled]="!!draft() || busy() || locked()">Edit</button>
                        <button class="btn" type="button" (click)="remove(row)" [disabled]="!!draft() || busy() || locked()">Delete</button>
                      </div>
                    </td>
                  </tr>
                } @empty {
                  @if (!draft()) { <tr><td class="faint" colspan="5">No encounter-level overheads.</td></tr> }
                }
              </tbody>
            </table>
          </div>
        }
      </v-state>
    </section>
  `,
  styles: `
    :host { display: block; position: relative; z-index: 2; }
    .card.pick-open { z-index: 5; }
    .head { display: flex; gap: 12px; align-items: flex-end; justify-content: space-between; flex-wrap: wrap; margin-bottom: 12px; }
    .head h2 { margin: 0; max-width: 72ch; }
    .notice { margin: 0 0 12px; }
    .sr-only { position: absolute; width: 1px; height: 1px; overflow: hidden; clip: rect(0 0 0 0); }
    .draft-form { display: flex; flex-direction: column; gap: 12px; margin: 0 0 16px; position: relative; z-index: 3; }
    .draft-fields {
      display: grid; grid-template-columns: minmax(16rem, 1.6fr) minmax(12rem, 1.4fr) 8rem minmax(9rem, 11rem);
      gap: 12px 14px; align-items: end;
    }
    .field { display: flex; flex-direction: column; gap: 5px; min-width: 0; }
    .field span { font-size: 12px; font-weight: 600; letter-spacing: .04em; text-transform: uppercase; color: var(--text-3); }
    .with-hint { display: inline-flex; align-items: center; gap: 4px; }
    .hint-icon { display: inline-flex; color: var(--text-3); cursor: help; }
    .hint { margin: 0; max-width: 72ch; }
    .cc-field { position: relative; }
    .cc-pick { position: relative; }
    .cc-input, .desc-input, .num-input {
      width: 100%; box-sizing: border-box; font: 14px var(--font); padding: 6px 8px;
      border: 1px solid var(--border); border-radius: var(--radius-sm);
      background: var(--surface); color: var(--text);
    }
    .cc-list {
      position: absolute; z-index: 40; left: 0; right: auto; top: calc(100% + 4px); min-width: 100%; width: max(100%, 24rem);
      max-height: 16rem; overflow: auto; margin: 0; padding: 4px 0; list-style: none;
      background: var(--surface-solid); border: 1px solid var(--border); border-radius: var(--radius-sm);
      box-shadow: var(--shadow);
    }
    .cc-list button {
      display: flex; align-items: baseline; gap: 16px; width: 100%; text-align: left;
      padding: 6px 10px; background: none; border: 0; font: inherit; color: inherit; cursor: pointer;
    }
    .cc-list button strong { flex: 0 0 auto; white-space: nowrap; }
    .cc-list button:hover { background: var(--surface-2); }
    .cc-list .faint { padding: 6px 10px; }
    .draft-actions { display: flex; gap: 8px; }
    th.acts, td.acts { width: 1%; white-space: nowrap; text-align: right; }
    .acts-row { display: inline-flex; gap: 6px; justify-content: flex-end; }
    tr.editing td { background: var(--accent-soft); }
    @media (max-width: 900px) {
      .draft-fields { grid-template-columns: 1fr 1fr; }
    }
  `,
})
export class EncounterOverheads {
  private api = inject(Api);
  private ctx = inject(RunContext);
  res = apiResource<Envelope<EncounterOverheadPage>>(() => this.ctx.hospitalParam() ? 'indirect-costing/encounter-overheads' : null, () => this.ctx.configParams());
  draft = signal<OverheadDraft | null>(null);
  ccOpen = signal(false);
  busy = signal(false);
  error = signal(false);
  message = signal<string | null>(null);
  local = hisProcedureId;
  fmtPct = formatOverheadPct;
  versionCode = computed(() => this.res.value()?.meta.costingVersion ?? '');
  versionStatus = computed(() => this.res.value()?.meta.costingVersionStatus ?? '');
  locked = computed(() => versionFrozen(this.versionStatus()));
  items = computed(() => this.res.value()?.data.items ?? []);
  centres = computed(() => this.res.value()?.data.costCentres ?? []);
  filteredCentres = computed(() => {
    const q = this.draft()?.ccQuery ?? '';
    return this.centres().filter(c => matchesNameOrCode(q, c.name, c.code) || matchesNameOrCode(q, c.name, hisProcedureId(c.code))).slice(0, 40);
  });
  startAdd(): void {
    if (this.locked()) return;
    this.draft.set({ id: null, costCenterId: '', ccQuery: '', description: '', overheadPct: '', overheadAmount: '' });
    this.ccOpen.set(true);
    this.message.set(null);
    this.error.set(false);
  }
  startEdit(row: EncounterOverheadRow): void {
    if (this.locked()) return;
    this.draft.set({
      id: row.id, costCenterId: row.costCenterId, ccQuery: `${hisProcedureId(row.costCenterCode)} · ${row.costCenterName}`,
      description: row.description ?? '', overheadPct: formatOverheadPctInput(row.overheadPct),
      overheadAmount: row.overheadAmount == null ? '' : String(row.overheadAmount),
    });
    this.ccOpen.set(false);
    this.message.set(null);
    this.error.set(false);
  }
  cancelDraft(): void { this.draft.set(null); this.ccOpen.set(false); this.message.set(null); this.error.set(false); }
  patchDraft(patch: Partial<OverheadDraft>): void {
    const cur = this.draft();
    if (!cur) return;
    this.draft.set({ ...cur, ...patch });
  }
  setCcQuery(value: string): void {
    this.patchDraft({ ccQuery: value, costCenterId: '' });
    this.ccOpen.set(true);
  }
  pickCentre(c: CostCentrePick): void {
    this.patchDraft({ costCenterId: c.id, ccQuery: `${hisProcedureId(c.code)} · ${c.name}` });
    this.ccOpen.set(false);
  }
  async saveDraft(): Promise<void> {
    if (this.locked()) return;
    let d = this.draft();
    if (!d) return;
    if (!d.costCenterId) {
      const q = d.ccQuery.trim().toLowerCase();
      const exact = this.centres().filter(c =>
        hisProcedureId(c.code).toLowerCase() === q || c.code.toLowerCase() === q || `${hisProcedureId(c.code)} · ${c.name}`.toLowerCase() === q);
      if (exact.length === 1) {
        this.pickCentre(exact[0]);
        d = this.draft()!;
      }
    }
    if (!d.costCenterId) {
      this.error.set(true);
      this.message.set('Select a cost centre.');
      return;
    }
    const pct = parseMargin(d.overheadPct, 'Overhead (%)');
    if (!pct.ok) { this.error.set(true); this.message.set(pct.message); return; }
    const amount = parseCost(d.overheadAmount, 'Max Cap (Fixed)');
    if (!amount.ok) { this.error.set(true); this.message.set(amount.message); return; }
    this.busy.set(true);
    this.message.set(null);
    this.error.set(false);
    try {
      const q = this.ctx.configQuery();
      const body = { costCenterId: d.costCenterId, description: d.description, overheadPct: pct.value, overheadAmount: amount.value };
      if (d.id) await firstValueFrom(this.api.put<Envelope<EncounterOverheadPage>>(`indirect-costing/encounter-overheads/${d.id}${q}`, body));
      else await firstValueFrom(this.api.post<Envelope<EncounterOverheadPage>>(`indirect-costing/encounter-overheads${q}`, body));
      this.draft.set(null);
      this.ccOpen.set(false);
      this.res.reload();
      this.message.set('Saved.');
    } catch (err) {
      this.error.set(true);
      this.message.set(toApiError(err).message);
    } finally {
      this.busy.set(false);
    }
  }
  async remove(row: EncounterOverheadRow): Promise<void> {
    if (this.locked()) return;
    if (!confirm(`Delete encounter overhead for ${hisProcedureId(row.costCenterCode)}?`)) return;
    this.busy.set(true);
    this.message.set(null);
    this.error.set(false);
    try {
      const q = this.ctx.configQuery();
      await firstValueFrom(this.api.delete(`indirect-costing/encounter-overheads/${row.id}${q}`));
      this.res.reload();
      this.message.set('Deleted.');
    } catch (err) {
      this.error.set(true);
      this.message.set(toApiError(err).message);
    } finally {
      this.busy.set(false);
    }
  }
}

@Component({
  selector: 'v-indirect-costing',
  imports: [EncounterOverheads, TypeCosting, VersionPick, ...UI],
  template: `
    <v-page title="Indirect costing" purpose="Indirect overhead cost on each procedure/activity type, as a percentage of contracted price, and the amount in SAR.">
      <v-version-pick />
      <v-encounter-overheads />
      <v-type-costing title="Procedure/activity overheads" purpose="" apiPath="indirect-costing" pctLabel="Overhead (%)" amountLabel="Overhead (SAR)" hint="" [embedded]="true" />
    </v-page>
  `,
})
export class IndirectCosting {}

interface PeriodRow {
  id: string; code: string; startDate: string; endDate: string; days: number; status: string; hospital?: string | null;
  run: string | null; runStatus: string | null; costingVersion: string | null;
}

@Component({
  selector: 'v-periods',
  imports: [...UI],
  template: `
    <v-page title="Periods" purpose="The costing periods of the selected hospital, with the run and costing model linked to each.">
      @if (message()) { <p class="notice" [class.bad]="error()">{{ message() }}</p> }
      @if (canEdit()) {
        <form class="card" style="display:flex;flex-wrap:wrap;gap:12px;align-items:flex-end;margin-bottom:14px" (submit)="add($event)">
          <label>From <input type="date" required [value]="start()" (input)="start.set($any($event.target).value)" /></label>
          <label>To <input type="date" required [value]="end()" (input)="end.set($any($event.target).value)" /></label>
          <label>Name <input type="text" placeholder="e.g. 2026 Oct" [value]="code()" (input)="code.set($any($event.target).value)" /></label>
          <button class="btn primary" type="submit" [disabled]="busy() || !start() || !end()">Add period</button>
        </form>
      }
      <v-table [rows]="rows()" [columns]="cols()" caption="Periods" empty="No periods for this hospital yet. Add one above, or import facts to create its months." />
    </v-page>
  `,
})
export class Periods {
  private ctx = inject(RunContext);
  private api = inject(Api);
  private auth = inject(Auth);
  busy = signal(false);
  error = signal(false);
  message = signal<string | null>(null);
  start = signal('');
  end = signal('');
  code = signal('');
  canEdit = computed(() => this.auth.can('masterdata.edit'));
  async add(event: Event): Promise<void> {
    event.preventDefault();
    const hospital = this.ctx.hospitalParam();
    if (!hospital || this.busy()) return;
    this.busy.set(true);
    this.message.set(null);
    this.error.set(false);
    try {
      const made = await firstValueFrom(this.api.post<{ code: string }>('periods', { hospital, code: this.code() || null, startDate: this.start(), endDate: this.end() }));
      await this.ctx.load(true);
      if (!this.ctx.periodParam()) this.ctx.selectScope(hospital, made.code);
      this.start.set(''); this.end.set(''); this.code.set('');
      this.message.set(`Period ${made.code} added.`);
    } catch (err) {
      this.error.set(true);
      this.message.set(toApiError(err).message);
    } finally {
      this.busy.set(false);
    }
  }
  rows = computed<PeriodRow[]>(() => {
    const hospital = this.ctx.hospitalParam();
    return this.ctx.periodsOf(hospital).map(p => {
      const run = hospital ? this.ctx.resolveRun(hospital, p.code) : null;
      return {
        id: p.id, code: p.code, startDate: p.startDate, endDate: p.endDate, days: p.days, status: p.status, hospital: p.hospital,
        run: run?.code ?? null, runStatus: run?.status ?? null, costingVersion: run?.costingVersion ?? null,
      };
    });
  });
  cols = computed<Col<PeriodRow>[]>(() => {
    const cols: Col<PeriodRow>[] = [
      { key: 'code', label: 'Period' }, { key: 'startDate', label: 'From' }, { key: 'endDate', label: 'To' },
      { key: 'days', label: 'Days', type: 'num', dp: 0 }, { key: 'status', label: 'Status', type: 'chip' },
      { key: 'costingVersion', label: 'Costing model', value: r => r.costingVersion ?? '—' },
      { key: 'run', label: 'Calculation run', value: r => r.run ?? 'Not run', link: r => r.run ? `/runs/${r.run}` : '/runs' },
      { key: 'runStatus', label: 'Run status', value: r => r.runStatus ?? '—' },
    ];
    if (this.auth.can('masterdata.edit')) {
      cols.push({
        key: 'delete', label: '', type: 'button', sortable: false,
        button: r => !r.run ? 'Delete' : null,
        onButton: r => void this.remove(r),
      });
    }
    return cols;
  });
  private async remove(row: PeriodRow): Promise<void> {
    if (this.busy() || !confirm(`Delete period ${row.code} (${row.startDate} to ${row.endDate})?`)) return;
    this.busy.set(true);
    this.message.set(null);
    this.error.set(false);
    try {
      await firstValueFrom(this.api.delete(`periods/${row.id}`));
      await this.ctx.load(true);
      const hospital = this.ctx.hospitalParam();
      if (hospital && this.ctx.periodParam() === row.code) {
        const next = this.ctx.preferredPeriod(hospital);
        if (next) this.ctx.selectScope(hospital, next);
      }
      this.message.set(`Period ${row.code} deleted.`);
    } catch (err) {
      this.error.set(true);
      this.message.set(toApiError(err).message);
    } finally {
      this.busy.set(false);
    }
  }
}

/** Mirrors Catalogue.IsRunDeletable: results under a published costing model are kept. */
/** What a re-run does, for its warning prompt. Results are cleared before it recalculates, so a failure leaves none. */
function rerunWarning(r: { period: string; costingVersion: string; costingVersionStatus: string; metrics?: unknown }, force: boolean) {
  const metrics = (r.metrics ?? {}) as { patientCost?: string | number; revenue?: string | number };
  return {
    force,
    effects: [
      `Delete the stored results for ${r.period} and recalculate them.`,
      `Use costing model ${r.costingVersion} (${r.costingVersionStatus}) with its current percentages, and the current encounters and prices.`,
      'May change the numbers on the Dashboard, Cost Centres, Procedures, Packages and Patient Encounters for this period.',
    ],
    current: [
      metrics.patientCost != null ? `Total cost: ${formatMoney(String(metrics.patientCost))}` : '',
      metrics.revenue != null ? `Revenue: ${formatMoney(String(metrics.revenue))}` : '',
    ].filter(Boolean),
  };
}

function runDeletable(r: { status: string; costingVersionStatus: string }): boolean {
  return r.status === 'DRAFT' || r.status === 'FAILED' || (r.status === 'RECONCILED' && r.costingVersionStatus !== 'PUBLISHED');
}


function toItemDraft(i: ProcedureCostingItem, parent: string): ItemDraft {
  const marginPct = marginText(i.marginPct) || parent;
  return { id: i.id, code: i.code, name: i.name, marginPct, savedMargin: marginPct, listPrice: i.listPrice ?? 0, packageId: i.packageId, compositionCost: i.compositionCost };
}

function mergeDrafts(row: ProcedureTypeDraft, incoming: ItemDraft[], reset: boolean): { items: ItemDraft[]; stash: ItemDraft[] } {
  const prev = new Map([...row.stash, ...row.items].map(i => [i.id, i]));
  const mapped = incoming.map(i => {
    const old = prev.get(i.id);
    return old ? { ...i, marginPct: old.marginPct, savedMargin: old.savedMargin } : i;
  });
  const shown = new Set(mapped.map(i => i.id));
  const leftover = [...prev.values()].filter(i => !shown.has(i.id) && i.marginPct !== i.savedMargin);
  if (reset) return { items: mapped, stash: leftover };
  const items = [...row.items, ...mapped.filter(n => !row.items.some(o => o.id === n.id))];
  const visible = new Set(items.map(i => i.id));
  return { items, stash: leftover.filter(i => !visible.has(i.id)) };
}

function followParent(items: ItemDraft[], prev: string, next: string): ItemDraft[] {
  const from = marginText(prev);
  return items.map(i => {
    const cur = marginText(i.marginPct);
    if (cur === '' || cur === from) return { ...i, marginPct: next };
    return i;
  });
}

function marginText(v: string | number | null | undefined): string {
  if (v == null || v === '') return '';
  const n = Number(v);
  return Number.isFinite(n) ? String(n) : String(v);
}

/** Evaluated cost = contracted price × (Margin Cost % / 100). Blank/0 follows recipe. */
function evaluatedCost(item: ItemDraft): string | null {
  const parsed = parseMargin(item.marginPct);
  if (!parsed.ok || parsed.value == null) return null;
  const price = Number(item.listPrice);
  const margin = Number(parsed.value);
  if (!Number.isFinite(price) || !Number.isFinite(margin)) return null;
  return String(Math.round(price * (margin / 100) * 1e4) / 1e4);
}

function parseCost(raw: string, label = 'Evaluated cost'): { ok: true; value: string | null } | { ok: false; message: string } {
  const t = raw.trim().replace(/^SAR\s*/i, '').replace(/,/g, '').replace(/\s/g, '');
  if (!t) return { ok: true, value: null };
  const n = Number(t);
  if (!Number.isFinite(n)) return { ok: false, message: `${label} must be a number.` };
  if (n < 0) return { ok: false, message: `${label} cannot be negative.` };
  return { ok: true, value: String(n) };
}

function marginFromEvaluated(listPrice: string | number, cost: string | null,
  pctLabel = 'Margin Cost (%)', amountLabel = 'Evaluated cost'):
  { ok: true; value: string | null } | { ok: false; message: string } {
  if (cost == null) return { ok: true, value: null };
  const price = Number(listPrice);
  const amount = Number(cost);
  if (!Number.isFinite(price) || price === 0) {
    return amount === 0 ? { ok: true, value: null }
      : { ok: false, message: `Contracted price is 0; cannot back-calculate ${pctLabel}.` };
  }
  if (!Number.isFinite(amount)) return { ok: false, message: `${amountLabel} must be a number.` };
  return parseMargin(String(Math.round(amount / price * 100 * 1e6) / 1e6), pctLabel);
}

/** 0.035000 → 0.035 for the edit field. */
function formatOverheadPctInput(v: string | number | null | undefined): string {
  if (v === null || v === undefined || v === '') return '';
  const s = String(v).trim();
  if (!s) return '';
  return s.replace(/(\.\d*?)0+$/, '$1').replace(/\.$/, '');
}

/** 0.035000 → 0.035%. */
function formatOverheadPct(v: string | number | null | undefined): string {
  const t = formatOverheadPctInput(v);
  return t === '' ? '—' : `${t}%`;
}

function versionFrozen(status?: string | null): boolean {
  return status === 'PUBLISHED' || status === 'RETIRED';
}

function parseMargin(raw: string, label = 'Margin Cost (%)'): { ok: true; value: string | null } | { ok: false; message: string } {
  const t = raw.trim().replace(/%$/, '');
  if (!t) return { ok: true, value: null };
  const n = Number(t);
  if (!Number.isFinite(n)) return { ok: false, message: `${label} must be a number.` };
  if (n < 0 || n > 999.999999) return { ok: false, message: `${label} must be between 0 and 999.999999.` };
  if (n === 0) return { ok: true, value: null };
  return { ok: true, value: String(n) };
}

// ── Runs ────────────────────────────────────────────────────────────────────────

@Component({
  selector: 'v-runs',
  imports: [RouterLink, ...UI],
  template: `
    <v-page title="Calculation runs" purpose="The only way costs are produced: one hospital, one period, one costing version, in observable stages.">
      @if (message()) { <p class="notice">{{ message() }}</p> }
      <section class="card explain">
        <h2>What a calculation run does</h2>
        <ul>
          @for (step of steps(); track step) { <li>{{ step }}</li> }
        </ul>
      </section>
      @if (canRun()) {
        <div class="row run-controls">
          <select [value]="period()" (change)="period.set($any($event.target).value)" aria-label="Period">
            @for (p of periodsForHospital(); track p.code) { <option [value]="p.code" [selected]="p.code === period()">{{ p.code }} ({{ p.startDate }} – {{ p.endDate }})</option> }
          </select>
          <span class="applied">Costing model:
            @if (applied.value(); as a) { <strong>{{ a.code ? a.code + ' - ' + a.status : 'None in effect for this period' }}</strong> }
          </span>
          <button class="btn primary" type="button" (click)="create()" [disabled]="busy() || !applied.value()?.code || !!applied.value()?.existingRun"
            [title]="applied.value()?.existingRun ? 'This period already has a run under this costing model.' : ''">New run</button>
          @if (applied.value()?.existingRun; as existing) {
            <span class="applied">{{ existing }} already exists for this period and costing model. <a [routerLink]="['/runs', existing]" queryParamsHandling="preserve">Open it to re-run</a>.</span>
          }
        </div>
      }
      <v-table [rows]="runs()" [columns]="cols()" caption="Runs" />
    </v-page>
  `,
  styles: `
    .explain { margin-bottom: 14px; }
    .explain h2 { margin: 0 0 8px; }
    .explain ul { margin: 0; padding-left: 20px; }
    .explain li { margin: 4px 0; }
    .run-controls { align-items: center; flex-wrap: wrap; gap: 12px; margin-bottom: 14px; }
    .applied { font-size: 14px; color: var(--text-3); }
    .applied strong { color: var(--text); font-weight: 600; }
  `,
})
export class Runs {
  ctx = inject(RunContext);
  private api = inject(Api);
  private auth = inject(Auth);
  canRun = computed(() => this.auth.can('run.execute'));
  /** Follows the period chosen here: the costing model a new run would be calculated under. */
  applied = apiResource<{ code: string | null; name: string | null; status: string | null; existingRun: string | null }>(
    () => this.canRun() && this.period() && this.ctx.hospitalParam() ? 'runs/applied-version' : null, () => ({ hospital: this.ctx.hospitalParam(), period: this.period() }));
  /** The method follows the hospital: its Direct and Indirect costing, or the ledger-based engine. */
  steps = computed(() => this.ctx.hospitals().find(h => h.code === this.ctx.hospitalParam())?.usesCostingModel ? [
    'Takes one hospital and one period, under the costing model in effect at the end of that period: a published one, or a draft if none is published.',
    'Checks the source data and warns about encounters with no services, and procedures with no Margin Cost % or no contracted price.',
    'Works out direct cost: contracted price × Margin Cost %, using the procedure\'s own %, else its type\'s, else All. A procedure with no % costs SAR 0.',
    'Works out indirect cost: contracted price × Overhead %, using the same order.',
    'Adds encounter level overheads: the % of the encounter\'s contracted value up to its cap, or the fixed amount when there is no %.',
    'Adds revenue and works out the margin for every encounter.',
    'Stores the results for the Dashboard, Cost Centres, Packages and Procedure/Activity screens.',
    'Changes to Direct or Indirect costing reach those results only when a run is executed again.',
  ] : [
    'Takes one hospital and one period, under the costing model in effect at the end of that period: a published one, or a draft if none is published.',
    'Checks the source data, and stops if anything blocks costing.',
    'Finds the resource rates in effect and builds cost pools from the general ledger.',
    'Works out capacity and unit costs, then allocates support and overhead costs to activities.',
    'Costs each procedure at standard, and each encounter from what it actually used.',
    'Rolls costs up to packages and DRGs, and explains the variance from standard.',
    'Reconciles the result back to the general ledger, and stores it for the costing screens.',
  ]);
  runs = computed(() => {
    const selected = this.ctx.hospitalParam();
    return this.ctx.runs().filter(r => !selected || r.hospital === selected);
  });
  period = signal('');
  busy = signal(false);
  message = signal<string | null>(null);
  /** New runs are for the hospital selected in the top bar; the period starts from its selection too. */
  private seed = effect(() => {
    const periods = this.periodsForHospital();
    this.period.set(this.ctx.periodParam() ?? periods.at(-1)?.code ?? '');
  });
  periodsForHospital = computed(() => {
    const h = this.ctx.hospitalParam();
    return (this.ctx.context()?.periods ?? []).filter(p => p.hospital === h);
  });
  cols = computed<Col<RunListItem>[]>(() => {
    const cols: Col<RunListItem>[] = [
      { key: 'code', label: 'Run', link: r => `/runs/${r.code}`, sub: r => r.errorMessage ?? r.name },
      { key: 'hospital', label: 'Hospital' }, { key: 'period', label: 'Period' }, { key: 'costingVersion', label: 'Version' },
      { key: 'status', label: 'Status', type: 'chip', value: r => runLiveStatus(r),
        sub: r => r.currentStep ? (r.currentStep === 'RECONCILE' ? 'GL reconciliation' : r.currentStep.replaceAll('_', ' ')) : runInFlight(r.status) ? 'In progress' : null },
      { key: 'forced', label: 'Forced', value: r => r.forced ? 'Forced — cannot publish' : '' },
      { key: 'durationMs', label: 'ms', type: 'num', dp: 0 }, { key: 'triggeredBy', label: 'Triggered by' },
    ];
    if (this.canRun()) {
      cols.push({
        key: 'delete', label: '', type: 'button', sortable: false,
        button: r => this.canDelete(r) && !this.busy() ? 'Delete' : null,
        onButton: r => void this.remove(r),
      });
    }
    return cols;
  });


  async create(): Promise<void> {
    const h = this.ctx.hospitalParam();
    const p = this.period() || this.periodsForHospital().at(-1)?.code;
    if (!h) return;
    if (!p) { this.message.set('This hospital has no periods yet. Add one under Periods first.'); return; }
    this.busy.set(true);
    try {
      const run = await firstValueFrom(this.api.post<RunListItem>('runs', { hospital: h, period: p }));
      await firstValueFrom(this.api.post(`runs/${run.code}/execute`, { force: false }));
      this.message.set(`${run.code} is running. Status on this list updates as stages complete.`);
      this.applied.reload();
      await this.ctx.load(true);
      if (h && p) this.ctx.selectScope(h, p);
    } catch (e) { this.message.set(toApiError(e).message); }
    finally { this.busy.set(false); }
  }

  canDelete(r: RunListItem): boolean { return runDeletable(r); }

  async remove(r: RunListItem): Promise<void> {
    if (!this.canDelete(r)) return;
    if (!confirm(`Delete ${r.status.toLowerCase()} run ${r.code} and its results? This cannot be undone.`)) return;
    this.busy.set(true);
    try {
      await firstValueFrom(this.api.delete(`runs/${r.code}`));
      this.message.set(`${r.code} deleted.`);
      this.applied.reload();
      await this.ctx.load(true);
    } catch (e) { this.message.set(toApiError(e).message); }
    finally { this.busy.set(false); }
  }
}

@Component({
  selector: 'v-run',
  imports: [...UI],
  template: `
    <v-state [loading]="res.isLoading() && !res.value()" [error]="res.error()">
      @if (res.value(); as r) {
        <v-page [title]="r.data.run.code" [purpose]="r.data.run.name">
          <div actions class="row">
            @if (canRun() && deletable(r.data.run)) {
              <button class="btn" type="button" (click)="remove()" [disabled]="busy()">Delete</button>
            }
            @if (canRun() && r.data.run.status !== 'PUBLISHED' && !inFlight(r.data.run.status)) {
              <button class="btn" type="button" (click)="askRerun(false)" [disabled]="busy()">Re-run</button>
              <button class="btn" type="button" (click)="askRerun(true)" [disabled]="busy()" title="Proceed past ERROR issues; the run can then never be published (FR-9.8)">Re-run with force</button>
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
        @if (rerun(); as w) {
          <div class="rerun-backdrop" (click)="rerun.set(null)" (document:keydown.escape)="rerun.set(null)">
            <div class="rerun-dialog" role="alertdialog" aria-modal="true" aria-labelledby="rerun-title" (click)="$event.stopPropagation()">
              <h2 id="rerun-title">Re-run {{ r.data.run.code }}?</h2>
              <p class="rerun-label">This will:</p>
              <ul>@for (line of w.effects; track line) { <li>{{ line }}</li> }</ul>
              @if (w.force) { <p class="notice warn">Force: it continues past ERROR issues, and a run forced past them can never be published.</p> }
              @if (w.current.length) {
                <p class="rerun-label">Current results, which will be replaced:</p>
                <ul>@for (line of w.current; track line) { <li>{{ line }}</li> }</ul>
              }
              <p class="bad">The old results cannot be restored. If the run fails, this period has no results until it runs successfully.</p>
              <div class="rerun-actions">
                <button class="btn proceed" type="button" (click)="confirmRerun()">Yes, Proceed</button>
                <button class="btn" type="button" (click)="rerun.set(null)">No, Close</button>
              </div>
            </div>
          </div>
        }
      }
    </v-state>
  `,
  styles: `
    .rerun-backdrop { position: fixed; inset: 0; z-index: 1000; display: flex; align-items: center; justify-content: center; padding: 16px; background: rgba(15, 23, 42, .5); }
    .rerun-dialog { width: min(560px, 100%); max-height: calc(100vh - 32px); overflow-y: auto; padding: 22px 24px; border: 1px solid var(--border); border-radius: var(--radius-sm); background: var(--surface-solid); color: var(--text); box-shadow: var(--shadow); }
    .rerun-dialog h2 { margin: 0 0 14px; }
    .rerun-dialog ul { margin: 0 0 14px; padding-left: 20px; }
    .rerun-dialog li { margin-bottom: 4px; }
    .rerun-label { margin: 0 0 6px; font-weight: 600; }
    .rerun-actions { display: flex; justify-content: flex-end; gap: 8px; margin-top: 18px; flex-wrap: wrap; }
    .btn.proceed, .btn.proceed:hover { background: var(--bad); border-color: var(--bad); color: #fff; }
    .btn.proceed:hover { filter: brightness(.9); }
  `,
})
export class RunDetail {
  deletable = runDeletable;
  id = input.required<string>();
  private api = inject(Api);
  private auth = inject(Auth);
  private ctx = inject(RunContext);
  private router = inject(Router);
  res = apiResource<{ meta: unknown; data: RunDetailDto }>(() => `runs/${this.id()}`);
  canRun = computed(() => this.auth.can('run.execute'));
  canRequest = computed(() => this.auth.can('approval.request'));
  canPublish = computed(() => this.auth.can('run.publish'));
  inFlight = runInFlight;
  private watch = effect(() => {
    const id = this.id();
    const row = this.ctx.runs().find(r => r.code === id || r.id === id);
    if (row && runInFlight(row.status)) this.res.reload();
    const detail = this.res.value()?.data.run;
    const hospital = detail?.hospital ?? row?.hospital;
    const period = detail?.period ?? row?.period;
    if (hospital && period && (this.ctx.hospitalParam() !== hospital || this.ctx.periodParam() !== period)) {
      this.ctx.selectScope(hospital, period);
    }
  });
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
  rerun = signal<ReturnType<typeof rerunWarning> | null>(null);
  askRerun(force: boolean): void {
    const run = this.res.value()?.data.run;
    if (run) this.rerun.set(rerunWarning(run, force));
  }
  confirmRerun() {
    const force = this.rerun()?.force ?? false;
    this.rerun.set(null);
    return this.act(() => firstValueFrom(this.api.post(`runs/${this.id()}/execute`, { force })), 'Running. Stages and the run list update as each stage completes.');
  }
  async remove(): Promise<void> {
    const run = this.res.value()?.data.run;
    if (!run || !runDeletable(run)) return;
    if (!confirm(`Delete ${run.status.toLowerCase()} run ${run.code} and its results? This cannot be undone.`)) return;
    this.busy.set(true);
    try {
      await firstValueFrom(this.api.delete(`runs/${this.id()}`));
      await this.ctx.load(true);
      await this.router.navigate(['/runs'], { queryParamsHandling: 'preserve' });
    } catch (e) { this.message.set(toApiError(e).message); }
    finally { this.busy.set(false); }
  }
  request() {
    const runId = this.res.value()!.data.run.id;
    return this.act(() => firstValueFrom(this.api.post('approvals', { entityType: 'CALCULATION_RUN', entityId: runId, note: 'Reconciled; requesting finance approval to publish.' })), 'Approval requested.');
  }
  publish() { return this.act(() => firstValueFrom(this.api.post(`runs/${this.id()}/transition`, { to: 'PUBLISHED' })), 'Published. This run is now immutable.'); }
}

// ── Patient encounters ──────────────────────────────────────────────────────────

interface PatientEncounterRow {
  id: string; encounterNo: string; mrn: string; patient: string; segment: string; encType: string; department: string; payer: string; insurer?: string | null;
  physician?: string | null; admitAt: string; dischargeAt?: string | null; lengthOfStay: number; services: number;
  gross: string | number; discount: string | number; net: string | number; cost?: string | number | null;
}
/** Resource and no-billable type have no dropdown, but the Dashboard's counts still open the list filtered by them. */
const ENCOUNTER_FILTER_KEYS = ['department', 'resource', 'physician', 'insurer', 'encType', 'segment', 'noBillableType'] as const;
type EncounterFilterKey = typeof ENCOUNTER_FILTER_KEYS[number];
interface PatientEncounterPage { rows: PatientEncounterRow[]; total: number; page: number; pageSize: number; phiMasked: boolean; }
interface PatientEncounterLine {
  procedure?: string | null; procedureId?: string | null; quantity?: string | number | null; costCentre?: string | null; physician?: string | null;
  payer?: string | null; performedAt?: string | null; gross?: string | number | null; discount?: string | number | null; net?: string | number | null; sourceRef: string; cost?: string | number | null; procedureCode?: string | null; isPackage?: boolean;
  /** When the line billed a package header, open Packages (same HIS code is excluded from Procedure/Activity). */
  packageId?: string | null;
  /** Refunded on this invoice line, already taken off its net. */
  refunded?: string | number | null;
}
interface HisEncounterSource {
  fields: { label: string; hisColumn: string; value?: string | null }[];
  services: unknown[]; refunds: unknown[]; packageItems: unknown[];
}

/** Values for checking against HIS: no money format, and true/false as HIS bits. A third part ":n" shows 2 decimals, or none when they are 00. */
const isZero = (v: unknown): boolean => /^0+(\.0+)?$/.test(String(v));

function hisCols(spec: string[]): Col<any>[] {
  return spec.map(s => {
    const [key, label, kind] = s.split(':');
    return { key, label, sortable: false, value: (r: any) => {
      const v = r[key];
      if (v === null || v === undefined || isZero(v)) return '';
      if (kind === 'n') { const n = Number(v).toFixed(2); return n.endsWith('.00') ? n.slice(0, -3) : n; }
      return typeof v === 'string' && /^\d{4}-\d{2}-\d{2}T/.test(v) ? v.slice(0, 16).replace('T', ' ') : String(v);
    } };
  });
}

interface PatientEncounterDetailDto { encounter: PatientEncounterRow; lines: PatientEncounterLine[]; }
interface InvoiceGroup { key: string; procedure: string; procedureCode: string | null; isPackage: boolean; procedureId: string | null; packageId: string | null; quantity: number; gross: number; discount: number; refunds: number; net: number; cost: number; }

interface HisInvoiceTypeCount { invoiceType: number | null; name: string; invoices: number; encounters: number; patients: number; refunded: number; revenue: number }

@Component({
  selector: 'v-his-invoicing',
  imports: [...UI],
  template: `
    <v-page title="HIS Invoicing" purpose="Invoice counts and revenue by HIS invoice type for the selected period. Revenue is after refunds.">
      <v-state [loading]="res.isLoading()" [error]="res.error()">
        @if (res.value(); as r) {
          <div class="grid tiles" style="margin-bottom: 16px;">
            <v-tile label="Min invoice no." [value]="r.data.minInvoiceNo" unit="id" />
            <v-tile label="Max invoice no." [value]="r.data.maxInvoiceNo" unit="id" />
          </div>
          <v-table [rows]="r.data.types" [columns]="cols" [total]="r.data.total" caption="Invoice counts by type" empty="No invoices imported for this period." />
        }
      </v-state>
    </v-page>
  `,
})
export class HisInvoicing {
  res = catalogueResource<{ minInvoiceNo: number | null; maxInvoiceNo: number | null; types: HisInvoiceTypeCount[]; total: HisInvoiceTypeCount | null }>(() => 'his-invoicing');
  cols: Col[] = [
    { key: 'invoiceType', label: 'Invoice type' },
    { key: 'name', label: 'Name' },
    { key: 'invoices', label: 'Invoices', type: 'num', dp: 0 },
    { key: 'encounters', label: 'Encounters', type: 'num', dp: 0 },
    { key: 'patients', label: 'Patients', type: 'num', dp: 0 },
    { key: 'refunded', label: 'Refunded', type: 'money', dp: 0, blankZero: true },
    { key: 'revenue', label: 'Revenue', type: 'money', dp: 0 },
  ];
}

@Component({
  selector: 'v-patient-encounters',
  imports: [RouterLink, ...UI],
  template: `
    <v-page title="Patient Encounters" purpose="Patient encounters in the selected period and what was invoiced for each. Patient names show only with phi.view (FR-0.7).">
      @if (billedPackageId()) {
        <p class="muted">Showing encounters billed to this package. <a [routerLink]="['/patient-encounters']" [queryParams]="{ billedPackageId: null }" queryParamsHandling="merge">Show all</a></p>
      }
      @if (resourceName(); as name) { <p class="muted">Showing encounters for resource: {{ name }}.</p> }
      @if (filters().noBillableType; as type) {
        <p class="muted">Showing encounters with non-billable procedures{{ type === 'ALL' ? '' : ' of type: ' + type.charAt(0) + type.slice(1).toLowerCase() }}.</p>
      }
      <section class="card filters">
        @for (f of filterDefs(); track f.key) {
          <label class="filter">{{ f.label }}
            <select [value]="filters()[f.key] ?? ''" (change)="setFilter(f.key, $any($event.target).value)" [attr.aria-label]="f.label">
              <option value="">All</option>
              @for (o of f.options; track o.value) { <option [value]="o.value" [selected]="o.value === filters()[f.key]">{{ o.label }}</option> }
            </select>
          </label>
        }
        <button class="btn clear" type="button" (click)="clearFilters()" [disabled]="!anyFilter()">Clear filters</button>
      </section>
      <div class="list-head">
        <input type="search" placeholder="Encounter number or MRN" [value]="search()" (change)="runSearch($any($event.target).value)" (input)="searchCleared($any($event.target).value)" (paste)="searchPasted($event)" aria-label="Search by encounter number or MRN" style="width:min(16rem,100%)" />
        @if (last(); as r) {
          <div class="row pager">
            <button class="btn" type="button" [disabled]="page() <= 1" (click)="page.set(page() - 1)">Previous</button>
            <span class="muted">Page {{ r.data.page }} of {{ pages() }} · {{ r.data.total }} encounters</span>
            <button class="btn" type="button" [disabled]="page() >= pages()" (click)="page.set(page() + 1)">Next</button>
          </div>
        }
      </div>
      <v-state [loading]="res.isLoading() && !last()" [error]="res.error()">
        @if (last(); as r) {
          <div [class.reloading]="res.isLoading()">
            <v-table [rows]="r.data.rows" [columns]="cols()" [serverSort]="tableSort()" (sortChange)="sortFromHeader($event)" caption="Patient encounters" empty="No encounters in this period." />
          </div>
        }
      </v-state>
    </v-page>
  `,
  styles: `
    .filters { display: grid; grid-template-columns: repeat(auto-fill, minmax(180px, 1fr)); gap: 10px 12px; align-items: end; margin-bottom: 14px; }
    .filter { display: flex; flex-direction: column; gap: 5px; min-width: 0; font-size: 12px; font-weight: 600; letter-spacing: .04em; text-transform: uppercase; color: var(--text-3); }
    .filter select { width: 100%; font: 14px var(--font); text-transform: none; letter-spacing: normal; font-weight: 400; color: var(--text); padding: 6px 8px; border: 1px solid var(--border); border-radius: var(--radius-sm); background: var(--surface); }
    .clear { justify-self: start; }
    .list-head { display: flex; flex-wrap: wrap; align-items: center; justify-content: space-between; gap: 8px 12px; margin-bottom: 10px; }
    .list-head p { margin: 0; }
    .pager { margin-left: auto; }
    .reloading { opacity: .6; transition: opacity .15s; }
  `,
})
export class PatientEncounters {
  private route = inject(ActivatedRoute);
  private router = inject(Router);
  private auth = inject(Auth);
  private ctx = inject(RunContext);
  page = signal(1);
  search = signal('');
  /** The same breakdowns as the Dashboard's tabs. */
  filters = signal<Partial<Record<EncounterFilterKey, string>>>(this.filtersIn(this.route.snapshot.queryParamMap));
  options = catalogueResource<Record<string, { value: string; label: string }[]>>(() => 'patient-encounters/filters');
  filterDefs = computed(() => {
    const o = this.options.value()?.data;
    const defs: { key: EncounterFilterKey; label: string; options: { value: string; label: string }[] }[] = [
      { key: 'department', label: 'Department', options: o?.['departments'] ?? [] }, { key: 'physician', label: 'Physician', options: o?.['physicians'] ?? [] },
      { key: 'insurer', label: 'Insurer', options: o?.['insurers'] ?? [] },
      { key: 'encType', label: 'Encounter type', options: o?.['encounterTypes'] ?? [] }, { key: 'segment', label: 'Patient segment', options: o?.['segments'] ?? [] },
    ];
    // A filter from a link shows as selected straight away, even before or without its choice in the list.
    const chosen = this.filters();
    return defs.map(d => {
      const value = chosen[d.key];
      if (!value || d.options.some(x => x.value === value)) return d;
      const label = !o ? 'Loading…' : value === 'none' ? 'Not assigned' : 'No encounters';
      return { ...d, options: [{ value, label }, ...d.options] };
    });
  });
  /** The resource a Dashboard count opened this list for. */
  resourceName = computed(() => {
    const id = this.filters().resource;
    if (!id) return null;
    return this.options.value()?.data['resources']?.find(x => x.value === id)?.label ?? (id === 'none' ? 'Not assigned' : 'selected resource');
  });
  anyFilter = computed(() => Object.values(this.filters()).some(v => !!v));
  setFilter(key: EncounterFilterKey, value: string): void {
    this.showFilters({ [key]: value || null });
  }
  clearFilters(): void {
    this.showFilters(Object.fromEntries(ENCOUNTER_FILTER_KEYS.map(k => [k, null])));
  }
  /** Filters live in the URL, so a link can open the list already filtered. */
  private showFilters(queryParams: Params): void {
    void this.router.navigate([], { relativeTo: this.route, queryParams, queryParamsHandling: 'merge', replaceUrl: true });
  }
  private filtersIn(q: ParamMap): Partial<Record<EncounterFilterKey, string>> {
    return Object.fromEntries(ENCOUNTER_FILTER_KEYS.filter(k => q.get(k)).map(k => [k, q.get(k)!]));
  }
  private scope: string | null = null;
  private scopeChanged = effect(() => {
    const scope = `${this.ctx.hospitalParam()}|${this.ctx.periodParam()}`;
    const changed = this.scope !== null && this.scope !== scope;
    this.scope = scope;
    if (changed) untracked(() => this.clearFilters());
  });
  sort = signal('admitAt');
  desc = signal(true);
  billedPackageId = signal(this.route.snapshot.queryParamMap.get('billedPackageId'));
  constructor() {
    this.route.queryParamMap.pipe(takeUntilDestroyed()).subscribe(q => {
      const next = this.filtersIn(q);
      if (JSON.stringify(next) !== JSON.stringify(this.filters())) {
        this.filters.set(next);
        this.page.set(1);
      }
      const id = q.get('billedPackageId');
      if (id === this.billedPackageId()) return;
      this.page.set(1);
      this.billedPackageId.set(id);
    });
  }
  res = catalogueResource<PatientEncounterPage>(() => 'patient-encounters', () => ({
    page: this.page(), pageSize: 20, search: this.search(), sort: this.sort(), desc: this.desc(),
    billedPackageId: this.billedPackageId(), ...this.filters(),
  }));
  /** The last page loaded: kept on screen while the next page, search, sort or filter loads, so only the table dims. */
  last = linkedSignal<Envelope<PatientEncounterPage> | undefined, Envelope<PatientEncounterPage> | undefined>({
    source: this.res.value,
    computation: (next, prev) => next ?? (this.res.error() ? undefined : prev?.value),
  });
  pages = computed(() => Math.max(1, Math.ceil((this.last()?.data.total ?? 0) / 20)));
  canRevenue = computed(() => this.auth.can('revenue.view'));
  /** The list is paged on the server, so headers ask the server to sort all of it. */
  tableSort = computed(() => ({ key: this.sort(), desc: this.desc() }));
  runSearch(value: string): void {
    this.search.set(value);
    this.page.set(1);
  }
  /** A paste searches straight away; the box only holds the pasted text on the next tick. */
  /** Emptying the box (typing it away or its clear button) drops the search at once, without waiting for Enter or blur. */
  searchCleared(value: string): void {
    if (!value.trim() && this.search()) this.runSearch('');
  }
  searchPasted(event: Event): void {
    const box = event.target as HTMLInputElement;
    setTimeout(() => this.runSearch(box.value));
  }
  sortFromHeader(next: { key: string; desc: boolean }): void {
    this.sort.set(next.key);
    this.desc.set(next.desc);
    this.page.set(1);
  }
  cols = computed<Col<PatientEncounterRow>[]>(() => {
    const cols: Col<PatientEncounterRow>[] = [
      { key: 'admitAt', label: 'Date', type: 'date' },
      { key: 'encounterNo', label: 'Encounter', link: r => `/patient-encounters/${r.id}`, sub: this.auth.can('phi.view') ? r => r.patient : undefined },
      { key: 'mrn', label: 'MRN' }, { key: 'encType', label: 'Type' },
      { key: 'services', label: 'Services', type: 'num', dp: 0, blankZero: true },
    ];
    if (this.canRevenue()) cols.push(
      { key: 'gross', label: 'Gross', type: 'money', blankZero: true }, { key: 'discount', label: 'Discount', type: 'money', blankZero: true },
      { key: 'net', label: 'Net Invoice', type: 'money', blankZero: true },
    );
    cols.push({ key: 'cost', label: 'Cost', type: 'money', blankZero: true, bad: r => this.canRevenue() && Number(r.cost ?? 0) > Number(r.net) });
    return cols;
  });
}

@Component({
  selector: 'v-encounter',
  imports: [...UI],
  template: `
    <v-state [loading]="res.isLoading()" [error]="res.error()">
      @if (res.value(); as r) {
        <v-page [title]="'Encounter ' + r.data.encounter.encounterNo" [purpose]="(canPhi() ? r.data.encounter.patient + ' · ' : '') + 'MRN ' + r.data.encounter.mrn + ' · ' + r.data.encounter.department + invoiceText(r.data.lines)">
          <div class="grid tiles">
            <v-tile label="Services" [value]="r.data.encounter.services" unit="" blankZero />
            @if (canRevenue()) {
              @let t = totals(r.data.lines);
              <v-tile label="Total Amount" [value]="t.gross" blankZero />
              <v-tile label="Discount" [value]="t.discount" blankZero />
              <v-tile label="Net invoiced" [value]="t.net" blankZero />
            }
            <v-tile label="Cost" [value]="r.data.encounter.cost ?? 0" blankZero />
          </div>
          <section class="card" style="margin-top:14px"><h2>Invoiced procedures ({{ groups(r.data.lines).length }})</h2>
            <v-table [rows]="groups(r.data.lines)" [columns]="groupCols()" caption="Invoiced procedures" empty="Nothing was invoiced on this encounter." />
          </section>
          <section class="card his-source" style="margin-top:14px">
            <h2>HIS source</h2>
            <p class="muted">The values loaded from HIS for this encounter, under the HIS table and column each came from, for checking against HIS.</p>
            <v-state [loading]="his.isLoading()" [error]="his.error()">
              @if (his.value(); as h) {
                <v-table [rows]="h.data.fields" [columns]="hisFieldCols" caption="Encounter fields" />
                @for (t of hisTables(h.data); track t.title) {
                  <details style="margin-top:12px">
                    <summary><strong>{{ t.title }} ({{ t.rows.length }})</strong> <span class="muted">{{ t.source }}</span></summary>
                    <div style="margin-top:8px"><v-table [rows]="t.rows" [columns]="t.cols" [caption]="t.title" empty="None loaded." /></div>
                  </details>
                }
              }
            </v-state>
          </section>
        </v-page>
      }
    </v-state>
  `,
})
export class EncounterDetail {
  id = input.required<string>();
  private auth = inject(Auth);
  canRevenue = computed(() => this.auth.can('revenue.view'));
  canPhi = computed(() => this.auth.can('phi.view'));
  res = catalogueResource<PatientEncounterDetailDto>(() => `patient-encounters/${this.id()}`);
  his = catalogueResource<HisEncounterSource>(() => `patient-encounters/${this.id()}/his-source`);
  hisFieldCols: Col<HisEncounterSource['fields'][number]>[] = [
    { key: 'label', label: 'Field', sortable: false }, { key: 'hisColumn', label: 'HIS table and column', sortable: false }, { key: 'value', label: 'Value', sortable: false, value: r => isZero(r.value) ? '' : r.value ?? '' },
  ];
  hisTables(h: HisEncounterSource): { title: string; source: string; rows: any[]; cols: Col<any>[] }[] {
    const tables = [
      { title: 'Service lines', source: 'HIS_InvoiceDetail. Child invoice lines are for reference: the parent line carries the money and cost.', rows: h.services, cols: hisCols(['transactionType:TransactionType', 'invoiceNo:InvoiceNo', 'lineItemNo:LineItemNo', 'procedureId:ProcedureID', 'costCenterId:CostCenterID', 'primaryDoctorId:PrimaryDoctorID', 'quantity:Quantity:n', 'performDate:PerformDate', ...(this.canRevenue() ? ['price:Price:n', 'discountAmount:DiscountAmount:n', 'patientShare:PatientShare:n', 'companyShare:CompanyShare:n'] : [])]) },
    ];
    if (!this.canRevenue()) return tables;
    return [
      ...tables,
      { title: 'Refunds', source: 'HIS_RefundMain + HIS_RefundDetail, cancelled ones included', rows: h.refunds, cols: hisCols(['refundNo:RefundNo', 'invoiceLineItemNo:InvoiceLineItemNo', 'refundType:RefundType', 'refundDate:RefundDate', 'originalTransactionType:OriginalTransactionType', 'originalTransactionRef:OriginalTransactionRef', 'originalTransactionDate:OriginalTransactionDate', 'invoiceCancelledOn:Invoice CancelledOn', 'procedureId:ProcedureID', 'refundAmount:RefundAmount', 'patientShare:PatientShare', 'companyShare:CompanyShare', 'patientPaid:PatientPaid', 'taxAmount:TaxAmount', 'headerRefundAmount:Main RefundAmount', 'headerAmountPaid:Main AmountPaid', 'status:Status', 'approvalStatus:ApprovalStatus', 'approvedDate:ApprovedDate', 'cancelledOn:CancelledOn', 'reasonId:ReasonID']) },
      { title: 'Package items', source: 'HIS_InvoicePackageItem', rows: h.packageItems, cols: hisCols(['transactionType:TransactionType', 'invoiceNo:InvoiceNo', 'lineItemNo:LineItemNo', 'procedureId:ProcedureID', 'procedurePackageId:ProcedurePackageID', 'packageLineItemNo:ProcedurePackageLineItemNo', 'packageSequence:ProcedurePackageSequence', 'invoiceDate:InvoiceDate', 'price:Price', 'discountedCharge:DiscountedCharge', 'patientShare:PatientShare', 'patientPaid:PatientPaid', 'companyShare:CompanyShare', 'companyPaid:CompanyPaid', 'childInvoiceNo:ChildInvoiceNo', 'childInvoiceLineItemNo:ChildInvoiceLineItemNo', 'isCompleted:IsCompleted', 'isRefunded:IsRefunded']) },
    ];
  }
  private moneyCols = (): Col<any>[] => this.canRevenue() ? [
    { key: 'gross', label: 'Contracted price', type: 'money', blankZero: true }, { key: 'discount', label: 'Discount', type: 'money', blankZero: true },
    { key: 'refunds', label: 'Refund', type: 'money', blankZero: true }, { key: 'net', label: 'Net Invoiced', type: 'money', blankZero: true },
  ] : [];
  groupCols = computed<Col<InvoiceGroup>[]>(() => [
    { key: 'procedureCode', label: 'Procedure code', value: r => r.procedureCode ? hisProcedureId(r.procedureCode) : '—',
      link: r => r.packageId ? `/packages/${r.packageId}` : r.procedureId ? `/procedures/${r.procedureId}` : null },
    { key: 'procedure', label: 'Procedure', link: r => r.packageId ? `/packages/${r.packageId}` : r.procedureId ? `/procedures/${r.procedureId}` : null },
    { key: 'isPackage', label: 'Package', value: r => r.isPackage ? '✓' : '' },
    { key: 'quantity', label: 'Qty', type: 'num', dp: 0, blankZero: true },
    ...this.moneyCols(),
    { key: 'cost', label: 'Cost', type: 'money', blankZero: true, bad: r => this.canRevenue() && r.cost > r.net },
  ]);
  /** The invoices behind this encounter's lines, as INV-2-7-1 and REV-2-7-1 both hold invoice 7. */
  invoiceText(lines: PatientEncounterLine[]): string {
    const invoices = [...new Set(lines.map(l => /^(?:INV|REV)-\d+-(\d+)-\d+$/i.exec(l.sourceRef)?.[1]).filter(Boolean))].sort((a, b) => Number(a) - Number(b));
    if (!invoices.length) return '';
    return ` · ${invoices.length > 1 ? 'Invoices' : 'Invoice'} ${invoices.join(', ')}`;
  }
  /** The tiles add up the same rows as the table, so the two always agree. */
  totals(lines: PatientEncounterLine[]): { gross: number; discount: number; refunds: number; net: number } {
    const dp4 = (n: number) => Math.round(n * 10000) / 10000;
    const all = this.groups(lines);
    const sum = (f: (g: InvoiceGroup) => number) => dp4(all.reduce((total, g) => total + f(g), 0));
    return { gross: sum(g => g.gross), discount: sum(g => g.discount), refunds: sum(g => g.refunds), net: sum(g => g.net) };
  }
  groups(lines: PatientEncounterLine[]): InvoiceGroup[] {
    const map = new Map<string, InvoiceGroup>();
    for (const l of lines) {
      const key = l.procedureId ?? l.procedure ?? l.sourceRef;
      const g = map.get(key) ?? { key, procedure: l.procedure ?? 'Not linked to a procedure', procedureCode: l.procedureCode ?? null, isPackage: false, procedureId: l.procedureId ?? null, packageId: l.packageId ?? null, quantity: 0, gross: 0, discount: 0, refunds: 0, net: 0, cost: 0 };
      g.quantity += Number(l.quantity ?? 0);
      g.isPackage ||= !!l.isPackage;
      if (l.packageId) g.packageId = l.packageId;
      // Refunds are negative rows of their own, shown apart from what was charged.
      if (l.sourceRef.toUpperCase().startsWith('REF-')) g.refunds += Number(l.gross ?? 0);
      else g.gross += Number(l.gross ?? 0);
      g.refunds -= Number(l.refunded ?? 0);
      g.discount += Number(l.discount ?? 0);
      g.net += Number(l.net ?? 0);
      g.cost += Number(l.cost ?? 0);
      map.set(key, g);
    }
    // Sums of 4dp amounts as numbers: without this, a charge and its refund can leave a residue instead of zero.
    const dp4 = (n: number) => Math.round(n * 10000) / 10000;
    for (const g of map.values()) { g.gross = dp4(g.gross); g.discount = dp4(g.discount); g.refunds = dp4(g.refunds); g.net = dp4(g.net); g.cost = dp4(g.cost); }
    return [...map.values()].sort((a, b) => b.net - a.net);
  }
}
