/** Always call the API host so the Network tab shows the real URL, not the UI origin. */
const localApi = 'http://localhost:5230';
const deployedApi = 'https://app-verita-pilot-api.azurewebsites.net';

function localHost(): boolean {
  const host = globalThis.location?.hostname;
  return !host || host === 'localhost' || host === '127.0.0.1';
}

export const apiV1 = `${localHost() ? localApi : deployedApi}/api/v1`;
