# Azure test environment

A live test deployment of the SaaS Accelerator, provisioned 2026-06-21.

## What's running

| Thing | Value |
|---|---|
| Resource group | `rg-saas-accelerator-test` (West Europe) |
| CustomerSite (landing + webhook + notify) | https://app-saasacc-customer-1c734f.azurewebsites.net |
| AdminSite (publisher portal) | https://app-saasacc-admin-1c734f.azurewebsites.net |
| App Service plan | `asp-saasacc-test` — Linux **B1** (both apps share it) |
| Azure SQL | server `sql-saasacc-aa3b15` (France Central), DB `AMPSaaSDB`, **Basic** |

Security posture: HTTPS-only, TLS 1.2 min, FTPS disabled, Always On. SQL admin
password is stored only in App Service settings (encrypted at rest) — not in the
repo and not in CI. SQL firewall allows Azure services (the web apps) + a
temporary admin IP.

## Redeploy pipeline (GitHub Actions → Azure)

`.github/workflows/cicd.yml` builds both apps and deploys them on every push to
`main`. Auth is **passwordless OIDC** (federated credentials) — there are no
secrets or publish profiles. The deploy identity (`github-saas-accelerator-deploy`,
an Entra app) has **Contributor on this resource group only**.

### One-time setup you must do

Add three **non-secret repository variables** (Settings → Secrets and variables →
Actions → **Variables** tab → New repository variable):

| Variable | Value |
|---|---|
| `AZURE_CLIENT_ID` | `4ea5e989-383f-487e-9c4c-cd821ff5bf8f` |
| `AZURE_TENANT_ID` | `46ddab58-a339-461b-a275-ecd23fb52eba` |
| `AZURE_SUBSCRIPTION_ID` | `5e30fb06-9e4f-42a7-b25d-8584d4e48b68` |

After that, every push to `main` redeploys. You can also trigger it manually
(Actions → "Deploy to Azure (test)" → Run workflow).

## Database migrations (run manually when the schema changes)

CI never holds DB credentials, so schema changes are applied out-of-band:

```bash
# 1. add your IP to the SQL firewall
az sql server firewall-rule create -g rg-saas-accelerator-test -s sql-saasacc-aa3b15 \
  -n my-ip --start-ip-address <YOUR_IP> --end-ip-address <YOUR_IP>

# 2. generate the idempotent script
dotnet tool restore   # in src/AdminSite
dotnet dotnet-ef migrations script --idempotent --context SaaSKitContext \
  --project src/DataAccess/DataAccess.csproj --startup-project src/AdminSite/AdminSite.csproj \
  --output schema.sql

# 3. apply it (SQL admin creds are in the CustomerSite App Service connection string)
sqlcmd -S sql-saasacc-aa3b15.database.windows.net -U saasadmin -P '<password>' -d AMPSaaSDB -i schema.sql
```

## What still needs real config (Entra + Marketplace)

The apps boot and the landing page renders, but SSO and the Marketplace token
flow use **placeholder** Entra credentials. To make a real purchase flow work:

1. Register a multi-tenant Entra app; set these App Service settings on **both**
   apps (Configuration → Application settings), replacing the `000...` placeholders:
   `SaaSApiConfiguration__TenantId`, `__ClientId`, `__ClientSecret`, `__MTClientId`.
2. In Partner Center, set the offer's **landing page URL** to the CustomerSite URL
   and the **webhook URL** to `<CustomerSite>/api/AzureWebhook`.
3. Set `WebNotificationUrl` in the `ApplicationConfiguration` table (or AdminSite)
   to your aidea `POST /api/marketplace/notify` endpoint, plus the shared HMAC secret.

## Tearing it down

```bash
az group delete -n rg-saas-accelerator-test --yes --no-wait
```

This removes everything (apps, plan, SQL) and stops all billing.
