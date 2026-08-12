# Stuart Lyons Haulage TMS API

Backend-first .NET 8 API for authenticated master-data lookups and approval-based import staging. Routes are versioned beneath `/api/v1`. Inbox and form data never writes directly to live tables.

## Architecture

Power Automate submits an item to `POST /api/v1/staging`. SQL stores the original JSON and a unique idempotency key as `PendingReview`. An approval flow calls the approve or reject route. Approval promotes supported entities inside the API; rejection retains the audit record. The `order` entity is staged now, but its promotion is intentionally deferred until the final TMS order aggregate is agreed.

## Azure components

1. Resource group: `slh-tms-prod-rg`, UK South.
2. Linux Azure App Service, .NET 8, HTTPS only, minimum TLS 1.2.
3. Azure SQL Database for live and staged records.
4. Application Insights for logs and correlation diagnostics.
5. Microsoft Entra app registration for the API.
6. Microsoft Entra client registration used by the Power Automate custom connector.
7. Key Vault for deployment secrets. Prefer App Service managed identity for SQL in production.

The fixed API host will be `https://<globally-unique-app-name>.azurewebsites.net`. A branded hostname can be added later without changing the routes.

## Entra ID setup

### API registration

1. Azure portal > Microsoft Entra ID > App registrations > New registration.
2. Name: `SLH TMS API`. Select single tenant. No redirect URI.
3. Copy Tenant ID and Application client ID.
4. Expose an API > set Application ID URI to `api://<API-CLIENT-ID>`.
5. Add delegated scope `Tms.Access`, consent for Admins and users.
6. App roles > create `Tms.Write`, `Tms.Approve`, and `Tms.Admin`; allowed member type `Users/Groups` and `Applications`.
7. Assign the appropriate roles through Enterprise applications. Planners need `Tms.Write`; approvers need `Tms.Approve`; administrators may use `Tms.Admin`.

### Connector client registration

1. Create app registration `SLH Power Automate Connector`, single tenant.
2. Authentication > Web > add the redirect URL displayed by the custom connector after it is saved.
3. API permissions > My APIs > SLH TMS API > delegated permission `Tms.Access`; grant admin consent.
4. Certificates & secrets > create a short-lived client secret. Copy it once and enter it into the connector security page. Rotate it before expiry.

## App Service configuration

Set these environment variables:

| Name | Value |
| --- | --- |
| `Entra__TenantId` | Your tenant GUID |
| `Entra__Audience` | `api://<API-CLIENT-ID>` |
| `ConnectionStrings__TmsDb` | Azure SQL connection string; use Key Vault reference or managed identity |
| `Integrations__SageHr__Enabled` | `true` when Sage HR driver sync should be available |
| `Integrations__SageHr__BaseUrl` | Sage HR API base URL |
| `Integrations__SageHr__ApiKey` | Sage HR API token, preferably Key Vault reference |
| `Integrations__TextBee__Enabled` | `true` when TextBee duty-phone SMS dispatch is ready |
| `Integrations__TextBee__ApiKey` | TextBee API key, preferably Key Vault reference |
| `Integrations__TextBee__DeviceId` | TextBee device ID for the duty phone |
| `Integrations__TextBee__DutyPhoneLabel` | Friendly label shown in Admin, e.g. `SLH duty phone` |
| `Integrations__Fleetio__Enabled` | `true` when Fleetio service/VOR integration is ready |
| `Integrations__Fleetio__ApiKey` | Fleetio API key, preferably Key Vault reference |
| `Integrations__Fleetio__AccountToken` | Fleetio account token, preferably Key Vault reference |

Deploy the API, run EF Core migrations, then verify `GET https://<host>/health` returns HTTP 200. Do not put SQL passwords or API secrets in source control.

`GET /api/v1/diagnostics/tables` verifies each operational table without exposing data. Use it when portal pages show a generic request failure.

## Custom connector: exact setup

