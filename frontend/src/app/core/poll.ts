import { DestroyRef } from '@angular/core';
import { Observable, Subscription, timer } from 'rxjs';

// Load now, and again every second while `keepGoing` says so (a job is Queued/Running).
// Status lives in the DB, so a page refresh just starts polling again (ING-02).
// SignalR push would replace this in production.
export function pollWhile<T>(
  destroyRef: DestroyRef,
  load: () => Observable<T>,
  handle: (value: T) => boolean,
  intervalMs = 1000,
): { restart: () => void } {
  let current: Subscription | undefined;

  const tick = () => {
    current = load().subscribe({
      next: (value) => {
        if (handle(value)) current = timer(intervalMs).subscribe(tick);
      },
      error: () => (current = timer(intervalMs * 3).subscribe(tick)),   // API briefly down: retry slower
    });
  };

  const restart = () => {
    current?.unsubscribe();
    tick();
  };

  destroyRef.onDestroy(() => current?.unsubscribe());
  tick();
  return { restart };
}
