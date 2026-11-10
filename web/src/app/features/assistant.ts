import { Component, DestroyRef, ElementRef, computed, effect, inject, signal, viewChild } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { FormsModule } from '@angular/forms';
import { ActivatedRoute, Router, RouterLink } from '@angular/router';
import { firstValueFrom } from 'rxjs';
import {
  Api, AssistantAskBody, AssistantAskResult, AssistantAssumption, AssistantLink, AssistantMessage, AssistantOption,
  AssistantThread, AssistantThreadDetail,
} from '../core/api';
import { RunContext } from '../core/context';
import { Icon } from '../shared/icon';

const LAST_THREAD = 'verita.assistant.thread';
const COLLAPSED = 'verita.assistant.threadsCollapsed';
function readCollapsed(): boolean {
  try { return localStorage.getItem(COLLAPSED) === '1'; } catch { return false; }
}
/** Numbers, money, percentages and signed changes, with an optional "(±x%)" after them. */
const NUMERIC = /^[+−-]?(?:[A-Z]{3}\s)?[+−-]?[\d,]+(?:\.\d+)?%?(?:\s+[A-Za-z]+)?(?:\s*\([+−-]?[\d,.]+%\))?$/;

function remember(id: string | null): void {
  try { id ? localStorage.setItem(LAST_THREAD, id) : localStorage.removeItem(LAST_THREAD); } catch { /* storage may be blocked */ }
}
function recall(): string | null {
  try { return localStorage.getItem(LAST_THREAD); } catch { return null; }
}

/**
 * Ask VERITA: the rule-based assistant (no model, no inference) as its own screen. Conversations
 * are listed on the left; the open one is in the URL (?thread=). Answers come from the same read
 * queries as the screens, list what they assumed, and use the context bar's hospital and period
 * unless the question names others.
 */
