# Azure App Service Deployment with GitHub Actions OIDC

## Required GitHub Actions secrets
Add these repository secrets in GitHub Settings > Secrets and variables > Actions:

- `AZURE_CLIENT_ID`: The Application (client) ID of the Azure AD app registration used for GitHub OIDC.
- `AZURE_TENANT_ID`: `5aec48a1-c3c7-4cfd-a073-b38ae50041b1`
- `AZURE_SUBSCRIPTION_ID`: The Azure subscription ID containing the target App Service.
- `AZURE_RESOURCE_GROUP`: The Azure resource group containing the target App Service.
- `AZURE_WEBAPP_NAME`: The App Service name.

## Deployment process
1. Create or reuse an Azure AD App Registration for CI/CD.
2. Add a federated credential under Certificates & secrets > Federated credentials:
   - Issuer: `https://token.actions.githubusercontent.com`
   - Subject: `repo:<GITHUB_OWNER>/<REPO>:ref:refs/heads/main`
   - Audience: `api://AzureADTokenExchange`
3. Assign the service principal a role with deployment permissions:
   - Recommended: `Contributor` at the App Service resource group or web app scope.
4. Do not store client secrets in GitHub.

## Workflow details
- `.github/workflows/ci.yml` runs on every push and pull request.
- `.github/workflows/deploy.yml` publishes the app when code lands on `main`.
- The deployment workflow uses OIDC via `azure/login@v1` and does not require a GitHub-stored client secret.

## App settings required at runtime
Set these in Azure App Service configuration, not in source control:
- `Entra:TenantId` = `5aec48a1-c3c7-4cfd-a073-b38ae50041b1`
- `Entra:Audience` = `api://497f6ea5-9753-43ee-8ccf-afaa0a3869c2`
- `ConnectionStrings__TmsDb` = your database connection string

## Security notes
- Do not commit client secrets, passwords, connection strings, publish profiles, or operational data.
- Use environment settings and GitHub secrets for deployment configuration.
- The API uses Microsoft Entra JWT authentication and requires the `Tms.Access` scope.
