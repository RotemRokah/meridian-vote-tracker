import { Injectable, computed, signal } from '@angular/core';
import { UserDto } from '../api/api-client';

const STORAGE_KEY = 'meridian.userId';

// Who the UI is acting as. Not security: the API checks the role on every request.
// No dependencies on purpose: the HTTP interceptor reads it on every request.
@Injectable({ providedIn: 'root' })
export class SelectedUser {
  readonly users = signal<UserDto[]>([]);
  readonly id = signal<string>(readStored() ?? 'dana');
  readonly user = computed(() => this.users().find((u) => u.id === this.id()));
  readonly isAdmin = computed(() => this.user()?.role === 'Admin');

  select(id: string): void {
    this.id.set(id);
    try {
      localStorage.setItem(STORAGE_KEY, id);   // survives a page refresh; fine if storage is blocked
    } catch {}
  }
}

function readStored(): string | null {
  try {
    return localStorage.getItem(STORAGE_KEY);
  } catch {
    return null;
  }
}
