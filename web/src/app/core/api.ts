import { HttpClient, HttpParams } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { Observable } from 'rxjs';
import type { components } from './api-types';

/**
 * Types come from the API's OpenAPI document (NG-11), regenerated with `npm run api:types`.
 * Nothing here re-declares the contract by hand.
 */
type S = components['schemas'];
export type Schema<K extends keyof S> = S[K];

export type Meta = S['Meta'];
export interface Envelope<T> { meta: Meta; data: T; }

export type ContextDto = S['ReadQueriesContextDto'];
export type RunListItem = S['ReadQueriesRunListItem'];
export type RunDetail = S['ReadQueriesRunDetail'];
export type DashboardDto = S['ReadQueriesDashboardDto'];
export type ProfitDto = S['ReadQueriesProfitDto'];
export type VarianceRow = S['ReadQueriesVarianceRow'];
export type VarianceDetail = S['ReadQueriesVarianceDetail'];
export type CapacityDto = S['ReadQueriesCapacityDto'];
export type CostCentreRow = S['ReadQueriesCostCentreRow'];
export type CostCentreDetail = S['ReadQueriesCostCentreDetail'];
export type PoolRow = S['ReadQueriesPoolRow'];
export type PoolDetail = S['ReadQueriesPoolDetail'];
export type ResourceRow = S['ReadQueriesResourceRow'];
export type ResourceDetail = S['ReadQueriesResourceDetail'];
export type DriverRow = S['ReadQueriesDriverRow'];
export type DriverDetail = S['ReadQueriesDriverDetail'];
export type ActivityRow = S['ReadQueriesActivityRow'];
export type ActivityDetail = S['ReadQueriesActivityDetail'];
export type ProcedureRow = S['ReadQueriesProcedureRow'];
export type ProcedureDetail = S['ReadQueriesProcedureDetail'];
export type PackageRow = S['ReadQueriesPackageRow'];
export type PackageDetail = S['ReadQueriesPackageDetail'];
export type DrgRow = S['ReadQueriesDrgRow'];
export type DrgDetail = S['ReadQueriesDrgDetail'];
export type CostingModelsDto = S['ReadQueriesCostingModelsDto'];
export type RuleRow = S['ReadQueriesRuleRow'];
export type ReconDto = S['ReadQueriesReconDto'];
export type EncounterPage = S['ReadQueriesEncounterPage'];
export type EncounterDetail = S['ReadQueriesEncounterDetail'];
export type QualityDto = S['ReadQueriesQualityDto'];
export type ScenarioDto = S['ReadQueriesScenarioDto'];
export type AuditPage = S['ReadQueriesAuditPage'];
export type UserDto = S['ReadQueriesUserDto'];
export type GovernanceDto = S['ReadQueriesGovernanceDto'];
export type ComponentDto = S['ReadQueriesComponentDto'];
export type AllocationDto = S['ReadQueriesAllocationDto'];
export type LoginResponse = S['AuthControllerLoginResponse'];
export type SessionDto = S['AuthControllerSessionDto'];

export type Params = Record<string, string | number | boolean | null | undefined>;

@Injectable({ providedIn: 'root' })
export class Api {
  private http = inject(HttpClient);
  private base = '/api/v1';

  private params(p?: Params): HttpParams {
    let hp = new HttpParams();
    for (const [k, v] of Object.entries(p ?? {})) if (v !== null && v !== undefined && v !== '') hp = hp.set(k, String(v));
    return hp;
  }

  get<T>(path: string, params?: Params): Observable<T> {
    return this.http.get<T>(`${this.base}/${path}`, { params: this.params(params) });
  }

  post<T>(path: string, body: unknown): Observable<T> {
    return this.http.post<T>(`${this.base}/${path}`, body);
  }

  /** A run-scoped read: ?run= carries the exact run the numbers came from (FR-11.13). */
  run<T>(path: string, run: string | null, params?: Params): Observable<Envelope<T>> {
    return this.get<Envelope<T>>(path, { ...params, run });
  }
}
