# Outbound email - delivery paths, retries, bounces

## Paths
| Mail | Path | Retry / idempotency |
|---|---|---|
| Notifications (payslip ready, leave decisions, approvals, ...) | `NotificationService` -> `notification_deliveries` outbox -> `NotificationDeliveryWorker` | Dedupe key (unique per tenant), lease, exponential back-off, dead-letter, ambiguous-outcome policy |
| Offer letters | `OfferDispatchWorker` | Pre-submission vs unknown outcome is distinguished; only a conclusive pre-submission failure is repeated |
| Secret-bearing mail sent inline: password reset (self-service and admin), reset link, access invitation, welcome code, MFA notices | `IEmailService.DeliverResilientAsync` / `DeliverPlatformResilientAsync` (`ResilientMail`) | In-process: up to 3 attempts for transient failures (connection/timeout/SMTP 4xx) with 0.5 s / 2 s jittered back-off; the same message (hash of tenant, kind, recipient, subject, body) accepted by the relay is not sent again for 2 minutes |
| Scheduled report delivery with attachments | `ReportScheduleWorker` | The schedule's own retry; unchanged |
| Subscription / platform-operator mail | `DeliverPlatformAsync` | Unchanged |

## Why the secret-bearing mail does not use the outbox
`notification_deliveries.body` is kept after the send (only the raw destination is cleared). A password-reset link or a one-time
welcome code stored there would leave a live credential at rest in a retained, backed-up table. Those messages therefore get
retry and de-duplication in memory for the length of the request. The trade-off: the idempotency cache is per process, so it removes
double-clicks, retried POSTs and transient relay failures, but not a crash between relay acceptance and the HTTP response, and not a
duplicate sent by two instances within the window. The user-visible fallbacks (printed slip, shareable link) cover a lost message.

## Bounces
SMTP gives `250 Accepted` and nothing more: the product reports "accepted by the mail server", never "delivered". There is **no
bounce, complaint or suppression-list ingestion** and none can exist on raw SMTP - a bounce arrives later as a message to the
envelope sender, which KynexOne does not read. Capturing bounces needs a transactional provider with a webhook (Amazon SES via SNS,
Postmark, SendGrid, Mailgun): add a `POST /api/email/webhooks/{provider}` endpoint that verifies the provider's signature,
maps the event to the `notification_deliveries` row by `provider_reference`, moves it to `failed`/`suppressed`, and records the
address on a per-tenant suppression list that the channel dispatcher checks before sending. That work is deliberately not started
until a provider is chosen (owner decision); the outbox already has the columns it needs (`provider_reference`, `outcome`, `suppressed`).