1. Power Automate > More > Discover all > Custom connectors > New custom connector > Import an OpenAPI file.
2. Import `openapi-power-automate.yaml` after replacing its three placeholders.
3. General:
   - Scheme: `HTTPS`
   - Host: `<app-name>.azurewebsites.net` only, with no `https://` and no trailing slash
   - Base URL: `/api/v1`
4. Security:
   - Authentication type: OAuth 2.0
   - Identity provider: Azure Active Directory
   - Client ID: connector client registration ID
   - Client secret: connector client secret
   - Authorisation URL: `https://login.microsoftonline.com/<TENANT-ID>/oauth2/v2.0/authorize`
   - Token URL: `https://login.microsoftonline.com/<TENANT-ID>/oauth2/v2.0/token`
   - Refresh URL: same as Token URL
   - Scope: `api://<API-CLIENT-ID>/Tms.Access`
5. Save connector. Copy its generated redirect URL into the connector client registration under Authentication > Web, then save the connector again.
6. Definitions should contain `GetCustomers`, `GetVehicles`, `GetDrivers`, `SubmitStagedImport`, `GetStagedImport`, `ApproveStagedImport`, and `RejectStagedImport`.
7. Test > New connection > sign in > run `SubmitStagedImport`. A successful new item returns HTTP 202 and `PendingReview`.

## Power Automate intake flow

1. Trigger: `When a new email arrives in a shared mailbox (V2)`, `When an item is created`, or `Manually trigger a flow`.
2. Action: `Compose - Idempotency Key`. For email use the message ID; for SharePoint use `concat('sharepoint:', triggerBody()?['ID'])`.
3. Action: `Compose - Staging Payload`. Create the normalised fields extracted from the source.
4. Action: `SLH TMS API - Submit a staged import`:

```json
{
  "entityType": "order",
  "idempotencyKey": "@{outputs('Compose_-_Idempotency_Key')}",
  "source": "PowerAutomate/InfoMailbox",
  "payload": {
    "customerCode": "@{variables('CustomerCode')}",
    "poNumber": "@{variables('PoNumber')}",
    "collectionDate": "@{variables('CollectionDate')}",
    "deliveryDate": "@{variables('DeliveryDate')}",
    "pallets": "@{variables('Pallets')}",
    "sourceEmailId": "@{triggerBody()?['id']}"
  }
}
```

5. Action: `Parse JSON - Staging response`, using:

```json
{
  "type": "object",
  "properties": {
    "stagingId": { "type": "string" },
    "status": { "type": "string" },
    "receivedAtUtc": { "type": "string" },
    "reviewUrl": { "type": "string" }
  },
  "required": ["stagingId", "status", "receivedAtUtc", "reviewUrl"]
}
```

6. Action: `Start and wait for an approval`. Include the staged ID and a human-readable summary. Do not include confidential raw payloads in approval notifications.
7. Condition: Outcome is `Approve`.
   - Yes: `SLH TMS API - Approve a staged import`, ID = parsed `stagingId`, note = approval comments.
   - No: `SLH TMS API - Reject a staged import`, ID = parsed `stagingId`, note = approval comments.
8. Add failure scopes. Configure `Scope - Handle Failure` to run after failed or timed out. Record the flow run ID, source ID, HTTP status and correlation ID. Do not automatically resubmit a rejected item.

## Fast HTTP prototype

Use the premium `HTTP` action with Method `POST`, URI `https://<host>/api/v1/staging`, Authentication `Active Directory OAuth`, tenant ID, audience `api://<API-CLIENT-ID>`, client ID and secret. Header `Content-Type: application/json`; body is the same JSON above. Store the secret in a connection reference or environment variable, never directly in flow actions. Move to the custom connector before production.

## Local/deployment commands

```bash
dotnet restore
dotnet tool install --global dotnet-ef
dotnet ef migrations add InitialCreate
dotnet ef database update
dotnet run
```

Deploy infrastructure with the included Bicep template, then publish the API through your controlled CI/CD process. The template starts with a Basic App Service and Basic Azure SQL database; review Azure pricing before deployment.
