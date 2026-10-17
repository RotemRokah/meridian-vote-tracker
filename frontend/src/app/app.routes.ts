import { Routes } from '@angular/router';
import { ImportsPage } from './imports/imports-page';
import { ImportReport } from './imports/import-report';
import { ReviewPage } from './review/review-page';
import { VotesPage } from './votes/votes-page';
import { VoteDetail } from './votes/vote-detail';
import { SnapshotsPage } from './snapshots/snapshots-page';
import { SnapshotDetail } from './snapshots/snapshot-detail';

// The 4 screens of spec 7.7, plus their detail pages
export const routes: Routes = [
  { path: '', redirectTo: 'imports', pathMatch: 'full' },
  { path: 'imports', component: ImportsPage, title: 'Imports' },
  { path: 'imports/:id', component: ImportReport, title: 'Import report' },
  { path: 'review', component: ReviewPage, title: 'Review' },
  { path: 'votes', component: VotesPage, title: 'Votes' },
  { path: 'votes/:id', component: VoteDetail, title: 'Vote' },
  { path: 'snapshots', component: SnapshotsPage, title: 'Snapshots' },
  { path: 'snapshots/:id', component: SnapshotDetail, title: 'Snapshot' },
  { path: '**', redirectTo: 'imports' },
];
