/** Local `ng serve` talks to localhost. Deployed UI calls the API host for that environment. */
const localApi = 'http://localhost:5230';
const flyApi = 'https://verita-abc-api.fly.dev';
const azureApi = 'https://app-verita-pilot-api.azurewebsites.net';

/**
 * A deployment can name its API in index.html: `<meta name="verita-api" content="/verita-api">`
 * (relative to this host) or a full URL. The IIS packaging script fills it in; empty means "decide by host".
 */
function configuredApi(): string | null {
  const value = globalThis.document?.querySelector('meta[name="verita-api"]')?.getAttribute('content')?.trim();
  if (!value) return null;
  const base = value.replace(/\/+$/, '');
  return base.startsWith('/') ? `${globalThis.location.origin}${base}` : base;
}

function apiOrigin(): string {
  const configured = configuredApi();
  if (configured) return configured;
  const host = globalThis.location?.hostname;
  if (!host || host === 'localhost' || host === '127.0.0.1') return localApi;
  if (host.endsWith('.fly.dev')) return flyApi;
  return azureApi;
}

export const apiV1 = `${apiOrigin()}/api/v1`;
