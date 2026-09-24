# large-aspnetcore DIFF task card (rev2)

corpus: `dotnet/aspnetcore` (`main`) @ `7cc41c501115f365c0b7f1b50ebaa134a328f0e5` (MIT). This
card holds the input for the [protocol.md Revision 2](../../protocol.md#r2-2-task) DIFF
composite (D1 VCS, D2 Session, D3 RESOLVE). Per R2-1/R2-9 it does not contain corpus
source text, file paths, line numbers, or answers — only commit IDs.

## Commit pairs (select-diff, R2-2 "DIFF 커밋 선정 규칙" rules 1-5)

`benchmarks/corpora/tools/CorpusSyntaxTool select-diff` walked `git rev-list
--first-parent --max-count=500` from the pinned commit (192 of 500 commits were
eligible: 1-10 changed `.cs` files outside generated paths, 2-400 added+deleted `.cs`
lines counted with git's default rename detection on (`git diff --numstat`, no
`-M`/`--no-renames` override — see `benchmarks/corpora/tools/README.md`), non-whitespace-
only `git diff -w`), then paired them newest-first with the
sliding-window algorithm in R2-2 rule 3 (tree(c)->tree(c') capped at 20 files / 800
lines; 2 candidates were rejected for exceeding that cap). Pair 0 is `tuning`, pairs
1-5 are `held-out` (R2-2 "tuning / held-out").

| pair | role | `p` (D1 HEAD / base) | `c` (D2 session-start tree) | `c'` (D1/D2 target tree) |
|---|---|---|---|---|
| 0 | tuning | `8d321a88f375c5418086f153a2eb40fc2c930ced` | `26e9547de9301d6da2aa0f3a1a52eca6350060c5` | `e47e89d2c8c69381d4f0dfbe334e2d76776def65` |
| 1 | held-out | `f5930dbb4ea7d3b2d4940f06c018a9f5651e84d4` | `e10a071e2c7744370cb4652bfc40b31c9de7853b` | `60d2043c192aca6529546096d8fbb08fe26aeb84` |
| 2 | held-out | `80ce68b5280ef01757bdf9d94b653da62e5724d5` | `9817792d3ba9aa6f6c9ac5804a672ce5954f3288` | `f5930dbb4ea7d3b2d4940f06c018a9f5651e84d4` |
| 3 | held-out | `8821f7eb710e9ab70f1493e9be6fb9d5cff3d75b` | `905287949f949a686bccd2fb5f1d096ffbd02f95` | `443b141661671e4cf9f33ece77f1a0d5e036bf80` |
| 4 | held-out | `79afe979a028e7823b3c1bda2c34e60b86afc39a` | `f509e3d50f6c18f4d1ee89703eb1635f87a244b9` | `84ee09314ff93187dea88d1c2207d99091609e76` |
| 5 | held-out | `c6bffb3a075720353418b41787132799b9f041e3` | `061691db08006bd4d573d7c074ab1e3c6e6e0bdd` | `480b1324632716d8fa79c80331ef5dd084f92f2b` |

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
