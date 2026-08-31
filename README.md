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

## Seed content

`content/` holds a curated, hand-verified seed set covering every content type,
so the API, the site, search and cross-linking all have correct data to be built
and demonstrated against. Three rules hold it together, each enforced by a test
in `tests/Sw5e.Database.Tests/SeedContentTests.cs`:

- every file validates against the schema for the directory it sits in;
- no file contains U+FFFD, the replacement character left behind wherever the
  original scrape lost an apostrophe, a dash or an accented letter;
- every cross-reference resolves inside the set: `sourceKey`, a background's
  suggested feats, a feat prerequisite naming another feat, a maneuver
  prerequisite or upgrade naming another maneuver, the class, archetype or
  species a feature is granted by, and the class an archetype or a class
  improvement belongs to.

The six combat-option types — maneuvers, fighting styles, fighting masteries,
lightsaber forms, weapon focuses and weapon supremacies — are the exception to
"curated seed set": all 219 of them are published, because they are small,
self-contained, and complete. `CombatOptionContentTests` asserts the size and
shape of each type, so a partial import fails rather than quietly publishing a
sample.

## Starship content

The six `starship-*` directories are not a sample. They carry the whole of
*Starships of the Galaxy*: all six base sizes, six deployments, 104 pieces of
equipment, 257 modifications, 67 ventures and the 13 rule chapters, 453
documents in total. `tests/Sw5e.Database.Tests/StarshipContentTests.cs` holds
them to the archive one-for-one and checks that every value the archive carries
survives into them unchanged.

Three of those values had to be recovered from prose rather than copied, because
the 2022 scrape zeroed the columns that held them:

- every numeric field on all six `StarshipBaseSize` records is `0` and every
  list is `null`, so hull dice, the modification budget, the six roles and the
  tier table are read out of the size's own `fullText`;
- all nineteen pieces of ammunition carry a name and a price and nothing else,
  so their damage, weight, range and properties come from the Tertiary
  Ammunition table in rule chapter 5, joined on name and cross-checked on price;
- armour and shields lost their table columns entirely — a shield's archived
  `regenerationRateCoefficient` in fact holds the *capacity* column — so both
  come from the Armor and Shields table in the same chapter.

The test names each of these losses and asserts the archive field is still
empty, so a re-scrape that recovers one fails loudly instead of leaving the
recovery in place unnoticed.


## The class graph

Classes, their archetypes, the features either of them grants, and the three
optional improvement rules each class carries are imported from the legacy
archive rather than hand-written, because there are 2,859 of them.

| Directory | Documents | What it holds |
|---|---|---|
| `content/class` | 10 | The class, and its twenty-row level table as data |
| `content/class-improvement` | 30 | Class, multiclass and splashclass improvements, three per class |
| `content/archetype` | 137 | Specialisations, each belonging to one class |
| `content/feature` | 2,682 | One document per granted ability, keyed by the level it arrives at |

They are a graph, not four lists. An archetype names its class in `className`;
a feature names what grants it in `grantedBy` and `grantedByName`, and the level
it arrives at in `level`. A class's level table names, per row, the proficiency
bonus, whatever the class prints in its Features column, and the class-specific
columns as labelled cells — so a character sheet can ask what a 7th-level scout
has without reading a word of prose, and a print layout can lay the columns out
in the order the book does.

A third of the features are granted by a species rather than by a class or an
archetype, and they carry no level because a species trait is held from
character creation. They wait on `content/species` rather than on anything
here: every one of them names its species, the cross-reference guard requires
that name to resolve, and it does now that all 141 species are published.

### Regenerating it

The import is `tests/Sw5e.Database.Tests/LegacyContentImport.cs`, and it runs in
one step: map the archive record mechanically, repair the encoding damage, apply
the handful of named adjudications, and drop table cells that lost their
contents. It is deterministic — the same archive produces the same bytes.

