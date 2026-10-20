# dotnet-csv-etl

A small, dependency-free toolkit for validating and cleaning messy customer CSV exports: the kind of file a forward-deployed
engineer gets from a customer ("export.csv, please load it by Friday"). C# on .NET 10, a class library plus a command-line tool
(`csvetl`), xunit tests. **No CSV library and no CLI library**: the RFC 4180 reader/writer, the schema loader and the argument
parser are all written here, on purpose.

All data in this repository is synthetic (fake names at example.com / example.test, no real people, no card numbers).

CI ran green on the first run on GitHub Actions (ubuntu-latest, 2026-10-20).

## What it does

- **Streaming RFC 4180 reader and writer**: quoted fields, doubled quotes, embedded delimiters / CR / LF, LF + CRLF + lone CR,
  UTF-8 BOM, configurable delimiter. Ragged rows, stray quotes and unterminated quotes are *reported on the record* (with line
  numbers), never thrown. Only one record plus a 16 K buffer is held in memory; field, field-count and record sizes are capped.
- **Schema file** (JSON, comments allowed): columns with type (`string`, `int`, `decimal`, `date`, `bool`, `enum`), `required`, `min`/`max`,
  `regex`, `allowed` values, `trim`, case normalisation, header aliases, and a per-column policy for bad values: `reject` the row,
  `null` the value or use a `default`.
- **Validator / transformer** producing clean rows plus a structured error report (row, line, column, rule, raw value, message, action)
  with a hard cap on the number of listed errors (totals stay exact).
- **Outputs**: clean CSV, JSON Lines (typed), and a rejects CSV that keeps the *original raw record* plus the reasons.
- **Duplicate detection** on a key, streaming, with bounded memory (see design notes).
- **CLI**: `csvetl validate | clean | schema-check | sample`, documented exit codes, progress on stderr, data on stdout.

## Quick start

```bash
# needs the .NET 10 SDK
dotnet build -warnaserror
dotnet test

# generate a synthetic messy file, check it, clean it
dotnet run --project src/CsvEtl.Cli -- sample --rows 200 --seed 42 --bom --output messy.csv
dotnet run --project src/CsvEtl.Cli -- schema-check --schema samples/customers.schema.json --input messy.csv
dotnet run --project src/CsvEtl.Cli -- validate --schema samples/customers.schema.json --input messy.csv
dotnet run --project src/CsvEtl.Cli -- clean --schema samples/customers.schema.json --input messy.csv \
    --output clean.csv --jsonl clean.jsonl --rejects rejects.csv --report report.json
```

`samples/customers_messy.csv` (200 rows, mixed CRLF/LF, BOM, blank lines, ragged rows, comma decimals, bad dates, duplicate ids) is exactly
what `sample --rows 200 --seed 42 --bom` writes (a test checks this byte for byte). `.gitattributes` marks it `-text` so git never
rewrites its line endings.

## CLI

```
csvetl validate     --schema FILE --input FILE [options]    report problems, write nothing
csvetl clean        --schema FILE --input FILE [options]    write clean CSV / JSONL and a rejects file
csvetl schema-check --schema FILE [--input FILE]            lint a schema, optionally match a header
csvetl sample       [--rows N] [--seed N] [--output FILE]   generate synthetic messy data
```

Common options: `--delimiter` (a character, or `comma|semicolon|tab|pipe`), `--encoding utf-8|latin1|utf-16`, `--max-field-size N`,
`--max-errors N` (listed issues, default 1000), `--dedupe hash|exact|off`, `--max-keys N`, `--progress`, `--progress-every N`.
`clean` also takes `--output FILE|-`, `--jsonl FILE`, `--rejects FILE`, `--report FILE`, `--newline lf|crlf`, `--fail-on-reject`.
`validate` takes `--format text|json`. Both `--name value` and `--name=value` work; `--input -` reads stdin.

### Exit codes

| code | meaning |
|------|---------|
| 0 | success. `validate`: no rejected rows. `clean`: finished (rejects are allowed unless `--fail-on-reject`) |
| 1 | data problems: rows rejected (`validate` always; `clean` only with `--fail-on-reject`) or an unusable header (missing required column, empty input, malformed header) |
| 2 | usage error (unknown command/option, missing or bad option value, output path equals input) or an invalid schema file |
| 3 | I/O error (missing file, unwritable output) or any other fatal error |

