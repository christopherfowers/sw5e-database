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

## Where content is authored

Content management lives in the API. Contributors draft changes, an
administrator publishes them, and PostgreSQL holds the result with a full
revision history. Publishing through this repository would mean a commit, an
image build and a redeploy between an edit and anyone seeing it, and the API
container mounts its content volume read-only.

This repository is now the seed and the export rather than the origin:

- **The schemas are authoritative.** The API validates every authored document
  against them, and CI validates the whole corpus against them. Adding or
  changing a content type is a reviewed change here.
- **`content/` is the seed and the fallback.** It is what the container image
  publishes, and a deployment that has not enabled the database store serves
  exactly this.
- **`content/` is refreshed from the database by the exporter.**
  `dotnet Sw5e.Migrator.dll export --output <path-to-this-checkout>` rewrites
  the tree from what is published in PostgreSQL, drafts excluded and reverts
  reflected, and leaves a working tree for review. It does not commit or push.

Editing `content/` by hand is still right for bulk import and for repairing the
corpus. It is not how somebody fixes a typo.

## Canonical form

Every document under `content/` is committed in one form: members in the order
its schema declares them, two-space indentation, a bare newline, a trailing
newline, UTF-8 without a byte-order mark, and no `\uXXXX` escape for a character
that does not need one.

PostgreSQL stores documents as `jsonb`, which keeps values and discards text, so
member order, indentation and whitespace are gone by the time the exporter reads
a row. The exporter derives the bytes from the document rather than remembering
how the file was written. A file written any other way is rewritten on the first
export, and that reformatting shows up in a pull request with the real edit
buried in it.

`CanonicalContent` in `src/Sw5e.Database.Schemas` is the only writer of that
form. The API references it through the submodule, for the same reason it
references `SchemaValidator`: two implementations of one byte-exact format
would drift.

```bash
dotnet run --project src/Sw5e.Database.Tools -- canonicalise           # rewrite
dotnet run --project src/Sw5e.Database.Tools -- canonicalise --check   # report only
```

`CanonicalFormTests` asserts every committed document already matches, so a hand
edit that reorders members fails the build here.

Member order comes from the schema, which means reordering a schema's
`properties` changes the file format of every document of that type.

## Consumed as a submodule

`sw5e-api` pins a commit of this repository at `external/sw5e-database` and
references `src/Sw5e.Database.Schemas` directly, so the validator gating a write
in the API is the one gating the corpus here.

Changing `SchemaValidator` changes what the API accepts. Its evaluation options,
`OutputFormat.List` and `RequireFormatValidation`, are part of that contract
rather than an implementation detail, as is `SchemaRepository`'s
`{root}/{contentType}/v{version}.json` layout.

`content/` is not part of the shipped contract; the API excludes it from its
Docker build context and no running API process reads it. The API's test suite
does read it, over the pinned submodule commit: it imports the corpus into
PostgreSQL, exports it again, and asserts the result is byte-identical to what
is committed here.

## Requirements

- .NET SDK 10.0.302 or later
- Docker, for local PostgreSQL 17

## Getting started

```bash
cp .env.example .env
docker compose up -d
dotnet test
dotnet run --project src/Sw5e.Database.Tools -- validate schemas content
```

## Seed content

`content/` holds a curated seed set covering every content type, so the API, the
site, search and cross-linking have correct data to build against. Three rules
are enforced by `tests/Sw5e.Database.Tests/SeedContentTests.cs`:

- every file validates against the schema for the directory it sits in;
- every U+FFFD, left wherever the original scrape lost an apostrophe, a dash or
  an accented letter, is either repaired or recorded per file in the ledger of
  characters that cannot be recovered without inventing content;
- every cross-reference resolves inside the set: `sourceKey`, a background's
  suggested feats, a feat prerequisite naming another feat, a maneuver
  prerequisite or upgrade, the class, archetype or species a feature is granted
  by, and the class an archetype or class improvement belongs to.