@Component({
  selector: 'v-assistant',
  imports: [FormsModule, RouterLink, Icon],
  template: `
    <div class="screen" [class.list-open]="listOpen()" [class.collapsed]="collapsed()">
      <aside class="threads" aria-label="Conversations">
        <div class="side-head">
          <button class="icon-btn collapse" type="button" (click)="toggleCollapsed()" aria-label="Collapse conversations" title="Collapse conversations"><v-icon name="panel-left" [size]="16" /></button>
          <strong class="grow">Conversations</strong>
          <button class="new" type="button" (click)="newThread()"><v-icon name="plus" [size]="16" /> New</button>
        </div>
        <input class="search" type="search" [ngModel]="filterSig()" (ngModelChange)="filterSig.set($event)" placeholder="Search conversations" aria-label="Search conversations" />
        <div class="list">
          @for (t of filteredThreads(); track t.id) {
            <div class="thread" [class.current]="t.id === threadId()">
              @if (renaming() === t.id) {
                <input class="rename" [(ngModel)]="renameText" (keydown.enter)="saveRename(t)" (keydown.escape)="renaming.set(null)" (blur)="saveRename(t)" aria-label="Conversation title" />
              } @else {
                <button type="button" class="open" (click)="openThread(t.id)" (dblclick)="startRename(t)" [attr.aria-current]="t.id === threadId() ? 'true' : null">
                  <span class="t">@if (t.isPinned) { <v-icon name="pin" [size]="13" /> }<span class="tt">{{ t.title }}</span></span>
                  <small>{{ ago(t.updatedAt) }}@if (t.messages) { · {{ questions(t) }} }</small>
                </button>
              }
              <div class="acts">
                <button type="button" class="mini" (click)="pin(t)" [attr.aria-label]="t.isPinned ? 'Unpin' : 'Pin'" [title]="t.isPinned ? 'Unpin' : 'Pin'"><v-icon name="pin" [size]="14" /></button>
                <button type="button" class="mini" (click)="startRename(t)" aria-label="Rename" title="Rename"><v-icon name="data-entry" [size]="14" /></button>
                <button type="button" class="mini" (click)="archive(t)" [attr.aria-label]="t.isArchived ? 'Unarchive' : 'Archive'" [title]="t.isArchived ? 'Unarchive' : 'Archive'"><v-icon name="archive" [size]="14" /></button>
                <button type="button" class="mini danger" (click)="remove(t)" aria-label="Delete" title="Delete"><v-icon name="trash" [size]="14" /></button>
              </div>
            </div>
          } @empty {
            <p class="faint pad">{{ filterSig() ? 'No conversation matches.' : archived() ? 'Nothing archived.' : 'No conversations yet. Ask something to start one.' }}</p>
          }
        </div>
        <button type="button" class="link-btn" (click)="toggleArchived()"><v-icon name="archive" [size]="14" /> {{ archived() ? 'Back to conversations' : 'Archived' }}</button>
      </aside>

      <section class="chat" aria-label="Conversation">
        <header class="chat-head">
          <button class="icon-btn list-toggle" type="button" (click)="listOpen.set(!listOpen())" aria-label="Conversations" title="Conversations"><v-icon name="list" /></button>
          @if (collapsed()) {
            <button class="icon-btn expand" type="button" (click)="toggleCollapsed()" aria-label="Show conversations" title="Show conversations"><v-icon name="panel-left" [size]="16" /></button>
            <button class="icon-btn expand" type="button" (click)="newThread()" aria-label="New conversation" title="New conversation"><v-icon name="plus" [size]="16" /></button>
          }
          <div class="title">
            <h1>{{ thread()?.title ?? 'Ask VERITA' }}</h1>
          </div>
        </header>

        <div class="log" aria-live="polite">
          <div class="col">
            @if (messages().length === 0 && !busy()) {
              <div class="intro">
                <h2>What would you like to know?</h2>
              </div>
            }
            @for (m of messages(); track m.id) {
              @if (m.role === 'user') {
                <div class="msg user"><div class="bubble">{{ m.text }}</div></div>
              } @else {
                <div class="msg bot" [class.hidden]="m.hidden">
                  @if (m.reply; as r) {
                    @if (r.band === 'confirm') { <div class="band warn">Check the assumptions below: I'm not fully sure I understood.</div> }
                    <p class="text">{{ streaming()?.id === m.id ? streaming()!.shown : r.text }}@if (streaming()?.id === m.id) { <span class="caret" aria-hidden="true"></span> }</p>
                    @if (streaming()?.id !== m.id) {
                      @if (r.assumptions.length) {
                        <div class="assume" aria-label="What this answer assumed">
                          @for (a of r.assumptions; track a.kind) {
                            <span class="as" [class]="'as src-' + a.source" [class.changed]="a.changed" [title]="assumptionTitle(a)">
                              <span class="k">{{ a.label }}</span> {{ a.value }}
                              @if (a.removable && isLast(m)) {
                                <button type="button" class="x" (click)="removeAssumption(a)" [attr.aria-label]="'Ask again without ' + a.label">×</button>
                              }
                            </span>
                          }
                        </div>
                      }
                      @for (b of r.blocks; track $index) {
                        @if (b.type === 'steps') {
                          @if (b.title) { <div class="btitle">{{ b.title }}</div> }
                          <ol class="steps">@for (row of b.rows; track $index) { <li>{{ row[0] }}</li> }</ol>
                        } @else if (b.type === 'list') {
                          @if (b.title) { <div class="btitle">{{ b.title }}</div> }
                          <ul class="steps check">@for (row of b.rows; track $index) { <li>{{ row[0] }}</li> }</ul>
                        } @else if (b.type === 'compare' && b.rows.length) {
                          @let flip = isSwapped(m.id, $index);
                          <div class="cmp-head">
                            <span class="btitle">{{ b.title }}</span>
                            <button type="button" class="swap" (click)="toggleSwap(m.id, $index)" [attr.aria-pressed]="flip"
                                    [title]="'Show ' + (flip ? b.columns[1] : b.columns[2]) + ' first'">
                              <v-icon name="swap" [size]="14" /> Switch
                            </button>
                          </div>
                          <div class="tbl"><table>
                            <thead><tr><th>{{ b.columns[0] }}</th><th class="num">{{ flip ? b.columns[2] : b.columns[1] }}</th>
                              <th class="num">{{ flip ? b.columns[1] : b.columns[2] }}</th><th class="num">{{ b.columns[3] }}</th></tr></thead>
                            <tbody>@for (row of b.rows; track $index) {
                              <tr><td>{{ row[0] }}</td><td class="num">{{ flip ? row[2] : row[1] }}</td><td class="num">{{ flip ? row[1] : row[2] }}</td>
                                <td class="num" [class.up]="(flip ? row[4] : row[3]).startsWith('+')" [class.down]="(flip ? row[4] : row[3]).startsWith('−')">{{ flip ? row[4] : row[3] }}</td></tr>
                            }</tbody>
                          </table></div>
                        } @else if (b.type === 'figures') {
                          <dl class="figs">@for (row of b.rows; track $index) { <div><dt>{{ row[0] }}</dt><dd>{{ row[1] }}</dd></div> }</dl>
                        } @else if (b.rows.length) {
                          @if (b.title) { <div class="btitle">{{ b.title }}</div> }
                          <div class="tbl"><table>
                            @let nums = numericColumns(b.rows);
                            <thead><tr>@for (c of b.columns; track $index) { <th [class.num]="nums[$index]">{{ c }}</th> }</tr></thead>
                            <tbody>@for (row of b.rows; track $index) { <tr>@for (cell of row; track $index) { <td [class.num]="nums[$index]">{{ cell }}</td> }</tr> }</tbody>
                          </table></div>
                        }
                        @if (b.note) { <div class="faint small">{{ b.note }}</div> }
                      }
                      @for (n of r.notes; track $index) { <div class="note">{{ n }}</div> }
                      @if (r.options.length) {
                        <div class="chips">@for (o of r.options; track $index) { <button type="button" class="chip-btn opt" (click)="choose(o)">{{ o.label }}</button> }</div>
                      }
                      @if (r.links.length) {
                        <div class="links">@for (l of r.links; track $index) { <a [routerLink]="['/', l.path]" [queryParams]="withScope(l)" target="_blank" rel="noopener" [title]="l.label + ' (opens in a new tab)'">{{ l.label }} ↗</a> }</div>
                      }
                      @if (isLast(m) && r.suggestions.length) {
                        <div class="chips">@for (s of r.suggestions; track $index) { <button type="button" class="chip-btn" (click)="choose(s)">{{ s.label }}</button> }</div>
                      }
                      <div class="foot">
                        @if (r.run) { <span class="faint">{{ r.run }} · {{ r.runStatus }}</span> }
                        @if (m.stale) {
                          <span class="stale">A newer run exists</span>
                          <button type="button" class="mini" (click)="refresh(m)" title="Answer again from the current run" aria-label="Refresh this answer"><v-icon name="refresh" [size]="14" /></button>
                        }
                        <span class="spacer"></span>
                        @if (r.intent) {
                          <button type="button" class="conf" (click)="toggleWhy(m.id)" [attr.aria-expanded]="why() === m.id">{{ pct(r.confidence) }} sure</button>
                        }
                        <button type="button" class="mini" [class.on]="m.feedback === 1" (click)="rate(m, 1)" aria-label="Helpful" title="Helpful"><v-icon name="thumb-up" [size]="14" /></button>
                        <button type="button" class="mini" [class.on]="m.feedback === -1" (click)="rate(m, -1)" aria-label="Not helpful" title="Not helpful"><v-icon name="thumb-down" [size]="14" /></button>
                      </div>
                      @if (why() === m.id) {
                        <div class="why">
                          Understood as <strong>{{ r.intentLabel }}</strong> ({{ r.turnType?.toLowerCase() }}).
                          Intent {{ pct(r.score.intent) }} · names {{ pct(r.score.entities) }} · words used {{ pct(r.score.coverage) }}@if (r.score.penalty > 0) { · −{{ pct(r.score.penalty) }} }.
                          @for (x of r.score.reasons; track $index) { <div>· {{ x }}</div> }
                        </div>
                      }
                    }
                  } @else {
                    <p class="text faint">{{ m.text }}</p>
                  }
                </div>
              }
            }
            @if (busy()) { <div class="msg bot"><p class="text faint typing">Looking it up…</p></div> }
            @if (error()) { <div class="note bad">{{ error() }}</div> }
          </div>
        </div>

        <form class="ask" (submit)="$event.preventDefault(); send(input)">
          <div class="col row">
            <textarea #box rows="1" [(ngModel)]="input" name="q" maxlength="1000" placeholder="Ask about costs, margins, variances, runs…" aria-label="Your question"
              (keydown.enter)="enter($event)"></textarea>
            <button class="send" type="submit" [disabled]="busy() || !!streaming() || !input.trim()" aria-label="Send"><v-icon name="send" [size]="17" /></button>
          </div>
        </form>
      </section>
      <div class="scrim" (click)="listOpen.set(false)" aria-hidden="true"></div>
    </div>
  `,
  styles: `
    :host { display: block; margin: -26px -28px -40px; }
    /* The page scrolls; the conversation list, the chat header and the question box stay in place. --top is the context bar's height. */
    .screen { display: grid; grid-template-columns: 290px 1fr; min-height: calc(100vh - var(--top, 61px)); position: relative; }
    .threads { display: flex; flex-direction: column; gap: 8px; padding: 14px 10px 10px; border-right: 1px solid var(--border); background: var(--surface);
               position: sticky; top: var(--top, 61px); height: calc(100vh - var(--top, 61px)); align-self: start; min-height: 0; }
    .side-head { display: flex; align-items: center; gap: 8px; padding: 0 4px 0 0; }
    .side-head .grow { flex: 1; }
    .side-head .collapse { width: 30px; height: 30px; }
    .screen.collapsed { grid-template-columns: 1fr; }
    .screen.collapsed .threads { display: none; }
    .new { display: inline-flex; align-items: center; gap: 4px; font: inherit; font-size: 13px; font-weight: 600; padding: 6px 10px; border-radius: 10px;
           border: 0; background: var(--accent); color: #fff; cursor: pointer; }
    .search { border-radius: 10px; padding: 7px 10px; }
    .list { flex: 1; overflow-y: auto; min-height: 0; }
    .thread { display: flex; align-items: center; gap: 2px; border-radius: 10px; padding: 1px 4px; }
    .thread:hover { background: var(--surface-2); }
    .thread.current { background: var(--accent-soft); }
    .thread .open { flex: 1; min-width: 0; text-align: left; border: 0; background: none; padding: 7px 6px; cursor: pointer; color: var(--text); font: inherit; display: flex; flex-direction: column; gap: 2px; }
    .thread .t { display: flex; gap: 4px; align-items: center; font-weight: 600; font-size: 13.5px; min-width: 0; }
    .thread .tt { white-space: nowrap; overflow: hidden; text-overflow: ellipsis; }
    .thread small { color: var(--text-3); font-size: 11.5px; }
    .thread .acts { display: none; }
    .thread:hover .acts, .thread .acts:focus-within, .thread.current .acts { display: flex; }
    .rename { flex: 1; margin: 4px; border-radius: 8px; padding: 6px 8px; }
    .link-btn { display: inline-flex; align-items: center; gap: 6px; border: 0; background: none; color: var(--text-2); cursor: pointer; font: inherit; font-size: 12.5px; padding: 6px; }
    .link-btn:hover { color: var(--accent-strong); }
    .chat { display: flex; flex-direction: column; min-width: 0; }
    .chat-head { display: flex; align-items: center; gap: 10px; padding: 14px 24px; border-bottom: 1px solid var(--border);
                 position: sticky; top: var(--top, 61px); z-index: 3; background: var(--surface); backdrop-filter: blur(18px); -webkit-backdrop-filter: blur(18px); }
    .chat-head h1 { margin: 0; font-size: 18px; white-space: nowrap; overflow: hidden; text-overflow: ellipsis; }
    .chat-head small { color: var(--text-3); font-size: 12px; }
    .title { min-width: 0; display: flex; flex-direction: column; }
    .icon-btn { width: 36px; height: 36px; flex: none; display: grid; place-items: center; border-radius: 10px; border: 1px solid var(--border); background: var(--surface-solid); color: var(--text-2); cursor: pointer; }
    .list-toggle, .scrim { display: none; }
    .log { flex: 1; padding: 20px 24px; }
    .col { max-width: 880px; margin: 0 auto; display: flex; flex-direction: column; gap: 14px; }
    .intro { text-align: center; padding: 48px 12px 12px; }
    .intro h2 { margin: 0; font-size: 22px; }
    .msg.user { display: flex; justify-content: flex-end; }
    .msg.user .bubble { max-width: 75%; background: var(--accent); color: #fff; padding: 9px 14px; border-radius: 16px 16px 4px 16px; white-space: pre-wrap; overflow-wrap: anywhere; }
    .msg.bot { background: var(--surface-solid); border: 1px solid var(--border); border-radius: 4px 16px 16px 16px; padding: 12px 14px; min-width: 0; box-shadow: var(--shadow-sm); }
    .msg.bot.hidden { opacity: .7; }
    .text { margin: 0 0 10px; line-height: 1.55; overflow-wrap: anywhere; }
    .band { font-size: 12px; padding: 4px 8px; border-radius: 8px; margin-bottom: 8px; }
    .band.warn { background: var(--warn-soft); color: var(--warn); }
    .assume { display: flex; flex-wrap: wrap; align-items: flex-start; gap: 6px; margin: 0 0 10px; }
    .as { flex: 0 1 auto; max-width: 100%; display: inline-flex; align-items: baseline; gap: 4px; font-size: 12px; line-height: 1.4; padding: 3px 9px;
          border-radius: 12px; border: 1px solid var(--border); background: var(--surface-2); color: var(--text); overflow-wrap: anywhere; }
    .as .k { flex: none; }
    .as .k { color: var(--text-3); white-space: nowrap; }
    /* Prefixed: a bare "screen" class would pick up the page layout's .screen rule. */
    .as.src-default, .as.src-screen { border-style: dashed; }
    .as.changed { border-color: var(--accent); background: var(--accent-soft); }
    .as .x { border: 0; background: none; color: var(--text-3); cursor: pointer; padding: 0 0 0 2px; font-size: 14px; line-height: 1; }
    .as .x:hover { color: var(--bad); }
    .figs { display: grid; grid-template-columns: repeat(auto-fill, minmax(150px, 1fr)); gap: 8px; margin: 0 0 10px; }
    .figs div { background: var(--surface-2); border: 1px solid var(--border); border-radius: 10px; padding: 7px 10px; }
    .figs dt { font-size: 11.5px; color: var(--text-3); }
    .figs dd { margin: 0; font-weight: 600; font-variant-numeric: tabular-nums; overflow-wrap: anywhere; }
    .btitle { font-size: 12.5px; font-weight: 600; color: var(--text-2); margin: 4px 0; }
    .cmp-head { display: flex; align-items: center; justify-content: space-between; gap: 8px; }
    .swap { display: inline-flex; align-items: center; gap: 5px; font: inherit; font-size: 12px; padding: 3px 10px; border-radius: 8px;
            border: 1px solid var(--border-strong); background: var(--surface-solid); color: var(--text-2); cursor: pointer; margin-bottom: 4px; }
    .swap:hover { color: var(--accent-strong); border-color: var(--accent); }
    .swap[aria-pressed="true"] { background: var(--accent-soft); color: var(--accent-strong); border-color: var(--accent); }
    th.num { text-align: right; }
    td.up { color: var(--text); } td.down { color: var(--text-2); }
    .tbl { overflow-x: auto; margin: 0 0 10px; border: 1px solid var(--border); border-radius: 10px; }
    table { border-collapse: collapse; width: 100%; font-size: 12.5px; }
    th, td { padding: 6px 10px; border-bottom: 1px solid var(--border); text-align: left; white-space: nowrap; }
    th { color: var(--text-3); font-weight: 600; background: var(--surface-2); }
    td.num { text-align: right; font-variant-numeric: tabular-nums; }
    tr:last-child td { border-bottom: 0; }
    .steps { margin: 0 0 10px; padding-left: 22px; line-height: 1.55; }
    .steps li { margin: 3px 0; }
    .steps.check { list-style: none; padding-left: 4px; }
    .steps.check li::before { content: "✓"; color: var(--accent); font-weight: 700; margin-right: 8px; }
    .note { font-size: 12.5px; color: var(--warn); margin: 0 0 6px; }
    .note.bad { color: var(--bad); }
    .links { display: flex; flex-wrap: wrap; gap: 14px; margin: 0 0 10px; font-size: 13px; }
    .chips { display: flex; flex-wrap: wrap; gap: 6px; margin: 0 0 10px; }
    .chip-btn { font: inherit; font-size: 13px; padding: 5px 12px; border-radius: 16px; text-align: left; max-width: 100%; border: 1px solid var(--border-strong); background: var(--surface-solid); color: var(--text); cursor: pointer; }
    .chip-btn:hover { border-color: var(--accent); color: var(--accent-strong); }
    .chip-btn.opt { border-color: var(--accent); }
    .foot { display: flex; align-items: center; gap: 6px; font-size: 11.5px; }
    .spacer { flex: 1; }
    .stale { color: var(--warn); }
    .mini { width: 26px; height: 26px; flex: none; display: grid; place-items: center; border-radius: 8px; border: 1px solid transparent; background: none; color: var(--text-3); cursor: pointer; }
    .mini:hover { color: var(--accent); border-color: var(--border); }
    .mini.on { color: var(--accent); background: var(--accent-soft); }
    .mini.danger:hover { color: var(--bad); }
    .conf { border: 0; background: none; color: var(--text-3); cursor: pointer; font: inherit; text-decoration: underline dotted; }
    .why { margin-top: 6px; font-size: 12px; color: var(--text-2); background: var(--surface-2); border-radius: 8px; padding: 6px 8px; }
    .ask { padding: 12px 24px 16px; border-top: 1px solid var(--border); background: var(--surface); backdrop-filter: blur(18px); -webkit-backdrop-filter: blur(18px);
           position: sticky; bottom: 0; z-index: 3; }
    .row { flex-direction: row; gap: 8px; }
    .ask textarea { flex: 1; resize: none; min-height: 44px; max-height: 160px; border-radius: 14px; padding: 11px 14px; font: inherit; }
    .send { width: 44px; height: 44px; flex: none; border-radius: 14px; border: 0; background: var(--accent); color: #fff; display: grid; place-items: center; cursor: pointer; }
    .send:disabled { opacity: .45; cursor: default; }
    .faint { color: var(--text-3); } .small { font-size: 11.5px; margin: -4px 0 8px; } .pad { padding: 8px; }
    .typing { animation: pulse 1.2s ease-in-out infinite; }
    .caret { display: inline-block; width: 0.55ch; height: 1.05em; margin-left: 1px; vertical-align: -0.12em;
             background: currentColor; opacity: .55; animation: blink 1s step-end infinite; }
    @keyframes pulse { 50% { opacity: .45; } }
    @keyframes blink { 50% { opacity: 0; } }
    @media (prefers-reduced-motion: reduce) { .typing, .caret { animation: none; } }
    @media (max-width: 960px) {
      :host { margin: -18px -16px -32px; }
      .screen, .screen.collapsed { grid-template-columns: 1fr; }
      .screen.collapsed .threads { display: flex; }
      .side-head .collapse, .expand { display: none; }
      .threads { position: fixed; inset: var(--top, 57px) auto 0 0; height: auto; width: min(300px, 86vw); z-index: 5; transform: translateX(-105%); transition: transform .2s ease; background: var(--surface-solid); box-shadow: var(--shadow-lg); }
      .screen.list-open .threads { transform: none; }
      .screen.list-open .scrim { display: block; position: fixed; inset: var(--top, 57px) 0 0 0; background: rgba(15,23,42,.3); z-index: 4; }
      .list-toggle { display: grid; }
      .chat-head, .log { padding-left: 16px; padding-right: 16px; }
      .ask { padding: 10px 16px 12px; }
      .msg.user .bubble { max-width: 88%; }
    }
    @media print { .threads, .ask, .chat-head .list-toggle { display: none; } .screen { display: block; height: auto; } }
  `,
})
export class AssistantPage {
  private api = inject(Api);
  private ctx = inject(RunContext);
  private router = inject(Router);
  private route = inject(ActivatedRoute);
  private box = viewChild<ElementRef<HTMLTextAreaElement>>('box');

