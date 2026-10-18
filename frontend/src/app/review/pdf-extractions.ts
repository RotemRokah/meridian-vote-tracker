import { Component, DestroyRef, computed, inject, signal } from '@angular/core';
import { DatePipe } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { MeridianApi, PdfExtractionDetailsDto, PdfExtractionDto, SheetRow } from '../api/api-client';
import { Problem, ProblemComponent, FieldErrorComponent, toProblem } from '../core/problem';
import { pollWhile } from '../core/poll';

// Review > PDF extractions (bonus): upload a scanned sheet, follow the background read (PDF-01, PDF-06),
// see every flagged value (PDF-03, PDF-04), approve into the normal ingestion or reject (PDF-05).
@Component({
  selector: 'app-pdf-extractions',
  imports: [DatePipe, FormsModule, ProblemComponent, FieldErrorComponent],
  template: `
    @if (enabled() === false) {
      <p class="problem notice">PDF extraction is off: Document Intelligence is not configured
        (AZURE_DOCUMENT_INTELLIGENCE_ENDPOINT / _KEY in .env).</p>
    } @else {
      <div class="row">
        <input type="file" accept="application/pdf" (change)="pick($event)" data-testid="pdf-file" />
        <button class="primary" [disabled]="!file || uploading()" (click)="upload()">Upload PDF</button>
        <app-field-error [problem]="uploadProblem()" field="file" />
      </div>
      <app-problem [problem]="uploadProblem()?.status === 400 ? null : uploadProblem()" />

      <table>
        <tr><th>#</th><th>File</th><th>Uploaded by</th><th>When</th><th>Status</th><th></th></tr>
        @for (x of list(); track x.id) {
          <tr [class.selected]="details()?.extraction?.id === x.id">
            <td>{{ x.id }}</td><td>{{ x.fileName }}</td><td>{{ x.uploadedBy }}</td>
            <td>{{ x.uploadedAt | date: 'yyyy-MM-dd HH:mm' }}</td>
            <td><span class="badge {{ x.status }}">{{ x.status }}</span>
              @if (x.status === 'Failed') { <div class="problem">{{ x.error }}</div> }</td>
            <td>
              @if (x.status === 'Failed') { <button (click)="retry(x)">Retry</button> }
              @if (x.status !== 'Queued' && x.status !== 'Running' && x.status !== 'Failed') { <button (click)="open(x.id)">Open</button> }
            </td>
          </tr>
        } @empty { <tr><td colspan="6" class="muted">No PDFs uploaded yet.</td></tr> }
      </table>

      @if (details(); as d) {
        @if (d.sheet; as s) {
          <div class="panel" data-testid="pdf-details">
            <h2>{{ d.extraction.fileName }}: {{ s.assemblyVoteId }} · session {{ s.session }} · {{ s.voteDate }}</h2>
            <p>{{ s.title }}</p>
            @if (s.printedTotals; as t) {
              <p class="muted">Printed totals: Yes {{ t.yes }}, No {{ t.no }}, Abstentions {{ t.abstain }}, Not voting {{ t.notVoting }}.
                Extracted: Yes {{ count('Y') }}, No {{ count('N') }}, Abstentions {{ count('A') }}, Not voting {{ count('X') }}.</p>
            }
            @for (issue of s.issues; track $index) { <p class="problem notice">⚠ {{ issue }}</p> }
            @if (d.blockingProblems.length) {
              <p class="problem">Can't be approved until fixed: {{ d.blockingProblems.join(', ') }}</p>
            }

            <label><input type="checkbox" [(ngModel)]="onlyFlagged" /> only flagged rows ({{ flagged().length }})</label>
            <table>
              <tr><th>Code</th><th>Member state</th><th>Ballot</th><th>Confidence</th><th>Issues</th></tr>
              @for (r of (onlyFlagged ? flagged() : s.rows); track r.code + $index) {
                <tr [class.flagged]="r.issues.length > 0" [attr.data-testid]="'row-' + r.code">
                  <td>{{ r.code }}</td><td>{{ r.name }}</td>
                  <td>
                    @if (d.extraction.status === 'ReadyForReview') {
                      <!-- PDF-04: the analyst corrects a value; the API re-runs every check -->
                      <select [ngModel]="r.ballot ?? ''" (ngModelChange)="edit(r.code, $event)" [attr.aria-label]="'Ballot of ' + r.code">
                        <option value="" disabled>?</option>
                        <option value="Y">Y</option><option value="N">N</option><option value="A">A</option><option value="X">X</option>
                      </select>
                    } @else { <strong>{{ r.ballot }}</strong> }
                  </td>
                  <td>{{ r.confidence == null ? '' : (r.confidence * 100).toFixed(0) + '%' }}</td>
                  <td>@for (i of r.issues; track $index) { <div class="problem">⚠ {{ i }}</div> }</td>
                </tr>
              }
            </table>

            @if (d.extraction.status === 'ReadyForReview') {
              <div class="row" style="margin-top: 8px">
                <button class="primary" [disabled]="busy() || d.blockingProblems.length > 0" (click)="decide('approve')">Approve and import</button>
                <button [disabled]="busy()" (click)="decide('reject')">Reject</button>
              </div>
            } @else {
              <p>{{ d.extraction.status }} by <strong>{{ d.extraction.decidedBy }}</strong>
                at {{ d.extraction.decidedAt | date: 'yyyy-MM-dd HH:mm:ss' }}
                @if (d.extraction.importId) { · imported as import #{{ d.extraction.importId }} (see Imports) }</p>
            }
            <app-field-error [problem]="decisionProblem()" field="sheet" />
            <app-problem [problem]="decisionProblem()?.status === 400 ? null : decisionProblem()" />
          </div>
        }
      }
    }
  `,
  styles: `tr.flagged td { background: var(--flag-bg, #fff4e5); } tr.selected td { font-weight: 600; }`,
})
export class PdfExtractions {
  private readonly api = inject(MeridianApi);

