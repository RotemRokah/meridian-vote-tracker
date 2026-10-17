import { Component, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { Router } from '@angular/router';
import { MeridianApi, VoteListItemDtoPagedResult } from '../api/api-client';
import { Problem, ProblemComponent, FieldErrorComponent, toProblem } from '../core/problem';

// Votes list: search, session, date range, paging
@Component({
  selector: 'app-votes-page',
  imports: [FormsModule, ProblemComponent, FieldErrorComponent],
  template: `
    <h1>Votes</h1>
    <div class="row">
      <label>Search (id or title) <input [(ngModel)]="search" (keyup.enter)="find()" data-testid="vote-search" /></label>
      <label>Session <input type="number" [(ngModel)]="session" style="width: 80px" /></label>
      <label>From <input type="date" [(ngModel)]="from" />
        <app-field-error [problem]="problem()" field="from" /></label>
      <label>To <input type="date" [(ngModel)]="to" /></label>
      <button class="primary" (click)="find()">Search</button>
    </div>
    <app-problem [problem]="problem()?.status === 400 ? null : problem()" />

    @if (result(); as r) {
      <table>
        <tr><th>Vote</th><th>Date</th><th class="num">Session</th><th>Title</th><th class="num">Ballots</th>
            <th class="num">Open conflicts</th><th>Position</th></tr>
        @for (v of r.items; track v.id) {
          <tr class="clickable" (click)="open(v.id)">
            <td>{{ v.assemblyVoteId }}</td>
            <td>{{ v.voteDate }}</td>
            <td class="num">{{ v.session }}</td>
            <td>{{ v.title }}</td>
            <td class="num">{{ v.ballotCount }}</td>
            <td class="num">{{ v.pendingConflicts || '' }}</td>
            <td>{{ v.stance ? v.stance + ', weight ' + v.weight : '—' }}</td>
          </tr>
        } @empty {
          <tr><td colspan="7" class="muted">No votes match.</td></tr>
        }
      </table>
      <div class="row">
        <button [disabled]="page <= 1" (click)="page = page - 1; load()">Previous</button>
        <span>page {{ page }} of {{ pages() }} ({{ r.total }} votes)</span>
        <button [disabled]="page >= pages()" (click)="page = page + 1; load()">Next</button>
      </div>
    }
  `,
})
export class VotesPage {
  private readonly api = inject(MeridianApi);
  private readonly router = inject(Router);

  readonly result = signal<VoteListItemDtoPagedResult | null>(null);
  readonly problem = signal<Problem | null>(null);
  search = '';
  session: number | null = null;
  from = '';
  to = '';
  page = 1;
  readonly pageSize = 50;

  constructor() {
    this.load();
  }

  find(): void {
    this.page = 1;
    this.load();
  }

  load(): void {
    this.problem.set(null);
    this.api.getVotes(this.search || undefined, this.session ?? undefined, this.from || undefined, this.to || undefined,
      this.page, this.pageSize).subscribe({
      next: (r) => this.result.set(r),
      error: (err) => this.problem.set(toProblem(err)),
    });
  }

  pages(): number {
    return Math.max(1, Math.ceil((this.result()?.total ?? 0) / this.pageSize));
  }

  open(id: number): void {
    this.router.navigate(['/votes', id]);
  }
}
