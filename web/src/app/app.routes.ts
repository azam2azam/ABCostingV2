import { Routes } from '@angular/router';
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

const r = (path: string, load: () => Promise<any>, name: string, runScoped = true) => ({
  path, canActivate: runScoped ? [moduleGuard, runGuard] : [moduleGuard],
  loadComponent: () => load().then(m => m[name]),
});

export const routes: Routes = [
  { path: 'login', component: Login },
  {
    path: '', component: Shell, canActivate: [authGuard],
    children: [
      { path: '', pathMatch: 'full', redirectTo: 'dashboard' },
      r('dashboard', overview, 'Dashboard'),
      r('profitability', overview, 'Profitability'),
      r('variance', overview, 'Variance'), r('variance/:id', overview, 'VarianceDetail'),
      r('capacity', overview, 'Capacity'),
      r('cost-centres', model, 'CostCentres'), r('cost-centres/:id', model, 'CostCentreDetail'),
      r('cost-pools', model, 'CostPools'), r('cost-pools/:id', model, 'CostPoolDetail'),
      r('resources', model, 'Resources'), r('resources/:id', model, 'ResourceDetail'),
      r('cost-drivers', model, 'Drivers'), r('cost-drivers/:id', model, 'DriverDetail'),
      r('activities', model, 'Activities'), r('activities/:id', model, 'ActivityDetail'),
      r('procedures', outputs, 'Procedures'), r('procedures/:id', outputs, 'ProcedureDetail'),
      r('packages', outputs, 'Packages'), r('packages/:id', outputs, 'PackageDetail'),
      r('drgs', outputs, 'Drgs'), r('drgs/:id', outputs, 'DrgDetail'),
      r('costing-models', exec, 'CostingModels', false),
      r('allocation-rules', exec, 'AllocationRules'),
      r('runs', exec, 'Runs'), r('runs/:id', exec, 'RunDetail'),
      r('reconciliation', exec, 'Reconciliation'),
      r('patient-costing', exec, 'PatientCosting'), r('patient-costing/:id', exec, 'EncounterDetail'),
      r('simulation', analysis, 'Simulation'), r('simulation/:id', analysis, 'ScenarioDetail'),
      r('reports', analysis, 'Reports'),
      r('data-quality', analysis, 'DataQuality'),
      r('governance', analysis, 'Governance', false),
      r('audit', analysis, 'Audit', false),
      r('administration', analysis, 'Administration', false),
      r('help', analysis, 'Help', false),
      { path: 'forbidden', component: Forbidden },
    ],
  },
  { path: '**', redirectTo: '' },
];
