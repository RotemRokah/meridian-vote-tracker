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
        <!-- each option says if it is the acting user: the options arrive after the stored id,
             so a [value] on the select alone left the browser showing the first option (Dana) while acting as another -->
        <select (change)="selected.select($any($event.target).value)" data-testid="user-switcher">
          @for (u of selected.users(); track u.id) {
            <option [value]="u.id" [selected]="u.id === selected.id()">{{ u.name }} ({{ u.role }})</option>
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
    inject(MeridianApi).getUsers().subscribe((users) => {
      this.selected.users.set(users);
      // a stored id that is not a user (anymore): act as the first user, visibly, instead of an unknown one
      if (users.length && !users.some((u) => u.id === this.selected.id())) this.selected.select(users[0].id);
    });
  }
}