  readonly threads = signal<AssistantThread[]>([]);
  readonly thread = signal<AssistantThread | null>(null);
  readonly threadId = computed(() => this.thread()?.id ?? null);
  readonly messages = signal<AssistantMessage[]>([]);
  readonly busy = signal(false);
  readonly error = signal<string | null>(null);
  readonly archived = signal(false);
  readonly renaming = signal<string | null>(null);
  readonly why = signal<string | null>(null);
  readonly listOpen = signal(false);
  /** Live typewriter for the newest answer — tables/links appear when it finishes. */
  readonly streaming = signal<{ id: string; shown: string } | null>(null);
  /** The conversations panel folded away on wide screens; remembered for this browser. */
  readonly collapsed = signal(readCollapsed());
  readonly filterSig = signal('');
  /** Comparison tables shown with their two columns switched, by message and block. */
  private readonly swapped = signal<ReadonlySet<string>>(new Set());
  private host = inject(ElementRef<HTMLElement>);
  private streamToken = 0;
  input = '';
  renameText = '';
  /** Hold "Looking it up…" at least this long so a fast API still feels deliberate. */
  private static readonly ThinkMs = 700;
  /** Typewriter pace; long answers are capped so they finish in a couple of seconds. */
  private static readonly TypeMs = 18;
  private static readonly TypeMaxMs = 2200;

