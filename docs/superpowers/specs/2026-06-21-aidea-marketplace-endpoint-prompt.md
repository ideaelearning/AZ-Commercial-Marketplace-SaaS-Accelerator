# Prompt for aidea — Build the Azure Marketplace provisioning endpoint

Paste everything below into aidea.

---

Build a new backend feature: **receive Azure Marketplace SaaS subscription notifications and open an aidea organization on purchase.** We sell aidea as a license-only SaaS offer through Azure Marketplace (Azure handles billing; we provision the tenant on our side; no usage metering). A Microsoft "SaaS Accelerator" app sits in front of Azure Marketplace and will POST a JSON notification to one of our endpoints on each event. Your job is the receiving endpoint + org provisioning.

## 1. Endpoint

- `POST /api/marketplace/notify`
- Accepts `Content-Type: application/json`.
- Must **read the raw request body** (you need the exact bytes for signature verification — do this before any model binding/deserialization).
- Must **always return HTTP 2xx quickly** on a well-formed, authentic request. The caller is fire-and-forget and never retries; a non-2xx is silently dropped. Do the heavy work (org creation) but don't let a slow downstream turn into a 5xx — if creation can be slow, enqueue it and return 200.

## 2. Authentication (mandatory — this endpoint creates organizations)

- The caller sends header `X-Aidea-Signature: <hex>` where `<hex> = HMAC-SHA256(secret, rawBodyBytes)`, lowercase hex.
- Load `secret` from config/secret store as `MARKETPLACE_NOTIFY_SECRET`.
- Recompute the HMAC over the raw body and compare in **constant time**. On mismatch or missing header → return `401` and do nothing else.
- (We will configure the SaaS Accelerator to send this exact header. Until that patch ships, support a config flag `MARKETPLACE_NOTIFY_REQUIRE_SIGNATURE=true|false` so we can test against the stock accelerator, but default it to `true`.)

## 3. Request contract (real payload shape)

The accelerator emits this JSON. Property names are exactly as below (camelCase). Two event types share one endpoint, discriminated by `eventType`.

```jsonc
{
  "applicationName": "aidea",
  "eventType": "LandingPage",          // "LandingPage" | "Webhook"
  "payloadFromLandingpage": {          // populated when eventType == "LandingPage"
    "landingpageSubscriptionParams": [ // custom fields collected on the landing page (may be [])
      { "key": "Company name", "value": "Contoso" }
    ],
    "id": "f1e2d3c4-...-guid",          // SUBSCRIPTION ID (Marketplace) — your dedupe key
    "publisherId": "...",
    "offerId": "aidea-saas",
    "name": "Contoso subscription",
    "saasSubscriptionStatus": "Subscribed",
    "planId": "pro-monthly",
    "quantity": 5,
    "purchaser":   { "emailId": "buyer@contoso.com",  "tenantId": "<entra-guid>", "objectId": "<entra-guid>" },
    "beneficiary": { "emailId": "admin@contoso.com",  "tenantId": "<entra-guid>", "objectId": "<entra-guid>", "puid": "..." },
    "term": { "startDate": "2026-06-21T00:00:00Z", "endDate": "2026-07-21T00:00:00Z", "termUnit": "P1M" }
  },
  "payloadFromWebhook": {              // populated when eventType == "Webhook"
    "action": "Unsubscribe",          // Unsubscribe | Suspend | Reinstate | ChangePlan | ChangeQuantity | Renew | Transfer
    "subscriptionId": "f1e2d3c4-...-guid",
    "offerId": "aidea-saas",
    "planId": "pro-monthly",
    "quantity": 5,
    "publisherId": "..."
  }
}
```

Note: the unused branch is serialized as `{}` (empty object), not null. Treat empty-object as "not present." Exact nested casing for `purchaser`/`beneficiary`/`term` should be confirmed by logging the first real payload, but build against the above.

## 4. Behavior

**`eventType == "LandingPage"` → provision the org (idempotent):**
- Dedupe key = `payloadFromLandingpage.id` (the subscription id). Also store `beneficiary.tenantId`.
- **One org per beneficiary Entra tenant.** Upsert logic:
  - If an org already exists for `beneficiary.tenantId`: attach this subscription to it (update plan/quantity/term, status active) — do **not** create a duplicate.
  - Else: create a new aidea organization. Org owner = `beneficiary.emailId` (identity = `beneficiary.objectId` + `beneficiary.tenantId`).
- Persist a `MarketplaceSubscription` record: `subscriptionId (unique)`, `tenantId`, `offerId`, `planId`, `quantity`, `term.startDate/endDate/termUnit`, `status`, `orgId`, raw payload, timestamps.
- Idempotency: receiving the same `subscriptionId` again must NOT create a second org or second subscription row — upsert by `subscriptionId`.

**`eventType == "Webhook"` → record + alert only (no automatic org change):**
- Persist the event (subscriptionId, action, timestamp, raw payload) to a `MarketplaceEventLog`.
- Emit an alert/notification to ops (e.g. log at warn + whatever alert channel aidea uses) — especially for `Suspend` and `Unsubscribe`.
- Do **not** disable, delete, or modify the org automatically. A human decides. Return 200.

## 5. Persistence (suggested, adapt to aidea's ORM)

- `marketplace_subscription` — unique index on `subscription_id`; index on `tenant_id`. Columns per §4.
- `marketplace_event_log` — append-only; `subscription_id`, `event_type`, `action`, `received_at`, `raw_payload`.
- Map `planId` → aidea entitlement/seats using `quantity`. (Define the plan→features mapping with the product owner; for now store planId + quantity and grant a sane default.)

## 6. Edge cases

- Duplicate / replayed LandingPage notification → upsert, no duplicate org (test this).
- Second purchase from the same `beneficiary.tenantId` → same org, additional subscription row (per "one org per tenant").
- `beneficiary` may differ from `purchaser` (reseller/CSP) — anchor on **beneficiary**.
- Empty `landingpageSubscriptionParams` is normal.
- Unknown/extra JSON fields must be ignored, not rejected (forward-compat).
- Malformed JSON or failed signature → 401/400, log, no side effects.

## 7. Acceptance tests (write these)

1. Valid signed LandingPage payload → 200, org created, subscription row present, owner = beneficiary email.
2. Same payload sent twice → still exactly one org and one subscription row (idempotent).
3. Second LandingPage with same `beneficiary.tenantId`, different `subscriptionId` → same org, two subscription rows.
4. Webhook `Unsubscribe` payload → 200, event logged, alert emitted, org untouched.
5. Bad/missing `X-Aidea-Signature` → 401, no DB writes.
6. Payload with extra unknown fields → still 200 and processed.

## 8. Deliverables

- The endpoint + handlers + persistence + the 6 tests above.
- A `.env`/config entry `MARKETPLACE_NOTIFY_SECRET` and `MARKETPLACE_NOTIFY_REQUIRE_SIGNATURE`.
- A short README snippet: how to point the SaaS Accelerator's `WebNotificationUrl` at this endpoint and set the shared secret.

Keep it minimal and idiomatic to aidea's existing stack. Don't add metering, billing, or Azure-resource logic — none of that applies.
