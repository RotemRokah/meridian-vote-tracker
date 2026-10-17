import { Component, DestroyRef, computed, effect, inject, input, signal } from '@angular/core';
import { DatePipe } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { RouterLink } from '@angular/router';
import { MeridianApi, ImportDetailsDto, ImportRowDtoPagedResult, RowClassification } from '../api/api-client';
import { Problem, ProblemComponent, toProblem } from '../core/problem';
import { pollWhile } from '../core/poll';

const CLASSES: RowClassification[] = ['New', 'Unchanged', 'Duplicate', 'Invalid', 'Contradiction', 'Conflict'];

// One import's report (ING-14), stored with the import so it opens days later (ING-15)
@Component({
  selector: 'app-import-report',
  imports: [DatePipe, FormsModule, RouterLink, ProblemComponent],
  template: `
    <p><a routerLink="/imports">← Imports</a></p>
    <app-problem [problem]="problem()" />

    @if (details(); as d) {
      <h1>Import #{{ d.import.id }}: {{ d.import.fileName }}</h1>
      <p class="muted">
        Uploaded by {{ d.import.uploadedBy }} at {{ d.import.uploadedAt | date: 'yyyy-MM-dd HH:mm:ss' }}
        · <span class="badge {{ d.import.status }}">{{ d.import.status }}</span>
        @if (d.import.status === 'Running') {
          · step {{ d.import.stage === 'Reading' ? '1 of 2: reading the file' : '2 of 2: classifying votes' }}
          <span class="progress"><div [style.width.%]="percent()"></div></span>
          {{ d.import.processedRows }} / {{ d.import.totalRows ?? '?' }}
        }
      </p>
      @if (d.import.error) { <div class="problem">{{ d.import.error }}</div> }

      @if (d.summary; as s) {
        <h2>Rows per class</h2>
        <table style="width: auto">
          <tr>
            @for (c of classes; track c) { <th class="num">{{ c }}</th> }
          </tr>
          <tr>
            @for (c of classes; track c) { <td class="num" [attr.data-testid]="'count-' + c">{{ s.counts?.[c] ?? 0 }}</td> }
          </tr>
        </table>
        <p>
          {{ s.votesCreated }} votes created · {{ s.ballotsAdded }} ballots added ·
          {{ s.newConflicts }} new conflicts · {{ s.linkedConflicts }} linked to existing conflicts ·
          {{ s.normalizedRows }} rows normalized
        </p>
        @for (note of s.fileNotes; track note) { <p class="muted">ⓘ {{ note }}</p> }

        @if (flaggedVotes().length) {
          <h2>Votes that need attention</h2>
          <table>
            <tr><th>Vote</th><th>Outcome</th><th>Reason</th></tr>
            @for (v of flaggedVotes(); track v.assemblyVoteId) {
              <tr><td>{{ v.assemblyVoteId }}</td><td>{{ v.outcome }}</td><td class="pre">{{ v.reason }}</td></tr>
            }
          </table>
        }

        <h2>Rows</h2>
        <div class="row">
          <label>Class
            <select [(ngModel)]="classFilter" (ngModelChange)="page = 1; loadRows()" data-testid="class-filter">
              <option value="">All</option>
              @for (c of classes; track c) { <option [value]="c">{{ c }} ({{ s.counts?.[c] ?? 0 }})</option> }
            </select>
          </label>
        </div>
        @if (rows(); as r) {
          <table>
            <tr><th class="num">Line</th><th>Class</th><th>Reason / note</th><th>Original line</th></tr>
            @for (row of r.items; track row.lineNo) {
              <tr>
                <td class="num">{{ row.lineNo }}</td>
                <td>{{ row.classification }}</td>
                <td class="pre">
                  {{ row.reason }}
                  @if (row.normalizationNote) { <div class="muted">normalized: {{ row.normalizationNote }}</div> }
                  @if (row.conflictId) { <div><a routerLink="/review">conflict #{{ row.conflictId }}</a></div> }
                </td>
                <td><code class="pre">{{ row.rawLine }}</code></td>
              </tr>
            }
          </table>
          <div class="row">
            <button [disabled]="page <= 1" (click)="page = page - 1; loadRows()">Previous</button>
            <span>page {{ page }} of {{ pages() }} ({{ r.total }} rows)</span>
            <button [disabled]="page >= pages()" (click)="page = page + 1; loadRows()">Next</button>
          </div>
        }
      }
    }
  `,
})
export class ImportReport {
  private readonly api = inject(MeridianApi);
  private readonly destroyRef = inject(DestroyRef);

  readonly id = input.required<string>();   // from the route
  readonly classes = CLASSES;
  readonly details = signal<ImportDetailsDto | null>(null);
  readonly rows = signal<ImportRowDtoPagedResult | null>(null);
  readonly problem = signal<Problem | null>(null);
  classFilter: RowClassification | '' = '';
  page = 1;
  readonly pageSize = 100;

  readonly percent = computed(() => {
    const i = this.details()?.import;
    return i?.totalRows ? Math.round((100 * i.processedRows!) / i.totalRows) : 0;
  });
  readonly flaggedVotes = computed(() =>
    (this.details()?.summary?.votes ?? []).filter((v) => v.outcome !== 'Created' && v.outcome !== 'Unchanged'));
  readonly pages = computed(() => Math.max(1, Math.ceil((this.rows()?.total ?? 0) / this.pageSize)));

  constructor() {
    // start once the route id is known; poll while the import runs, then load its rows
    let started = false;
    effect(() => {
      const id = Number(this.id());
      if (started) return;
      started = true;
      pollWhile(this.destroyRef, () => this.api.getImport(id), (d) => {
        this.details.set(d);
        const running = d.import.status === 'Queued' || d.import.status === 'Running';
        if (!running) this.loadRows();
        return running;
      });
    });
  }

  loadRows(): void {
    this.api.getImportRows(Number(this.id()), this.classFilter || undefined, this.page, this.pageSize).subscribe({
      next: (r) => this.rows.set(r),
      error: (err) => this.problem.set(toProblem(err)),
    });
  }
}
