import { TestBed } from '@angular/core/testing';
import { HttpClient, provideHttpClient, withInterceptors } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { apiHeadersInterceptor } from './api-headers.interceptor';
import { SelectedUser } from './selected-user';

describe('apiHeadersInterceptor', () => {
  let http: HttpClient;
  let backend: HttpTestingController;

  beforeEach(() => {
    TestBed.configureTestingModule({
      providers: [provideHttpClient(withInterceptors([apiHeadersInterceptor])), provideHttpClientTesting()],
    });
    http = TestBed.inject(HttpClient);
    backend = TestBed.inject(HttpTestingController);
  });

  it('sends the selected user, and switching user changes the header', () => {
    const selected = TestBed.inject(SelectedUser);
    selected.select('omer');
    http.get('/api/me').subscribe();
    expect(backend.expectOne('/api/me').request.headers.get('X-User-Id')).toBe('omer');

    selected.select('lena');
    http.get('/api/me').subscribe();
    expect(backend.expectOne('/api/me').request.headers.get('X-User-Id')).toBe('lena');
  });

  it('gives every request its own correlation id', () => {
    http.get('/api/a').subscribe();
    http.get('/api/b').subscribe();
    const a = backend.expectOne('/api/a').request.headers.get('X-Correlation-ID');
    const b = backend.expectOne('/api/b').request.headers.get('X-Correlation-ID');
    expect(a).toBeTruthy();
    expect(b).toBeTruthy();
    expect(a).not.toBe(b);
  });
});
