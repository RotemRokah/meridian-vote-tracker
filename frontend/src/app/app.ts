import { Component, inject } from '@angular/core';
import { RouterLink, RouterLinkActive, RouterOutlet } from '@angular/router';
import { MeridianApi } from './api/api-client';
import { SelectedUser } from './core/selected-user';

@Component({
  selector: 'app-root',
  imports: [RouterOutlet, RouterLink, RouterLinkActive],
  template: `
    <header>
      <strong>Meridian Vote Tracker</strong>
      <nav>
        <a routerLink="/imports" routerLinkActive="active">Imports</a>
        <a routerLink="/review" routerLinkActive="active">Review</a>
        <a routerLink="/votes" routerLinkActive="active">Votes</a>
        <a routerLink="/snapshots" routerLinkActive="active">Snapshots</a>
      </nav>
      <label class="user">
        Acting as
        <select [value]="selected.id()" (change)="selected.select($any($event.target).value)" data-testid="user-switcher">
          @for (u of selected.users(); track u.id) {
            <option [value]="u.id">{{ u.name }} ({{ u.role }})</option>
          }
        </select>
      </label>
    </header>
    <main>
      <router-outlet />
    </main>
  `,
})
export class App {
  protected readonly selected = inject(SelectedUser);

  constructor() {
    inject(MeridianApi).getUsers().subscribe((users) => this.selected.users.set(users));
  }
}
