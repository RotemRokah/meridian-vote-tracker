import { TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { of } from 'rxjs';
import { MeridianApi, UserDto } from './api/api-client';
import { App } from './app';
import { SelectedUser } from './core/selected-user';

const users: UserDto[] = [
  { id: 'dana', name: 'Dana', role: 'Analyst' },
  { id: 'lena', name: 'Lena', role: 'Admin' },
  { id: 'omer', name: 'Omer', role: 'Analyst' },
];

// The user switcher must show the user the app really acts as (X-User-Id).
describe('App user switcher', () => {
  function start(storedUserId: string | null) {
    if (storedUserId) localStorage.setItem('meridian.userId', storedUserId);
    else localStorage.removeItem('meridian.userId');
    TestBed.configureTestingModule({
      imports: [App],
      providers: [{ provide: MeridianApi, useValue: { getUsers: () => of(users) } }, provideRouter([])],
    });
    const fixture = TestBed.createComponent(App);
    fixture.detectChanges();
    const select: HTMLSelectElement = fixture.nativeElement.querySelector('[data-testid="user-switcher"]');
    return { shown: select.options[select.selectedIndex]?.value, acting: TestBed.inject(SelectedUser).id() };
  }

  afterEach(() => localStorage.removeItem('meridian.userId'));

  it('shows the stored user, not the first option (bug: showed Dana while acting as Lena)', () => {
    const { shown, acting } = start('lena');
    expect(acting).toBe('lena');
    expect(shown).toBe('lena');
  });

  it('defaults to Dana when nothing is stored', () => {
    expect(start(null)).toEqual({ shown: 'dana', acting: 'dana' });
  });

  it('falls back to the first user when the stored id is not a user', () => {
    expect(start('mallory')).toEqual({ shown: 'dana', acting: 'dana' });
  });
});
