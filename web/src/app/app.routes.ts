import { CanActivateFn, Routes } from '@angular/router';
import { authGuard } from './core/auth';
import { moduleGuard, runGuard } from './core/context';
import { Forbidden, Login } from './layout/login';
import { Shell } from './layout/shell';

// NG-1: each feature area is a lazy chunk; every module passes the central permission guard (FR-0.4).
const overview = () => import('./features/overview');
const model = () => import('./features/cost-model');
const outputs = () => import('./features/outputs');
const exec = () => import('./features/execution');
const analysis = () => import('./features/analysis');
const data = () => import('./features/data');

const r = (path: string, load: () => Promise<any>, name: string, _runScoped = true, extra: CanActivateFn[] = []) => ({
  path, canActivate: [moduleGuard, runGuard, ...extra],
  loadComponent: () => load().then(m => m[name]),
});

export const routes: Routes = [
  { path: 'login', component: Login },
  {
    path: '', component: Shell, canActivate: [authGuard],
    children: [
      { path: '', pathMatch: 'full', redirectTo: 'dashboard' },
      r('dashboard', overview, 'Dashboard'),
      { path: 'profitability', redirectTo: 'dashboard' },
      r('cost-centres', model, 'CostCentres'), r('cost-centres/:id', model, 'CostCentreDetail'),
      r('resources', model, 'Resources'), r('resources/:id', model, 'ResourceDetail'),
      r('activities', model, 'Activities'), r('activities/:id', model, 'ActivityDetail'),
      r('procedures', outputs, 'Procedures'), r('procedures/:id', outputs, 'ProcedureDetail'),
      r('packages', outputs, 'Packages'), r('packages/:id', outputs, 'PackageDetail'),
      r('costing-models', exec, 'CostingModels', false),
      r('direct-costing', exec, 'DirectCosting'),
      r('indirect-costing', exec, 'IndirectCosting'),
      r('periods', exec, 'Periods', false),
      { path: 'procedure-costing', redirectTo: 'direct-costing' },
      r('runs', exec, 'Runs'), r('runs/:id', exec, 'RunDetail'),
      r('patient-encounters', exec, 'PatientEncounters'), r('patient-encounters/:id', exec, 'EncounterDetail'),
      { path: 'patient-costing', redirectTo: 'patient-encounters' }, { path: 'patient-costing/:id', redirectTo: 'patient-encounters/:id' },
      r('reports', analysis, 'Reports'),
      r('audit', analysis, 'Audit', false),
      r('data-entry', data, 'DataEntry', false),
      r('data-import', data, 'DataImport', false),
      r('administration', analysis, 'Administration', false),
      r('help', analysis, 'Help', false),
      { path: 'forbidden', component: Forbidden },
    ],
  },
  { path: '**', redirectTo: '' },
];