  readonly filteredThreads = computed(() => {
    const f = this.filterSig().trim().toLowerCase();
    return this.threads().filter(t => !f || t.title.toLowerCase().includes(f) || (t.lastText ?? '').toLowerCase().includes(f));
  });

  constructor() {
    void this.loadThreads();
    // The open conversation is in the URL, so it survives a reload and can be bookmarked.
    this.route.queryParamMap.pipe(takeUntilDestroyed()).subscribe(m => {
      const id = m.get('thread');
      if (id && id !== this.threadId()) void this.openThread(id, true);
      else if (!id && !this.thread() && recall()) void this.openThread(recall()!, true);
    });
    effect(() => { this.messages(); this.busy(); this.streaming(); queueMicrotask(() => this.scrollDown()); });
    // Sticky parts sit just under the context bar, which wraps to two rows on narrow windows.
    const header = document.querySelector('header.ctx');
    const fit = () => {
      const top = header ? Math.round(header.getBoundingClientRect().height) : 61;
      (this.host.nativeElement as HTMLElement).style.setProperty('--top', `${top}px`);
    };
    const ro = typeof ResizeObserver !== 'undefined' ? new ResizeObserver(() => fit()) : null;
    if (header && ro) ro.observe(header);
    window.addEventListener('resize', fit);
    queueMicrotask(fit);
    inject(DestroyRef).onDestroy(() => { this.streamToken++; ro?.disconnect(); window.removeEventListener('resize', fit); });
    queueMicrotask(() => this.box()?.nativeElement.focus());
  }

