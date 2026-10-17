# Octopus

**Point Octopus at a GitHub repository and it builds and runs it in Docker — automatically on every push.**

[![CI](https://github.com/jasserhouimli/octopus-mini-cloud/actions/workflows/ci.yml/badge.svg)](https://github.com/jasserhouimli/octopus-mini-cloud/actions/workflows/ci.yml)
[![License: MIT](https://img.shields.io/badge/License-MIT-green.svg)](LICENSE)
![.NET 10](https://img.shields.io/badge/.NET-10-512BD4)
![Docker](https://img.shields.io/badge/Docker-required-2496ED?logo=docker&logoColor=white)

## What it is

Octopus is a small, self-hosted Platform-as-a-Service. An application is registered
once with a GitHub repository and a branch; from then on Octopus clones the source,
decides how to build it, builds an image, starts a container under per-application
resource limits, and serves it behind a reverse proxy at `/apps/{slug}/`.

Builds run from a repository `Dockerfile`, or from a generated .NET buildpack when
none is present. Deployments happen on demand, or automatically on `git push`
through signed GitHub webhooks — with health-gated releases so a bad build never
replaces a running one.

It is a modular monolith: five domain modules, an HTTP API, and a background worker,
with no cyclic dependencies and no domain logic in the transport layer.

## Highlights

- Deploy any public GitHub repository to Docker — with or without a `Dockerfile`.
- Auto-deploy on push through HMAC-signed, idempotent GitHub webhooks.
- Health-gated blue/green releases: a new container must pass readiness checks before it receives traffic, and a failed build leaves the previous one serving.
- Per-application memory and CPU quotas, isolated host ports, and loopback-only containers.
- Reverse-proxy routing with optional custom domains.
- API-key authentication and per-application environment variables, with values kept out of logs.
- Bounded log tail plus a live Server-Sent Events log stream.
- Lease-based worker claims with heartbeats and crash recovery.

## How it works

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

## Modules

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

## Tech stack

- **Runtime:** .NET 10, ASP.NET Core Minimal APIs
- **Persistence:** EF Core 10 — SQLite by default, PostgreSQL opt-in
- **Routing:** YARP reverse proxy
- **Execution:** Docker (build and run), per-application quotas
- **API documentation:** OpenAPI
- **Tests:** xUnit — unit, integration, and architecture (157 tests)

## Quick start

Requires Docker and the .NET 10 SDK. On Linux:

```bash
# Start the API and worker (data persists in the octopus-data volume)
docker compose up --build -d

# Create the first API key (allowed once, before any key exists)
curl -sX POST http://localhost:5000/api/keys \
  -H "Content-Type: application/json" -d '{"name":"admin"}'

# Register an application
curl -sX POST http://localhost:5000/api/apps \
  -H "Content-Type: application/json" \
  -d '{"name":"demo","repoUrl":"https://github.com/owner/repo","branch":"main"}'

# Deploy it, then open http://localhost:5000/apps/demo/
```

On Windows or macOS (where `network_mode: host` is unavailable), run the API and
worker with `dotnet run` instead. See the usage guide for the full walkthrough.

## Documentation

- [Usage guide](docs/usage.md) — setup, configuration, day-to-day usage, webhooks, and build detection.
- [API reference](docs/api.md) — endpoints, security model, and limitations.

## Roadmap

| Area | Status |
|---|---|
| GitHub webhooks, .NET buildpack, health-gated blue/green, CI, API-key auth, log streaming, multi-worker leases, custom domains | Complete |
| PostgreSQL with EF Core migrations | Planned |
| OIDC authentication | Planned |

## Project structure

```text
src/Octopus.Api        HTTP API, YARP, EF Core wiring
src/Octopus.Worker     background deployment worker
src/Modules            Apps, Deployments, GitHub, Runtime, Routing
src/BuildingBlocks     Result, Slug, ProcessRunner
tests/Octopus.Tests    unit, integration, and architecture tests
```

## License

Released under the MIT License. See [LICENSE](LICENSE).
