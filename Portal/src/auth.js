import { PublicClientApplication } from '@azure/msal-browser';

const clientId = import.meta.env.VITE_ENTRA_CLIENT_ID;
const tenantId = import.meta.env.VITE_ENTRA_TENANT_ID;

export const apiScope = import.meta.env.VITE_TMS_API_SCOPE;
export const apiUrl = import.meta.env.VITE_TMS_API_URL;

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