  // ── Threads ──────────────────────────────────────────────────────────────────

  async loadThreads(): Promise<void> {
    try { this.threads.set(await firstValueFrom(this.api.get<AssistantThread[]>('assistant/threads', { archived: this.archived() }))); }
    catch { this.threads.set([]); }
  }

  async toggleArchived(): Promise<void> {
    this.archived.set(!this.archived());
    await this.loadThreads();
  }

  async openThread(id: string, quiet = false): Promise<void> {
    this.listOpen.set(false);
    this.stopStream();
    try {
      const d = await firstValueFrom(this.api.get<AssistantThreadDetail>(`assistant/threads/${id}`));
      this.thread.set(d.thread);
      this.messages.set(d.messages);
      this.error.set(null);
      this.setUrl(id);
    } catch {
      if (!quiet) this.error.set('That conversation could not be opened.');
      remember(null);
      this.setUrl(null);
    }
  }

  newThread(): void {
    this.stopStream();
    this.thread.set(null);
    this.messages.set([]);
    this.error.set(null);
    this.listOpen.set(false);
    this.setUrl(null);
    queueMicrotask(() => this.box()?.nativeElement.focus());
  }

  private setUrl(id: string | null): void {
    remember(id);
    if (this.route.snapshot.queryParamMap.get('thread') === id) return;
    void this.router.navigate([], { relativeTo: this.route, queryParams: { thread: id }, queryParamsHandling: 'merge', replaceUrl: true });
  }

