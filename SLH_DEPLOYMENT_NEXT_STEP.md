# SLH TMS deployment next step

Status as of 13 Aug 2026:

- `Stuart-Lyons-Haulage/slh-tms-web` latest deploy is passing.
- `Stuart-Lyons-Haulage/slh-tms-api` latest CI build/tests are passing on commit `f6a2930`.
- API deploy is blocked only by Azure Entra federated credential trust after moving from the personal GitHub account to the SLH organisation.

## Required Entra federated credential

Create or update the federated credential on the Azure app registration used by the API deploy workflow.

Use these values:

- Issuer: `https://token.actions.githubusercontent.com`
- Organization: `Stuart-Lyons-Haulage`
- Organization ID: `316358944`
- Repository: `slh-tms-api`
- Repository ID: `1321977752`
- Entity type: `Environment`
- Environment / Based on selection: `production`
- Audience: `api://AzureADTokenExchange`
- Subject identifier: `repo:Stuart-Lyons-Haulage@316358944/slh-tms-api@1321977752:environment:production`

After saving it, rerun this failed workflow:

`CD - Deploy TMS to Azure Container Apps`

GitHub URL:

`https://github.com/Stuart-Lyons-Haulage/slh-tms-api/actions`