stdout carries data only (clean CSV for `clean`, the report for `validate`, the generated file for `sample`). Progress lines and the
`clean` summary go to stderr, so `csvetl clean ... > clean.csv` is always a valid CSV file. All of the above is covered by tests that call
the real entry point and by a few tests that spawn the built `csvetl.dll`.

## Schema format

```json
{
  "name": "customers",
  "key": ["customer_id"],
  "onDuplicate": "reject",
  "columns": [
    { "name": "customer_id", "aliases": ["Cust ID", "CustomerID"], "type": "string", "required": true,
      "trim": true, "case": "upper", "regex": "^C\\d{6,9}$", "onInvalid": "reject" },
    { "name": "age", "type": "int", "min": 0, "max": 120, "onInvalid": "null" },
    { "name": "balance", "type": "decimal", "required": true, "min": -100000, "max": 1000000, "decimalSeparator": "." },
    { "name": "signup_date", "type": "date", "formats": ["yyyy-MM-dd", "dd.MM.yyyy", "d MMM yyyy"], "min": "2015-01-01" },
    { "name": "tier", "type": "enum", "allowed": ["bronze", "silver", "gold"], "default": "bronze", "onInvalid": "default" }
  ]
}
```

Top level: `name`, `key` (column names used for duplicate detection), `onDuplicate` (`reject` = first occurrence wins, later rows are rejected;
`keep` = flag only), `columns`.

Column properties (anything else is a schema error with a "did you mean" hint):

| property | applies to | meaning |
|----------|-----------|---------|
| `name` | all | canonical output column name (required) |
| `aliases` | all | other header spellings; names are compared ignoring case and punctuation, so `Cust ID`, `cust_id` and `CustID` are the same name |
| `type` | all | `string` (default), `int` (64-bit), `decimal`, `date`, `bool`, `enum` |
| `required` | all | empty (after trim) is an error. Absent from the header: fatal for the file |
| `trim` | all | default `true`; an empty value after trimming counts as empty |
| `case` | string, enum | `none`, `upper`, `lower`, `title` (invariant culture) |
| `minLength`, `maxLength` | string | inclusive |
| `min`, `max` | int, decimal, date | inclusive; dates are ISO `yyyy-MM-dd` strings |
| `regex` | all | .NET regex, `IsMatch` (anchor it yourself), applied to the trimmed, case-normalised text, 500 ms timeout (a timeout is a violation, not a hang) |
| `allowed` | string, enum | allowed values. String: exact match after case normalisation. Enum: case-insensitive, output uses your spelling |
| `formats` | date | list of .NET custom formats tried in order (first match wins, so list the unambiguous one first); default `yyyy-MM-dd` |
| `outputFormat` | date | default `yyyy-MM-dd` |
| `decimalSeparator` | decimal | `.` (default) or `,`; no thousands separators are ever accepted |
| `trueValues`, `falseValues` | bool | case-insensitive; defaults `true,yes,y,1,t` / `false,no,n,0,f` |
| `onInvalid` | all | `reject` (default: reject the row), `null` (keep the row, empty value), `default` (keep the row, use `default`) |
| `default` | all | used for `onInvalid: default` **and** to fill blank optional cells and columns missing from the file; must itself satisfy the column rules |

Evaluation order per cell: trim, empty/required, case, regex, then type rules (length or allowed for strings, parse for the others), then min/max.
The first failing rule of a column is reported; all columns of a row are checked so one row can produce several issues.

Header handling: columns are matched by name/alias in any order; extra file columns are ignored and listed; a duplicated mapping warns and the
first one wins; a missing optional column is filled with its default (or empty); a missing *required* column makes the whole file fail with exit 1.

Output formats are canonical: ints as digits, decimals with a dot and the scale of the input (`12.50` stays `12.50`), dates ISO, booleans `true`/`false`.
JSON Lines keeps types (numbers, booleans, `null`); non-BMP characters such as emoji are written as `😀` escapes (valid JSON).

## Design notes

**Why not `Split(',')`**: quoted fields may contain delimiters, doubled quotes and newlines, and a record can span physical lines. The reader is a
character state machine over a `TextReader` with a small buffer.

**Streaming and memory.** `CsvReader.Read` returns a lazy `IEnumerable<CsvRecord>`; `EtlRunner` pulls one record, validates it, writes it to the sinks and
drops it. Bounds: `MaxFieldSize` (1 Mi chars), `MaxFieldsPerRecord` (10,000) and `MaxRecordSize` (4 Mi chars total) are enforced while parsing: excess data is
consumed but not stored and the record is flagged `field-too-long` / `too-many-fields`. The raw text kept for the rejects file is capped at about twice
`MaxRecordSize`. The worst case for one record is therefore a few MiB, not "the rest of the file". Consequence to know about: one unterminated quote
swallows the remainder of the file into a single flagged record (reported once, with the line where the quote opened); there is no resynchronisation heuristic.