  startRename(t: AssistantThread): void {
    this.renameText = t.title;
    this.renaming.set(t.id);
  }

  async saveRename(t: AssistantThread): Promise<void> {
    if (this.renaming() !== t.id) return;
    this.renaming.set(null);
    const title = this.renameText.trim();
    if (!title || title === t.title) return;
    await this.patch(t, { title, pinned: null, archived: null });
  }

  pin(t: AssistantThread): Promise<void> { return this.patch(t, { title: null, pinned: !t.isPinned, archived: null }); }
  archive(t: AssistantThread): Promise<void> { return this.patch(t, { title: null, pinned: null, archived: !t.isArchived }); }

  private async patch(t: AssistantThread, body: { title: string | null; pinned: boolean | null; archived: boolean | null }): Promise<void> {
    const updated = await firstValueFrom(this.api.patch<AssistantThread>(`assistant/threads/${t.id}`, body));
    if (this.thread()?.id === t.id) this.thread.set(updated);
    await this.loadThreads();
  }

  async remove(t: AssistantThread): Promise<void> {
    if (!confirm(`Delete “${t.title}”? This cannot be undone.`)) return;
    await firstValueFrom(this.api.delete(`assistant/threads/${t.id}`));
    if (this.thread()?.id === t.id) this.newThread();
    await this.loadThreads();
  }

