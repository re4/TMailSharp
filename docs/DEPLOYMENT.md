# Manual deployment with GitHub Actions

TMailSharp follows the deployment pattern in GravHosting: build an image in GitHub Actions, transfer it over SSH, and run it with host networking against MongoDB on the server. No container registry is required.

## What triggers a deployment

`.github/workflows/deploy-tmailsharp.yml` has only a manual `workflow_dispatch` trigger. To deploy, select **Actions → Deploy TMailSharp → Run workflow**, choose **main**, and start the run. There are no push, pull-request, scheduled, or other automatic triggers. `.github/workflows/build.yml` is reusable only and cannot start independently.

Before the server is contacted, the reusable build workflow restores locked dependencies, runs the integration tests against a temporary MongoDB service, publishes the app, and runs the deployment/rollback tests. A failed test prevents deployment.

The deployment job uses the **Auto Deploy** GitHub environment, matching GravHosting's existing name. Both workflows independently require a manual event, the main branch, a non-fork repository, and an approved repository ID. Forks, pull requests, other events, and other refs skip all jobs, including validation. Checkouts use the exact commit selected by the manual run and do not persist GitHub credentials in the checkout.

## Allow the original repository

Under **Settings → Secrets and variables → Actions → Variables**, create the repository-level variable `TMAIL_REPOSITORY_ID` with the original repository's numeric GitHub ID. Use a repository variable, not an environment variable, because it is checked before an environment is entered. If it is missing or does not match, every job is skipped.

You can obtain the ID from the original repository's GitHub API response, or with the GitHub CLI:

```sh
gh api repos/OWNER/TMailSharp --jq .id
```

Replace `OWNER/TMailSharp` with your actual repository. The ID is not a secret. Forks receive different IDs and are also rejected explicitly using GitHub's fork metadata.

These restrictions protect the shipped workflows. A fork owner controls their own copy and can remove restrictions or create new workflows; upstream cannot disable Actions in independently owned repositories. Forks do not receive your deployment environment's SSH secrets. The GitHub workflow-run record may still appear for a manual attempt in a fork, but no jobs in these unchanged workflows run.

Protect main and the workflow/deployment files through repository branch rules and code review. Keep SSH secrets scoped to **Auto Deploy**, restrict that environment to main, and enable required reviewers/prevent self-review where your GitHub plan supports them. Do not add `pull_request_target` or execute unreviewed PR code in a privileged workflow. These repository settings require configuration on GitHub and cannot be enforced merely by editing YAML files in this local folder.

## GitHub environment secrets

Create an environment named **Auto Deploy** in the TMailSharp repository under **Settings → Environments**, with the same SSH secret names used by GravHosting:

| Secret | Value |
| --- | --- |
| `SSH_HOST` | Linux server address |
| `SSH_USERNAME` | Deployment account |
| `SSH_KEY` | Deployment account's SSH private key |
| `SSH_PASSPHRASE` | Private key passphrase, if encrypted |
| `SSH_PORT` | SSH port; defaults to 22 when omitted |
| `SSH_FINGERPRINT` | Optional SHA-256 SSH host-key fingerprint for server verification |

Environment secrets belong to their repository; creating a new repository does not automatically copy GravHosting's environment secrets. If you use organization-level secrets, grant the TMailSharp repository access. Use GitHub's secret settings rather than committing any of these values.

The SSH account needs Docker access, permission to read the server environment file, and permission to open `.deploy.lock` in the key directory. If migrating an existing native `tmailsharp.service`, it also needs permission to stop/start that service. A root deployment account, as commonly used for this deployment pattern, has these permissions; a restricted account needs equivalent explicit permissions.

## One-time Linux server setup

The server needs Docker Engine, Bash, curl, sha256sum, and flock, plus an existing reachable MongoDB deployment. The workflow deploys only the application container; it does not install Docker, create MongoDB, change firewall rules, or modify DNS.

Run once on the server:

```sh
# Skip user creation if tmailsharp already exists.
id tmailsharp >/dev/null 2>&1 || sudo useradd --system \
  --home-dir /var/lib/tmailsharp --no-create-home \
  --shell /usr/sbin/nologin tmailsharp

sudo install -d -m 0750 -o tmailsharp -g tmailsharp /var/lib/tmailsharp/keys
sudo install -d -m 0700 /etc/tmailsharp
```

Copy `deploy/tmailsharp.env.example` from this repository to `/etc/tmailsharp/tmailsharp.env`, restrict it to the deployment account, and edit it on the server. Do not overwrite an existing populated configuration during an update.

For an initial root-owned configuration file:

```sh
sudo install -m 0600 deploy/tmailsharp.env.example /etc/tmailsharp/tmailsharp.env
sudoedit /etc/tmailsharp/tmailsharp.env
```

