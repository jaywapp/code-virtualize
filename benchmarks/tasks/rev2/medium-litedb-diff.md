# medium-litedb DIFF task card (rev2)

corpus: `litedb-org/LiteDB` (`dev`) @ `0fd277aaed127b9dec99524277fb4173a2351167` (MIT). This
card holds the input for the [protocol.md Revision 2](../../protocol.md#r2-2-task) DIFF
composite (D1 VCS, D2 Session, D3 RESOLVE). Per R2-1/R2-9 it does not contain corpus
source text, file paths, line numbers, or answers — only commit IDs.

## Commit pairs (select-diff, R2-2 "DIFF 커밋 선정 규칙" rules 1-5)

`benchmarks/corpora/tools/CorpusSyntaxTool select-diff` walked `git rev-list
--first-parent --max-count=500` from the pinned commit (360 of 500 commits were
eligible: 1-10 changed `.cs` files outside generated paths, 2-400 added+deleted `.cs`
lines counted with git's default rename detection on (`git diff --numstat`, no
`-M`/`--no-renames` override — see `benchmarks/corpora/tools/README.md`), non-whitespace-
only `git diff -w`), then paired them newest-first with the
sliding-window algorithm in R2-2 rule 3 (tree(c)->tree(c') capped at 20 files / 800
lines). Pair 0 is `tuning`, pairs 1-5 are `held-out` (R2-2 "tuning / held-out").

| pair | role | `p` (D1 HEAD / base) | `c` (D2 session-start tree) | `c'` (D1/D2 target tree) |
|---|---|---|---|---|
| 0 | tuning | `1c10567869bf7d1b969cbedf08b314473a873b34` | `b3e2bafcb055f48c51118abb5f101112641b2536` | `6fbc81b7648d78d8519481d1feb0681f9bb37aca` |
| 1 | held-out | `ffc3edfc9728cc8cceccd8337f4a31a6817d2b37` | `ec8de1ca83d711807f921a0028d2db04c60ed0d9` | `a460a660bb7351da8df3f6b06ba65bd1084a14ca` |
| 2 | held-out | `ab9c69da942f6acdc4c8dbcab3d0f4da761aa7df` | `1b69713044b630b36fee0e7f616fda7f081fe828` | `1ac0faf57f9e2bf64845be68a1900a61a1474728` |
| 3 | held-out | `fb7a59b218d6a72a923813f68959073b31f03e42` | `2f1b114aaea1f30ec42466e9a5b68adee9fa7f5c` | `f0afcc13ffdab1aacf5f9d2b69824a88e225646a` |
| 4 | held-out | `2472a9651b8ae63e2e9fd47ef82700596eda926e` | `b93a0d43aa6cd85893de33c3515c8c0dc9c902ad` | `fb7a59b218d6a72a923813f68959073b31f03e42` |
| 5 | held-out | `760766c7f5959630a65d101d898fbe8a07094ae2` | `9c7425428a965e6fd09c3e4dffeb297566a12b6d` | `2472a9651b8ae63e2e9fd47ef82700596eda926e` |

`p` is the first parent of `c`. Pair 4's `c'` equals pair 3's `p` here (adjacent
eligible commits with no intervening rejection between them); this is expected, not an
error — the sliding window simply continues from the next unused eligible commit after
each accepted pair. Full eligibility/rejection detail and the exact `git`
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