**Error report caps.** The report stores at most `--max-errors` issues (default 1000, `0` = counts only). Totals, per-rule and per-column counts stay exact, raw values in
issues are clipped to 200 characters, and the rejects file is *not* capped (it is the data, not the report).

**Duplicate detection.** `key` columns are compared on their *cleaned* values (trimmed, case-normalised, typed), the first occurrence wins, rows already rejected for
another reason do not register their key, and rows with an empty key part are never duplicates. Two modes, both exact (no Bloom filter, no false positives):
`hash` (default) remembers a 128-bit SHA-256 prefix per key (16 bytes plus set overhead; collision odds about n^2 / 2^129, negligible), `exact` remembers the key strings.
Both are capped by `--max-keys` (default 5,000,000, roughly 250 MB for `hash` on a 64-bit runtime including hash-set overhead: an estimate, not measured). When the cap is
reached new keys are no longer remembered: duplicates of keys remembered earlier are still caught, duplicates of later keys are **missed**, and the report says so
(`dedupeSaturated`, `dedupeUncheckedRows`). The alternatives (external sort, disk-backed set) were not built.

**Culture handling.** Nothing in the library reads the current culture. Numbers and dates are parsed with `InvariantCulture` (or the column's explicit `decimalSeparator`),
dates are formatted ISO, case mapping uses the invariant mappers (so `"TITLE".ToLower()` is `title` even under tr-TR), and the generator and reports format with the
invariant culture. Custom date formats are always evaluated against the invariant calendar (this matters under th-TH and ar-SA, whose current calendars are not Gregorian).
Tests run under en-US, de-DE, fr-FR, tr-TR, ar-SA, th-TH and ja-JP, and the entire suite can be run under another culture with `CSVETL_TEST_CULTURE=de-DE dotnet test`.

**Encodings.** UTF-8 (with or without BOM), UTF-16 and UTF-32 BOMs are detected; `--encoding latin1|utf-16` sets the fallback when there is no BOM; stdin is always read as UTF-8.
Invalid UTF-8 bytes become U+FFFD (replacement character) and are *not* flagged: mojibake passes through as data. Outputs are always UTF-8 without BOM.

## Real CLI output

`csvetl validate --schema samples/customers.schema.json --input samples/customers_messy.csv --max-errors 12` (exit code 1):

```text
csvetl validate: samples/customers_messy.csv against schema 'customers'
rows read      : 200
rows clean     : 179 (7 repaired by null/default policy)
rows rejected  : 21
duplicates     : 1
issues         : 28 total, 12 listed, 16 not listed (cap 12)
by rule        : allowed=2, duplicate=1, ragged-row=4, regex=6, required=6, type=9
by column      : (row)=4, age=1, balance=3, customer_id=4, email=3, full_name=6, marketing_opt_in=1, signup_date=4, tier=2

row   line  column            rule              action     message
1     2     customer_id       regex             rejected   value 'X1' does not match /^C\d{6,9}$/  [X1]
11    13    full_name         required          rejected   value is required but empty  []
15    19    customer_id       duplicate         rejected   duplicate key (customer_id); first occurrence wins  [C000014]
22    26    email             regex             nulled     value 'casey.example22@@example.com' does not match /^[^@\s]+@[^@\s]+\.[a-z]{2,}$/  [casey.example22@@example.com]
24    29    customer_id       regex             rejected   value 'X24' does not match /^C\d{6,9}$/  [X24]
30    35    (row)             ragged-row        rejected   Line 35 has 10 fields, expected 9.  [C000030,Riley Fixture,riley.fixture30@e~]
32    37    balance           type              rejected   '1,234.56' is not a decimal number (decimal separator '.')  [1,234.56]
42    49    email             regex             nulled     value 'jamie.testcase42@@example.com' does not match /^[^@\s]+@[^@\s]+\.[a-z]{2,}$/  [jamie.testcase42@@example.com]
48    55    full_name         required          rejected   value is required but empty  []
54    65    signup_date       type              rejected   '2024-13-45' is not a date in any of: yyyy-MM-dd, dd.MM.yyyy, d MMM yyyy  [2024-13-45]
57    68    (row)             ragged-row        rejected   Line 68 has 7 fields, expected 9.  [C000057,Sam Dummy,sam.dummy57@example.c~]
76    91    tier              allowed           defaulted  value 'diamond' is not one of: bronze, silver, gold, platinum  [diamond]
... 16 more issue(s) not listed (raise --max-errors to see them)
```

`csvetl clean ... --output clean.csv --jsonl clean.jsonl --rejects rejects.csv --report report.json` (exit code 0; summary on stderr):

```text
csvetl clean: samples/customers_messy.csv against schema 'customers'
rows read      : 200
rows clean     : 179 (7 repaired by null/default policy)
rows rejected  : 21
duplicates     : 1
issues         : 28 total, 28 listed, 0 not listed (cap 1000)
by rule        : allowed=2, duplicate=1, ragged-row=4, regex=6, required=6, type=9
by column      : (row)=4, age=1, balance=3, customer_id=4, email=3, full_name=6, marketing_opt_in=1, signup_date=4, tier=2
```

clean.csv (first rows; dates normalised to ISO, bools to true/false, enum canonicalised, embedded newline kept inside quotes):

```text
customer_id,full_name,email,age,balance,signup_date,tier,marketing_opt_in,notes
C000002,Casey Example,casey.example2@example.com,77,11522.79,2021-01-17,bronze,true,"called on Monday, no answer"
C000003,Sam Fixture,sam.fixture3@example.com,68,10304.09,2019-06-06,silver,true,
C000004,Jamie Testcase,jamie.testcase4@example.com,28,478.10,2018-05-23,gold,false,"address line 1
line 2"
```

clean.jsonl (first rows):

```text
{"customer_id":"C000002","full_name":"Casey Example","email":"casey.example2@example.com","age":77,"balance":11522.79,"signup_date":"2021-01-17","tier":"bronze","marketing_opt_in":true,"notes":"called on Monday, no answer"}
{"customer_id":"C000003","full_name":"Sam Fixture","email":"sam.fixture3@example.com","age":68,"balance":10304.09,"signup_date":"2019-06-06","tier":"silver","marketing_opt_in":true,"notes":null}
```

rejects.csv (columns: row, line, reasons, raw; `raw` is the original record text, quoting included, so it can be fixed and re-submitted):

```text
row,line,reasons,raw
1,2,"customer_id: regex: value 'X1' does not match /^C\d{6,9}$/","X1,Casey Sample,casey.sample1@example.com,37,14474.38,2021-12-07,PLATINUM,Y,""called on Monday, no answer"""
11,13,full_name: required: value is required but empty,"C000011,,11@example.com,24,19869.36,2018-12-06,"" silver"",TRUE,""VIP, handle with care"""
15,19,customer_id: duplicate: duplicate key (customer_id); first occurrence wins,"C000014,Jamie Testcase,jamie.testcase15@example.com,61,7970.59,16 Dec 2018,platinum,no,prefers email"
```

### Rough timing (single machine, not a benchmark)

A generated 1,000,000-row file (`sample --rows 1000000 --seed 42`, 103 MB, 10% of rows with a defect), Release build, Windows, 8 logical cores, **while other builds were running on the same machine**, so the numbers are noisy:

| run | wall time |
|-----|-----------|
| `validate` (all rules, hash dedupe) | 12 s, 14 s, 20 s, 29 s, 31 s in five runs |
| `validate` with a one-column schema (mostly parser cost) | about 4 to 7 s |
| `clean` with clean CSV + JSONL + rejects + JSON report | 44 s (one run) |

Output of that `clean` run: 922,329 clean rows (43,094 repaired), 77,671 rejected, 9,794 duplicates, 120,765 issues of which 1,000 were listed. Treat it as "tens of seconds per million rows, single-threaded", nothing more.

## How it is tested

`dotnet test` runs **361 test cases** (xunit counts every theory row; about 140 `[Fact]` and 35 `[Theory]` methods). Last full local run: 361 passed, Windows, .NET SDK 10.0.402. The same suite was also run, passing, with `CSVETL_TEST_CULTURE=de-DE` and `=fr-FR` (330 cases at the time, before the mutation-driven tests were added). Not run by hand on Linux/macOS; CI ran the suite once on Ubuntu and it passed.

- **Parser corpus**: 30 hand-written cases with exact expected rows (embedded CRLF in quotes, quote in the middle of an unquoted field, trailing delimiter, empty last line, BOM, header only, single column with empty value vs blank line, ...), delimiters, buffer-boundary reads at every buffer size, laziness, structured issues with line numbers, size limits.
- **Seeded property test**: `write(rows) -> read == rows` for random rows made of quotes, commas, semicolons, tabs, CR, LF, CRLF, spaces, accents, CJK, emoji, U+FEFF, across 4 delimiters, 3 newline styles and random buffer sizes (3 seeds x 1500 iterations); the raw text of every record re-parses to the same fields.
- **Fuzz**: 3000 seeded random byte blobs x 4 option sets: 390,904 records parsed, 236,079 carried a structured issue, 0 exceptions; plus 800 random inputs through the whole pipeline (counts always add up).
- **Independent oracle, `Microsoft.VisualBasic.FileIO.TextFieldParser`** (Windows, .NET 10). On 1,500 random well-formed files without CR/LF in fields: identical. On 3,000 random well-formed files with the full nasty alphabet: 1,784 identical; 1,019 differ only in the exact CR/LF characters inside quoted fields; 197 more differ in files with embedded line breaks (the oracle drops text around a CR inside quotes); 0 differences in any file without an embedded line break; the oracle never rejected well-formed input. So **TextFieldParser is lossy for line breaks inside quoted fields and this reader is not** (the hand corpus and the round-trip property cover that, not the oracle). On malformed input they differ by design: TextFieldParser throws `MalformedLineException` for a stray character after a closing quote and for an unterminated quote; this reader returns the record with an issue and a line number. Blank lines are skipped by both. The comparison categories are asserted in `CsvPropertyTests`; the oracle's behaviour on Linux was not checked (it may join lines differently).
- **Schema and rules**: loader errors (unknown property with hint, wrong JSON types, contradictory rules, alias collisions), every rule type with boundary values (int/decimal/date min and max, min/max length, regex, allowed, enum canonical spelling, bool, trim, required, case), comma-decimal columns, leap days, a catastrophic regex that must time out instead of hanging.
- **Pipeline**: alias mapping, free column order, missing required/optional columns, the three bad-value policies, multiple issues per row, ragged rows, unterminated quote, duplicates (exact and hash, composite keys, saturation), error caps (50,000 bad rows with `--max-errors 10`), exact clean CSV / JSONL / rejects output, rejects raw column re-parses to the original record.
- **Culture**: decimals, ints, dates, case mapping, bool/enum matching, whole-pipeline and CLI output byte-identical under en-US, de-DE, fr-FR, tr-TR, ar-SA, th-TH, ja-JP.
- **CLI**: all four exit codes, usage errors with suggestions, stdout/stderr separation, `--fail-on-reject`, `--max-errors`, stdin, delimiter/encoding, refusal to overwrite the input, schema-check, sample determinism (and the committed sample equals the generator output), plus 4 tests that spawn the real `csvetl.dll`.
- **Streaming memory** (`StreamingTests`, run serially): 3,000,000 generated rows (about 314 million characters, 11 to 13 GB allocated in total) through the full pipeline with a bounded dedupe table: live heap measured with `GC.GetTotalMemory(true)` every 250,000 rows stayed at the 4.0 MiB baseline (delta under 0.1 MiB at one-decimal resolution). Also: 500k-row reader-only run, a 500k-row all-bad file with `--max-errors 50`, an endless 20M-character quoted field, and a **negative control** (materialising 100k records shows +76 MiB with the same probe). Reliability: `GetTotalMemory(true)` sees live managed objects at the sample instants only; short spikes between samples, native memory and the OS working set are not measured. The 3M-row test takes about 40 s on a quiet machine and 3 to 4 minutes on a busy one.

## Mutation check

**Tool: Stryker.NET 5.0.0** (`dotnet tool install dotnet-stryker`), which does work on .NET 10 here. The run is **partial**: a full-solution run was estimated at 1.5+ hours on this busy machine, so only these scopes were mutated, with the suite minus the slow `StreamingTests` class (326 tests at that point):

| scope | mutants tested | killed (incl. timeouts) | survived / no coverage | score |
|-------|----------------|-------------------------|------------------------|-------|
| `Schema/ColumnEvaluator.cs`, `Pipeline/KeyTracker.cs` | 100 | 74 | 22 survived, 4 no coverage | 74.0 % (7.5 min) |
| `Csv/CsvReader.cs`, `Csv/CsvWriter.cs`, `Validation/Validation.cs`, `Pipeline/EtlRunner.cs` | 365 | 314 (21 of them by Stryker's own timeout, hung mutants) | 49 survived, 2 no coverage | 86.0 % (39 min) |

**Not mutated at all:** `SchemaLoader.cs`, `Sinks.cs`, `ReportWriter.cs`, `Synthetic/`, and the whole CLI project (a CLI run failed to start because of a wrong project name in my config and was abandoned). Stryker compile-error mutants and mutants removed by its "already covered" filter are excluded from the counts. No processes were killed by hand; the timeouts were handled by Stryker itself.

**What the survivors were.** About half of the survivors are mutations of message strings (`""`, `"Stryker was here!"`) that no test pinned; they changed no behaviour. The rest, classified by hand:

- *Equivalent or practically equivalent (not fixed):* `_eof = true` to `false` (a TextReader keeps returning 0 at EOF); `_field.Clear()` removed at record start (the field is cleared at field end); `Peek() >= 0` to `> 0` after the last field (next char is always newline or EOF); `_expected < 0` to `<= 0` (first record always has at least one field); `rec.Raw ?? string.Join(...)` and `CaptureRaw = rejects != null || ...` (only differ when raw capture is switched off, which the runner never does by default); `schema.Key.Count == 0 ? Off : ...` (`BuildKey` already returns null without a key); `norm.Length > 0` to `>= 0` (an empty name never matches); `fi < Fields.Length` to `<=` (ragged rows are rejected before this line); `Fail(...)` setting the `Empty` flag (ignored when a failure exists); the UTF-8 BOM flag of the fallback encoding (only matters for writing); an unreachable `throw` for an unknown column type.
- *Real test gaps, tests added afterwards (`MessageAndEdgeTests.cs`, `MutationSurvivorTests.cs`):* NUL characters ending the input early, the raw-text cap arithmetic, first-structural-issue-wins, raw text missing stray characters, null-argument guards, `Kept` vs `Rejected` action on duplicates, `RowsRepaired` counting (the original test could not tell "repaired" from "not repaired"), the duplicate-header warning text, the `(row)` bucket in the per-column breakdown, `false` bool values being treated as empty when a column has no default, lowercase bool output in the clean CSV, clipping boundary at exactly 60 characters, exact failure messages for every rule, and `DedupeMode.Off` in `KeyTracker`.

**Honest status of the fixes:** the new tests were written for those survivors and the suite passes (361 cases), but **Stryker was not re-run afterwards**, so I cannot claim those mutants are now killed, and the scores above are the scores of the earlier suite. A hand-rolled re-verification script was drafted but never run, so it was deleted rather than shipped.

## Known limits

- Not a CSV dialect zoo: RFC 4180 plus leniencies listed above. No backslash escapes, no comment lines, no multi-character delimiters, no fixed-width files.
- No Excel files (`.xlsx` / `.xls`); export to CSV first.
- Encodings: UTF-8/16/32 via BOM, plus `latin1`/`utf-16` as a fallback option and UTF-8 stdin. Other code pages (for example windows-1252 vs latin1 differences, Shift-JIS) are not supported. Invalid bytes are replaced by U+FFFD silently.
- One unterminated quote turns the rest of the file into a single flagged record (see design notes).
- Duplicate detection is bounded, not unbounded: past `--max-keys` it silently (but reported) stops remembering new keys.
- Numbers: no thousands separators, no exponents, no currency symbols, no parentheses for negatives. Dates: only the formats you list; two-digit years use the .NET default cutoff; time zones and times of day are not supported.
- Regexes are .NET regexes with a 500 ms timeout, not a sandbox for hostile patterns written by an attacker (schemas are trusted input).
- No CSV/formula-injection sanitising on output (a cell starting with `=` is written as is).
- Single-threaded; throughput is modest (see timing). Only tested on Windows with .NET 10 SDK 10.0.402; other operating systems have not been run locally.
- CI ran green on the first run on GitHub Actions (ubuntu-latest, 2026-10-20).

## Repository layout

```
src/CsvEtl.Core   library: Csv/ (reader, writer), Schema/ (model, loader, evaluator), Validation/, Pipeline/ (runner, sinks, key tracker, report), Synthetic/
src/CsvEtl.Cli    csvetl executable: ArgParser, CliApp (testable Run(args, stdin, stdout, stderr)), Program
tests/CsvEtl.Tests
samples/          synthetic customers_messy.csv and customers.schema.json
```

## Clone

```
git clone https://github.com/Sup3rMar10/dotnet-csv-etl.git
```

MIT licensed (see LICENSE).
