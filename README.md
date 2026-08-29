# sw5e-database

Schema definitions, migrations, canonical game content, and import tooling for
the SW5e community platform.

## What lives here

| Directory | Contents |
|---|---|
| `schemas/` | Versioned JSON Schema documents defining every content type |
| `migrations/` | Reviewed SQL migrations |
| `content/` | Canonical game content, one file per item |
| `src/` | Schema library and import tooling |
| `tests/` | Schema conformance and tooling tests |

Content changes arrive as pull requests, so every edit to canonical game data is
reviewed before it reaches the site.

## Requirements

- .NET SDK 10.0.302 or later
- Docker (for local PostgreSQL 17)

## Getting started

```bash
cp .env.example .env
docker compose up -d
dotnet test
dotnet run --project src/Sw5e.Database.Tools -- validate schemas content
```

## Adding a content type

Create `schemas/<content-type>/v1.json` as a JSON Schema 2020-12 document, then
add content files under `content/<content-type>/`. CI validates every content
file against its schema on each pull request. Adding a content type requires no
code change.

## License

MIT — see [LICENSE](LICENSE). Game content is governed separately; see
[CONTENT-LICENSE.md](CONTENT-LICENSE.md).
