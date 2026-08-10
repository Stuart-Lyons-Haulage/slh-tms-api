import { PublicClientApplication } from '@azure/msal-browser';

const runtimeConfig = window.__SLH_TMS_CONFIG__ || {};
const clientId = runtimeConfig.entraClientId || import.meta.env.VITE_ENTRA_CLIENT_ID;
const tenantId = runtimeConfig.entraTenantId || import.meta.env.VITE_ENTRA_TENANT_ID;

export const apiScope = runtimeConfig.apiScope || import.meta.env.VITE_TMS_API_SCOPE;
export const apiUrl = runtimeConfig.apiUrl || import.meta.env.VITE_TMS_API_URL;

export const msalInstance = clientId && tenantId
  ? new PublicClientApplication({
      auth: {
        clientId,
        authority: `https://login.microsoftonline.com/${tenantId}`,
        redirectUri: window.location.origin
      },
      cache: { cacheLocation: 'sessionStorage' }
    })
  : null;

export const loginRequest = { scopes: apiScope ? [apiScope] : [] };