The six combat-option types (maneuvers, fighting styles, fighting masteries,
lightsaber forms, weapon focuses and weapon supremacies) are published in full
rather than sampled, all 219 of them, because they are small and complete.
`CombatOptionContentTests` asserts the size and shape of each, so a partial
import fails rather than publishing a sample.

## Enhanced items, properties and rules

Five more types are published whole rather than sampled, because they were
imported from the 2022 archive of the old API rather than authored: 1,918
enhanced items, 46 weapon properties, 30 armour properties, 75 passages of rules
prose and 30 reference tables.

```bash
dotnet run --project src/Sw5e.Database.Tools -- import-legacy ../sw5e-legacy-archive/api content
```

The importer is deterministic and re-runnable: the same archive produces
byte-identical documents. It is the only stage that repairs anything, so the
archive's encoding damage is fixed once and nothing downstream needs to know the
corpus was scraped badly. It writes files and never deletes them, so a document
corrected by hand after import is not reverted.

Two things to know before editing any of it.

**Enhanced items are not equipment.** The two types share `key`, `name`,
`sourceKey`, `contentSet` and `description`, and no mechanical field. Equipment
carries a price, a weight and a stealth flag on all 505 documents; an enhanced
item carries a rarity band, an attunement requirement and a kind on all 1,918,
and no price at all. `valueText` is null on every archived record. They
relate by cross-reference: an enhanced item's `subtype` names the gear it is
built on, and for 20 of the 56 subtypes that is exactly one equipment document.

**Three types carry no `sourceKey`.** The archive records `contentSource` as
"None" for all 46 weapon properties, all 30 armour properties and all 33
reference tables. Unlike the rule chapters, where the file a record sits in
names the book, there is nothing to infer one from, so they are published
without a citation rather than with a guessed one.

Four archived records are not imported: the Player's Handbook preface and three
starship reference tables, each reduced by the scrape to a title with no text.
They are named with their reasons in `ArchiveConformanceTests.KnownCorruptItems`
and asserted to still be unusable, so a recovery upstream fails the test rather
than going unnoticed.

## Starship content

The six `starship-*` directories carry the whole of *Starships of the Galaxy*:
six base sizes, six deployments, 104 pieces of equipment, 257 modifications, 67
ventures and 13 rule chapters, 453 documents.
`tests/Sw5e.Database.Tests/StarshipContentTests.cs` holds them to the archive
one for one.

Three groups of values had to be recovered from prose, because the 2022 scrape
zeroed the columns that held them:

- every numeric field on all six `StarshipBaseSize` records is `0` and every
  list is `null`, so hull dice, the modification budget, the six roles and the
  tier table are read out of the size's own `fullText`;
- all nineteen pieces of ammunition carry a name and a price and nothing else,
  so damage, weight, range and properties come from the Tertiary Ammunition
  table in rule chapter 5, joined on name and cross-checked on price;
- armour and shields lost their table columns entirely, and a shield's archived
  `regenerationRateCoefficient` in fact holds the capacity column, so both come
  from the Armor and Shields table in the same chapter.

The tests assert each of those archive fields is still empty, so a re-scrape
that recovers one fails rather than leaving the recovery in place.

## The class graph

Classes, their archetypes, the features either grants, and the three optional
improvement rules each class carries are imported from the legacy archive rather
than hand-written, because there are 2,859 of them.

| Directory | Documents | What it holds |
|---|---|---|
| `content/class` | 10 | The class, and its twenty-row level table as data |
| `content/class-improvement` | 30 | Class, multiclass and splashclass improvements, three per class |
| `content/archetype` | 137 | Specialisations, each belonging to one class |
| `content/feature` | 2,682 | One document per granted ability, keyed by the level it arrives at |

They form a graph rather than four lists. An archetype names its class in
`className`; a feature names what grants it in `grantedBy` and `grantedByName`
and the level it arrives at in `level`. A class's level table gives, per row,
the proficiency bonus, whatever the class prints in its Features column, and the
class-specific columns as labelled cells, so a character sheet can ask what a
7th-level scout has without parsing prose.