Set at least:

- `Mongo__ConnectionString`: point to your existing MongoDB with a dedicated application's `readWrite` credentials. With host networking, `127.0.0.1:27017` refers to the server's MongoDB. The example uses authentication against the `tmailsharp` database; change `authSource` if your database user lives elsewhere.
- `Admin__Password`: a random password of at least 20 characters.
- `AllowedHosts`: include your web hostname and `localhost` for local readiness checks.
- `Mail__Domains__0`, `Mail__Domains__1`, and `Mail__Domains__2`: replace the examples with domains you own. Repeating the same domain is supported and avoids inheriting `.localhost` seeds. Domains are only seeded on the first start; manage later changes in `/admin`.
- `DataProtection__KeyDirectory`: use `/var/lib/tmailsharp/keys`, or another existing absolute directory writable by the `tmailsharp` account. Keep its contents across deployments.

The file uses literal Docker `KEY=value` syntax. Do not use `export`, shell substitutions, or surrounding quotes. Percent-encode special characters in a MongoDB username or password inside the connection URI. GitHub receives SSH credentials only; application/database secrets remain in this file on the server.

## Ports and HTTPS

`Deploy__HttpPort=5080` selects the local HTTP listener. This differs from GravHosting's port 5000, so both applications can run on the same server. Put your existing HTTPS proxy in front of `http://127.0.0.1:5080`, preserve the Host header, and forward `X-Forwarded-Proto` and `X-Forwarded-For`. An HTTP proxy on loopback is trusted by default; configure exact trusted proxy addresses for a remote proxy.

`Deploy__HealthHost=localhost` is sent as the Host header for deployment probes. If you remove `localhost` from `AllowedHosts`, change this setting to a hostname that is allowed. It is also saved in the container's configuration so rollback can probe the old app using its original settings.

SMTP uses the `Smtp__Host` and `Smtp__Port` values in the environment file. Keep the default loopback binding when using a local mail gateway. To accept direct incoming mail, configure the receiver/network to accept public port 25 and configure the domain's MX records as described in [README.md](../README.md#receiving-real-internet-email). A server cannot bind two mail receivers to the same address and port.

For STARTTLS, put the PFX file inside the mounted key directory and configure its path/password in the environment file. Files outside that directory are not mounted into this application container automatically.

## What happens on each deployment

1. Actions builds `tmailsharp:<commit SHA>`, saves the image to an archive, and generates its SHA-256 checksum.
2. The archive, checksum, and deployment script are copied to a unique `/tmp/tmailsharp-<SHA>-<run>-<attempt>` directory on the server.
3. The script validates the environment, takes a deployment lock, checks the archive's checksum, and loads the image. It verifies that the non-root app user can write to the persistent key directory before stopping the existing app.
4. The existing TMailSharp container is stopped and renamed temporarily, preserving its full original configuration. An active native `tmailsharp.service` can also be migrated on the first container deployment.
5. The replacement starts with host networking and a loopback web listener. Its `/health` endpoint must report healthy MongoDB/SMTP, and `/app.js` must contain the frontend entry point.
6. Only a ready image is promoted to `tmailsharp:current`. The old image is retained as `tmailsharp:previous`; the stopped old container is then removed.
7. If startup, readiness, or promotion fails, the script restores the previous container with its original ports, environment, mounts, and key path. If the deployment was a migration from a native service, that service is restarted on failure. The Actions run stays failed even when rollback succeeds.

There is a brief interruption while the old app is stopped and the new one starts. This is not a rolling or zero-downtime deployment. Queueing incoming mail at an upstream gateway avoids losing mail during restarts.

Only TMailSharp's container, tags, temporary archive, and optional `tmailsharp.service` are managed. The script does not prune the server's Docker images, change GravHosting, or remove MongoDB data. Retain any additional images you need for manual rollback and manage unused images separately.

## Verification and recovery

On the server:

```sh
docker ps --filter name=TMailSharp
docker logs --tail 80 TMailSharp
curl --fail --header 'Host: localhost' http://127.0.0.1:5080/health
```

Use your configured port and health host if they differ. If a deployment stops halfway and leaves `TMailSharp-rollback`, the next deployment aborts before replacing anything. Inspect both containers and the Actions logs, then decide which instance to keep. Do not remove the rollback container until you have verified the running app.

The local checks are:

```sh
bash -n scripts/deploy-linux.sh
bash tests/deployment/run.sh
```

The test harness substitutes fake Docker, HTTP, and systemd commands. It verifies successful promotion, persistent keys, rollback with the original port, failed readiness, promotion failure, missing configuration, corrupt archives, key permissions, overlapping deployments, and native-service recovery without contacting a real server. A real image build and remote deployment require Docker and the configured GitHub environment.