  // ── Asking ───────────────────────────────────────────────────────────────────

  enter(e: Event): void {
    const k = e as KeyboardEvent;
    if (k.shiftKey || k.isComposing) return;
    e.preventDefault();
    void this.send(this.input);
  }

  send(text: string): Promise<void> {
    return this.ask({ text: text.trim(), intent: null, pick: null, remove: null });
  }

  choose(o: AssistantOption): Promise<void> {
    return this.ask({ text: o.text, intent: o.intent, pick: o.pick, remove: null });
  }

  removeAssumption(a: AssistantAssumption): Promise<void> {
    return this.ask({ text: '', intent: null, pick: null, remove: [a.kind] });
  }

  private async ask(part: Pick<AssistantAskBody, 'text' | 'intent' | 'pick' | 'remove'>): Promise<void> {
    if (this.busy() || this.streaming() || (!part.text && !part.remove?.length)) return;
    this.stopStream();
    this.busy.set(true);
    this.error.set(null);
    const pending: AssistantMessage = { id: 'pending', role: 'user', text: part.text || 'Ask again without ' + (part.remove ?? []).join(', '),
      createdAt: new Date().toISOString(), turnType: null, reply: null, feedback: null, hidden: false, stale: false };
    this.messages.update(m => [...m, pending]);
    this.input = '';
    const isNew = !this.threadId();
    const started = performance.now();
    try {
      const body: AssistantAskBody = { threadId: this.threadId(), hospital: this.ctx.hospitalParam(), period: this.ctx.periodParam(), ...part };
      const r = await firstValueFrom(this.api.post<AssistantAskResult>('assistant/ask', body));
      await this.pause(AssistantPage.ThinkMs - (performance.now() - started));
      this.thread.set(r.thread);
      this.setUrl(r.thread.id);
      this.messages.update(m => [...m.filter(x => x.id !== 'pending'), r.question, r.answer]);
      this.busy.set(false);
      void this.loadThreads();
      await this.revealAnswer(r.answer);
      const nav = r.answer.reply?.navigate;
      if (nav) this.go(nav);
      else if (isNew) queueMicrotask(() => this.box()?.nativeElement.focus());
    } catch (e: any) {
      this.messages.update(m => m.filter(x => x.id !== 'pending'));
      this.input = part.text;
      this.error.set(e?.error?.error?.message ?? 'The assistant could not answer. Try again.');
      this.busy.set(false);
    } finally {
      if (this.busy()) this.busy.set(false);
      queueMicrotask(() => this.box()?.nativeElement.focus());
    }
  }

  async refresh(m: AssistantMessage): Promise<void> {
    if (this.busy() || this.streaming()) return;
    this.stopStream();
    this.busy.set(true);
    const started = performance.now();
    try {
      const r = await firstValueFrom(this.api.post<AssistantAskResult>(`assistant/messages/${m.id}/refresh`, {}));
      await this.pause(AssistantPage.ThinkMs - (performance.now() - started));
      this.messages.update(x => [...x, r.question, r.answer]);
      this.busy.set(false);
      await this.revealAnswer(r.answer);
    } catch { this.error.set('That answer could not be refreshed.'); this.busy.set(false); }
    finally { if (this.busy()) this.busy.set(false); }
  }