A third of the features are granted by a species rather than a class or
archetype and carry no level, because a species trait is held from character
creation. Each names its species, and the cross-reference guard requires that
name to resolve.

### Regenerating it

The import is `tests/Sw5e.Database.Tests/LegacyContentImport.cs`: map the
archive record, repair the encoding damage, apply the named adjudications, and
drop table cells that lost their contents. The same archive produces the same
bytes.

```bash
SW5E_WRITE_CONTENT=1 dotnet test --filter ImportedContentTests
```

`ImportedContentTests` asserts every committed file in those four directories is
exactly what the import produces. That is what makes 2,859 generated files
reviewable: a diff is a diff on the archive plus a named judgement, and a hand
correction fails the suite until it is recorded as an adjudication with a
reason. Like the other archive-backed tests, it reports and returns on a
machine with no archive checked out rather than passing silently.

## Adding a content type

Create `schemas/<content-type>/v1.json` as a JSON Schema 2020-12 document, then
add content files under `content/<content-type>/`. CI validates every content
file against its schema on each pull request. No code change is required here,
though the API keeps its own registry and needs one there.

## Container image

`ghcr.io/christopherfowers/sw5e-database` is an init container that publishes
this repository's content to the rest of the stack. It bakes `content/` and
`schemas/` into the image, copies them into a shared volume on start, verifies
the copy, and exits 0. It is not a long-running service and holds no secrets.

Every file is compared by SHA-256 against the baked-in source, both after
staging and again once in place. A partial or truncated publish exits non-zero,
because an API serving an incomplete catalogue while reporting healthy is worse
than a failed deploy. Publishing is idempotent, and content withdrawn upstream
is removed rather than left behind.

The image carries no database client and no connection string. Applying the SQL
migrations in `migrations/` is separate work.

### Environment variables

| Variable | Default | Purpose |
|---|---|---|
| `SW5E_CONTENT_SOURCE` | `/opt/sw5e/content` | Where the content is baked into the image |
| `SW5E_SCHEMA_SOURCE` | `/opt/sw5e/schemas` | Where the schemas are baked into the image |
| `SW5E_CONTENT_TARGET` | `/srv/content` | Where content is published for the API |
| `SW5E_SCHEMA_TARGET` | `/srv/schemas` | Where schemas are published for the API |

The two targets must be different directories.

### Volume

Mount a shared volume at `/srv`; the container writes `/srv/content` and
`/srv/schemas` into it for the API container to read.

The container runs as non-root, uid and gid `65532`, and the volume must be
writable by that user. Docker seeds a fresh named volume from the image's
ownership, so `-v sw5e-content:/srv` works as-is; under Kubernetes set
`securityContext.fsGroup: 65532` on the pod.

```bash
docker run --rm -v sw5e-content:/srv ghcr.io/christopherfowers/sw5e-database:latest
```

Images are built and pushed from `main` and from `v*.*.*` tags, tagged `latest`,
`sha-<short>` and semver respectively, with build provenance and an SBOM.

## License

MIT. See [LICENSE](LICENSE). Game content is governed separately; see
[CONTENT-LICENSE.md](CONTENT-LICENSE.md).

## QA deployment

QA is <https://sw5e.cfowers.io>, running the database, API and site as one
Compose stack behind a reverse proxy.

`release.yml` contains a deploy job, but **it is not currently active**. It is
gated on the `DEPLOY_ENABLED` repository variable, which is unset, and it
targets a self-hosted runner that is not registered. Deploys are run by hand
against `/srv/homelab/apps/sw5e/deploy.sh` on the QA host.

To turn it on: register a runner on the QA host with the labels `marvel` and
`sw5e`, give it permission to run that script, then set `DEPLOY_ENABLED` to
`true` under Settings, Secrets and variables, Actions. Setting the variable
before the runner exists makes every merge queue a job indefinitely.

Only the immutable `sha-<full commit SHA>` tag is ever deployed; `latest` is
refused. This repository deploys only the `database` service.
