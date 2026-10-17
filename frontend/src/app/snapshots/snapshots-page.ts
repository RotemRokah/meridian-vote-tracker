import { Component, DestroyRef, inject, signal } from '@angular/core';
import { DatePipe } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { Router } from '@angular/router';
import { MeridianApi, SnapshotDto } from '../api/api-client';
import { Problem, ProblemComponent, FieldErrorComponent, toProblem } from '../core/problem';
import { pollWhile } from '../core/poll';

// Snapshots: create (SNP-01, POS-04), list with status while computing in the background (SNP-02)
@Component({
  selector: 'app-snapshots-page',
  imports: [DatePipe, FormsModule, ProblemComponent, FieldErrorComponent],
  template: `
    <h1>Snapshots</h1>

    <div class="panel">
      <h2 style="margin-top: 0">New snapshot</h2>
      <div class="row">
        <label>Name <input [(ngModel)]="name" size="30" data-testid="snapshot-name" />
          <app-field-error [problem]="problem()" field="name" /></label>
        <label>From <input type="date" [(ngModel)]="from" />
          <app-field-error [problem]="problem()" field="from" /></label>
        <label>To <input type="date" [(ngModel)]="to" /></label>
        <label>Session <input type="number" [(ngModel)]="session" style="width: 80px" /></label>
      </div>
      <div class="row">
        <label>and/or these votes (one per line or comma-separated)
          <textarea [(ngModel)]="voteList" rows="2" cols="60" placeholder="MA/2024/003, MA/2024/009"></textarea>
          <app-field-error [problem]="problem()" field="voteIds" />
        </label>
      </div>
      <app-field-error [problem]="problem()" field="selection" />

      @if (withoutPosition().length) {
        <div class="problem notice" data-testid="missing-positions">
          {{ withoutPosition().length }} selected votes have no position:
          {{ withoutPosition().slice(0, 15).join(', ') }}{{ withoutPosition().length > 15 ? ', …' : '' }}.
          Ask an Admin to set them, or leave them out explicitly:
          <label style="display: block; margin-top: 6px">
            <input type="checkbox" [(ngModel)]="exclude" data-testid="exclude-checkbox" />
            Compute without these {{ withoutPosition().length }} votes (they will be listed in the snapshot's rules)
          </label>
        </div>
      }

      <button class="primary" (click)="create()" [disabled]="creating()" data-testid="create-snapshot">Create snapshot</button>
      <app-problem [problem]="problem()?.status === 400 ? null : problem()" />
    </div>

    <table>
      <tr><th>#</th><th>Name</th><th>Created</th><th>Status</th><th class="num">Votes</th><th>Published</th></tr>
      @for (s of snapshots(); track s.id) {
        <tr class="clickable" (click)="open(s.id)">
          <td>{{ s.id }}</td>
          <td>{{ s.name }}</td>
          <td>{{ s.createdBy }}, {{ s.createdAt | date: 'yyyy-MM-dd HH:mm' }}</td>
          <td><span class="badge {{ s.status }}">{{ s.status }}</span> {{ s.error ?? '' }}</td>
          <td class="num">{{ s.voteCount }}</td>
          <td>@if (s.isPublished) { <span class="badge published">{{ s.quarter }}</span> }</td>
        </tr>
      } @empty {
        <tr><td colspan="6" class="muted">No snapshots yet.</td></tr>
      }
    </table>
  `,
})
export class SnapshotsPage {
  private readonly api = inject(MeridianApi);
  private readonly router = inject(Router);

  readonly snapshots = signal<SnapshotDto[]>([]);
  readonly problem = signal<Problem | null>(null);
  readonly withoutPosition = signal<string[]>([]);
  readonly creating = signal(false);
  name = '';
  from = '';
  to = '';
  session: number | null = null;
  voteList = '';
  exclude = false;

  private readonly poll = pollWhile(inject(DestroyRef), () => this.api.getSnapshots(), (list) => {
    this.snapshots.set(list);
    return list.some((s) => s.status === 'Queued' || s.status === 'Running');
  });

  create(): void {
    this.creating.set(true);
    this.problem.set(null);
    const voteIds = this.voteList.split(/[\s,]+/).map((v) => v.trim()).filter((v) => v);
    this.api.createSnapshot({
      name: this.name,
      from: this.from || undefined,
      to: this.to || undefined,
      session: this.session ?? undefined,
      voteIds,
      excludeVotesWithoutPosition: this.exclude,
    }).subscribe({
      next: () => {
        this.creating.set(false);
        this.withoutPosition.set([]);
        this.exclude = false;
        this.poll.restart();   // 202: computing in the background, the list follows its status
      },
      error: (err) => {
        this.creating.set(false);
        const problem = toProblem(err);
        this.problem.set(problem);
        // POS-04: the API lists the votes without a position; the user can then exclude them explicitly
        this.withoutPosition.set((err as any)?.votesWithoutPosition ?? []);
      },
    });
  }

  open(id: number): void {
    this.router.navigate(['/snapshots', id]);
  }
}
