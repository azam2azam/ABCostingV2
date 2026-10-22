/** Local `ng serve` talks to localhost. Deployed UI calls the API host for that environment. */
const localApi = 'http://localhost:5230';
const flyApi = 'https://verita-abc-api.fly.dev';
const azureApi = 'https://app-verita-pilot-api.azurewebsites.net';

function apiOrigin(): string {
  const host = globalThis.location?.hostname;
  if (!host || host === 'localhost' || host === '127.0.0.1') return localApi;
  if (host.endsWith('.fly.dev')) return flyApi;
  return azureApi;
}

export const apiV1 = `${apiOrigin()}/api/v1`;
