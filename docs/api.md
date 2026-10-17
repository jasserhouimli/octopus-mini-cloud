# API Reference

## Deployed Applications

Applications are served at `GET /apps/{slug}/{path...}` through YARP, with the
prefix stripped. Applications with a custom domain (`PUT /api/apps/{id}/domain`)
are also served by host match at `/` with no prefix stripping; point DNS at the
host and terminate TLS in front of Octopus, which serves plain HTTP.

Deployment history is capped at the newest 50 entries per application, including
their logs; the worker prunes older records once a deployment reaches a terminal
state.

## Endpoints

| Method | Route | Notes |
|---|---|---|
| GET | `/health` | Liveness check. |
| POST | `/api/apps` | Body: `{name, repoUrl, branch?, containerPort?, memoryMb?, cpuMillicores?}`. |
| PATCH | `/api/apps/{id}` | Body: `{branch}`. Retargets the branch; name and slug are immutable. |
| PUT | `/api/apps/{id}/quota` | Body: `{memoryMb?, cpuMillicores?}` (allowlisted caps). |
| GET | `/api/apps` | List applications. |
| GET | `/api/apps/{id}` | Retrieve one application. |
| DELETE | `/api/apps/{id}` | Stops the container and deletes history and webhooks. |
| POST | `/api/apps/{id}/deployments` | Body: `{containerPort?, projectPath?, dockerfilePath?}`. Returns `202`, or `409` if a deployment is in progress. |
| GET | `/api/apps/{id}/deployments` | Last 50; `?status=Failed` filters (case-insensitive). |
| GET | `/api/deployments/{id}` | Retrieve one deployment. |
| POST | `/api/deployments/{id}/cancel` | Cancels a queued deployment (`409` once claimed). |
| POST | `/api/deployments/{id}/retry` | Requeues a failed or cancelled deployment (`202`, or `409` if one is in progress). |
| GET | `/api/deployments/{id}/logs?take=200` | Bounded log tail. |
| GET | `/api/deployments/{id}/logs/stream?afterId=0` | SSE stream: replay then live lines, `event: done` at a terminal state (5-minute cap). |
| POST | `/api/apps/{id}/stop` | Stops the container and marks the deployment `Stopped`. |
| POST | `/api/apps/{id}/webhook-token` | Creates or rotates the webhook secret (shown once). |
| POST | `/api/keys` | Creates an API key `{name}`; the raw key is shown once. |
| GET | `/api/keys` | Lists keys (prefixes only, never hashes). |
| POST | `/api/keys/{id}/revoke` | Revokes a key. |
| GET | `/api/apps/{id}/webhook-events?take=50` | Lists delivery receipts. |
| POST | `/api/webhook-events/{id}/redeploy` | Re-runs a push receipt as a fresh deployment (`409` if the branch moved or work is active). |
| GET | `/api/apps/{id}/env` | Lists environment-variable key names only (values are never returned). |
| PUT | `/api/apps/{id}/env` | Replaces the environment set: `{vars:{KEY:value}}`; max 50 variables, 8 KB per value, 64 KB total. |
| DELETE | `/api/apps/{id}/env/{key}` | Removes one variable. |
| POST | `/api/hooks/github/{id}` | GitHub receiver (`X-GitHub-Event`, `X-GitHub-Delivery`, `X-Hub-Signature-256`). |

## Security

- **Control-API authentication.** `Authorization: Bearer oct_...` is required on all `/api` routes except `/health` and the HMAC-authenticated GitHub receiver. Keys are `oct_` plus base64url(32 bytes); only SHA-256 hashes are stored and compared in constant time. While no key exists, `POST /api/keys {name}` is permitted once to bootstrap the first key; afterwards it requires authentication. List and revoke endpoints expose prefixes only.
- **Repository URLs.** Restricted to `https://github.com/owner/repo`; credentials are rejected and length is bounded.
- **Webhooks.** HMAC-SHA256 with constant-time comparison and a 1 MB payload cap. Unauthenticated payloads are never persisted. Secrets live in the control-plane database and are returned only at creation or rotation.
- **Secret handling.** No secret is logged. Logs reference `owner/repo` rather than full URLs or tokens.
- **Application environment variables.** Values are stored in the control-plane database and injected through a throwaway Docker `--env-file` (never as `docker run -e` arguments). List endpoints return key names only, and logs record key names and counts. Keys match `^[A-Za-z_][A-Za-z0-9_]*$` (max 64 characters); values are limited to 8 KB without NUL or newline characters, up to 50 variables and 64 KB per application.
- **Containers.** Bound to `127.0.0.1` with `--restart unless-stopped` for crash and reboot recovery (explicit stops still remove the container). Per-application quotas: memory `128/256/512/1024/2048` MB and CPU `250/500/1000/2000` millicores (defaults `512` MB and `1000` millicores). Host ports are restricted to the `5100-5999` range.
- **Resource limits.** Process timeouts are applied throughout (clone 2 minutes, build 10 minutes, run 2 minutes), and log output is truncated.
- **Input validation.** Names, branches, URLs, and paths are length-checked; logs are capped per line and per query.

## Limitations

- SQLite is the default store and uses `DbBootstrap`; PostgreSQL with full EF Core migrations is planned.
- Concurrent builds share a single Docker host, so run one worker per host.
- The GitHub integration supports public repositories only.
- TLS is not terminated by Octopus; it serves plain HTTP behind an external terminator.
- Claims are lease-based (2-minute lease, 30-second heartbeat) with optimistic concurrency; a restarting worker requeues only deployments whose lease has lapsed, and stale `Cloning`, `Building`, or `Starting` deployments are requeued on worker restart.
- SQLite cannot order by `DateTimeOffset` server-side, so list queries prefetch a bounded window and sort in memory (`DeploymentQueries`, covered by `SqliteOrderingTests`).
