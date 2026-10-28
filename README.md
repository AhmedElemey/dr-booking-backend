# moonlight-backend

.NET 8 Web API for Moonlight, a dentist reservation platform. The MVP spec for all four Moonlight repos is in [docs/SPEC.md](docs/SPEC.md).

## Run locally

```bash
docker compose up -d          # Postgres on localhost:5432
dotnet run --project src/Moonlight.Api
curl http://localhost:5267/health
```

Swagger UI is at `/swagger` in Development. Migrations run on startup in Development only.

## Test

```bash
dotnet test
```

Integration tests start a throwaway Postgres with Testcontainers (needs Docker). To use an existing database instead, set `TEST_DATABASE_URL` to an Npgsql connection string. CI does this with a Postgres service container.

## Layout

- `src/Moonlight.Api` — the API: `Features/` (one folder per area), `Domain/`, `Infrastructure/`, `Common/`
- `tests/Moonlight.Tests` — unit and integration tests
