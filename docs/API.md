# Mailbox API

The public, browser-friendly mailbox reference is available at **`/docs/`** on your running server, linked from the inbox sidebar and footer. It includes copyable curl and JavaScript examples and does not require sign-in to read.

All responses containing mailbox data use `Cache-Control: no-store`. An opaque `tmailsharp.session` HttpOnly cookie identifies the browser session. Creating an inbox without an existing cookie sets one; persist it for subsequent requests.

Every non-GET request requires `X-TMail-Request: 1`. JSON request bodies require `Content-Type: application/json`. If `Origin` is present, it must match the web app's scheme and host. Cross-origin access is not enabled.

## Inbox endpoints

| Method | Path | Behavior |
| --- | --- | --- |
| GET | `/api/bootstrap` | Available domains, this session's active inboxes, and mailbox limits |
| POST | `/api/mailboxes` | Create an inbox; body `{ "localPart": null, "domain": "drop.localhost" }` |
| DELETE | `/api/mailboxes/{id}` | Delete an owned inbox and its messages |
| GET | `/api/mailboxes/{id}/messages` | Message summaries, newest first |
| GET | `/api/mailboxes/{id}/messages/{messageId}` | Message details; marks it read |
| GET | `/api/mailboxes/{id}/messages/{messageId}/content` | Sanitized HTML with restrictive iframe CSP |
| DELETE | `/api/mailboxes/{id}/messages/{messageId}` | Delete an owned message |
| GET | `/api/mailboxes/{id}/messages/{messageId}/attachments/{attachmentId}` | Download an owned attachment |

An inbox object includes `id`, `address`, `domain`, `createdAt`, and `expiresAt`. IDs are opaque. Neither an address nor a mailbox ID grants access without the owning session cookie. There is no public lookup by email address.

Use `localPart: null` or omit it for a random name. Custom names contain 3–40 lowercase ASCII letters, digits, dots, underscores, or hyphens and must start and end with a letter or digit. Consecutive dots and reserved address names are rejected. Use an available domain from `/api/bootstrap`.

Message summaries include `id`, `sender`, `senderName`, `subject`, `preview`, `receivedAt`, `isRead`, and `attachmentCount`. Details include `textBody`, sanitized `htmlBody`, `recipient`, and attachment metadata (`id`, `fileName`, `size`). Binary attachments are only returned by their download endpoint.

## Status codes

- `201`: inbox created.
- `204`: delete completed.
- `400`: invalid address, domain, or request body.
- `403`: missing custom request header or mismatched origin.
- `404`: unavailable/expired inbox, wrong owner, or missing message/attachment.
- `409`: address reserved or inbox limit reached.
- `429`: per-IP rate limit; wait approximately one minute.
- `503`: address generation temporarily unavailable.

Application errors use `{ "error": "Human-readable explanation" }`. Framework validation may return its standard error response.
