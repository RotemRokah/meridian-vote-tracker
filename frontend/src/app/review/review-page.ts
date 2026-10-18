import { Component, inject, signal } from '@angular/core';
import { DatePipe } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { ActivatedRoute } from '@angular/router';
import { MeridianApi, ConflictDto, ConflictStatus } from '../api/api-client';
import { Problem, ProblemComponent, FieldErrorComponent, toProblem } from '../core/problem';
import { SelectedUser } from '../core/selected-user';
import { PdfExtractions } from './pdf-extractions';

type Tab = 'Pending' | 'Decided' | 'Pdf';

// Review screen: pending conflicts (CON-01), side by side with sources (CON-02),
// approve/reject (CON-04), a clear message when someone else decided first (CON-05), decided ones (CON-06)
@Component({
  selector: 'app-review-page',
  imports: [DatePipe, FormsModule, ProblemComponent, FieldErrorComponent, PdfExtractions],
  template: `
    <h1>Review</h1>
    <div class="tabs">
      <button [class.active]="tab() === 'Pending'" (click)="show('Pending')">Pending conflicts</button>
      <button [class.active]="tab() === 'Decided'" (click)="show('Decided')">Decided</button>
      <button [class.active]="tab() === 'Pdf'" (click)="show('Pdf')">PDF extractions</button>
    </div>

    @if (tab() === 'Pdf') {
      <app-pdf-extractions />
    } @else {

    <div class="row">
      <label>Vote
        <input [(ngModel)]="voteFilter" placeholder="MA/2024/003" (keyup.enter)="load()" data-testid="vote-filter" />
      </label>
      <button (click)="load()">Filter</button>
      @if (voteFilter) { <button (click)="voteFilter = ''; load()">Clear</button> }
      <span class="muted">{{ conflicts().length }} conflicts</span>
    </div>

    <app-problem [problem]="listProblem()" />

    @for (c of conflicts(); track c.id) {
      <div class="panel" [attr.data-testid]="'conflict-' + c.id">
        <div>
          <strong>#{{ c.id }} · {{ c.assemblyVoteId }}</strong> {{ c.voteTitle }}
          · {{ c.countryCode ? c.countryCode + ' ballot' : 'vote ' + c.field.toLowerCase() }}
          · <span class="badge {{ c.status }}">{{ c.status }}</span>
        </div>

        <div class="side-by-side">
          <div>
            <div class="muted">Approved</div>
            <div class="pre"><strong>{{ c.approvedValue }}</strong></div>
            <small class="muted">from {{ c.approvedSource.fileName }}, uploaded by {{ c.approvedSource.uploadedBy }}
              at {{ c.approvedSource.uploadedAt | date: 'yyyy-MM-dd HH:mm' }}</small>
          </div>
          <div>
            <div class="muted">Proposed</div>
            <div class="pre"><strong>{{ c.proposedValue }}</strong></div>
            <small class="muted">from {{ c.proposedSource.fileName }}, uploaded by {{ c.proposedSource.uploadedBy }}
              at {{ c.proposedSource.uploadedAt | date: 'yyyy-MM-dd HH:mm' }}</small>
          </div>
        </div>

        @if (c.status === 'Pending' && c.currentApprovedValue !== c.approvedValue) {
          <p class="problem notice">The approved value is now <strong>{{ c.currentApprovedValue }}</strong>; this proposal was made against {{ c.approvedValue }}.</p>
        }

        @if (c.status === 'Pending') {
          <div class="row" style="margin-top: 8px">
            <label>Comment (optional)
              <input [(ngModel)]="comments[c.id]" size="50" maxlength="1000" />
              <app-field-error [problem]="problems()[c.id] ?? null" field="comment" />
            </label>
            <button class="primary" [disabled]="busy() === c.id" (click)="decide(c, 'Approve')">Approve {{ c.proposedValue }}</button>
            <button [disabled]="busy() === c.id" (click)="decide(c, 'Reject')">Reject (keep {{ c.approvedValue }})</button>
          </div>
        } @else {
          <p>
            {{ c.status }} by <strong>{{ c.decidedBy }}</strong> at {{ c.decidedAt | date: 'yyyy-MM-dd HH:mm:ss' }}
            @if (c.comment) { · "{{ c.comment }}" }
          </p>
        }
        <!-- outside the Pending branch: after a 409 the card turns Approved/Rejected, the explanation must stay -->
        <app-problem [problem]="problems()[c.id] ?? null" />
      </div>
    } @empty {
      <p class="muted">No {{ tab() === 'Pending' ? 'pending' : 'decided' }} conflicts{{ voteFilter ? ' for ' + voteFilter : '' }}.</p>
    }
    }
  `,
})
export class ReviewPage {
  private readonly api = inject(MeridianApi);
  protected readonly selected = inject(SelectedUser);

  readonly tab = signal<Tab>('Pending');
  readonly conflicts = signal<ConflictDto[]>([]);
  readonly problems = signal<Record<number, Problem>>({});
  readonly listProblem = signal<Problem | null>(null);
  readonly busy = signal<number | null>(null);
  voteFilter = inject(ActivatedRoute).snapshot.queryParamMap.get('vote') ?? '';
  comments: Record<number, string> = {};

  constructor() {
    this.load();
  }

  show(tab: Tab): void {
    this.tab.set(tab);
    if (tab !== 'Pdf') this.load();
  }

  load(): void {
    const vote = this.voteFilter.trim() || undefined;
    this.listProblem.set(null);
    const statuses: ConflictStatus[] = this.tab() === 'Pending' ? ['Pending'] : ['Approved', 'Rejected'];
    const lists: ConflictDto[][] = [];
    for (const status of statuses) {
      this.api.getConflicts(status, vote).subscribe({
        next: (list) => {
          lists.push(list);
          if (lists.length === statuses.length) this.conflicts.set(lists.flat().sort((a, b) => a.id - b.id));
        },
        error: (err) => this.listProblem.set(toProblem(err)),
      });
    }
  }

  decide(c: ConflictDto, decision: 'Approve' | 'Reject'): void {
    this.busy.set(c.id);
    this.setProblem(c.id, null);
    this.api.decideConflict(c.id, { decision, comment: this.comments[c.id] || undefined }).subscribe({
      next: (updated) => {
        this.busy.set(null);
        this.replace(updated);
      },
      error: (err) => {
        this.busy.set(null);
        const problem = toProblem(err);
        this.setProblem(c.id, problem);
        // 409: someone else decided first, or the approved value changed. Show the current state, not stale data.
        if (problem.status === 409 && problem.current) this.replace(problem.current as ConflictDto, true);
      },
    });
  }

  // put the updated conflict in place; keep it visible after a 409 so the message stays next to it
  private replace(updated: ConflictDto, keep = false): void {
    this.conflicts.update((list) =>
      list.map((x) => (x.id === updated.id ? updated : x))
        .filter((x) => keep || this.tab() !== 'Pending' || x.status === 'Pending' || x.id === updated.id));
  }

  private setProblem(id: number, problem: Problem | null): void {
    this.problems.update((all) => {
      const next = { ...all };
      if (problem) next[id] = problem;
      else delete next[id];
      return next;
    });
  }
}
