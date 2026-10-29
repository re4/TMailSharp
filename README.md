# TMailSharp

**Self-hosted temporary and permanent email addresses, built with ASP.NET Core MVC and MongoDB.**

Create disposable inboxes or register to reserve permanent addresses across your own domains. Receive messages and attachments, with configurable automatic message cleanup. TMailSharp includes MVC views, ASP.NET Core Identity accounts, authenticator-based 2FA, FIDO2 passkeys and YubiKeys, and an administration dashboard.

Upgrading from 1.0? Read [the v1.1.0 migration guide](docs/UPGRADING-v1.1.0.md) before deploying. Identity accounts require persistent SQLite storage alongside MongoDB and the authentication keys.

[Getting started](#getting-started) · [Receiving email](#receiving-email) · [API reference](docs/API.md) · [Deployment guide](docs/DEPLOYMENT.md) · [License](LICENSE)

![TMailSharp inbox in dark mode](docs/screenshots/inbox-dark-blue.png)

## Features

- **Multiple domains** — enable or disable your email domains in the admin dashboard.
- **Disposable addresses** — generate a random address or choose a custom name.
- **Multiple inboxes** — switch between active addresses within one browser session.
- **Automatic updates** — check for new messages every six seconds while the tab is visible.
- **Message tools** — search, track read status, delete messages, and download attachments.
- **HTML and plain text** — read sanitized HTML in an isolated frame or switch to plain text.
- **Permanent addresses** — registered users can reserve addresses and use them across devices.
- **Account security** — Identity password hashing, lockout, authenticator 2FA, recovery codes, and FIDO2 passkeys/security keys.
- **Automatic cleanup** — administrators choose default or per-domain message retention; permanent addresses remain reserved.
- **Built-in SMTP receiver** — accept incoming mail with optional STARTTLS and duplicate-delivery protection.
- **HTTP API** — create inboxes and retrieve or delete received mail from your own integrations.
- **Administration** — Identity-protected domain management, domain deletion, and retention settings.
- **Dark mode by default** — blue accents, an optional light theme, and a responsive mobile layout.

TMailSharp is **receive-only**. It does not send or reply to email, provide IMAP/POP3 access, or create a mailbox automatically for every unknown recipient.

## Getting started

Clone the repository:

```sh
git clone https://github.com/re4/TMailSharp.git
cd TMailSharp
```

Choose Docker Compose, the Windows development launcher, or an existing MongoDB installation.

### Docker Compose

Requires Docker Engine with the Compose plugin, or Docker Desktop.

```sh
cp .env.example .env
```

Edit `.env` and set two different random passwords:

| Variable | Purpose |
| --- | --- |
| `MONGO_PASSWORD` | MongoDB password; required |
| `ADMIN_PASSWORD` | Administrator password; at least 20 characters |
| `MONGO_USERNAME` | MongoDB username; defaults to `tmail` |
| `MAIL_DOMAIN` | Initial email domain; defaults to `drop.localhost` |
| `SMTP_HOSTNAME` | SMTP greeting hostname; defaults to `mail.localhost` |
| `WEB_HOSTS` | Allowed website hostnames, separated by semicolons |

A hexadecimal password avoids special-character escaping in the MongoDB URI. Keep `.env` private; it is excluded from Git and the source archive.

Start the application:

```sh
docker compose up --build -d
```

- Website: **http://localhost:5080**
- Administration: **http://localhost:5080/admin** — sign in as `admin` (or `ADMIN_USERNAME`) with the initial `ADMIN_PASSWORD` from `.env`.
- Incoming SMTP: **port 2525**.
- MongoDB: available only on the private Compose network.

MongoDB data, SQLite Identity accounts, and authentication keys are stored in separate named Docker volumes. The Compose configuration uses MongoDB 8.0, starts the app as a non-root user, and disables sample-message delivery. Put an HTTPS reverse proxy in front of the web listener for production.

To stop the services while keeping their data:

```sh
docker compose down
```

### Windows development launcher

Requires the [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0).

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\start-local.ps1
```

The launcher downloads a pinned MongoDB Community archive from the official server, verifies its SHA-256 checksum, restores the locked dependencies, builds TMailSharp, and starts both services locally.

| Service or file | Location |
| --- | --- |
| Website | `http://localhost:5080` |
| Administration | `http://localhost:5080/admin` |
| Administrator password | `.local/admin-password.txt` |
| SMTP receiver | `127.0.0.1:2525` |
| MongoDB | `127.0.0.1:27018` |
| Logs | `.local/web.log`, `.local/web-error.log`, `.local/mongo.log` |

The initial `.localhost` domains are for local testing. Use **Try it with a sample message** to explore the reader and attachment downloads before configuring real email delivery.

The Windows development database binds to loopback without authentication. Use an authenticated database for a public deployment.

Stop the local services:

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\stop-local.ps1
```

Your database and keys remain in the ignored `.local` directory. Stop a running instance before changing its ports:

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\start-local.ps1 -WebPort 5081 -MongoPort 27019 -SmtpPort 2526 -NoBrowser
```

### Existing MongoDB installation

Install the .NET 10 SDK and configure the application through environment variables before running it:

```text
Mongo__ConnectionString=mongodb://APP_USER:URI_ENCODED_PASSWORD@127.0.0.1:27017/tmailsharp?authSource=tmailsharp
Mongo__Database=tmailsharp
Admin__Password=YOUR_RANDOM_PASSWORD_AT_LEAST_20_CHARACTERS
DataProtection__KeyDirectory=/path/to/private/persistent/keys
```

Then run:

```sh
dotnet run --project src/TMailSharp.Web --urls http://localhost:5080
```

The database user needs `readWrite` access to the application database. Set `authSource` to the database where that user was created. TMailSharp creates its own collections and indexes on startup.

`Admin__UserName` (default `admin`) and `Admin__Password` create the initial Identity administrator once. Afterward, account passwords are managed in Account security; changing the environment variable does not overwrite an existing password. Without an initial password, no administrator is created. Startup fails if MongoDB or Identity storage is unavailable.

## Receiving email

Running the website does not configure internet email delivery. You need a domain you control and an SMTP route to the application.

1. Open `/admin` and add an enabled domain, such as `inbox.example.com`.
2. Create the DNS records below.
3. Forward public **TCP port 25** to TMailSharp's SMTP listener on **port 2525**, or use an inbound mail gateway.
4. Create an inbox on the website, then send a message to that exact address.

Example DNS records:

| Type | Name | Value | Priority |
| --- | --- | --- | --- |
| A | `mx.example.com` | Your mail server's public IPv4 | — |
| MX | `inbox.example.com` | `mx.example.com` | `10` |

For additional domains, create an MX record pointing to the same mail hostname and enable each domain in TMailSharp.

With a local SMTP gateway or TCP forwarder, keep the receiver on loopback:

```env
Smtp__Enabled=true
Smtp__Host=127.0.0.1
Smtp__Port=2525
Smtp__ServerName=mx.example.com
```

If the listener must be reached directly from another machine, configure its bind address and network access accordingly. Internet mail servers deliver to port 25; changing an MX record cannot select port 2525. The supplied application runs as a non-root user, so use a gateway or port forwarding for public port 25.

For STARTTLS, configure a valid PFX certificate readable by the app:

```env
Smtp__CertificatePath=/path/to/mail.pfx
Smtp__CertificatePassword=YOUR_CERTIFICATE_PASSWORD
```

Mount the certificate into the container when using Docker. The supplied SSH deployment mounts its configured authentication-key directory; a certificate placed there is available to the container. Without a certificate, SMTP accepts plaintext. Certificate renewal is managed outside TMailSharp.

Only **existing, unexpired inboxes** accept mail. Disabled domains, unknown addresses, and full inboxes reject new deliveries.

### Cloudflare

The website and HTTP API can run behind Cloudflare Tunnel. Keep the mail server's A/AAAA record **DNS only** and point the MX record at that hostname. Ordinary web tunnels do not provide public SMTP delivery; use a reachable mail server or inbound mail gateway for email.

## HTTP API

Visitors can open **`/docs/`** for the public mailbox API documentation, with curl and server-side JavaScript examples, response fields, and status codes. Reading it does not require sign-in. Using any `/api/*` endpoint requires an account-issued API key.

Register or sign in, then open **API keys** in the sidebar. Confirm your password to create a named key and copy it immediately; the full key is shown only once. Send `Authorization: Bearer YOUR_API_KEY` on every API request. Keys access only their account's mailboxes and cannot administer the server. Users can revoke them at any time; password or account-security changes invalidate existing keys.

| Method | Endpoint | Purpose |
| --- | --- | --- |
| `GET` | `/api/bootstrap` | Available domains and the key owner's inboxes |
| `POST` | `/api/mailboxes` | Create an inbox |
| `GET` | `/api/mailboxes/{id}/messages` | List received messages |
| `GET` | `/api/mailboxes/{id}/messages/{messageId}` | Read a message and mark it as read |
| `DELETE` | `/api/mailboxes/{id}/messages/{messageId}` | Delete a message |
| `DELETE` | `/api/mailboxes/{id}` | Delete an inbox |
| `POST` | `/api/mailboxes/{id}/reserve` | Reserve an owned address permanently |
| `GET` | `/api/mailboxes/{id}/messages/{messageId}/attachments/{attachmentId}` | Download an attachment |

Example body for creating an inbox:

```json
{
  "localPart": "my-temporary-inbox",
  "domain": "inbox.example.com"
}
```

Omit `localPart`, or set it to `null`, for a random address.

Start with `/api/bootstrap`, using the bearer key. JSON bodies also require `Content-Type: application/json`; API requests do not require cookies or CSRF tokens. Set `permanent: true` to keep an address. Store keys in server-side secret settings, never in URLs or public browser code. Cross-origin browser access is not enabled. There are no outbound-mail endpoints. The website continues to support guest inboxes through separate cookie-protected routes.

Guests can create and use temporary addresses on the website without an account or API key. Website creation requests are limited to 12 per minute per client IP, even across fresh guest cookies, with 10 active inboxes per guest session by default. Mutations require the session's antiforgery token. Permanent addresses and API keys still require an account.

See the [mailbox API reference](docs/API.md) for response fields and status codes.

## Configuration

TMailSharp uses ASP.NET Core configuration. Environment variables use `__` in place of `:`. Defaults are in [`appsettings.json`](src/TMailSharp.Web/appsettings.json).

| Setting | Default | Purpose |
| --- | --- | --- |
| `Mongo__ConnectionString` | `mongodb://127.0.0.1:27017` | Database connection and authentication |
| `Mongo__Database` | `tmailsharp` | Database name |
| `Mail__LifetimeMinutes` | `60` | New inbox lifetime; 5–1440 minutes |
| `Mail__MaxMailboxesPerSession` | `10` | Active inbox limit per browser session |
| `Mail__MaxMailboxesPerAccount` | `25` | Active inbox limit per registered account |
| `Mail__MessageRetentionMinutes` | `60` | Initial default message retention; afterward managed in `/admin` |
| `Mail__MaxMessageBytes` | `5242880` | Raw MIME size limit; 5 MiB |
| `Mail__MaxMessagesPerMailbox` | `100` | Stored message limit per inbox |
| `Mail__AddressReuseDelayDays` | `7` | Address reservation after scheduled expiry |
| `Smtp__Enabled` | `true` | Start the SMTP receiver |
| `Smtp__Host` | `127.0.0.1` | SMTP bind address |
| `Smtp__Port` | `2525` | SMTP listener port |
| `Admin__Password` | Empty | Initial Identity administrator password; at least 20 characters |
| `Admin__UserName` | `admin` | Initial administrator account name |
| `Identity__DataDirectory` | Private local directory, or `identity/` inside the configured key directory | Persistent SQLite Identity database |
| `Identity__PasskeyDomain` | Request hostname | Exact public hostname for FIDO2 passkeys |
| `Demo__Enabled` | `false` | Sample delivery; Development environment only |
| `DataProtection__KeyDirectory` | `.local/identity/keys` under the app content root | Persistent authentication-cookie keys |

Initial domains are read from `Mail__Domains__0`, `Mail__Domains__1`, and so on only when the domain collection is empty. After that, manage domains in `/admin`. Configuration changes do not overwrite administrator-managed domains.

Expiry is enforced immediately; MongoDB TTL cleanup and a background worker remove expired messages. Reading a message only marks it as read. Permanent addresses have no mailbox expiry, but their messages follow the retention schedule. Deleting a permanent address starts the configured reuse delay; temporary addresses stay reserved until their scheduled expiry plus that delay.

Restart the application to apply server configuration changes. With the SSH deployment, run another manual deployment after editing the server's environment file.

## Keeping private files out of the repository

Keep real credentials in environment variables or ignored local configuration. Commit only sanitized `.env.example` and `appsettings.*.example.json` templates. Authentication keys, account databases, mail exports, backups, and local settings are excluded from Git and Docker builds.

Before committing or sharing a source archive, run:

```powershell
./scripts/check-public-repo.ps1 -IncludeUntracked
```

The check rejects private file paths and common credential patterns without printing their values. The build workflow runs it for tracked files, and `scripts/package-source.ps1` runs it before creating an archive from files eligible for Git. It supplements manual review; it cannot detect every kind of secret. If a credential has already been committed, revoke or rotate it; deleting the file does not remove earlier commits or copies.

## Manual deployment with GitHub Actions

Deployments are **manual only**. Pushes and pull requests do not trigger the shipped workflows.

Maintainer setup for [re4/TMailSharp](https://github.com/re4/TMailSharp):

1. Add the **repository variable** `TMAIL_REPOSITORY_ID` with your repository's numeric GitHub ID (obtain it with `gh api repos/OWNER/REPO --jq .id`). It belongs under **Settings → Secrets and variables → Actions → Variables**, not under secrets or inside an environment.
2. Create a GitHub environment named **Auto Deploy**.
3. Add `SSH_HOST`, `SSH_USERNAME`, and `SSH_KEY` as environment secrets. Set `SSH_PORT`, `SSH_PASSPHRASE`, and `SSH_FINGERPRINT` as needed.
4. Prepare the Linux server and `/etc/tmailsharp/tmailsharp.env` using the [deployment guide](docs/DEPLOYMENT.md).
5. Select **Actions → Deploy TMailSharp → Run workflow → main**.

The workflow runs tests, builds a Docker image, transfers it over SSH, and deploys it with host networking against the server's MongoDB. It checks database/SMTP readiness and the frontend before promoting the image. If deployment fails, it restores the previous container and its original configuration when available.

All jobs require the approved repository ID, a manual event, the main branch, and a non-fork repository. Unchanged forked workflows skip their jobs. Fork owners can modify their own copies; these checks protect the original deployment rather than controlling other repositories.

See [docs/DEPLOYMENT.md](docs/DEPLOYMENT.md) for the server setup, persistent keys, port configuration, and recovery steps.

## Development and tests

The backend uses ASP.NET Core MVC controllers and Razor views. ASP.NET Core Identity stores accounts in SQLite; MongoDB stores mail. The frontend uses plain CSS and JavaScript, with no Node.js build step. Use the .NET SDK version in `global.json` (10.0.403 or a compatible newer feature band). Direct and indirect NuGet versions are managed in `Directory.Packages.props` and recorded in the project lock files.

```sh
dotnet restore tests/TMailSharp.Tests/TMailSharp.Tests.csproj --locked-mode
dotnet build src/TMailSharp.Web/TMailSharp.Web.csproj -c Release --no-restore
```

Tests require a reachable **development MongoDB instance**. They create randomly named `tmailsharp_test_*` databases, use isolated SMTP ports, and remove those test databases on completion.

```sh
TMAIL_TEST_MONGO='mongodb://127.0.0.1:27017' dotnet test tests/TMailSharp.Tests/TMailSharp.Tests.csproj -c Release
```

With the Windows local services running:

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\test-local.ps1
```

The integration suite covers inbox isolation, attachment access, HTML sanitization, expiry enforcement, TTL indexes, duplicate delivery, administrator authentication, domain disabling, request-origin checks, and real SMTP delivery. The deployment harness covers promotion and rollback without contacting a real server:

```sh
bash tests/deployment/run.sh
```

Publish the app:

```sh
dotnet publish src/TMailSharp.Web -c Release -o .artifacts/publish
```

### Project layout

```text
src/TMailSharp.Web/
  Controllers/     MVC pages and mailbox HTTP endpoints
  Views/           Razor page templates
  Identity/        Identity account database and initialization
  Configuration/   Server options
  Data/            MongoDB documents, indexes, and queries
  Services/        Address rules, MIME processing, and SMTP hosting
  wwwroot/         Interface assets

tests/
  TMailSharp.Tests/ Integration tests
  deployment/      Deployment and rollback checks

scripts/           Local launchers and deployment tools
deploy/            Server configuration template
docs/              API, architecture, and deployment guides
```

Read [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md) for the request, storage, and mail-processing design.

## Security and operational limits

- Guest sessions and registered accounts isolate inbox access, but mail is **not end-to-end encrypted**. Server and database administrators can access stored messages.
- Clearing a guest cookie loses access to its temporary inboxes. Registered users can sign back in to reach their account-owned addresses. Temporary addresses are unsuitable for accounts that need long-term recovery.
- HTML is sanitized and isolated; remote images, scripts, forms, and embedded content are blocked. Attachments are downloaded as files.
- The app includes request and storage limits, but no spam classifier, antivirus scanner, or durable inbound mail queue. Use a mail gateway for those services in a public deployment.
- Use HTTPS, authenticate MongoDB, keep database access private, and protect persistent keys and runtime configuration files.
- Application expiry does not remove infrastructure backups. Set backup and log retention policies appropriate to the service you operate.

## Troubleshooting

| Symptom | Check |
| --- | --- |
| Deployment jobs are skipped | Start a manual run on `main`; verify `TMAIL_REPOSITORY_ID` is a repository variable containing the correct numeric ID. |
| The website works but email does not arrive | Check the MX record, DNS-only mail hostname, incoming port 25, and SMTP forwarding. Create an active inbox before sending. |
| MongoDB connection fails | Check the connection string, authentication database, credentials, and database reachability from the app. |
| Administrator login is unavailable | Configure `Admin__Password` with at least 20 characters and restart/redeploy. |
| Domain configuration changes have no effect | Initial domains are seeded only once. Update existing domains in the admin dashboard. |

## Contributing

Bug reports and pull requests are welcome through [GitHub Issues](https://github.com/re4/TMailSharp/issues) and pull requests.

Keep changes focused, include reproduction steps for bugs, and run the relevant tests before submitting. Pull requests do not automatically run this repository's deployment workflow.

## License

TMailSharp's application source and original interface assets are available under the [MIT License](LICENSE). No activation service or purchase code is required.

Dependencies retain their own licenses. MongoDB Community Server is separately licensed under the SSPL; it is not covered by this project's MIT license. See [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).