  readonly enabled = signal<boolean | null>(null);
  readonly list = signal<PdfExtractionDto[]>([]);
  readonly details = signal<PdfExtractionDetailsDto | null>(null);
  readonly uploading = signal(false);
  readonly busy = signal(false);
  readonly uploadProblem = signal<Problem | null>(null);
  readonly decisionProblem = signal<Problem | null>(null);
  readonly flagged = computed<SheetRow[]>(() => this.details()?.sheet?.rows.filter((r) => r.issues.length > 0) ?? []);
  onlyFlagged = true;
  file: File | null = null;
  private readonly poller;

  constructor() {
    this.api.getPdfFeature().subscribe((f) => this.enabled.set(f.enabled));
    // poll while a PDF is being read; status lives in the DB, so a refresh just resumes
    this.poller = pollWhile(inject(DestroyRef), () => this.api.getPdfExtractions(), (list) => {
      this.list.set(list);
      return list.some((x) => x.status === 'Queued' || x.status === 'Running');
    });
  }

  pick(event: Event): void {
    this.file = (event.target as HTMLInputElement).files?.[0] ?? null;
  }

  upload(): void {
    if (!this.file) return;
    this.uploading.set(true);
    this.uploadProblem.set(null);
    this.api.uploadPdf({ data: this.file, fileName: this.file.name }).subscribe({
      next: () => { this.uploading.set(false); this.poller.restart(); },
      error: (err) => { this.uploading.set(false); this.uploadProblem.set(toProblem(err)); },
    });
  }

  retry(x: PdfExtractionDto): void {
    this.api.retryPdfExtraction(x.id).subscribe({ next: () => this.poller.restart(), error: (err) => this.uploadProblem.set(toProblem(err)) });
  }

  open(id: number): void {
    this.decisionProblem.set(null);
    this.api.getPdfExtraction(id).subscribe((d) => this.details.set(d));
  }

  count(ballot: string): number {
    return this.details()?.sheet?.rows.filter((r) => r.ballot === ballot).length ?? 0;
  }

  edit(code: string, ballot: string): void {
    const id = this.details()!.extraction.id;
    this.decisionProblem.set(null);
    this.api.editPdfBallot(id, code, { ballot }).subscribe({
      next: (d) => this.details.set(d),
      error: (err) => { this.decisionProblem.set(toProblem(err)); this.open(id); },
    });
  }

  decide(action: 'approve' | 'reject'): void {
    const id = this.details()!.extraction.id;
    this.busy.set(true);
    this.decisionProblem.set(null);
    const call = action === 'approve' ? this.api.approvePdfExtraction(id) : this.api.rejectPdfExtraction(id);
    call.subscribe({
      next: () => { this.busy.set(false); this.open(id); this.poller.restart(); },
      error: (err) => {
        this.busy.set(false);
        this.decisionProblem.set(toProblem(err));
        this.open(id);   // 409: someone else decided; show the current state
      },
    });
  }
}
