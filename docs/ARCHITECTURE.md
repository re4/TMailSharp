# Architecture

## Request flow

ASP.NET Core MVC controllers serve Razor views, the website's `/inbox/*` routes, and the public `/api/*` mailbox API. ASP.NET Core Identity provides password hashing, account lockout, role authorization, authenticator 2FA, recovery codes, and FIDO2 passkeys. Identity uses the standard Entity Framework Core SQLite store with schema version 3.

Guests receive a protected random token in an HttpOnly, SameSite=Strict cookie. Its SHA-256 digest identifies their mailboxes. Account-owned inboxes use an owner identifier derived from the authenticated Identity user ID. Every mailbox, message, and attachment operation checks ownership. Administrators use the same Identity sign-in flow and must have the Administrator role.

Website mutations validate antiforgery tokens; `/inbox/*` mutations also require the custom request header and check any supplied Origin. Public `/api/*` routes authenticate exclusively through a separate bearer-key scheme and ignore cookie identities. They do not require CSRF tokens. Both route groups reuse the same mailbox actions and ownership checks. API-key principals contain the owner's ID without administrator roles and cannot authenticate account-management routes.

API keys contain 256 random bits. SQLite stores only their SHA-256 hashes, display prefixes, owner IDs, security stamps, creation times, and revocation times. Authentication checks revocation, current account security stamp, account availability, and lockout on every request. Startup adds the table/indexes to existing Identity databases without replacing account data. Key creation requires an authenticated website session, the current password, and antiforgery validation, with a transactional limit of 10 active keys per account.

Production cookies require HTTPS. The disclosed v1.0 key is revoked on startup if found; the v1.1 application discriminator and cookie names also change.

The UI remembers only the selected mailbox ID and theme in local storage. It never stores authentication tokens there. It polls at six-second intervals while visible. Mail content is inserted as text or served in a sandboxed iframe after server-side allowlist sanitization. Attachments always download as `application/octet-stream` with a filename and `nosniff`.

The homepage is not cached, and its scripts and styles use content-versioned URLs. The server supplies the website mailbox route prefix in page metadata so the client uses the cookie-protected `/inbox` routes, including for guests. Creation is limited to 12 requests per minute per client IP, independent of cookie changes, with the configured active-inbox cap per guest owner. Rate-limit responses include `Retry-After` and `retryAfterSeconds`.

## MongoDB

| Collection | Contents | Indexes |
| --- | --- | --- |
| `mailboxes` | Address, domain, owner hash, timestamps, active message count | Unique address; owner/expiry; TTL on expiry |
| `messages` | Per-recipient parsed MIME content, sanitized HTML, attachment bytes | Mailbox/date; unique mailbox/delivery hash; TTL on expiry |
| `domains` | Normalized domain, enabled flag, created timestamp | Domain is `_id` |
| `address_reservations` | Previously allocated addresses with reuse time | Address is `_id`; TTL on reservation expiry |
| `settings` | Message retention and domain initialization state | Settings ID is `_id` |

Dates are UTC BSON dates. Permanent mailboxes and reservations omit expiry, so TTL cleanup leaves their addresses intact. Message expiry is based on receipt time and the domain override or default retention, capped by a temporary mailbox's expiry. Reading changes only the read flag. A worker removes expired messages and reconciles counters every 30 seconds. Application expiry checks are immediate.

Attachments share a message's lifecycle. Raw MIME is capped at 5 MiB, and a per-mailbox atomic counter bounds stored messages. Retention changes update existing message expiries and immediately clear messages older than the new limit.

The address reservation is created before the mailbox. If a creation fails afterward, the address remains reserved rather than becoming reusable. Message count updates are compensated when insertion fails. Delivery is idempotent for identical raw MIME bytes within a mailbox using a unique hash index. Every successful store rechecks mailbox existence to clean up concurrent-deletion orphans.

There are no multi-document transactions or distributed job workers. A database/server crash between deletion of a mailbox and deletion of its messages can leave inaccessible messages until their original TTL expires. Active inbox count checks and HTTP rate limits are not distributed quotas; use gateway limits if running several application instances.

## SMTP

`SmtpReceiver` runs as an ASP.NET Core hosted service using SmtpServer. It accepts at most 20 recipients per transaction, verifies each recipient exists and is active, enforces the raw-message size, and rejects relay attempts. `MailProcessor` parses MIME using MimeKit, normalizes attachment filenames, caps attachment count at 20, sanitizes HTML, and stores a separate message for each valid recipient. No outbound SMTP functionality exists.

SMTP message ingestion is distinct from the development-only sample HTTP endpoint. Both invoke the same processor. Samples cannot be enabled outside the Development environment. STARTTLS is offered only when a PFX certificate is configured.

## Deployment boundaries

Use HTTPS and a trusted reverse proxy. Persist and protect the Identity SQLite database and ASP.NET Core key directory. The SQLite setup supports one application instance; multi-node account storage needs a shared database provider. Trust forwarding headers only from known proxies. Configure the exact public passkey domain and do not serve untrusted content under it. Keep MongoDB private and authenticated. Use a mail gateway for connection throttling, spam filtering, virus scanning, and queuing.

Stored mail is not encrypted end-to-end; infrastructure administrators and backup operators may access it. Review operational logging and backup retention before making a public privacy claim. Logs intentionally avoid recording MIME bodies or administrator passwords.
