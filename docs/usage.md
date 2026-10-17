# Usage Guide

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

`network_mode: host` is not supported on Docker Desktop for Windows or macOS. On
those platforms, use Option 2.

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

The default `Data Source=octopus.db` is relative to each project's working
directory, so the API and worker would otherwise use different files. Set an
absolute `ConnectionStrings__Octopus` and a matching `Octopus__Workspace` in both
processes. Existing SQLite files are upgraded in place on startup (`DbBootstrap`);
SQLite uses `EnsureCreated` for zero-setup development.

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

The examples below use PowerShell.

Create the first API key. This is permitted without authentication only while no
key exists; afterwards the endpoint requires authentication.

```powershell
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

Values are stored and injected at container start; list endpoints return key names
only.

```powershell
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

A webhook is configured once per application. Perform this step over localhost or a
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