```bash
SW5E_WRITE_CONTENT=1 dotnet test --filter ImportedContentTests
```

`ImportedContentTests` then asserts that every committed file in those four
directories is exactly what the import produces. That is what makes 2,859
generated files reviewable: a diff on `content/` is a diff on the archive plus a
named judgement, never an unexplained edit, and a hand-correction fails the
suite until it is written down as an adjudication with a reason. Like every
other archive-backed test here, it reports and returns on a machine with no
archive checked out rather than passing silently.

## Adding a content type

Create `schemas/<content-type>/v1.json` as a JSON Schema 2020-12 document, then
add content files under `content/<content-type>/`. CI validates every content
file against its schema on each pull request. Adding a content type requires no
code change.

## Container image

`ghcr.io/christopherfowers/sw5e-database` is an init container that publishes
this repository's canonical content to the rest of the stack. It bakes
`content/` and `schemas/` into the image, copies them into a shared volume on
start, verifies the copy, and exits 0. It is not a long-running service, and it
holds no secrets.

The copy is checked rather than assumed: every file is compared by SHA-256
against the baked-in source, both after staging and again once it is in place.
A partial or truncated publish exits non-zero, because an API serving an
incomplete catalogue while reporting healthy is worse than a failed deploy.
Publishing is idempotent, so re-running it against a populated volume is safe,
and content withdrawn upstream is removed rather than left behind.

The image carries no database client and no connection string. The API reads
content from these files. Applying the SQL migrations in `migrations/` is
separate work that lands together with the content graph, and this image gains
that step at that point.

### Environment variables

| Variable | Default | Purpose |
|---|---|---|
| `SW5E_CONTENT_SOURCE` | `/opt/sw5e/content` | Where the content is baked into the image |
| `SW5E_SCHEMA_SOURCE` | `/opt/sw5e/schemas` | Where the schemas are baked into the image |
| `SW5E_CONTENT_TARGET` | `/srv/content` | Where content is published for the API |
| `SW5E_SCHEMA_TARGET` | `/srv/schemas` | Where schemas are published for the API |

The two targets must be different directories. The defaults suit the QA stack,
so in practice only the targets are ever overridden, and usually not even those.

### Volume

Mount a shared volume at `/srv` and the container writes `/srv/content` and
`/srv/schemas` into it for the API container to read.

The container runs as non-root, uid and gid `65532`. The volume must be
writable by that user or the publish fails by design. Docker seeds a fresh named
volume from the image's ownership, so `-v sw5e-content:/srv` works as-is; under
Kubernetes set `securityContext.fsGroup: 65532` on the pod.

```bash
docker run --rm -v sw5e-content:/srv ghcr.io/christopherfowers/sw5e-database:latest
```

Images are built and pushed from `main` and from `v*.*.*` tags, tagged `latest`,
`sha-<short>` and semver respectively, with build provenance and an SBOM
attached.

## License

MIT — see [LICENSE](LICENSE). Game content is governed separately; see
[CONTENT-LICENSE.md](CONTENT-LICENSE.md).

## QA deployment

Merging to `main` publishes the image and then deploys it to the internal QA
environment at <https://sw5e.cfowers.io>, which runs the database, API and site
as one Compose stack behind the reverse proxy.

The deploy step runs on a self-hosted runner on the QA host. That runner polls
GitHub outbound — no inbound port is opened — holds no secrets, and is
permitted to run exactly one script via a narrow sudoers rule. Only the
immutable `sha-<full commit SHA>` tag is ever deployed; `latest` is refused.
This repository deploys only the `database` service, so a merge here cannot move
the other two.

The step is gated on the `DEPLOY_ENABLED` repository variable. A job targeting
an unregistered runner label queues indefinitely rather than failing, so until
the runner is registered the gate keeps merges clean. Set `DEPLOY_ENABLED` to
`true` under Settings → Secrets and variables → Actions to turn it on.
