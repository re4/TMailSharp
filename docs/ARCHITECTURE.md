# Architecture

## Request flow

ASP.NET Core serves static UI assets and a same-origin HTTP API. A cryptographically random 256-bit browser token is stored in an HttpOnly, SameSite=Strict cookie. Only its SHA-256 digest is stored with each mailbox. All mailbox, message, and attachment reads check that digest and the mailbox's expiry. Administrator authentication uses a separate signed, four-hour ASP.NET Core cookie.

The UI remembers only the selected mailbox ID and theme in local storage. It never stores authentication tokens there. It polls at six-second intervals while visible. Mail content is inserted as text or served in a sandboxed iframe after server-side allowlist sanitization. Attachments always download as `application/octet-stream` with a filename and `nosniff`.

## MongoDB

| Collection | Contents | Indexes |
| --- | --- | --- |
| `mailboxes` | Address, domain, owner hash, timestamps, active message count | Unique address; owner/expiry; TTL on expiry |
| `messages` | Per-recipient parsed MIME content, sanitized HTML, attachment bytes | Mailbox/date; unique mailbox/delivery hash; TTL on expiry |
| `domains` | Normalized domain, enabled flag, created timestamp | Domain is `_id` |
| `address_reservations` | Previously allocated addresses with reuse time | Address is `_id`; TTL on reservation expiry |

Dates are UTC BSON dates. Attachments share a message's lifecycle rather than using a separate file store. Raw MIME is capped at 5 MiB to keep parsed content and attachment documents within MongoDB's document limit. A per-mailbox atomic counter bounds stored messages. Application expiry checks are immediate; physical TTL deletion is asynchronous.

The address reservation is created before the mailbox. If a creation fails afterward, the address remains reserved rather than becoming reusable. Message count updates are compensated when insertion fails. Delivery is idempotent for identical raw MIME bytes within a mailbox using a unique hash index. Every successful store rechecks mailbox existence to clean up concurrent-deletion orphans.

There are no multi-document transactions or distributed job workers. A database/server crash between deletion of a mailbox and deletion of its messages can leave inaccessible messages until their original TTL expires. Active inbox count checks and HTTP rate limits are not distributed quotas; use gateway limits if running several application instances.

## SMTP

`SmtpReceiver` runs as an ASP.NET Core hosted service using SmtpServer. It accepts at most 20 recipients per transaction, verifies each recipient exists and is active, enforces the raw-message size, and rejects relay attempts. `MailProcessor` parses MIME using MimeKit, normalizes attachment filenames, caps attachment count at 20, sanitizes HTML, and stores a separate message for each valid recipient. No outbound SMTP functionality exists.

SMTP message ingestion is distinct from the development-only sample HTTP endpoint. Both invoke the same processor. Samples cannot be enabled outside the Development environment. STARTTLS is offered only when a PFX certificate is configured.

## Deployment boundaries

Use HTTPS and a trusted reverse proxy for the web endpoint. Persist and protect the ASP.NET Core data protection key directory. Trust forwarding headers only from known proxy addresses. Keep MongoDB private and authenticated outside local development. Use an inbound mail gateway for connection throttling, spam filtering, virus scanning, abuse controls, and production queuing.

Stored mail is not encrypted end-to-end; infrastructure administrators and backup operators may access it. Review operational logging and backup retention before making a public privacy claim. Logs intentionally avoid recording MIME bodies or administrator passwords.
