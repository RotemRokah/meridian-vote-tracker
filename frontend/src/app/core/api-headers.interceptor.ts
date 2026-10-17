import { HttpInterceptorFn } from '@angular/common/http';
import { inject } from '@angular/core';
import { SelectedUser } from './selected-user';

// Every API call carries who is acting (X-User-Id) and a new correlation id,
// which the API puts in its logs, in the import/snapshot jobs and in error responses.
export const apiHeadersInterceptor: HttpInterceptorFn = (req, next) => {
  const userId = inject(SelectedUser).id();
  return next(req.clone({ setHeaders: { 'X-User-Id': userId, 'X-Correlation-ID': newCorrelationId() } }));
};

export function newCorrelationId(): string {
  // randomUUID needs a secure context (https or localhost); fall back elsewhere
  if (typeof crypto !== 'undefined' && 'randomUUID' in crypto) {
    return crypto.randomUUID();
  }
  return 'ui-' + Date.now().toString(36) + '-' + Math.random().toString(36).slice(2, 10);
}
