#!/usr/bin/env node
/**
 * Rejoins tables the conversion split in half.
 *
 * The original books print wide tables as two side-by-side columns, and the
 * conversion read each column as a table of its own, so the site shows one
 * table twice over: a header, the first half of the rows, then the same header
 * again and the rest.
 *
 * ## What counts as split, and what does not
 *
 * Adjacent tables sharing a header is not enough on its own. `ec-backgrounds`
 * carries fifty `|d8|Feat|` tables, one per background, and merging those
 * would destroy the document. The signal is a **continuation**: the second
 * table's first column picks up where the first's leaves off, with no value in
 * both.
 *
 * A pair is rejoined only when all of these hold:
 *
 *   - the tables are adjacent, separated by nothing but blank lines
 *   - their header rows match after collapsing whitespace
 *   - their alignment rows match after collapsing whitespace
 *   - every first-column cell is a number or a numeric range
 *   - the two value sets do not intersect
 *   - the second table's lowest value is above the first's highest
 *
 * The numeric requirement is deliberately strict. It excludes tables keyed by
 * name, where "continues" has no meaning, and it is the property that
 * distinguishes one table printed in two columns from two tables printed one
 * after the other.
 *
 * ## Parsed line by line, not by regular expression
 *
 * The first version of this matched pairs with one regular expression and hung:
 * two adjacent `(?:\|[^\n]*\|\n)+` groups over a document holding fifty tables
 * is catastrophic backtracking, and `ec-backgrounds` is exactly that document.
 * Walking the lines is both bounded and easier to be sure of.
 *
 *   node tools/rejoin_tables.mjs --check     report and change nothing
 *   node tools/rejoin_tables.mjs             rewrite in place
 *
 * Then canonicalise, always:
 *
 *   dotnet run --project src/Sw5e.Database.Tools -- canonicalise
 *
 * `JSON.stringify` is not the repository's writer and does not agree with it
 * everywhere. It emits an em space as the character where the canonical form
 * escapes it as ` `, which is one file in this corpus and enough to fail
 * `CanonicalFormTests`. Reimplementing the canonical writer in this script
 * would be a second implementation to keep in step, so this one writes
 * approximately and the repository's own tool makes it exact.
 */

import { readFile, readdir, writeFile } from "node:fs/promises";
import path from "node:path";
import process from "node:process";

const ROOT = "content";

const isRow = (line) => /^\s*\|.*\|\s*$/.test(line);
const isAlignment = (line) => /^\s*\|[-: |]+\|\s*$/.test(line);
const isBlank = (line) => line.trim() === "";
const squash = (line) => line.replace(/\s+/g, "");

/**
 * Every integer a first-column cell stands for, or null when it is not numeric.
 *
 * A d100 table's cells are ranges (`01-02`, `99-100`) so one cell covers many
 * values and overlap has to be tested across the whole span. Reading only the
 * first number of each range is how an earlier survey reported a table ending
 * at 75 when it actually ran to 100.
 */
function valuesOf(cell) {
  const range = /^(\d{1,3})\s*[-–]\s*(\d{1,3})$/.exec(cell);
  if (range) {
    const from = Number(range[1]);
    const to = Number(range[2]);
    if (to < from) return null;
    return Array.from({ length: to - from + 1 }, (_, index) => from + index);
  }

  const single = /^(\d{1,3})$/.exec(cell);
  return single ? [Number(single[1])] : null;
}

/** Every value the rows cover, or null if any row's first cell is not numeric. */
function spanOf(rows) {
  const values = [];

  for (const row of rows) {
    const inner = row.trim().replace(/^\|/, "").replace(/\|$/, "");
    const first = inner.split("|")[0]?.trim();
    if (!first) return null;

    const parsed = valuesOf(first);
    if (!parsed) return null;
    values.push(...parsed);
  }

  return values.length ? values : null;
}

