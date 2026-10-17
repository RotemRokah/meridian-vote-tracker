import { Component, input } from '@angular/core';
import { SwaggerException } from '../api/api-client';

// Every API error is RFC 7807 ProblemDetails. This turns whatever the generated client throws into one shape.
export interface Problem {
  status: number;
  title: string;
  detail?: string;
  fieldErrors: Record<string, string[]>;   // 400: messages per field name
  current?: any;                            // 409: the current state, sent by the API
  traceId?: string;                         // = correlation id, to find the request in the logs
}

export function toProblem(err: unknown): Problem {
  // declared statuses (400/404/409): the client throws the parsed ProblemDetails object itself;
  // anything else: a SwaggerException holding the response text
  let body: any = err;
  let status = 0;
  if (err instanceof SwaggerException) {
    status = err.status;
    try {
      body = err.response ? JSON.parse(err.response) : {};
    } catch {
      body = { title: err.message };
    }
  }
  return {
    status: body?.status ?? status,
    title: body?.title ?? 'Unexpected error',
    detail: body?.detail,
    fieldErrors: body?.errors ?? {},
    current: body?.current,
    traceId: body?.traceId,
  };
}

// A problem shown as a box. 409 is a normal outcome (someone else was faster), so it's styled as a notice.
@Component({
  selector: 'app-problem',
  template: `
    @if (problem(); as p) {
      <div class="problem" [class.notice]="p.status === 409">
        <strong>{{ p.title }}</strong>
        @if (p.detail) { <div>{{ p.detail }}</div> }
        @if (p.status === 403) { <div>Switch to a user with the right role (Lena is the Admin).</div> }
        @if (p.status !== 400 && p.status !== 409 && p.traceId) { <small>reference: {{ p.traceId }}</small> }
      </div>
    }
  `,
})
export class ProblemComponent {
  readonly problem = input<Problem | null>(null);
}

// The messages for one field, shown right under it (POS-02 and every form)
@Component({
  selector: 'app-field-error',
  template: `
    @for (message of problem()?.fieldErrors?.[field()] ?? []; track message) {
      <small class="field-error">{{ message }}</small>
    }
  `,
})
export class FieldErrorComponent {
  readonly problem = input<Problem | null>(null);
  readonly field = input.required<string>();
}
