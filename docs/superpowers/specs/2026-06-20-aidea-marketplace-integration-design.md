# aidea ⇄ Azure Marketplace Integration — Technical Design

**Date:** 2026-06-20
**Status:** Approved design (analysis session — no implementation yet)
**Scope:** Sell aidea licenses through Azure Commercial Marketplace as a **SaaS offer (license-only, no metering)**. On purchase, automatically open an aidea organization (tenant) for the buyer.

---

## 1. Goal & Constraints

- **Selling model:** Per-seat / flat license sold *over* Azure Marketplace. Azure handles billing; we do **not** deploy or charge for Azure resources, and we do **not** emit usage meters.
- **Outcome on purchase:** A new aidea organization is provisioned on our side, owned by the buyer.
- **Out of scope:** `MeteredTriggerJob` (metered billing) — unused.

### Decisions locked in this session

| Decision | Choice |
|---|---|
| Integration architecture | **Deploy the SaaS Accelerator as-is**; point its `WebNotificationUrl` at an aidea backend endpoint |
| Org-creation trigger | **On Activate** (landing-page `LandingPage` notification) |
| Identity mapping | **Beneficiary** `tenantId` → aidea org; beneficiary `email`/`objectId` → org owner |
| Endpoint trust | **Shared secret / HMAC header** (small accelerator patch) |
| Lifecycle (Suspend/Unsubscribe) | **Notify only** — aidea logs/alerts, no automatic org change |

---

## 2. System Components

```
                 Azure Marketplace (Partner Center SaaS offer)
                        │  landing page redirect (?token=...)     │ lifecycle webhooks
                        ▼                                         ▼
        ┌──────────────────────────────────────────────────────────────┐
        │            SaaS Accelerator (deployed as-is, .NET 8)          │
        │  CustomerSite: HomeController.Index  ← landing + SSO + resolve │
        │               AzureWebhookController ← lifecycle events        │
        │  Services:    FulfillmentApiService (resolve/activate)         │
        │               WebNotificationService → POST WebNotificationUrl │
        │  DataAccess:  SQL Server (subscription state, audit logs)      │
        └──────────────────────────────────────────────────────────────┘
                        │  HTTPS POST (JSON + X-Aidea-Signature)
                        ▼
        ┌──────────────────────────────────────────────────────────────┐
        │   aidea backend — new endpoint: POST /marketplace/notify      │
        │   • verify shared-secret/HMAC header                          │
        │   • EventType=LandingPage → create org (idempotent)          │
        │   • EventType=Webhook     → log/alert only                    │
        └──────────────────────────────────────────────────────────────┘
```

**Accelerator projects in play:** CustomerSite (landing + webhook), Services (Fulfillment + notification), DataAccess (SQL). AdminSite is optional (publisher ops portal). MeteredTriggerJob is **not deployed**.

---

## 3. Purchase → Provision Flow (happy path)

