# Upgrading to v1.1.0

Back up MongoDB, the key directory, and your deployment configuration before upgrading. The app still receives mail only; permanent addresses do not provide outbound email, IMAP, or POP3.

## Accounts and storage

The site now uses MVC controllers/Razor views and ASP.NET Core Identity. Users register with a username and password, then manage their authenticator, recovery codes, passkeys, and FIDO2 security keys under `/account/security`. Email-based password recovery is not configured; keep passwords and recovery codes safe.

Identity stores password hashes, roles, security stamps, recovery codes, authenticator secrets, and public passkey credentials in SQLite. Mail remains in MongoDB. Keep both stores private and persistent; back up SQLite using a consistent database backup or stop the app before copying its database and journal files.

- Docker Compose adds the `identity_data` volume mounted at `/app/identity`.
- The Windows launcher stores account data in `.local/identity`.
- The SSH deployment defaults to an `identity` directory inside the mounted authentication-key directory. The example uses `/var/lib/tmailsharp/keys/identity`.
- Custom deployments can set `Identity__DataDirectory` or `ConnectionStrings__Identity` to a persistent SQLite database. Its directory must be writable by the app user. Use one application instance per SQLite database; this release does not provide multi-node Identity database deployment.

The initial account is `Admin__UserName` (default `admin`) with `Admin__Password` (at least 20 characters). It is created with the Administrator role on the first startup. Existing accounts are never promoted from configuration, and changing this variable does not reset an existing password. Administrators use the same sign-in and account-security pages as other users.

The former shared-password admin endpoints are removed. Do not depend on `/api/admin/login`; use the site's Identity sign-in. Administrator page operations require the Administrator role and antiforgery validation.

Login and registration now require the browser's proof-of-work security check, including the beginning of passkey sign-in. JavaScript must be enabled. Challenges expire after three minutes and cannot be reused; a restart requires a fresh check. `SecurityCheck__Difficulty` defaults to 20 leading zero bits. Higher values increase computation, especially on mobile devices, so test changes before applying them to users.

## HTTPS and security keys

Use HTTPS in production, including correctly forwarded scheme headers from your trusted proxy. The app redirects ordinary HTTP traffic to HTTPS and uses secure host-only account and guest cookies. `/health` remains available locally for readiness checks.

For Compose behind a proxy on the Docker host, set `TRUSTED_PROXY_IP` to that proxy's address as seen inside the container (often the bridge gateway). Forward `X-Forwarded-Proto: https` and preserve the public Host header. Do not trust arbitrary forwarding addresses. SSH deployments use host networking, so the local loopback proxy is trusted by default.

Set `Identity__PasskeyDomain` to the exact public website hostname. Registration and sign-in must use that hostname consistently; moving to another hostname requires new passkey enrollment. YubiKeys must support FIDO2/WebAuthn. Device PIN or biometric verification is required. Security keys are passkey sign-in methods, replacing password plus authenticator code; TOTP remains available for password-based two-factor sign-in.

The old public v1.0 signing key is automatically revoked if it is found in the configured key directory. The application and cookie purposes have also changed. Existing administrator sessions must sign in again. Keys and Identity files are excluded from Git and container build inputs.

## Mailboxes and retention

Existing MongoDB mailbox and message documents remain compatible. When a returning guest calls `/api/bootstrap`, a valid existing v1.0 guest cookie transfers their active mailboxes to a new protected guest session. Registering or signing in with a password transfers the current guest's temporary inboxes to that account. Addresses can then be made permanent with **Keep address**.

Permanent addresses stay reserved until their owner deletes them. Their mailbox documents and address reservations have no expiry timestamp, so MongoDB TTL cleanup does not remove them. Deleting a permanent address starts the configured address-reuse delay.

Messages have a separate expiry based on receipt time. Reading only sets the read flag. The administrator can choose default or per-domain retention in minutes. Changing retention applies to existing messages and may immediately remove messages older than the new limit. Background cleanup runs every 30 seconds; expired messages are inaccessible immediately.

Deleting a domain stops new inboxes and incoming deliveries for that domain. Existing addresses remain reserved, and stored messages remain readable until their retention limit. Deleting the last domain does not recreate the seeded domains on restart.

## API migration

All `/api/*` requests now require `Authorization: Bearer YOUR_API_KEY`, including reads and downloads. Register or sign in on the website, open **API keys**, and create a key with your current password. Copy it once and keep it in your client's secret settings. Cookie-only integrations must migrate to a bearer key; CSRF tokens and `X-TMail-Request` are no longer required for these API requests. The public `/docs/` reference covers mailbox operations only.

Verified keys share account-based rate limits: 60 inbox-creation requests and 600 other mailbox requests per minute by default. Configure `ApiLimits__CreatesPerMinute` and `ApiLimits__RequestsPerMinute` to change these independently. Website guests retain IP limits, and active-inbox caps are unchanged.

Startup adds the API-key table and indexes to an existing Identity database without recreating account tables. Only hashed keys and their metadata are stored. Up to 10 active keys are allowed per account. Revoked keys stop working immediately; account-security changes invalidate existing keys. API keys cannot access account management or administration. The website uses separate cookie/CSRF-protected routes and retains guest inbox support.

Mailbox responses include `isPermanent`; `expiresAt` is null for permanent addresses. `POST /api/mailboxes` accepts `permanent: true`. `POST /api/mailboxes/{id}/reserve` converts an owned temporary address into a permanent one.

## Verification

Run `dotnet test tests/TMailSharp.Tests/TMailSharp.Tests.csproj -c Release` against a development MongoDB instance. Tests use isolated SQLite files and disposable MongoDB databases. They cover ownership, CSRF, registration, administrator access, 2FA recovery, permanent reservations, retention, message reopening, and a cryptographically simulated FIDO2 authenticator with invalid-origin and replay rejection.
