import { apiScope, apiUrl } from './auth';

export async function getPendingStaging(instance, account) {
  if (!apiUrl || !apiScope) throw new Error('Portal API configuration is incomplete.');
  const token = await instance.acquireTokenSilent({ account, scopes: [apiScope] });
  const response = await fetch(`${apiUrl}/api/v1/staging?status=PendingReview&take=100`, {
    headers: { Authorization: `Bearer ${token.accessToken}` }
  });
  if (!response.ok) throw new Error(`The review queue could not be loaded (${response.status}).`);
  return response.json();
}
