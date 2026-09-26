# CorpusSyntaxTool

Independent corpus measurement tool for [protocol.md Revision 2](../../protocol.md#revision-2--mediumlarge-재측정-프로토콜-task-024)
(R2-1 grading metrics, R2-2 NAV target sampling, R2-2 DIFF commit-pair selection).

It depends only on the public Roslyn syntax API (`Microsoft.CodeAnalysis.CSharp`,
version pinned in `CorpusSyntaxTool.csproj` / `packages.lock.json`) and the local `git`
and `rg` (ripgrep) executables. It does **not** reference, build, or run any
`CodeVirtualize.*` project, and it is intentionally **not** added to `CodeVirtualize.sln`.

## Build

```
cd benchmarks/corpora/tools/CorpusSyntaxTool
dotnet restore --locked-mode
dotnet build -c Release
```

## Commands used to produce `benchmarks/corpora/rev2-manifest.json` and
`benchmarks/tasks/rev2/*.md`

Run from `benchmarks/corpora/tools/CorpusSyntaxTool`, against a byte-exact checkout of
each pinned commit (see "Checkout requirements" below). `<corpusRoot>` is the local
working tree path of the corpus clone (outside this repository, per R2-1).

```
dotnet run -c Release --no-build -- grade          <corpusRoot> <out-grade.json>
dotnet run -c Release --no-build -- nav-candidates  <corpusRoot> <out-candidates.json>
dotnet run -c Release --no-build -- select-nav      <corpusRoot> <out-candidates.json> <out-selected.json> [seed=20260920] [take=12]
dotnet run -c Release --no-build -- select-diff     <corpusRoot> <pinnedCommit> <out-diffpairs.json> [maxCommits=500] [pairs=6]
```

Exact invocations for the three rev2 corpora (aliases and pinned commits from
[protocol.md R2-1](../../protocol.md#r2-1-corpus)):

```
dotnet run -c Release --no-build -- grade          <litedb-root>    _work/medium-litedb-grade.json
dotnet run -c Release --no-build -- nav-candidates  <litedb-root>    _work/medium-litedb-nav-candidates.json
dotnet run -c Release --no-build -- select-nav      <litedb-root>    _work/medium-litedb-nav-candidates.json _work/medium-litedb-nav-selected.json
dotnet run -c Release --no-build -- select-diff     <litedb-root>    0fd277aaed127b9dec99524277fb4173a2351167 _work/medium-litedb-diff-pairs.json

dotnet run -c Release --no-build -- grade          <quartznet-root> _work/medium-quartznet-grade.json
dotnet run -c Release --no-build -- nav-candidates  <quartznet-root> _work/medium-quartznet-nav-candidates.json
dotnet run -c Release --no-build -- select-nav      <quartznet-root> _work/medium-quartznet-nav-candidates.json _work/medium-quartznet-nav-selected.json
dotnet run -c Release --no-build -- select-diff     <quartznet-root> 1e039471fc457f6efa1d3bf1224324b82236b759 _work/medium-quartznet-diff-pairs.json

dotnet run -c Release --no-build -- grade          <aspnetcore-root> _work/large-aspnetcore-grade.json
dotnet run -c Release --no-build -- nav-candidates  <aspnetcore-root> _work/large-aspnetcore-nav-candidates.json
dotnet run -c Release --no-build -- select-nav      <aspnetcore-root> _work/large-aspnetcore-nav-candidates.json _work/large-aspnetcore-nav-selected.json
dotnet run -c Release --no-build -- select-diff     <aspnetcore-root> 7cc41c501115f365c0b7f1b50ebaa134a328f0e5 _work/large-aspnetcore-diff-pairs.json
```

`_work/*.json` are intermediate outputs consumed while assembling
`benchmarks/corpora/rev2-manifest.json`; they are not themselves an R2-9 deliverable and
are not committed (see `.gitignore` entry added for `benchmarks/corpora/_work/`).

`select-nav` and `grade`/`nav-candidates` are fully deterministic given the same corpus
bytes (verified by re-running `select-nav` twice and diffing byte-identical output).
`select-diff` is deterministic given the same corpus history; it does not read commit
messages or diff content to decide selection (R2-2 rule 5) — only file-count and
added+deleted line-count thresholds, computed from `git diff --numstat` (git's own
default, no `-M`/`--no-renames` override) and non-emptiness of `git diff -w`.

### DIFF rename-detection default

R2-2 rules 2/3 say literally `git diff --numstat`, with no `-M`/`--no-renames` flag
(unlike the R2-3 condition A/B evidence commands, which explicitly say `git diff -M`).
`select-diff` therefore relies on git's own runtime default for rename detection rather
than forcing it on or off. On this tool's git installation (2.50.1.windows.1, with
`diff.renames` unset in both the global and every corpus-local config), that default is
empirically **on** — `git diff --numstat` with no flags produces output identical to an
explicit `-M`, confirmed with a throwaway synthetic repo containing a pure rename. This
does mean `select-diff`'s output could differ on a machine where `diff.renames` is
explicitly set to `false`; that dependency is inherent in following the protocol text
literally and is not otherwise worked around.

To parse renamed-file numstat records unambiguously, `DiffCsNumstat` passes `-z`
(NUL-delimited output) instead of parsing the human-readable `old => new` /
`dir/{old => new}/rest` compaction that plain `--numstat` prints for renames. Under
`-z`, a rename record is `"<added>\t<deleted>\t"` NUL `<oldPath>` NUL `<newPath>` NUL
(empty third field signals a rename, and the two path tokens that follow are
unambiguous — no brace/arrow syntax to parse). Renames are accounted under the **new**
path (post-rename name) and as a single changed file with git's reported added/deleted
counts for that path (not as a synthetic full delete-of-old + add-of-new).

This was verified against an earlier implementation that forced `--no-renames`
(treating every rename as a plain delete+add): re-running `select-diff` for all three
rev2 corpora with the default-rename-detection `-z` implementation produced the exact
same 6 selected pairs (same commit SHAs, same `cFilesChanged`/`cLinesChanged`/
`rangeFilesChanged`/`rangeLinesChanged` for every pair) and the exact same rejected
candidates as the `--no-renames` run; only `eligibleCommitCount` changed slightly
(medium-litedb 358→360, medium-quartznet 258→259, large-aspnetcore 191→192 out of 500
walked), from a handful of commits whose rename-adjusted diff size crossed the rule 2
eligibility thresholds but were not needed to fill the 6 pairs either way. See
`rev2-manifest.json`'s `diffPairs.renameDetectionCheck` per corpus.

## Checkout requirements (R2-1: "checkout은 core.autocrlf=false로 commit의 bytes를 그대로 둔다")

`core.autocrlf=false` alone is **not sufficient** to get byte-exact checkouts on
Windows when a repository's `.gitattributes` sets `* text=auto` (true for all three
rev2 corpora). With `text=auto`, Git still line-ending-normalizes checked-out files
according to `core.eol` (which defaults to `native`, i.e. CRLF on Windows), regardless
of `core.autocrlf`. The clones under this task also set `core.eol=lf` and
`core.longpaths=true` (aspnetcore has test-snapshot filenames over 260 characters), then
force a full re-checkout (`git rm -r --cached -q .` followed by `git checkout HEAD -- .`,
since a fresh working tree does not need to be discarded via `git checkout -f` — there is
nothing else in the working tree yet):

```
git config core.autocrlf false
git config core.eol lf
git config core.longpaths true   # only needed for large-aspnetcore
git rm -r --cached -q .
git checkout HEAD -- .
```

Verify with `git status --short` (must be empty) and by summing `git cat-file
--batch-check='%(objectsize)'` over all tracked `.cs` blobs vs. the actual on-disk byte
sum — both were confirmed to match exactly (down to the byte) for all three corpora
before any measurement in this tool was run.
