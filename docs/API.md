# Mailbox API

The public, browser-friendly mailbox reference is available at **`/docs/`** on your running server, linked from the inbox sidebar and footer. It includes copyable curl and JavaScript examples and does not require sign-in to read.

All responses containing mailbox data use `Cache-Control: no-store`. Guest inboxes use a protected HttpOnly cookie; permanent inboxes belong to the signed-in Identity account. Persist all cookies returned by the server.

Start with `GET /api/bootstrap`. Every non-GET request requires `X-TMail-Request: 1` and `X-CSRF-TOKEN` set to its `csrfToken`, together with the cookies from that request. Refresh this token after sign-in or sign-out. JSON bodies require `Content-Type: application/json`. If `Origin` is present, it must match the app. Cross-origin access is not enabled.

## Inbox endpoints

| Method | Path | Behavior |
| --- | --- | --- |
| GET | `/api/bootstrap` | Available domains, this session's active inboxes, and mailbox limits |
| POST | `/api/mailboxes` | Create an inbox; body `{ "localPart": null, "domain": "drop.localhost" }` |
| DELETE | `/api/mailboxes/{id}` | Delete an owned inbox and its messages |
| POST | `/api/mailboxes/{id}/reserve` | Signed-in owner converts a temporary inbox to a permanent address |
| GET | `/api/mailboxes/{id}/messages` | Message summaries, newest first |
| GET | `/api/mailboxes/{id}/messages/{messageId}` | Message details; marks it read |
| GET | `/api/mailboxes/{id}/messages/{messageId}/content` | Sanitized HTML with restrictive iframe CSP |
| DELETE | `/api/mailboxes/{id}/messages/{messageId}` | Delete an owned message |
| GET | `/api/mailboxes/{id}/messages/{messageId}/attachments/{attachmentId}` | Download an owned attachment |

An inbox includes `id`, `address`, `domain`, `createdAt`, `expiresAt`, and `isPermanent`. Setting `permanent: true` on creation requires sign-in. Permanent inboxes have a null expiry; their addresses stay reserved until their owner deletes them. Messages expire independently on the configured retention schedule. Opening mail only marks it as read. IDs alone never grant access.

Use `localPart: null` or omit it for a random name. Custom names contain 3–40 lowercase ASCII letters, digits, dots, underscores, or hyphens and must start and end with a letter or digit. Consecutive dots and reserved address names are rejected. Use an available domain from `/api/bootstrap`.

Message summaries include `id`, `sender`, `senderName`, `subject`, `preview`, `receivedAt`, `isRead`, and `attachmentCount`. Details include `textBody`, sanitized `htmlBody`, `recipient`, and attachment metadata (`id`, `fileName`, `size`). Binary attachments are only returned by their download endpoint.

## Status codes

- `201`: inbox created.
- `204`: delete completed.
- `400`: invalid address, domain, or request body.
- `400`: missing or invalid request-verification token.
- `401`: sign-in required for a permanent address.
- `403`: missing custom request header or mismatched origin.
- `404`: unavailable/expired inbox, wrong owner, or missing message/attachment.
- `409`: address reserved or inbox limit reached.
- `429`: per-IP rate limit; wait approximately one minute.
- `503`: address generation temporarily unavailable.

Application errors use `{ "error": "Human-readable explanation" }`. Framework validation may return its standard error response.
