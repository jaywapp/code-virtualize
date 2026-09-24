# medium-litedb NAV task card (rev2)

corpus: `litedb-org/LiteDB` (`dev`) @ `0fd277aaed127b9dec99524277fb4173a2351167` (MIT). This card holds the
input for the [protocol.md Revision 2](../../protocol.md#r2-2-task) NAV composite
(N1 DECL, N2 SRC, N3 REF, N4 RECOVERY). Per R2-1/R2-9 it does not contain corpus
source text, file paths, line numbers, or answers.

## Candidate order (select-nav, seed `20260920`)

`benchmarks/corpora/tools/CorpusSyntaxTool` (`nav-candidates` + `select-nav`) produced
the ordinary-method candidate pool for this corpus, filtered to names that appear on
2-40 lines corpus-wide (`rg -c -w`, R2-2 rule 2), sorted by `(path ordinal, line)`
(R2-2 rule 3), then shuffled with `System.Random(20260920)` Fisher-Yates. This table
is the first 12 of that shuffled order — candidate 0 is `tuning`, 1-11 are ordered
`held-out` candidates (R2-2 "tuning / held-out", TASK-027 instruction step 4).

Final `NAV-01..NAV-06` task-ID assignment happens in TASK-027 Phase B: walk this order
from candidate 0 and keep the first 6 candidates (candidate 0 stays `tuning`; the next
kept ones become `held-out`) whose independently-read static reference count is >= 1,
skipping any candidate with 0 static references (R2-2 rule 3 last sentence). This card
lists all 12 so Phase B never has to re-derive or re-shuffle the order.

| order | role (provisional) | normalized name | parameter types |
|---|---|---|---|
| 0 | tuning | `LiteDB.BsonExpression.WithoutParameters` | (none) |
| 1 | held-out candidate | `LiteDB.Engine.DiskService.MarkAsInvalidState` | (none) |
| 2 | held-out candidate | `LiteDB.Tests.Engine.MemoryManagement_Tests.CreateDisposedDatabaseReferences` | (none) |
| 3 | held-out candidate | `LiteDB.Engine.IndexService.AddNode` | `CollectionIndex, BsonValue, PageAddress, byte, IndexNode` |
| 4 | held-out candidate | `LiteDB.Shell.StringScanner.Scan` | `Regex` |
| 5 | held-out candidate | `LiteDB.Internals.WalTestDatabase.Recover` | `string, bool` |
| 6 | held-out candidate | `LiteDB.Tests.Engine.RebuildFaultRun.ReadLive` | (none) |
| 7 | held-out candidate | `LiteDB.LinqExpressionTranslator.TranslateUnary` | `UnaryExpression` |
| 8 | held-out candidate | `LiteDB.BsonExpressionMethods.DOUBLE` | `BsonValue, BsonValue` |
| 9 | held-out candidate | `LiteDB.Tests.Issues.Issue2824_Tests.AssertRejectedOpensDidNotChangeFile` | `string, Action` |
| 10 | held-out candidate | `LiteDB.Tests.Issues.Issue2357InvalidTime_Tests.Connection_string_key_is_parsed_and_reaches_the_engine_settings` | `string` |
| 11 | held-out candidate | `LiteDB.Engine.BorrowedDocumentReader.AsciiEqualsIgnoreCase` | `byte, byte` |

Full detail (including source-relative path/line used only to re-locate the candidate
for answer writing, and the `rg -c -w` occurrence count) is in
[`benchmarks/corpora/rev2-manifest.json`](../../corpora/rev2-manifest.json) — that file
is machine-readable input for TASK-027 Phase B, not a task-card input for conditions
A-E.

## Composite steps (apply to every confirmed NAV task; R2-2)

| step | content | expected answer item |
|---|---|---|
| N1 DECL | Find every declaration of the target simple name inside its containing type (overloads, partial parts, explicit interface implementations included) | list of declaration locations |
| N2 SRC | Get the declaration source of the one target overload identified by the parameter-type list above | declaration line range and normalized-text hash |
| N3 REF | Find all static references to the target overload across the whole corpus | static reference locations; dynamic/string candidates listed separately |
| N4 RECOVERY | Get the current declaration source again after two edits | edit 1: append `// cv-bench-edit-1` after the last line of the target file (outside the declaration span). edit 2: insert `// cv-bench-edit-2` on its own line, immediately after the line with the declaration's first `{` (or immediately before the declaration's last line if it has no `{`). The current declaration text after each edit is the expected answer |

Edit 1 does not shift the target declaration's line number. See
[protocol.md R2-4 R2](../../protocol.md#r2-4-계상-규칙) for why both edits are required
(one outside, one inside the declaration span). Each condition reverts its edits at the
end of the task, and condition E republishes the pre-edit state via `cv-update`.