  /** Type the answer text out; structured blocks stay hidden until the caret finishes. */
  private async revealAnswer(answer: AssistantMessage): Promise<void> {
    const full = answer.reply?.text ?? answer.text ?? '';
    const reduce = typeof matchMedia === 'function' && matchMedia('(prefers-reduced-motion: reduce)').matches;
    if (reduce || !full) { this.streaming.set(null); return; }
    const token = ++this.streamToken;
    this.streaming.set({ id: answer.id, shown: '' });
    const step = Math.max(1, Math.ceil(full.length * AssistantPage.TypeMs / AssistantPage.TypeMaxMs));
    for (let i = 0; i < full.length; i += step) {
      if (token !== this.streamToken) return;
      this.streaming.set({ id: answer.id, shown: full.slice(0, Math.min(full.length, i + step)) });
      await this.pause(AssistantPage.TypeMs);
    }
    if (token === this.streamToken) this.streaming.set(null);
  }

  private stopStream(): void {
    this.streamToken++;
    this.streaming.set(null);
  }

  private pause(ms: number): Promise<void> {
    const wait = Math.max(0, Math.round(ms));
    return wait === 0 ? Promise.resolve() : new Promise(resolve => setTimeout(resolve, wait));
  }

  async rate(m: AssistantMessage, value: 1 | -1): Promise<void> {
    const next = m.feedback === value ? 0 : value;
    const updated = await firstValueFrom(this.api.post<AssistantMessage>(`assistant/messages/${m.id}/feedback`, { value: next, reason: null }));
    this.messages.update(x => x.map(y => y.id === m.id ? { ...y, feedback: updated.feedback } : y));
  }

  // ── View helpers ─────────────────────────────────────────────────────────────

  /** Links in the chat open in a new tab, so the conversation stays where it is. */
  withScope(l: AssistantLink): Record<string, string> {
    return { ...this.ctx.scopeParams(), ...l.query };
  }

  /** "Open cost centres": a new tab with that screen. If the browser blocks it, the link is in the reply. */
  private go(l: AssistantLink): void {
    const url = this.router.serializeUrl(this.router.createUrlTree(['/', l.path], { queryParams: this.withScope(l) }));
    window.open(url, '_blank', 'noopener');
  }

  isLast(m: AssistantMessage): boolean {
    const all = this.messages();
    for (let i = all.length - 1; i >= 0; i--) if (all[i].role === 'assistant') return all[i].id === m.id;
    return false;
  }

  questions(t: AssistantThread): string { const n = Math.ceil(t.messages / 2); return n === 1 ? '1 question' : `${n} questions`; }
  isSwapped(id: string, block: number): boolean { return this.swapped().has(`${id}:${block}`); }
  toggleSwap(id: string, block: number): void {
    const key = `${id}:${block}`;
    this.swapped.update(s => { const n = new Set(s); n.has(key) ? n.delete(key) : n.add(key); return n; });
  }
  toggleCollapsed(): void {
    const next = !this.collapsed();
    this.collapsed.set(next);
    try { localStorage.setItem(COLLAPSED, next ? '1' : '0'); } catch { /* storage may be blocked */ }
  }
  toggleWhy(id: string): void { this.why.set(this.why() === id ? null : id); }
  pct(v: number): string { return `${Math.round(v * 100)}%`; }
  /**
   * A column is numeric when its filled cells are numbers, money, percentages or signed changes
   * ("SAR 1,234.00", "−56,929 (−78.0%)", "14.3%"); its header and every cell then align right.
   */
  numericColumns(rows: string[][]): boolean[] {
    const width = Math.max(0, ...rows.map(r => r.length));
    return Array.from({ length: width }, (_, i) => {
      const cells = rows.map(r => (r[i] ?? '').trim()).filter(c => c && c !== '—' && c !== 'masked');
      return cells.length > 0 && cells.every(c => NUMERIC.test(c));
    });
  }

  assumptionTitle(a: AssistantAssumption): string {
    const from = { explicit: 'from your question', carried: 'carried from earlier in this conversation', screen: 'from the context bar', default: 'picked by default' }[a.source] ?? a.source;
    return `${a.label}: ${a.value}, ${from}${a.note ? ` (${a.note})` : ''}${a.changed ? ' — changed' : ''}`;
  }

  ago(iso: string): string {
    const s = (Date.now() - new Date(iso).getTime()) / 1000;
    if (s < 60) return 'just now';
    if (s < 3600) return `${Math.floor(s / 60)} min ago`;
    if (s < 86400) return `${Math.floor(s / 3600)} h ago`;
    return new Date(iso).toLocaleDateString();
  }

  /** The page scrolls, so the newest message is brought into view by scrolling the window. */
  private scrollDown(): void {
    window.scrollTo({ top: document.documentElement.scrollHeight });
  }
}
