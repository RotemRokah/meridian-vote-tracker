import { TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { of, throwError } from 'rxjs';
import { ConflictDto, MeridianApi } from '../api/api-client';
import { ReviewPage } from './review-page';

const source = { importId: 1, fileName: 'q1.csv', uploadedBy: 'dana', uploadedAt: '2026-10-17T10:00:00Z' };
const pending: ConflictDto = {
  id: 4, assemblyVoteId: 'MA/2024/003', voteTitle: 'Food Security', countryCode: 'BRA', field: 'Ballot', status: 'Pending',
  approvedValue: 'Y', approvedSource: source, proposedValue: 'A', proposedSource: { ...source, importId: 2, fileName: 'rev2.csv' },
  currentApprovedValue: 'Y', createdAt: '2026-10-17T10:01:00Z', decidedBy: undefined, decidedAt: undefined, comment: undefined,
};

// The screen is tested with a fake API that answers immediately: no HTTP, no timing.
// Parsing real HTTP errors is covered by problem.spec.ts and by the browser walkthrough.
describe('ReviewPage', () => {
  it('shows a clear message and the current state when someone else decided first (409)', () => {
    // what the generated client throws for a 409: the parsed ProblemDetails, with the current state
    const problem409 = {
      status: 409, title: 'Conflict already decided',
      detail: 'dana approved this conflict at 2026-10-17 10:05:00 UTC. Your decision was not applied.',
      current: { ...pending, status: 'Approved', decidedBy: 'dana', decidedAt: '2026-10-17T10:05:00Z', currentApprovedValue: 'A' },
    };
    const api = {
      getConflicts: () => of([pending]),
      decideConflict: () => throwError(() => problem409),
    };
    TestBed.configureTestingModule({
      imports: [ReviewPage],
      providers: [{ provide: MeridianApi, useValue: api }, provideRouter([])],
    });
    const fixture = TestBed.createComponent(ReviewPage);
    fixture.detectChanges();

    // Omer clicks Approve, but Dana already approved it
    const approve = [...fixture.nativeElement.querySelectorAll('button')]
      .find((b: HTMLButtonElement) => b.textContent!.includes('Approve')) as HTMLButtonElement;
    approve.click();
    fixture.detectChanges();

    const card: HTMLElement = fixture.nativeElement.querySelector('[data-testid="conflict-4"]');
    expect(card.textContent).toContain('Approved by dana');            // the card shows the current state...
    expect(card.textContent).toContain('Conflict already decided');    // ...and still says why
    expect(card.textContent).toContain('dana approved this conflict');
    expect(card.querySelector('.problem.notice')).toBeTruthy();         // a notice, not an error
    expect(card.querySelector('button.primary')).toBeNull();            // no more Approve button
  });
});
