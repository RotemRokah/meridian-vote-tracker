import { SwaggerException } from '../api/api-client';
import { toProblem } from './problem';

describe('toProblem', () => {
  it('keeps field errors from a 400 validation problem', () => {
    const problem = toProblem({ status: 400, title: 'One or more validation errors occurred.', errors: { weight: ['Weight must be between 1 and 5.'] } });

    expect(problem.status).toBe(400);
    expect(problem.fieldErrors['weight']).toEqual(['Weight must be between 1 and 5.']);
  });

  it('keeps the current state sent with a 409', () => {
    const problem = toProblem({ status: 409, title: 'Conflict already decided', detail: 'dana approved this conflict at …', current: { id: 4, status: 'Approved' } });

    expect(problem.status).toBe(409);
    expect(problem.detail).toContain('dana approved');
    expect(problem.current.status).toBe('Approved');
  });

  it('reads a ProblemDetails body out of a SwaggerException', () => {
    const body = JSON.stringify({ status: 403, title: 'Forbidden', traceId: 'abc-123' });
    const problem = toProblem(new SwaggerException('Forbidden', 403, body, {}, null));

    expect(problem.status).toBe(403);
    expect(problem.title).toBe('Forbidden');
    expect(problem.traceId).toBe('abc-123');
  });

  it('copes with a response that is not JSON', () => {
    const problem = toProblem(new SwaggerException('Bad Gateway', 502, '<html>nginx</html>', {}, null));

    expect(problem.status).toBe(502);
    expect(problem.title).toBe('Bad Gateway');
    expect(problem.fieldErrors).toEqual({});
  });
});
