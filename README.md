# Octopus

A minimal, self-hosted Platform-as-a-Service for deploying GitHub repositories with Docker.

[![CI](https://github.com/jasserhouimli/octopus-mini-cloud/actions/workflows/ci.yml/badge.svg)](https://github.com/jasserhouimli/octopus-mini-cloud/actions/workflows/ci.yml)

## Overview

Octopus turns a GitHub repository into a running, reverse-proxied application. An
application is registered once, then deployed either manually or automatically when
code is pushed. The worker clones the repository, resolves a build plan, builds an
image (from a repository `Dockerfile` or a generated .NET buildpack), starts the
container, and the API reverse-proxies traffic to `/apps/{slug}/`.

The system is a modular monolith. Each module owns its domain and exposes explicit
contracts; the HTTP API and the background worker contain composition and transport
only, never domain rules.

## Features

- Register applications with a GitHub repository, branch, container port, and quotas.
- Deploy on demand, or automatically on `git push` through GitHub webhooks.
- Build detection for a repository `Dockerfile`, or a generated .NET buildpack when none exists.
- Health-gated (blue/green) releases: a new container must pass readiness checks before it receives traffic.
- Docker-based execution with per-application memory and CPU limits.
- Reverse-proxy routing, including optional custom domains.
- API-key authentication and per-application environment variables, with values kept out of logs.
- Deployment logs with a bounded tail and a live Server-Sent Events (SSE) stream.
- Lease-based worker claims with heartbeats and crash recovery.

## Architecture

```text
Developer   -> POST /api/apps { repoUrl } -> POST /api/apps/{id}/deployments
GitHub push -> POST /api/hooks/github/{id} (HMAC) -> queued deployment
                                                      |
                                                Octopus.Worker
                                     clone -> build plan -> build -> start
                                                      |
                    Octopus.Api (EF Core + SQLite) <-- status / logs
                          |
                    YARP reverse proxy: /apps/{slug}/{**catch-all} -> 127.0.0.1:{port}
```

Domain behavior lives in the modules; `Octopus.Api` and `Octopus.Worker` wire them
together. Modules under `src/Modules` have no cyclic dependencies.

| Module | Owns | Does not own |
|---|---|---|
| `Apps` | App entity, creation validation, environment-variable keys and validation | builds, containers, secret values in logs |
| `Deployments` | Deployment state, logs, webhook intake and idempotency, environment-variable storage, `OctopusDbContext` | Docker, Git |
| `GitHub` | Repository URL validation, shallow clone | deployment state |
| `Runtime` | Build plans, Docker build/run/stop (with `--env-file` injection), readiness probes, port allocation | Git, routing, secret values in logs |
| `Routing` | YARP dynamic configuration provider | deployment decisions |
| `BuildingBlocks` | `Result`, `Slug`, `ProcessRunner` | domain rules |

## Requirements

- .NET SDK 10 (pinned through `global.json`; verify with `dotnet --info`).
- A running Docker Engine or Docker Desktop (verify with `docker info`).
- Git (verify with `git --version`).

## Getting Started

### Option 1: Docker Compose

Recommended on Linux hosts.

```powershell
docker compose up --build -d
curl http://localhost:5000/health
docker compose logs -f
docker compose down   # data persists in the octopus-data volume
```

The API and worker share a single SQLite file and workspace through the
`octopus-data` volume, and the worker builds and runs application containers
through the host's Docker socket.

> `network_mode: host` is not supported on Docker Desktop for Windows or macOS.
> On those platforms, run the API and worker with the .NET SDK instead (Option 2).

### Option 2: .NET SDK (Windows, macOS, or Linux)

```powershell
dotnet build Octopus.slnx
dotnet test  Octopus.slnx

# Terminal 1 - API (http://localhost:5000)
$env:ConnectionStrings__Octopus = "Data Source=C:\data\octopus.db"
$env:Octopus__Workspace        = "C:\data\workspace"
dotnet run --project src/Octopus.Api

# Terminal 2 - worker (must use the same connection string and workspace)
$env:ConnectionStrings__Octopus = "Data Source=C:\data\octopus.db"
$env:Octopus__Workspace        = "C:\data\workspace"
dotnet run --project src/Octopus.Worker
```

> The default `Data Source=octopus.db` is relative to each project's working
> directory, so the API and worker would otherwise use different files. Set an
> absolute `ConnectionStrings__Octopus` and a matching `Octopus__Workspace` in
> both processes. Existing SQLite files are upgraded in place on startup
> (`DbBootstrap`); SQLite uses `EnsureCreated` for zero-setup development.

## Configuration

### Database

SQLite is the default and requires no setup. PostgreSQL is supported as an opt-in
alternative; any connection string containing `Host=` selects the Npgsql provider
automatically (`OctopusDbOptions`).

`docker-compose.yml` includes an idle `db` service (`postgres:17-alpine`, data in
the `pgdata` volume). To use it, create a `.env` file next to `docker-compose.yml`:

```powershell
# Never commit real credentials.
"POSTGRES_PASSWORD=long-random-value" | Out-File -Encoding ascii .env
```

Then set `ConnectionStrings__Octopus` on both the `api` and `worker` services and
run `docker compose up --build -d`. On PostgreSQL the schema is applied with
`MigrateAsync` from the checked-in `InitialCreate` migration
(`src/Modules/Deployments/Octopus.Deployments/Migrations`); subsequent schema
changes are added as new `dotnet ef` migrations.

### Environment variables

An application container listens on port `8080` by default
(for example, `ASPNETCORE_URLS=http://+:8080`). Per-application variables are set
through the API as described below.

## Usage

The examples below use PowerShell. Store the API key once and reuse it.

```powershell
# Create the first API key. Allowed without authentication only while no key exists.
$key = Invoke-RestMethod -Method Post -Uri http://localhost:5000/api/keys `
  -ContentType application/json -Body '{"name":"admin"}'
$h = @{ Authorization = "Bearer $($key.key)" }   # use on every /api call below
```

### Register an application

```powershell
$app = Invoke-RestMethod -Method Post -Uri http://localhost:5000/api/apps `
  -ContentType application/json `
  -Body '{"name":"demo","repoUrl":"https://github.com/owner/repo","branch":"main"}'
$app.id
```

### Configure environment variables

```powershell
# Values are stored and injected at container start; list endpoints return key names only.
Invoke-RestMethod -Method Put -Uri "http://localhost:5000/api/apps/$($app.id)/env" `
  -ContentType application/json -Body '{"vars":{"API_KEY":"s3cret","PORT":"8080"}}'
Invoke-RestMethod "http://localhost:5000/api/apps/$($app.id)/env"   # -> keys only
```

### Deploy

A repository `Dockerfile` is used when present; otherwise the worker generates a
.NET buildpack. Build inputs can be pinned per deployment.

```powershell
# Docker web project via the buildpack, optionally pinned to a project:
Invoke-RestMethod -Method Post -Uri "http://localhost:5000/api/apps/$($app.id)/deployments" `
  -ContentType application/json -Body '{"projectPath":"src/Web/Web.csproj"}'

# Or a nested Dockerfile in a monorepo (mutually exclusive with projectPath):
# -Body '{"dockerfilePath":"deploy/prod/Dockerfile"}'

Invoke-RestMethod http://localhost:5000/api/apps
# Open: http://localhost:5000/apps/demo/
```

### Inspect logs

```powershell
$dep = (Invoke-RestMethod "http://localhost:5000/api/apps/$($app.id)/deployments" -Headers $h)[0].id
Invoke-RestMethod "http://localhost:5000/api/deployments/$dep/logs?take=200" -Headers $h

# Live tail (SSE, requires authentication):
# curl -N -H "Authorization: Bearer $OCTOPUS_KEY" "http://localhost:5000/api/deployments/$dep/logs/stream"
```

### Stop, cancel, and retry

```powershell
Invoke-RestMethod -Method Post "http://localhost:5000/api/apps/$($app.id)/stop" -Headers $h
```

Stopping halts the container and cancels queued deployments for the application. A
deployment the worker has already claimed runs to completion (the current version
does not interrupt in-flight work). Cancel a queued deployment with
`POST /api/deployments/{id}/cancel`, and requeue a failed one with
`POST /api/deployments/{id}/retry`.

## GitHub Webhook Auto-Deploy

Configure a webhook once per application. Perform this step over localhost or a
trusted network; the secret is shown a single time.

```powershell
$tok = Invoke-RestMethod -Method Post "http://localhost:5000/api/apps/$($app.id)/webhook-token"
$tok.secret      # GitHub: Settings > Webhooks > Secret
$tok.webhookUrl  # Payload URL; content type application/json; push events
```

Each delivery is processed in order: authenticates the request (HMAC-SHA256),
persists a receipt, checks idempotency on `(AppId, DeliveryId)`, applies the branch
filter, queues a deployment, and returns `202`.

- Redeliveries return the original deployment with `duplicate: true`.
- Pushes to other branches are ignored.
- A push received while a deployment is active returns `409`; redeliver to retry.

```powershell
Invoke-RestMethod "http://localhost:5000/api/apps/$($app.id)/webhook-events?take=50"
```

## Build Detection

When a repository has no `Dockerfile`, the worker locates the shallowest
`Microsoft.NET.Sdk.Web` project (`bin`, `obj`, `.git`, and `node_modules` excluded,
up to depth 4) and generates a multi-stage .NET 10 `Dockerfile` inside its
throwaway workspace copy. The generated file is never written to the source
repository, and a repository `Dockerfile` always takes precedence.

- `projectPath` pins a specific relative `.csproj` (no `..`).
- `dockerfilePath` selects a nested `Dockerfile` for monorepos (relative, no `..`,
  must exist) and is built with `docker build -f`.

## Deployment Strategy

Releases are health-gated:

1. The new image starts on a fresh port in the `5100-5999` range, beside the live container.
2. The worker waits up to 30 seconds for TCP readiness and confirms the container state via `docker inspect` (images declaring a `HEALTHCHECK` must report `healthy`).
3. On success, the new container is promoted (`docker rename` to the canonical name, the previous container is stopped) and the route is switched.
4. On failure, the new container is removed and the deployment is marked `Failed`; the previous container continues to serve traffic throughout.

If a worker crashes between start and promote, the orphaned container is removed by
the next recovery pass (its name is deterministic) before the deployment is
requeued. After a successful promotion, superseded image tags for the application
are removed so host disk usage does not grow by one image per deployment. Readiness
probes are loopback-only.

## API Reference

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

Deployed applications are served at `GET /apps/{slug}/{path...}` through YARP, with
the prefix stripped. Applications with a custom domain (`PUT /api/apps/{id}/domain`)
are also served by host match at `/` with no prefix stripping; point DNS at the host
and terminate TLS in front of Octopus, which serves plain HTTP.

Deployment history is capped at the newest 50 entries per application, including
their logs; the worker prunes older records once a deployment reaches a terminal state.

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

## Roadmap

| Area | Status |
|---|---|
| GitHub webhooks (HMAC, idempotency) with auto-deploy | Complete |
| .NET buildpack (no `Dockerfile` required) | Complete |
| Health-gated blue/green traffic switching | Complete |
| CI: build, tests, architecture tests, image builds, and Compose | Complete |
| API-key authentication | Complete |
| Log streaming (cursor tail and SSE) | Complete |
| Multi-worker leases and heartbeats | Complete |
| Custom domains | Complete |
| PostgreSQL with EF Core migrations | Planned |
| OIDC authentication | Planned |

## Project Structure

```text
src/Octopus.Api        HTTP composition, YARP, EF Core (SQLite) wiring
src/Octopus.Worker     DeploymentWorker: lease claim -> clone -> build plan -> build -> start (env-file), heartbeat, prune
src/Modules/...        Apps (+ env vars), Deployments (+ webhooks, env-var storage), GitHub, Runtime (+ buildpack, env-file), Routing
src/BuildingBlocks     Result, Slug, ProcessRunner
tests/Octopus.Tests    Validators, webhooks, buildpack, env vars, quotas, auth, leases, health probes, SQLite ordering, architecture (157 tests)
```

## License

Released under the MIT License. See [LICENSE](LICENSE).
