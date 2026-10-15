import { Component, inject, signal } from '@angular/core';
import { HttpClient } from '@angular/common/http';

// Scaffold page: proves the browser -> nginx -> API chain works.
// The plain HttpClient call is temporary; step 10 replaces it with the NSwag-generated client.
@Component({
  selector: 'app-root',
  styleUrl: './app.scss',
  templateUrl: './app.html',
})
export class App {
  private readonly http = inject(HttpClient);
  protected readonly apiStatus = signal('checking...');

  constructor() {
    this.http.get<{ status: string }>('/api/health').subscribe({
      next: (r) => this.apiStatus.set(r.status),
      error: () => this.apiStatus.set('unreachable'),
    });
  }
}
