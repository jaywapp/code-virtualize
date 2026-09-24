# medium-quartznet DIFF task card (rev2)

corpus: `quartznet/quartznet` (`main`) @ `1e039471fc457f6efa1d3bf1224324b82236b759` (Apache-2.0).
This card holds the input for the [protocol.md Revision 2](../../protocol.md#r2-2-task)
DIFF composite (D1 VCS, D2 Session, D3 RESOLVE). Per R2-1/R2-9 it does not contain
corpus source text, file paths, line numbers, or answers — only commit IDs.

## Commit pairs (select-diff, R2-2 "DIFF 커밋 선정 규칙" rules 1-5)

`benchmarks/corpora/tools/CorpusSyntaxTool select-diff` walked `git rev-list
--first-parent --max-count=500` from the pinned commit (259 of 500 commits were
eligible: 1-10 changed `.cs` files outside generated paths, 2-400 added+deleted `.cs`
lines counted with git's default rename detection on (`git diff --numstat`, no
`-M`/`--no-renames` override — see `benchmarks/corpora/tools/README.md`), non-whitespace-
only `git diff -w`), then paired them newest-first with the
sliding-window algorithm in R2-2 rule 3 (tree(c)->tree(c') capped at 20 files / 800
lines; 2 candidates were rejected for exceeding that cap). Pair 0 is `tuning`, pairs
1-5 are `held-out` (R2-2 "tuning / held-out"). Pair 0's `c'` equals the pinned commit
itself — the pinned commit was eligible against its own first parent, so it became the
newest usable pair endpoint; this is expected under R2-2 rule 1/4, not an error.

| pair | role | `p` (D1 HEAD / base) | `c` (D2 session-start tree) | `c'` (D1/D2 target tree) |
|---|---|---|---|---|
| 0 | tuning | `f79b16772a29caf432fcd369f3083649682b5034` | `907c86c8c878d197c44358b3a5728e5025ac3434` | `1e039471fc457f6efa1d3bf1224324b82236b759` |
| 1 | held-out | `85211444d47e36c0dcb51fe8814ffa3a1a1fc53a` | `8699baf26c196c44e50e19fee77c094410f09738` | `f79b16772a29caf432fcd369f3083649682b5034` |
| 2 | held-out | `6d99e5d934d10eec283b1bfc568596dbc11e93fe` | `b6651cd26ee35830dde06c3cb18871b325c5d0b8` | `85211444d47e36c0dcb51fe8814ffa3a1a1fc53a` |
| 3 | held-out | `e5b0c8499ee1d5c7e333d269d06e1071b4326f3f` | `0cc34e335097dff1d5b6646b4eac4c1acd1e7e81` | `10bdb516f3c7d927a81456769900f50245a606b0` |
| 4 | held-out | `26f0915d9201eda85b94de22a15f4c0a43e21065` | `0fedf8557e8b1263043c767a3ead26d95c08d866` | `e5b0c8499ee1d5c7e333d269d06e1071b4326f3f` |
| 5 | held-out | `55c8eb80c080c0e9bdd4dcdaa3d99198b15a705d` | `add7809473c62aa7d83639947e06acd5f733f31e` | `59d9a58a0a25df31ab901f680188fbd03d4fdaaf` |

`p` is the first parent of `c`. Full eligibility/rejection detail and the exact `git`
commands used are in
[`benchmarks/corpora/rev2-manifest.json`](../../corpora/rev2-manifest.json) and
reproducible via `select-diff` (see the tool's `--help`/usage banner).

## Composite steps (apply to every pair; R2-2)

| step | content |
|---|---|
| D1 VCS | HEAD = `p`, working tree = `tree(c')`. Report the HEAD-relative change per symbol |
| D2 Session | Session start working tree = `tree(c)` (dirty relative to HEAD `p`), current = `tree(c')`. Report only changes made after session start; the pre-session-start change (`p` to `c`) must be excluded |
| D3 RESOLVE | From VCS base `p`, resolve the declaration source of the designated symbol. Substituting the current file's text is a failure |

The D3 designated symbol is not fixed by this card. Per the TASK-027 spawn instructions,
D3 depends on the D1 answer (`(path ordinal, line)`-first deleted member, or if none,
first body-changed member) and is therefore assigned in TASK-027 Phase B together with
`expected.json`, not in this Phase A card.
