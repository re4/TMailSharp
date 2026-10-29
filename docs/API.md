# Mailbox API

The public mailbox reference is available at **`/docs/`**, with copyable curl and server-side JavaScript examples. Reading the documentation does not require sign-in. Every `/api/*` request requires an API key belonging to a registered account.

Register or sign in on the website, open **API keys** in the sidebar, and create a named key using your current password. Copy it immediately: the full key is shown once and only its SHA-256 hash is stored. An account can have up to 10 active keys. Revocation takes effect on the next request; password or account-security changes invalidate existing keys.

Send `Authorization: Bearer YOUR_API_KEY` on **every** request, including reads and attachment downloads. Use HTTPS. Cookies, query parameters, and CSRF tokens cannot replace the key. JSON bodies require `Content-Type: application/json`; no other custom header is needed. Cross-origin browser access is not enabled. Keep keys in server-side secret settings, outside URLs, public code, and browser storage.

```bash
BASE_URL='https://mail.example.com'
export TMAIL_API_KEY='YOUR_API_KEY'
curl --fail-with-body -sS -H "Authorization: Bearer $TMAIL_API_KEY" "$BASE_URL/api/bootstrap"
curl --fail-with-body -sS -H "Authorization: Bearer $TMAIL_API_KEY" \
  -H 'Content-Type: application/json' -d '{}' "$BASE_URL/api/mailboxes"
```

All keys for an account access that account's inboxes. A key never grants access to other accounts, account settings, or administration, including keys issued by an administrator. Responses containing mailbox data use `Cache-Control: no-store`.

## Inbox endpoints

| Method | Path | Behavior |
| --- | --- | --- |
| GET | `/api/bootstrap` | Available domains, the key owner's active inboxes, and mailbox limits |
| POST | `/api/mailboxes` | Create an inbox; body `{ "localPart": null, "domain": "drop.localhost" }` |
| DELETE | `/api/mailboxes/{id}` | Delete an owned inbox and its messages |
| POST | `/api/mailboxes/{id}/reserve` | Convert an owned temporary inbox to a permanent address |
| GET | `/api/mailboxes/{id}/messages` | Message summaries, newest first |
| GET | `/api/mailboxes/{id}/messages/{messageId}` | Message details; marks it read |
| GET | `/api/mailboxes/{id}/messages/{messageId}/content` | Sanitized HTML with restrictive iframe CSP |
| DELETE | `/api/mailboxes/{id}/messages/{messageId}` | Delete an owned message |
| GET | `/api/mailboxes/{id}/messages/{messageId}/attachments/{attachmentId}` | Download an owned attachment |

An inbox includes `id`, `address`, `domain`, `createdAt`, `expiresAt`, and `isPermanent`. Set `permanent: true` on creation to reserve the address; the default is a temporary inbox. Permanent inboxes have a null expiry; their addresses stay reserved until their owner deletes them. Messages expire independently on the configured retention schedule. Opening mail only marks it as read. IDs alone never grant access.

Use `localPart: null` or omit it for a random name. Custom names contain 3–40 lowercase ASCII letters, digits, dots, underscores, or hyphens and must start and end with a letter or digit. Consecutive dots and reserved address names are rejected. Use an available domain from `/api/bootstrap`.

Message summaries include `id`, `sender`, `senderName`, `subject`, `preview`, `receivedAt`, `isRead`, and `attachmentCount`. Details include `textBody`, sanitized `htmlBody`, `recipient`, and attachment metadata (`id`, `fileName`, `size`). Binary attachments are only returned by their download endpoint.

## Status codes

- `201`: inbox created.
- `204`: delete completed.
- `400`: invalid address, domain, or request body.
- `401`: missing, invalid, revoked, or invalidated API key, or an unavailable/locked account.
- `404`: unavailable/expired inbox, wrong owner, or missing message/attachment.
- `409`: address reserved or inbox limit reached.
- `429`: rate limit reached; wait the number of seconds in `Retry-After` before retrying.
- `503`: address generation temporarily unavailable.

Application errors use `{ "error": "Human-readable explanation" }`. Framework validation may return its standard error response.

By default, a verified API-key account may make 60 inbox-creation requests per minute and 600 other mailbox requests per minute. All of an account's keys share these limits, regardless of client IP. Invalid or missing keys do not receive the larger quota. Active-inbox limits still apply independently. Rate-limit errors also include `retryAfterSeconds`.