/** True when `second` continues `first` rather than repeating or preceding it. */
function continues(first, second) {
  const a = spanOf(first);
  const b = spanOf(second);
  if (!a || !b) return false;

  const seen = new Set(a);
  if (b.some((value) => seen.has(value))) return false;

  return Math.min(...b) > Math.max(...a);
}

/** Reads one table starting at `index`, or null if there is not one there. */
function readTable(lines, index) {
  if (!isRow(lines[index]) || isAlignment(lines[index])) return null;
  if (!isAlignment(lines[index + 1] ?? "")) return null;

  const rows = [];
  let cursor = index + 2;
  while (cursor < lines.length && isRow(lines[cursor])) {
    rows.push(lines[cursor]);
    cursor += 1;
  }

  if (rows.length === 0) return null;

  return {
    header: lines[index],
    alignment: lines[index + 1],
    rows,
    end: cursor,
  };
}

/**
 * Rejoins every split pair in one string.
 *
 * A single pass suffices, because after joining a pair the walk continues from
 * the joined table, so a table printed in three columns is folded left to
 * right in the same pass.
 */
function rejoin(text) {
  const lines = text.split("\n");
  const out = [];
  let joined = 0;
  let index = 0;

  while (index < lines.length) {
    const table = readTable(lines, index);
    if (!table) {
      out.push(lines[index]);
      index += 1;
      continue;
    }

    let { rows, end } = table;

    for (;;) {
      let cursor = end;
      while (cursor < lines.length && isBlank(lines[cursor])) cursor += 1;

      const next = readTable(lines, cursor);
      if (!next) break;
      if (squash(next.header) !== squash(table.header)) break;
      if (squash(next.alignment) !== squash(table.alignment)) break;
      if (!continues(rows, next.rows)) break;

      rows = [...rows, ...next.rows];
      end = next.end;
      joined += 1;
    }

    out.push(table.header, table.alignment, ...rows);
    index = end;
  }

  return { text: out.join("\n"), joined };
}

const countRows = (text) => (text.match(/^\s*\|/gm) ?? []).length;

function walk(value, visit) {
  if (typeof value === "string") return visit(value);
  if (Array.isArray(value)) return value.map((item) => walk(item, visit));
  if (value && typeof value === "object") {
    return Object.fromEntries(
      Object.entries(value).map(([key, item]) => [key, walk(item, visit)]),
    );
  }
  return value;
}

async function* documents(directory) {
  for (const entry of await readdir(directory, { withFileTypes: true })) {
    const full = path.join(directory, entry.name);
    if (entry.isDirectory()) yield* documents(full);
    else if (entry.name.endsWith(".json")) yield full;
  }
}

async function main() {
  const check = process.argv.includes("--check");
  let files = 0;
  let tables = 0;

  for await (const file of documents(ROOT)) {
    const document = JSON.parse(await readFile(file, "utf8"));

    let joinedHere = 0;
    let before = 0;
    let after = 0;

    const rewritten = walk(document, (text) => {
      if (!text.includes("|")) return text;

      const result = rejoin(text);
      if (result.joined) {
        joinedHere += result.joined;
        before += countRows(text);
        after += countRows(result.text);
      }
      return result.text;
    });

    if (!joinedHere) continue;

    /*
      The guard that makes this safe to run across a corpus rather than by hand.
      Only repeated headers and their alignment rows are dropped, so the row
      count must fall by exactly two per join. Anything else means a row was
      lost, and a lost row is a rule nobody can look up again.
    */
    if (before - after !== joinedHere * 2) {
      throw new Error(
        `${file}: joining ${joinedHere} tables changed the row count by ` +
          `${before - after}, expected ${joinedHere * 2}`,
      );
    }

    files += 1;
    tables += joinedHere;
    process.stdout.write(`  ${file}  (${joinedHere})\n`);

    if (!check) {
      await writeFile(file, `${JSON.stringify(rewritten, null, 2)}\n`, "utf8");
    }
  }

  process.stdout.write(
    `\n${tables} tables rejoined across ${files} files` +
      `${check ? " (nothing written)" : ""}\n`,
  );
}

await main();
