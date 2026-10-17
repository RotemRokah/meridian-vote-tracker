import { Component, DestroyRef, inject, signal } from '@angular/core';
import { DatePipe } from '@angular/common';
import { Router } from '@angular/router';
import { MeridianApi, ImportDto } from '../api/api-client';
import { Problem, ProblemComponent, FieldErrorComponent, toProblem } from '../core/problem';
import { pollWhile } from '../core/poll';

// Imports screen: upload (ING-01), live progress (ING-02), history (HIS-01)
@Component({
  selector: 'app-imports-page',
  imports: [DatePipe, ProblemComponent, FieldErrorComponent],
  template: `
    <h1>Imports</h1>

    <div class="panel">
      <div class="row">
        <label>CSV file
          <input type="file" accept=".csv" (change)="pick($event)" data-testid="file-input" />
          <app-field-error [problem]="problem()" field="file" />
        </label>
        <button class="primary" [disabled]="!file() || uploading()" (click)="upload()">Upload</button>
        @if (uploading()) { <span class="muted">Uploading…</span> }
      </div>
      <app-problem [problem]="problem()?.status === 400 ? null : problem()" />
    </div>

    <table>
      <thead>
        <tr><th>#</th><th>File</th><th>Uploaded by</th><th>When</th><th>Status</th><th>Progress</th></tr>
      </thead>
      <tbody>
        @for (i of imports(); track i.id) {
          <tr class="clickable" (click)="open(i)">
            <td>{{ i.id }}</td>
            <td>{{ i.fileName }}</td>
            <td>{{ i.uploadedBy }}</td>
            <td>{{ i.uploadedAt | date: 'yyyy-MM-dd HH:mm:ss' }}</td>
            <td><span class="badge {{ i.status }}">{{ i.status }}</span> {{ i.stage ?? '' }}</td>
            <td>
              <span class="progress"><div [style.width.%]="percent(i)"></div></span>
              {{ i.processedRows }} / {{ i.totalRows ?? '?' }}
              @if (i.error) { <div class="field-error">{{ i.error }}</div> }
            </td>
          </tr>
        } @empty {
          <tr><td colspan="6" class="muted">No imports yet.</td></tr>
        }
      </tbody>
    </table>
  `,
})
export class ImportsPage {
  private readonly api = inject(MeridianApi);
  private readonly router = inject(Router);

  readonly imports = signal<ImportDto[]>([]);
  readonly file = signal<File | null>(null);
  readonly uploading = signal(false);
  readonly problem = signal<Problem | null>(null);

  constructor() {
    // poll every second while an import is queued or running; stop when all are done
    pollWhile(inject(DestroyRef), () => this.api.getImports(), (list) => {
      this.imports.set(list);
      return list.some((i) => i.status === 'Queued' || i.status === 'Running');
    });
  }

  pick(event: Event): void {
    this.file.set((event.target as HTMLInputElement).files?.[0] ?? null);
    this.problem.set(null);
  }

  upload(): void {
    const file = this.file();
    if (!file) return;
    this.uploading.set(true);
    this.problem.set(null);
    this.api.uploadImport({ data: file, fileName: file.name }).subscribe({
      next: (created) => {
        this.uploading.set(false);
        this.router.navigate(['/imports', created.id]);   // 202: follow it on the report page
      },
      error: (err) => {
        this.uploading.set(false);
        this.problem.set(toProblem(err));
      },
    });
  }

  open(i: ImportDto): void {
    this.router.navigate(['/imports', i.id]);
  }

  percent(i: ImportDto): number {
    return i.totalRows ? Math.round((100 * i.processedRows!) / i.totalRows) : 0;
  }
}
