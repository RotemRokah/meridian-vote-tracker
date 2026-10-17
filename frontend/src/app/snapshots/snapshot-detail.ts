import { Component, DestroyRef, effect, inject, input, signal } from '@angular/core';
import { DatePipe, DecimalPipe } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { RouterLink } from '@angular/router';
import { MeridianApi, SnapshotDetailsDto, CountryBreakdownDto } from '../api/api-client';
import { Problem, ProblemComponent, FieldErrorComponent, toProblem } from '../core/problem';
import { SelectedUser } from '../core/selected-user';
import { pollWhile } from '../core/poll';

// One snapshot: ranked scores (SNP-03), per-country breakdown, rules it was computed with, publish (SNP-05, Admin)
@Component({
  selector: 'app-snapshot-detail',
  imports: [DatePipe, DecimalPipe, FormsModule, RouterLink, ProblemComponent, FieldErrorComponent],
  template: `
    <p><a routerLink="/snapshots">← Snapshots</a></p>
    <app-problem [problem]="loadProblem()" />

    @if (details(); as d) {
      <h1>Snapshot #{{ d.snapshot.id }}: {{ d.snapshot.name }}</h1>
      <p class="muted">
        {{ d.snapshot.voteCount }} votes · created by {{ d.snapshot.createdBy }} at {{ d.snapshot.createdAt | date: 'yyyy-MM-dd HH:mm' }}
        · <span class="badge {{ d.snapshot.status }}">{{ d.snapshot.status }}</span>
        @if (d.snapshot.isPublished) {
          · <span class="badge published">published for {{ d.snapshot.quarter }}</span> by {{ d.snapshot.publishedBy }}
        }
      </p>

      <div class="panel">
        <strong>Rules</strong> <span class="muted">(stored with the snapshot; never change)</span>
        <ul>
          <li>{{ d.rules.formula }}</li>
          <li>{{ d.rules.counted }}</li>
          <li>Matched: {{ d.rules.matched }}</li>
          <li>{{ d.rules.noCountedVotes }}</li>
          <li>Ranking: {{ d.rules.ranking }}</li>
          @if (d.rules.excludedVotesWithoutPosition.length) {
            <li><strong>Excluded (no position):</strong> {{ d.rules.excludedVotesWithoutPosition.join(', ') }}</li>
          }
          @if (d.rules.pendingConflictsAtCreation.length) {
            <li><strong>Pending conflicts when created</strong> (counted with the approved value):
              {{ d.rules.pendingConflictsAtCreation.join('; ') }}</li>
          }
        </ul>
      </div>

      @if (selected.isAdmin() && d.snapshot.status === 'Completed') {
        <div class="panel" data-testid="publish-panel">
          @if (d.snapshot.isPublished) {
            <button (click)="unpublish()">Unpublish {{ d.snapshot.quarter }}</button>
          } @else {
            <div class="row">
              <label>Quarter (empty = derived from the votes)
                <input [(ngModel)]="quarter" placeholder="2024-Q1" style="width: 100px" />
                <app-field-error [problem]="publishProblem()" field="quarter" />
              </label>
              <button class="primary" (click)="publish()">Publish</button>
            </div>
          }
          <app-problem [problem]="publishProblem()?.status === 400 ? null : publishProblem()" />
        </div>
      }

      @if (d.history.length) {
        <p class="muted">
          @for (h of d.history; track $index) { {{ h.eventType }} {{ h.newValue ?? h.oldValue }} by {{ h.actor }} at {{ h.at | date: 'yyyy-MM-dd HH:mm' }}. }
        </p>
      }

      @if (d.snapshot.status === 'Completed') {
        <div class="side-by-side">
          <div>
            <h2>Scores</h2>
            <table>
              <tr><th class="num">Rank</th><th>Country</th><th class="num">Score</th><th class="num">Matched / counted</th>
                  <th class="num">Counted votes</th><th class="num">A</th><th class="num">X</th></tr>
              @for (s of d.scores; track s.countryCode) {
                <tr class="clickable" [class.selected]="breakdown()?.score?.countryCode === s.countryCode" (click)="openCountry(s.countryCode)">
                  <td class="num">{{ s.rank ?? '—' }}</td>
                  <td>{{ s.countryCode }} <span class="muted">{{ s.countryName }}</span></td>
                  <td class="num">{{ s.score == null ? 'N/A' : (s.score | number: '1.1-1') }}</td>
                  <td class="num">{{ s.matchedWeight }} / {{ s.countedWeight }}</td>
                  <td class="num">{{ s.countedVotes }}</td>
                  <td class="num">{{ s.abstainCount }}</td>
                  <td class="num">{{ s.absentCount }}</td>
                </tr>
              }
            </table>
          </div>
          <div>
            @if (breakdown(); as b) {
              <h2>{{ b.score.countryCode }} {{ b.score.countryName }}: vote by vote</h2>
              <table>
                <tr><th>Vote</th><th>Ballot</th><th>Stance</th><th class="num">Weight</th><th class="num">Matched / counted</th></tr>
                @for (v of b.votes; track v.voteId) {
                  <tr>
                    <td><a [routerLink]="['/votes', v.voteId]">{{ v.assemblyVoteId }}</a></td>
                    <td>{{ v.ballot }}</td>
                    <td>{{ v.stance }}</td>
                    <td class="num">{{ v.weight }}</td>
                    <td class="num">{{ v.counted ? v.matchedWeight + ' / ' + v.countedWeight : 'not counted' }}</td>
                  </tr>
                }
              </table>
            } @else {
              <p class="muted">Click a country to see each vote's contribution.</p>
            }
          </div>
        </div>
      } @else if (d.snapshot.status !== 'Failed') {
        <p class="muted">Computing in the background…</p>
      }
    }
  `,
})
export class SnapshotDetail {
  private readonly api = inject(MeridianApi);
  private readonly destroyRef = inject(DestroyRef);
  protected readonly selected = inject(SelectedUser);

  readonly id = input.required<string>();   // from the route
  readonly details = signal<SnapshotDetailsDto | null>(null);
  readonly breakdown = signal<CountryBreakdownDto | null>(null);
  readonly loadProblem = signal<Problem | null>(null);
  readonly publishProblem = signal<Problem | null>(null);
  quarter = '';

  constructor() {
    let started = false;
    effect(() => {
      const id = Number(this.id());
      if (started) return;
      started = true;
      // poll while it is being computed (SNP-02)
      pollWhile(this.destroyRef, () => this.api.getSnapshot(id), (d) => {
        this.details.set(d);
        return d.snapshot.status === 'Queued' || d.snapshot.status === 'Running';
      });
    });
  }

  openCountry(code: string): void {
    this.api.getSnapshotCountry(Number(this.id()), code).subscribe({
      next: (b) => this.breakdown.set(b),
      error: (err) => this.loadProblem.set(toProblem(err)),
    });
  }

  publish(): void {
    this.publishProblem.set(null);
    this.api.publishSnapshot(Number(this.id()), { quarter: this.quarter.trim() || undefined }).subscribe({
      next: () => this.reload(),
      error: (err) => this.publishProblem.set(toProblem(err)),
    });
  }

  unpublish(): void {
    this.publishProblem.set(null);
    this.api.unpublishSnapshot(Number(this.id())).subscribe({
      next: () => this.reload(),
      error: (err) => this.publishProblem.set(toProblem(err)),
    });
  }

  private reload(): void {
    this.api.getSnapshot(Number(this.id())).subscribe((d) => this.details.set(d));
  }
}
