import { Component, computed, effect, inject, input, signal } from '@angular/core';
import { DatePipe } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { RouterLink } from '@angular/router';
import { MeridianApi, VoteDetailsDto, TimelineEntryDto, PositionDto } from '../api/api-client';
import { Problem, ProblemComponent, FieldErrorComponent, toProblem } from '../core/problem';
import { SelectedUser } from '../core/selected-user';

// Vote screen: details, ballots with their source (HIS-02), position (POS-01/02, Admin only), timeline (HIS-04)
@Component({
  selector: 'app-vote-detail',
  imports: [DatePipe, FormsModule, RouterLink, ProblemComponent, FieldErrorComponent],
  template: `
    <p><a routerLink="/votes">← Votes</a></p>
    <app-problem [problem]="loadProblem()" />

    @if (vote(); as v) {
      <h1>{{ v.assemblyVoteId }}: {{ v.title }}</h1>
      <p class="muted">Session {{ v.session }} · {{ v.voteDate }} · details from {{ v.detailsSource.fileName }}
        · Y {{ v.ballotCounts['Y'] }} · N {{ v.ballotCounts['N'] }} · A {{ v.ballotCounts['A'] }} · X {{ v.ballotCounts['X'] }}</p>

      <div class="panel">
        <h2 style="margin-top: 0">Institute position</h2>
        @if (v.position; as p) {
          <p data-testid="position">{{ p.stance }}, weight {{ p.weight }}
            <span class="muted">· set by {{ p.updatedBy }} at {{ p.updatedAt | date: 'yyyy-MM-dd HH:mm:ss' }}</span></p>
        } @else {
          <p class="muted" data-testid="position">No position yet: this vote can't enter a snapshot until an Admin sets one.</p>
        }

        @if (selected.isAdmin()) {
          <div class="row" data-testid="position-editor">
            <label>Stance
              <select [(ngModel)]="stance">
                <option value="">—</option><option value="For">For</option><option value="Against">Against</option>
              </select>
              <app-field-error [problem]="positionProblem()" field="stance" />
            </label>
            <label>Weight (1–5)
              <input type="number" [(ngModel)]="weight" style="width: 80px" data-testid="weight-input" />
              <app-field-error [problem]="positionProblem()" field="weight" />
            </label>
            <button class="primary" (click)="savePosition(v)" data-testid="save-position">Save</button>
            @if (saved()) { <span class="ok">Saved</span> }
          </div>
          <app-problem [problem]="positionProblem()?.status === 400 ? null : positionProblem()" />
        } @else {
          <p class="muted">Only an Admin can set the position.</p>
        }
      </div>

      @if (v.openConflicts.length) {
        <h2>Open conflicts</h2>
        <ul>
          @for (c of v.openConflicts; track c.id) {
            <li>#{{ c.id }} {{ c.countryCode ?? c.field }}: {{ c.approvedValue }} → {{ c.proposedValue }}
              (<a routerLink="/review" [queryParams]="{ vote: v.assemblyVoteId }">review</a>)</li>
          }
        </ul>
      }

      <h2>Ballots ({{ v.ballots.length }})</h2>
      <table>
        <tr><th>Country</th><th>Ballot</th><th>Source file</th><th>Changed by decision</th></tr>
        @for (b of v.ballots; track b.countryCode) {
          <tr>
            <td>{{ b.countryCode }} <span class="muted">{{ b.countryName }}</span></td>
            <td><strong>{{ b.ballot }}</strong></td>
            <td>{{ b.source.fileName }}</td>
            <td>@if (b.changedByDecision) { ✎ {{ b.approvedBy }} at {{ b.approvedAt | date: 'yyyy-MM-dd HH:mm' }}
              <a href="" (click)="$event.preventDefault(); country = b.countryCode; loadTimeline()">history</a> }</td>
          </tr>
        }
      </table>

      <h2>Timeline</h2>
      <div class="row">
        <label>Country <input [(ngModel)]="country" placeholder="all, or e.g. BRA" (keyup.enter)="loadTimeline()" style="width: 120px" /></label>
        <button (click)="loadTimeline()">Show</button>
        @if (country) { <button (click)="country = ''; loadTimeline()">All</button> }
      </div>
      <table>
        <tr><th>When</th><th>What</th><th>Who</th><th>Change</th><th>File</th><th>Details</th></tr>
        @for (t of timeline(); track $index) {
          <tr>
            <td>{{ t.at | date: 'yyyy-MM-dd HH:mm:ss' }}</td>
            <td>{{ t.eventType }} {{ t.countryCode ?? '' }}</td>
            <td>{{ t.actor }}</td>
            <td class="pre">{{ t.oldValue ? t.oldValue + ' → ' : '' }}{{ t.newValue }}</td>
            <td>{{ t.source?.fileName }}</td>
            <td class="pre">{{ t.detail }} @if (t.comment) { "{{ t.comment }}" }</td>
          </tr>
        }
      </table>
    }
  `,
})
export class VoteDetail {
  private readonly api = inject(MeridianApi);
  protected readonly selected = inject(SelectedUser);

  readonly id = input.required<string>();   // from the route
  readonly vote = signal<VoteDetailsDto | null>(null);
  readonly timeline = signal<TimelineEntryDto[]>([]);
  readonly loadProblem = signal<Problem | null>(null);
  readonly positionProblem = signal<Problem | null>(null);
  readonly saved = signal(false);
  readonly voteId = computed(() => Number(this.id()));
  stance = '';
  weight: number | null = null;
  country = '';

  constructor() {
    effect(() => this.load(this.voteId()));
  }

  load(id: number): void {
    this.api.getVote(id).subscribe({
      next: (v) => {
        this.vote.set(v);
        this.stance = v.position?.stance ?? '';
        this.weight = v.position?.weight ?? null;
      },
      error: (err) => this.loadProblem.set(toProblem(err)),
    });
    this.loadTimeline();
  }

  loadTimeline(): void {
    this.api.getVoteTimeline(this.voteId(), this.country.trim() || undefined).subscribe({
      next: (t) => this.timeline.set(t),
      error: (err) => this.loadProblem.set(toProblem(err)),
    });
  }

  // POS-02: the API validates; its field errors appear next to the fields.
  // The version is what this screen saw: if someone changed it meanwhile, the API answers 409.
  savePosition(v: VoteDetailsDto): void {
    this.saved.set(false);
    this.positionProblem.set(null);
    this.api.setVotePosition(v.id, {
      stance: this.stance || undefined,
      weight: this.weight ?? undefined,
      version: v.position?.version,
    }).subscribe({
      next: (p: PositionDto) => {
        this.saved.set(true);
        this.vote.set({ ...v, position: p });
        this.loadTimeline();
      },
      error: (err) => {
        const problem = toProblem(err);
        this.positionProblem.set(problem);
        if (problem.status === 409 && problem.current) this.vote.set({ ...v, position: problem.current as PositionDto });
      },
    });
  }
}