1. Buyer purchases the aidea SaaS offer in Azure Marketplace and clicks **Configure account**.
2. Azure redirects to the accelerator landing page with `?token=`.
3. `HomeController.Index` ([src/CustomerSite/Controllers/HomeController.cs:211](../../../src/CustomerSite/Controllers/HomeController.cs#L211)):
   - Forces Entra SSO (buyer signs in → this identity = **beneficiary**).
   - `FulfillmentApiService.ResolveAsync(token)` → subscription (offer, plan, purchaser & beneficiary tenantId/objectId/email).
   - Persists subscription + plans to SQL; shows the Activate page.
4. Buyer clicks **Activate** → `SubscriptionOperationAsync` (operation=`Activate`):
   - `PendingActivationStatusHandler.Process` → `FulfillmentApiService.ActivateSubscriptionAsync` (tells Marketplace the sub is live) → status `Subscribed`.
   - `WebNotificationService.PushExternalWebNotificationAsync(subscriptionId, params)` fires with `EventType = LandingPage` ([HomeController.cs:623](../../../src/CustomerSite/Controllers/HomeController.cs#L623)).
5. aidea `POST /marketplace/notify` receives the payload, verifies the signature, and **creates the org** keyed on the beneficiary tenantId (idempotent — see §6).

Ongoing lifecycle (ChangePlan, ChangeQuantity, Suspend, Unsubscribe, Reinstate) arrives at `AzureWebhookController`; `WebhookProcessor` ([src/Services/WebHook/WebhookProcessor.cs:53](../../../src/Services/WebHook/WebhookProcessor.cs#L53)) pushes each to the **same** `WebNotificationUrl` with `EventType = Webhook`. Per decision, aidea **logs/alerts only** on these.

---

## 4. The Integration Contract (aidea endpoint)

`WebNotificationService` already serializes `WebNotificationPayload`. Shape aidea must accept:

```jsonc
{
  "ApplicationName": "aidea",
  "EventType": "LandingPage" | "Webhook",
  "PayloadFromLandingpage": {              // present when EventType=LandingPage
    "Id": "<subscriptionId guid>",
    "OfferId": "...", "PlanId": "...", "Quantity": 5,
    "SaasSubscriptionStatus": "Subscribed",
    "Purchaser":   { "EmailId": "...", "TenantId": "...", "ObjectId": "..." },
    "Beneficiary": { "EmailId": "...", "TenantId": "...", "ObjectId": "...", "Puid": "..." },
    "Term": { "StartDate": "...", "EndDate": "...", "TermUnit": "P1M" },
    "LandingPageCustomFields": [ { "DisplayName": "...", "Value": "..." } ]
  },
  "PayloadFromWebhook": {                   // present when EventType=Webhook
    "Action": "Unsubscribe" | "Suspend" | "Reinstate" | "ChangePlan" | "ChangeQuantity",
    "SubscriptionId": "...", "PlanId": "...", "Quantity": ...
  }
}
```

**aidea endpoint behavior:**
- Verify `X-Aidea-Signature` (HMAC of raw body with shared secret) — reject otherwise.
- `EventType=LandingPage` → upsert org keyed on `Beneficiary.TenantId`; owner = `Beneficiary.EmailId`/`ObjectId`; store `subscriptionId`, `PlanId`, `Quantity`, `Term` for entitlement. Return 2xx.
- `EventType=Webhook` → record event + alert ops; **no** automatic org mutation. Return 2xx.
- Always return 2xx quickly (the POST is fire-and-forget; non-2xx is only logged by the accelerator, never retried).

---

## 5. Required Accelerator Change (only one)

`WebNotificationService.CallNotificationURL` ([src/Services/Services/WebNotificationService.cs:140](../../../src/Services/Services/WebNotificationService.cs#L140)) currently sends an **unauthenticated** POST. Add a shared-secret/HMAC header:

- Read a new app-config value (e.g. `WebNotificationSecret`) alongside `WebNotificationUrl`.
- Compute `X-Aidea-Signature = HMAC-SHA256(secret, rawBody)` and attach it to the request.
- No other accelerator code changes. Everything else is **configuration**.

> This is the single justified code change: the endpoint creates organizations, so the POST must be authenticable. (Design-discipline check: not an abstraction — one header on one existing method.)

---

## 6. Edge Cases & Reliability

- **Idempotency:** Landing-page activation can fire more than once (buyer revisits, retries). aidea must upsert on `Beneficiary.TenantId` + `subscriptionId`, not blindly create.
- **Delivery is best-effort:** The accelerator does **not** retry a failed POST. Mitigations: aidea returns 2xx fast; a reconciliation job (or AdminSite) can re-trigger. The accelerator's SQL remains the source of truth for subscription state.
- **Multiple subscriptions, same tenant:** Decide whether a second purchase from the same Entra tenant = same org (add entitlement) or a new org. **Open question — see §8.**
- **Beneficiary ≠ Purchaser** (CSP/reseller): we key on beneficiary by decision; confirm this holds for the target sales motion.

---

## 7. What It Takes to Run / Operate

| Requirement | Notes |
|---|---|
| **Entra app registration** | Multi-tenant; landing-page SSO + Fulfillment API auth (`clientid`/`clientsecret`/`tenantid` in `saasapiconfiguration`). |
| **SaaS offer in Partner Center** | Needed to get a real landing-page `token`. Can be in Preview to test end-to-end before going live. Technical config = landing page URL + webhook URL of the deployed accelerator. |
| **SQL Server** | Accelerator state store. Local dev on Mac → SQL Server in Docker. |
| **Hosting** | Two App Services (CustomerSite + AdminSite) + SQL + Key Vault, via the bundled PowerShell installer. CustomerSite is the only one strictly required for the buy-flow. |
| **Config to set** | `WebNotificationUrl` = aidea endpoint; `WebNotificationSecret` = shared secret; `IsAutomaticProvisioningSupported` = true (so Activate drives the flow). |

**Local run caveat:** Running locally lets you read/trace the flow, but exercising a *real* purchase needs a Partner Center offer issuing tokens. For pure endpoint development, aidea's `/marketplace/notify` can be tested with a captured/sample `WebNotificationPayload` JSON — no accelerator needed.

---

## 8. Open Questions (resolve before/while building aidea side)

1. **One org per Entra tenant, or per subscription?** Affects the upsert key in §4.
2. **Org naming** — derive from tenant domain, beneficiary email, or collect a custom landing-page field?
3. **Plan → aidea entitlement mapping** — how do `PlanId`/`Quantity` map to seats/features in aidea?
4. **Reconciliation** — do we want a scheduled job that reconciles aidea orgs against accelerator SQL to recover dropped notifications?

---

## 9. Build Sequence (when implementation starts)

1. Stand up accelerator infra (Entra app reg, SQL, deploy CustomerSite) — config-only.
2. Patch `WebNotificationService` to add the HMAC header (§5).
3. Build aidea `POST /marketplace/notify` (verify signature, create-org on LandingPage, log on Webhook) — idempotent.
4. Create a Preview SaaS offer in Partner Center; point landing/webhook URLs at the accelerator.
5. End-to-end test purchase via Preview → confirm org opens in aidea.
6. Resolve §8 open questions; add reconciliation if chosen.
