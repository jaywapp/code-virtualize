# TASK-025 — source-byte 반환 계약 개선 설계 제안

- 상태: Proposed. 사용자 확인 전이다. 확인 후 TASK-026이 구현한다.
- 작성일: 2026-09-23
- 범위: NAV(`cv_get`/`cv-resolve`)와 DIFF(Core `SymbolDiffService`)의 source 반환 계약
- 반영 대상(확인 후 TASK-026): `docs/contracts/cli.md`, `schemas/common.schema.json`, `schemas/diff.schema.json`, `docs/prepare/architecture.md`, `src/`, `tests/`
- 이 문서는 FUP-002의 동결 수치(stronger available baseline 대비 20% 이상 감소)를 바꾸지 않는다. protocol 변경은 TASK-024가 한다. 측정 규칙에 영향이 있는 사항은 9·10절에 TASK-024 전달 항목으로 따로 적는다.

## 1. 배경과 목표

TASK-016에서 E는 품질 6/6, recall 27/27, Critical 0을 달성했지만 source bytes가 NAV B 472 B → E 426 B(9.75% 감소), DIFF B 74 B → E 1,272 B(1,618.9% 증가)로 gate를 통과하지 못했다([cv-report](../../benchmarks/results/cv-report.md)). ADR 003 재개 조건 2는 "source-byte 반환 계약, 특히 diff evidence의 과다 materialization 개선"을 요구한다.

목표는 다음과 같다.

1. NAV: small corpus 기준 E 426 B를 377.6 B 이하로 낮출 수 있는 계약을 제공한다. 이 기준을 small fixture 전용 조정 없이 일반 규모에서도 성립하는 방식으로 달성한다.
2. DIFF: 현재 계상 방식에서 small DIFF는 DIFF-03의 필수 반환(74 B)이 통과선 59.2 B보다 커서 통과할 수 없다(B의 `git diff` 출력 미계상은 10절 R1 참고). 따라서 목표는 small 통과가 아니라 **과다 materialization 제거**다. 즉 반환량이 변경 규모에 비례하고 containing type이나 symbol 전체 크기에 비례하지 않게 한다.
3. 다음 안전성 불변식을 유지한다: stale 원문 오반환 0, 조용한 partial 0, coverage·freshness 표시, DIFF-03은 선택한 baseline의 base source 반환(현재 파일로 대체 금지), VCS/Session baseline 의미 유지.
4. 사용자 결정에 따라 schema 버전을 올리지 않고 v1을 직접 수정한다.

## 2. 원인 분석

### 2.1 측정 방법

source bytes를 세는 위치는 [CvEvaluationRunner.cs](../../benchmarks/runs/CvEvaluationRunner.cs)다.

| 조건·task | 세는 대상 | 근거 |
|---|---|---|
| E NAV | MCP `cv_get` 응답의 `structuredContent.source.content` UTF-8 bytes. 첫 조회와 stale 복구 후 재조회를 합산한다 | `CvEvaluationRunner.cs:398-409`, `:472-484`, `:933-939` |
| E DIFF | Core `SymbolDiffChange.BaseSource` + `TargetSource` UTF-8 bytes를 모든 change에 대해 합산한다. `TextualHunk`는 세지 않는다 | `CvEvaluationRunner.cs:614-615` |
| B NAV | `LinkedHelper.cs` 3~6행과 `Contracts.cs` 8~25행을 `\n`으로 join한 bytes. `rg` 출력은 세지 않는다 | `CvEvaluationRunner.cs:227-236` |
| B DIFF | `session-start/ReviewTarget.cs` 10~13행 join bytes. `git diff --unified=1` 두 번의 출력은 세지 않는다 | `CvEvaluationRunner.cs:246-248`, `:258-264` |

아래 분해는 fixture 원문과 위 코드를 대조해 계산했고, 기록된 중앙값(426 B, 1,272 B)과 바이트 단위로 일치한다. 측정 당시 fixture는 LF였다. 현재 Windows checkout은 `core.autocrlf=true`여서 working tree가 CRLF(`git ls-files --eol`: `i/lf w/crlf`)이므로 같은 계산을 CRLF로 하면 NAV 440 B, DIFF 1,334 B가 나온다. 이 불일치는 10절에 적는다. CLI 실행은 하지 않았다. `tests/fixtures/.../.code-virtualize`에 reader pin 파일이 생겨 금지 경로가 수정되기 때문이다.

### 2.2 NAV 426 B 분해

E NAV는 `Catalog.Load(string)`(`Contracts.cs:12-14`)을 `part=context`, `contextLines=2`로 두 번 조회한다. `SourceResolver.Range`는 context 요청 시 span 앞뒤 2줄을 줄 경계로 확장하고 마지막 줄의 줄바꿈까지 포함한다(`SourceResolver.cs:119-125`).

| 호출 | 반환 범위 | 구성 | bytes |
|---|---|---|---:|
| `cv_get` #1 (`CvEvaluationRunner.cs:398`) | `Contracts.cs:10-16` | 10~11행 주석(M-01·M-02) 114 B + 12~14행 선언 47 B + 15~16행 인접 코드(빈 줄, `Load(string, int)` 헤더) 52 B | 213 |
| stale 검출 `cv_get` (`:447`) | 없음 | `SOURCE_STALE` 오류라 source가 없다 | 0 |
| 복구 후 `cv_get` #2 (`:472`) | `Contracts.cs:10-16` | #1과 같은 범위. 수정은 10행 주석의 같은 길이 교체(`Loads one value.` → `Loads one item!!`)뿐이다 | 213 |
| 합계 | | | **426** |

원인은 다음과 같다.

- **N1. 반환 범위가 필요 이상이다.** 필요한 선언 자체는 span 기준 42 B(`public void Load(string value)` ~ `}`)인데 context가 주석 114 B와 이웃 선언 52 B를 함께 끌어온다. 현재 part는 `header`(30 B), `body`(중괄호 내부), `context` 세 가지뿐이라 "선언 전체만"을 요청할 방법이 없다(`ResolutionContracts.cs:5`).
- **N2. 복구 재조회가 전체를 다시 materialize한다.** NAV bytes의 50%다. 요청한 slice가 바뀌지 않았어도 계약에 "변경 없음" 응답이 없어서 213 B를 다시 보낸다. 이 fixture에서 선언 span(12~14행)은 실제로 바뀌지 않았다.
- **N3. 참고: 기본값은 이미 작다.** MCP·CLI의 기본 part는 `header`다(`McpServer.cs:202`, `ResolutionCommands.cs:14`). 426 B는 runner가 context를 명시해 생긴 값이다. 따라서 NAV 절감은 계약 변경과 함께 **E 호출 패턴을 protocol에 고정해야** 실현된다(9절 U7).
- E NAV 품질 판정은 `cv_get`의 content를 검사하지 않는다. `getQuality = !IsError(get)`뿐이다(`CvEvaluationRunner.cs:490`). 따라서 part를 줄여도 현재 품질 판정은 바뀌지 않는다.

### 2.3 DIFF 1,272 B 분해

`SymbolDiffService.CreateView`는 각 symbol의 source를 declaration span 전체로 만든다(`SymbolDiffService.cs:201-215`). 이때 type symbol의 span에는 모든 member 원문이 포함된다. matched symbol은 source 문자열이 다르면 곧바로 `BodyChanged`가 된다(`:81-93`). 그리고 `Change`는 모든 change에 before·after 전체 원문을 `SymbolDiffChange.BaseSource/TargetSource`로 싣는다(`:159-191`, `DiffModels.cs:55-63`).

| Mode | Change | Kind | Base bytes | Target bytes | 합 |
|---|---|---|---:|---:|---:|
| VCS | `ReviewTarget` (class) | body_changed (정답표 외) | 183 | 215 | 398 |
| VCS | `ReviewTarget.Existing()` | body_changed (V-01) | 66 | 82 | 148 |
| VCS | `ReviewTarget.AddedDuringSession()` | added (V-02) | 0 | 86 | 86 |
| VCS | `ReviewTarget.Removed()` | deleted (V-03) | 70 | 0 | 70 |
| Session | `ReviewTarget` (class) | body_changed (정답표 외) | 199 | 215 | 414 |
| Session | `ReviewTarget.AddedDuringSession()` | added (S-02) | 0 | 86 | 86 |
| Session | `ReviewTarget.Removed()` | deleted (S-03) | 70 | 0 | 70 |
| 합계 | | | | | **1,272** |

원인별 비중은 다음과 같다.

| 원인 | bytes | 비중 | 일반 규모에서의 증가 |
|---|---:|---:|---|
| **D1. containing type cascade**: member 하나만 바뀌어도 class 전체 before/after가 `body_changed`로 materialize된다. manifest의 DIFF false positive 2건이 이것이다 | 812 | 63.8% | class 크기에 비례한다. 큰 class 안의 한 줄 수정도 class 전체의 두 배가 된다 |
| **D2. body 변경의 전체 before/after**: 1줄 변경(V-01)에 method 전체 양쪽을 싣는다 | 148 | 11.6% | 변경 줄 수가 아니라 symbol 크기에 비례한다 |
| **D3. added/deleted 전체 원문 선반환**: 에이전트가 요청하지 않아도 body 전체를 싣는다 | 312 | 24.5% | 추가·삭제 symbol 크기에 비례한다 |

추가 관찰은 다음과 같다.

- **D4. `TextualHunk`도 hunk가 아니다.** `UnifiedHunk`는 symbol 전체를 `-`로, 전체를 `+`로 출력하고 `@@ -1,N +1,M @@`처럼 파일이 아닌 symbol 기준 줄 번호를 쓴다(`SymbolDiffService.cs:333-344`). 현재 runner는 이것을 세지 않는다. 그러나 정의상 "diff source evidence"에 해당하므로 대칭적으로 세면 E DIFF는 약 두 배가 된다.
- DIFF-03(삭제된 `Removed()`의 base source)은 D3의 `Removed` 70 B로 충족된다. 이 요구 자체는 줄일 수 없는 최소 비용이다. B는 같은 범위를 줄 단위로 읽어 74 B다.

## 3. 설계 대안 비교

### 3.1 DIFF

| 항목 | D-A: 선반환 유지, cascade만 제거 | **D-B: 줄 단위 hunk + 원문 lazy resolve (권장)** | D-C: fingerprint 전용 + 전부 lazy |
|---|---|---|---|
| 개요 | container 자기 텍스트 비교로 cascade를 제거한다. 변경 symbol의 before/after 전체 선반환과 pseudo hunk는 유지한다 | container cascade를 제거한다. body/signature/remark 변경은 파일 줄 번호를 쓰는 실제 line hunk(context 1줄)로 표현한다. added/deleted는 header 한 줄만 싣고 원문은 `DiffSourceResolver`로 lazy resolve한다 | entry에 hash와 위치만 둔다. 모든 원문은 lazy resolve한다 |
| small 예상 bytes (추정) | 460 B (−64%) | 약 272~282 B (−78%). 목록 202~212 B + DIFF-03 resolve 70 B | 목록 0 B + DIFF-03 70 B. V-01 검토에는 before/after 재조회 148 B가 더 든다 |
| 일반 규모 반환량 | 변경 symbol 전체 크기에 비례한다 | 변경 줄 수 + 2×context에 비례한다. 추가·삭제는 header 크기다 | 목록은 0이지만 검토에는 거의 항상 재조회가 필요하다 |
| 에이전트 추가 조회 비용 | 없음 | 추가·삭제 body가 필요할 때만 resolve 1회(해당 symbol 크기만큼). body 변경은 hunk로 충분한 경우가 많다 | 모든 검토에 resolve가 필요하다. 재조회도 bytes에 포함되므로 절감이 사라진다 |
| 안전성 영향 | 없음 | 생략을 `textMode`·`omitted*Lines`로 명시해야 한다. lazy resolve는 snapshot digest 검증과 baseline 결속이 필요하다 | D-B와 같다. 여기에 현재 정답 판정의 "모든 기대 diff에 textual evidence"(`CvEvaluationRunner.cs:611-612`, expected.md의 텍스트 근거 열)를 충족하지 못한다 |
| 구현 복잡도 | 낮다 | 중간이다. Myers line diff, container masking, resolver, budget이 필요하다 | 낮다(resolver만) |
| 판정 | 규모 문제(D2·D3)를 남긴다 | **권장** | 품질 요구 위반, 재조회로 이득 상쇄 |

### 3.2 NAV

| 항목 | N-A: 호출 패턴만 변경 | **N-B: `declaration` part + 조건부 재조회 (권장)** | N-C: 서버 세션 상태 기반 delta |
|---|---|---|---|
| 개요 | 계약은 그대로 두고 harness가 `header`를 요청한다 | 선언 span만 반환하는 `part=declaration`을 추가한다. 모든 slice에 `contentHash`를 두고, 요청의 `ifNoneMatch`가 현재 검증된 slice hash와 같으면 `notModified=true`로 content 없이 응답한다 | MCP 서버가 세션별로 반환 이력을 기억하고 재조회 시 이전 반환 대비 변경 줄만 보낸다 |
| small 예상 bytes (추정) | 60 B (header 30 B × 2) | 42 B (declaration 42 B + notModified 0 B). 조건부 재조회를 쓰지 않으면 84 B | 약 42~60 B |
| 정보 충분성 | signature만 있어 body를 볼 수 없다. `cv_find`의 `signature`와 거의 중복된다 | member의 전체 코드를 준다. 주석(M-01·M-02)은 기존 `context`로 계속 얻을 수 있다 | 전체 코드를 준다 |
| 일반 규모 효과 | 계약 개선이 아니다 | 이웃 코드가 빠져 member 크기에 비례한다. 편집 후 재조회가 잦은 긴 세션에서 바뀌지 않은 symbol의 재조회 비용이 0이 된다 | 효과는 비슷하다 |
| 안전성 영향 | 없음 | `ifNoneMatch`가 file digest 검증을 우회하지 않는다. 일치 판정은 검증된 현재 bytes의 hash로 한다 | 서버 상태와 클라이언트 context가 어긋나면 stale 오반환 위험이 있다(context 압축·재시작) |
| 구현 복잡도 | 없음 | 낮다 | 높다(상태·수명·동시성) |
| 판정 | 참고 기준선 | **권장** | 위험 대비 이득이 작다 |

N-B의 조건부 재조회는 선택 가능한 부가 요소다. N-B는 조건부 재조회 없이도 small 추정 84 B로 목표 377.6 B를 넘기지 않는다. 따라서 권장안의 NAV 판단은 복구 시나리오의 특정 편집 위치(주석 줄)에 의존하지 않는다.

## 4. 권장안: 계약 변경 명세

### 4.1 요약

- NAV: `SourcePart`에 `declaration`을 추가한다. 모든 source slice에 `contentHash`와 `notModified`를 둔다. `cv_get`/`cv-resolve`에 `ifNoneMatch`를 추가한다. 기본 part(`header`)와 `contextLines` 기본값(2)은 바꾸지 않는다.
- DIFF: (1) container type은 자기 텍스트(직계 member span을 제외한 텍스트)가 바뀔 때만 entry를 만든다. (2) 변경 evidence를 파일 줄 번호 기준의 실제 line hunk로 바꾼다. (3) added/deleted는 header만 싣는다. (4) `SymbolDiffChange`의 원문 선반환 필드를 제거하고 `DiffSourceResolver`로 lazy resolve한다. (5) diff 단위 evidence 예산과 명시적 생략·잘림 표시를 둔다.

### 4.2 NAV: source slice 계약

`SourcePart`는 `header | declaration | body | context`로 한다.

| part | 반환 범위 | 비고 |
|---|---|---|
| `header` | 선언 시작부터 첫 `{` 또는 `=>` 직전까지(뒤 공백 제거) | 기존과 같다. 기본값이다 |
| `declaration` | 선언 span 전체 `[span.start, span.start+span.length)` | **신규**. leading trivia(주석)는 포함하지 않는다. type symbol이면 member 전체가 포함되므로 예산 잘림이 명시될 수 있다 |
| `body` | 중괄호 내부 또는 `=>` 뒤 | 기존과 같다 |
| `context` | 선언 줄 ± `contextLines`, 줄 경계 정렬 | 기존과 같다. 주석이 필요하면 이 part를 쓴다 |

`sourceSlice`(common.schema.json)에 다음 필드를 추가하고 둘 다 required로 둔다.

| 필드 | 타입 | 의미 |
|---|---|---|
| `contentHash` | sha256 | 이번 요청 조건(part, declaration, contextLines, budget)으로 **반환되는 content**의 UTF-8 bytes SHA-256. `notModified=true`이면 반환했을 content의 hash이며 `ifNoneMatch`와 같다 |
| `notModified` | boolean | `true`이면 `content=""`, `usage={bytes:0, lines:0}`이다. `span`, `truncated`, `exhaustedBy`는 반환했을 content 기준이다 |

`SourceSliceContract.Validate`는 다음처럼 바꾼다: `notModified=false`이면 기존 규칙(`span.length == content.Length`, usage 일치)을 유지한다. `notModified=true`이면 `content==""`, `usage=(0,0)`이어야 하고 span 길이 규칙은 적용하지 않는다.

요청 입력은 다음과 같다.

| 인터페이스 | 추가 입력 |
|---|---|
| MCP `cv_get` | `part` enum에 `declaration`을 추가한다. `ifNoneMatch`: `^sha256:[0-9a-f]{64}$` |
| CLI `cv-resolve` | `--part header\|declaration\|body\|context`, `--if-none-match <sha256>` |
| Core `ResolveRequest` | `string? IfNoneMatch = null` |

`ifNoneMatch` 처리 순서는 다음과 같다. 기존 검증(파일 존재 → digest → encoding → span)을 **모두 통과한 뒤에만** 비교한다. stale이면 기존과 같이 `SOURCE_STALE` 오류를 반환하고 `notModified`를 쓰지 않는다. 일치하면 `status`, `freshness(returnedFiles=verified)`, `coverage`, `results[0]` 메타데이터(현재 span·path·contentHash)를 평소처럼 채운다.

예시(복구 후 재조회, 선언이 바뀌지 않은 경우):

```json
{
  "status": "ok",
  "freshness": { "returnedFiles": "verified", "workspace": "unknown" },
  "source": {
    "content": "",
    "span": { "start": 245, "length": 42, "startLine": 12, "endLine": 14, "offsetUnit": "utf16_code_unit", "lineBase": 1, "endLineInclusive": true },
    "budget": { "maxBytes": 8192, "maxLines": 100 },
    "usage": { "bytes": 0, "lines": 0 },
    "truncated": false,
    "exhaustedBy": "none",
    "contentHash": "sha256:<64 hex>",
    "notModified": true
  }
}
```

(값은 예시이며 envelope의 나머지 필드는 생략했다.)

### 4.3 DIFF: evidence 계약 (diff.schema.json)

`evidence`는 `textualHunk` 문자열을 유지하되 의미를 "파일 줄 번호 기준 unified hunk"로 바꾸고 다음 필드를 추가한다. 모두 required다.

| 필드 | 타입 | 의미 |
|---|---|---|
| `kind` | string | 기존과 같다 (`body-changed`, `symbol-added`, `member-order-changed` 등) |
| `textMode` | `line_hunks` \| `header_only` \| `fingerprint_only` | evidence 텍스트의 형태 |
| `textualHunk` | string \| null | `line_hunks`·`header_only`이면 필수, `fingerprint_only`이면 null |
| `contextLines` | integer ≥ 0 | hunk에 넣은 unchanged 문맥 줄 수. `header_only`·`fingerprint_only`이면 0 |
| `omittedBaseLines` | integer ≥ 0 | base 쪽 symbol 텍스트 중 hunk에 넣지 않은 줄 수(문맥 밖 unchanged 줄 포함) |
| `omittedTargetLines` | integer ≥ 0 | target 쪽 생략 줄 수 |
| `truncated` | boolean | 예산 때문에 `textMode`를 낮췄거나 hunk를 잘랐으면 `true`다. 정책상 생략(`header_only`)은 `false`다 |
| `baseContentHash` / `targetContentHash` | sha256 \| null | 기존과 같다. **symbol 전체 텍스트**의 hash다. lazy resolve 결과를 이 값으로 대조할 수 있다 |

`textMode`별 규칙은 다음과 같다.

| entry kind | 기본 textMode | hunk 내용 |
|---|---|---|
| `body_changed`, `signature_changed`, `remark_changed`, `formatting_only`, `rename_candidate` | `line_hunks` | before/after 텍스트의 Myers line diff 결과. 변경 줄 ± `contextLines`(기본 1)만 넣는다. `@@ -a,b +c,d @@`의 a·c는 **파일 줄 번호**다 |
| `added`, `deleted` | `header_only` | 선언 시작 줄부터 첫 `{`/`=>` 직전까지(=`header` part와 같은 규칙). 나머지 줄 수를 `omitted*Lines`에 기록한다 |
| container 멤버 순서만 바뀜 | `fingerprint_only` | 텍스트 없이 hash만 둔다. `kind=member-order-changed` |

`DiffContract`에 `evidenceTruncated`(boolean, required)를 추가한다. entry 하나라도 `truncated=true`이면 `true`이고, 이때 `limitations`에 `diff-evidence-budget-exhausted`를 반드시 넣는다. 기존 `truncated`/`nextCursor`는 **entry 목록**의 잘림에만 쓰고 의미를 바꾸지 않는다. evidence 생략은 변경 목록의 완전성과 별개 축이므로 coverage level을 낮추지 않는다. 대신 필드로 항상 드러낸다.

예시(V-01, VCS):

```json
{
  "kind": "body_changed",
  "baseSymbolId": "sym_<64 hex>",
  "targetSymbolId": "sym_<64 hex>",
  "baseLocations": [ { "path": "ReviewTarget.cs", "span": { "startLine": 5, "endLine": 8 } } ],
  "targetLocations": [ { "path": "ReviewTarget.cs", "span": { "startLine": 5, "endLine": 8 } } ],
  "evidence": [ {
    "kind": "body-changed",
    "textMode": "line_hunks",
    "textualHunk": "--- a/ReviewTarget.cs\n+++ b/ReviewTarget.cs\n@@ -6,3 +6,3 @@\n     {\n-        return \"base\";\n+        return \"dirty-before-session\";\n     }\n",
    "contextLines": 1,
    "omittedBaseLines": 1,
    "omittedTargetLines": 1,
    "truncated": false,
    "baseContentHash": "sha256:<64 hex>",
    "targetContentHash": "sha256:<64 hex>"
  } ],
  "matchConfidence": 1
}
```

예시(V-03, VCS):

```json
{
  "kind": "deleted",
  "baseSymbolId": "sym_<64 hex>",
  "targetSymbolId": null,
  "evidence": [ {
    "kind": "symbol-removed",
    "textMode": "header_only",
    "textualHunk": "--- a/ReviewTarget.cs\n+++ /dev/null\n@@ -10,1 +0,0 @@\n-public static string Removed()\n",
    "contextLines": 0,
    "omittedBaseLines": 3,
    "omittedTargetLines": 0,
    "truncated": false,
    "baseContentHash": "sha256:<64 hex>",
    "targetContentHash": null
  } ]
}
```

(location의 `fileId`·`contentHash`·span 고정 필드는 예시에서 생략했다.)

### 4.4 DIFF: container 자기 텍스트 비교

type symbol(다른 symbol의 `containerId`가 가리키는 symbol)의 비교 텍스트는 declaration span에서 **직계 member symbol의 declaration span을 제거한 텍스트**다. 비교 규칙은 다음과 같다.

1. signature가 다르면 기존대로 `signature_changed`로 판정한다. hunk는 자기 텍스트 기준이다.
2. 자기 텍스트가 whitespace 정규화 후에도 다르면 `body_changed`로 판정한다. 예: member 사이 주석, `#region`, index되지 않은 member. hunk는 자기 텍스트의 변경 줄만 담는다.
3. 자기 텍스트가 whitespace 정규화 후 같으면 entry를 만들지 않는다. member 추가·삭제로 생긴 빈 줄 차이도 여기에 해당한다.
4. base·target 양쪽에 있는 직계 member의 상대 순서가 다르면 `body_changed` + `fingerprint_only` evidence(`kind=member-order-changed`)를 만든다. 순서 변경이 조용히 사라지지 않게 하기 위해서다.

index되지 않은 텍스트는 자기 텍스트에 그대로 남는다. 따라서 member로 인식되지 않은 변경도 누락되지 않는다. 이 규칙은 모든 corpus에 동일하게 적용하며 fixture별 예외가 없다.

### 4.5 DIFF: lazy resolve (`DiffSourceResolver`)

`SymbolDiffChange`에서 `BaseSource`, `TargetSource`, `BaseRemark`, `TargetRemark`를 제거한다. 원문은 새 Core API로 요청할 때만 materialize한다.

```csharp
public enum DiffSide { Base, Target }

public sealed record DiffSourceRequest(
    string SelectionKey,              // SymbolDiffResult.SelectionKey
    BaselineContract Baseline,        // SymbolDiffResult.Contract.Baseline
    SymbolDiffSnapshot Snapshot,      // Base 또는 Target snapshot
    DiffSide Side,
    string SymbolId,
    SourcePart Part,                  // header | declaration | body | context
    SourceBudgetContract Budget,
    int? DeclarationIndex = null,
    int ContextLines = 0,
    string? IfNoneMatch = null,
    string? RequestId = null);

public sealed class DiffSourceResolver
{
    public ResponseContract Resolve(DiffSourceRequest request);
}
```

규칙은 다음과 같다.

- `Side=Base`이면 `Snapshot.SnapshotId == Baseline.BaseId`, `Side=Target`이면 `== Baseline.TargetId`여야 한다. 아니면 `DIFF_SNAPSHOT_INVALID`다. `SelectionKey`가 `Baseline`과 snapshot의 input fingerprint로 다시 계산한 값과 다르면 `STALE_DIFF_RESPONSE`다. 이 규칙으로 mode 전환 후 이전 결과를 쓰는 경우를 막는다.
- 원문은 **snapshot이 보존한 bytes**(`SymbolDiffSnapshot.Sources`)에서만 읽는다. 현재 workspace 파일은 열지 않는다. 읽기 전에 bytes digest와 location `contentHash`를 검증한다(`SymbolDiffService.Extract`/`Decode`와 같은 규칙). 불일치면 `SOURCE_STALE`, 누락이면 `SOURCE_FILE_MISSING`, Session snapshot이 없으면 `SESSION_BASE_MISSING`이다.
- 응답은 기존 `ResponseContract`다. `baseline`을 채우고 `freshness.returnedFiles=verified`(snapshot digest 검증), `freshness.workspace=not_applicable`로 둔다. `results[0]`은 `ResolveResult`에 `side`, `snapshotId`를 더한 `DiffResolveResult`다. `source`는 4.2의 slice 계약(contentHash·notModified 포함)을 그대로 쓴다.

DIFF-03 예시: `Side=Base`, `Part=declaration`, VCS 또는 Session 결과의 `Removed()` base symbol ID를 요청한다. 응답은 `vcs-base` 또는 `session-start`의 10~13행 span(LF 기준 70 B)이다.

### 4.6 evidence 예산 (`DiffEvidenceOptions`)

```csharp
public sealed record DiffEvidenceOptions(
    int ContextLines = 1,
    int MaxHunkBytesPerEntry = 8192,
    int MaxEvidenceBytesTotal = 65536,
    int MaxLinesForLineDiff = 20000);
```

`SymbolDiffRequest`에 `DiffEvidenceOptions? Evidence = null`(null이면 기본값)을 추가한다. entry 예산을 넘으면 hunk를 줄 경계에서 자르고 `truncated=true`로 둔다. 전체 예산을 넘은 뒤의 entry와 줄 수 상한을 넘는 symbol은 `fingerprint_only` + `truncated=true`로 낮춘다. 두 경우 모두 `evidenceTruncated=true`와 limitation을 남긴다. 수치는 source 기본 예산(65,536 B)에 맞춘 제안값이며 사용자 확인 대상이다(9절 U4).

## 5. 안전성 불변식 유지 논증

| 불변식 | 위험 지점 | 유지 방법 |
|---|---|---|
| stale 원문 오반환 0 | `notModified` 응답이 오래된 client 사본을 사실상 승인하는 효과 | 파일 digest 검증을 모두 통과한 뒤에 **현재 bytes로 계산한** slice hash와 비교한다. 일치하면 client 사본이 현재 bytes와 같으므로 stale이 아니다. 불일치나 stale이면 기존 오류·전체 반환 경로를 탄다. SHA-256 충돌은 고려하지 않는다 |
| | lazy resolve가 현재 파일을 읽는 경우 | `DiffSourceResolver`는 snapshot bytes만 읽고 digest를 검증한다. workspace 경로 인자가 없어 현재 파일을 열 수 없는 API 형태다 |
| | line hunk가 오래된 텍스트로 만들어지는 경우 | hunk는 기존과 같이 `Compare` 시점에 digest 검증을 거친 snapshot에서 만든다(`SymbolDiffService.cs:218-244`의 검증을 공유한다) |
| 조용한 partial 0 | added/deleted body 생략 | `textMode=header_only`와 `omitted*Lines`로 entry마다 생략을 명시한다. 전체 원문은 `baseContentHash`로 대조할 수 있다 |
| | 예산 잘림 | `evidence.truncated`, `DiffContract.evidenceTruncated`, `limitations`의 `diff-evidence-budget-exhausted`를 함께 요구한다(Validate로 강제) |
| | container entry 억제 | 억제는 자기 텍스트가 whitespace 정규화 후 같을 때만 한다. index되지 않은 텍스트는 자기 텍스트에 남고 순서 변경은 `fingerprint_only`로 보고한다 |
| coverage·freshness 표시 | notModified·lazy resolve 응답 | 두 응답 모두 기존 `ResponseContract`의 `status`·`freshness`·`coverage`를 그대로 채운다. Diff의 `coverage`·`limitations` 결합 규칙은 바꾸지 않는다 |
| DIFF-03 base source | 원문 선반환 제거 | `DiffSourceResolver(Side=Base)`가 선택한 baseline snapshot bytes에서 반환한다. snapshot ID·SelectionKey 결속으로 다른 baseline이나 현재 파일로 대체될 수 없다 |
| VCS/Session 의미 | container 억제·hunk 변경 | 두 mode에 같은 비교 함수를 쓴다. baseline 생성(`GitBaselineProvider`, `SessionBaselineProvider`)과 `BaselineContract` 규칙은 바꾸지 않는다. S-01(시작 전 dirty `Existing()`)은 계속 변경 목록에서 제외된다 |

### 기존 정답 표현 가능성 (TASK-025 Validation)

| 정답 | 새 계약에서의 표현 |
|---|---|
| N-01~N-13, R-01~R-07, D-01~D-04 | `cv_find`·`cv_impact` 계약이 바뀌지 않아 그대로 표현된다 |
| M-01·M-02 | `part=context`로 계속 반환된다. `declaration`에는 포함되지 않는다 |
| NAV-03 (`BuildLabel` source) | `part=declaration`으로 `LinkedHelper.cs:5-8` span을 반환한다 |
| V-01 | `body_changed` + line hunk에 `"base"` → `"dirty-before-session"`이 들어간다 |
| V-02·S-02 | `added` + `header_only`에 `AddedDuringSession()` 선언 줄이 들어간다 |
| V-03·S-03 | `deleted` + `header_only`에 `Removed()` 선언 줄이 들어간다 |
| S-01 | Session 목록에 나타나지 않는다. container 억제로 class-level 오탐 2건도 사라진다 |
| DIFF-03 | `DiffSourceResolver(Side=Base, Part=declaration)`로 선택한 baseline의 10~13행을 반환한다 |

## 6. 구현 변경 목록 (TASK-026 전달용)

### Core

| 파일 | 변경 |
|---|---|
| `src/CodeVirtualize.Core/Contracts/CommonContracts.cs` | `SourceSliceContract`에 `string ContentHash`, `bool NotModified`를 추가한다. `Validate`에 notModified 분기와 `ContractGuard.Sha256(ContentHash)`를 넣는다 |
| `src/CodeVirtualize.Core/Contracts/DiffContracts.cs` | `enum DiffEvidenceTextMode { LineHunks, HeaderOnly, FingerprintOnly }`를 추가한다. `DiffEvidenceContract`에 `TextMode`, `ContextLines`, `OmittedBaseLines`, `OmittedTargetLines`, `Truncated`를 추가하고 textMode별 `TextualHunk` null 규칙을 검증한다. `DiffContract`에 `EvidenceTruncated`를 추가하고 true이면 limitation 포함을 검증한다 |
| `src/CodeVirtualize.Core/Resolution/ResolutionContracts.cs` | `SourcePart`에 `Declaration`을 추가한다. `ResolveRequest`에 `string? IfNoneMatch = null`을 추가한다 |
| `src/CodeVirtualize.Core/Resolution/SourceResolver.cs` | `Range`에 `Declaration`(span 그대로)을 추가한다. `Budget`/`Slice`에서 content hash를 계산한다. 검증 후 `IfNoneMatch` 비교와 notModified slice 생성을 넣는다. `Range`·`Budget`·`Slice`를 `DiffSourceResolver`와 공유할 수 있게 `internal static`으로 분리한다 |
| `src/CodeVirtualize.Core/Diff/DiffModels.cs` | `SymbolDiffChange`에서 `BaseSource`·`TargetSource`·`BaseRemark`·`TargetRemark`를 제거한다. `SymbolDiffRequest`에 `DiffEvidenceOptions? Evidence`를 추가한다. `DiffSide`, `DiffSourceRequest`, `DiffEvidenceOptions`, `DiffResolveResult`를 추가한다 |
| `src/CodeVirtualize.Core/Diff/SymbolDiffService.cs` | `CreateView`에 container 자기 텍스트와 직계 member 순서 목록을 추가한다(`ContainerId` 사용). `AddMatchedChanges`는 container면 자기 텍스트로 비교하고 순서 변경을 판정한다. `Change`/`UnifiedHunk`를 line hunk·header_only·예산 로직으로 교체한다. `Decode`/`Extract`/`LineAt`을 아래 공유 reader로 옮긴다 |
| `src/CodeVirtualize.Core/Diff/LineHunkBuilder.cs` (신규) | Myers O(ND) line diff와 context 병합, 파일 줄 번호 기준 unified hunk 문자열 생성을 담당한다. 줄바꿈은 CRLF/LF/CR를 모두 한 줄로 취급하고 원문 줄 내용은 보존한다. 새 NuGet 의존성은 추가하지 않는다 |
| `src/CodeVirtualize.Core/Diff/DiffSnapshotReader.cs` (신규, internal) | snapshot bytes decode·digest 검증·span 추출을 `SymbolDiffService`와 `DiffSourceResolver`가 공유한다 |
| `src/CodeVirtualize.Core/Diff/DiffSourceResolver.cs` (신규) | 4.5 규칙을 구현한다 |

### CLI·MCP

| 파일 | 변경 |
|---|---|
| `src/CodeVirtualize.Mcp/ToolDefinitions.cs` | `cv_get.part` enum에 `declaration`을 추가하고 `ifNoneMatch`(sha256 pattern)를 추가한다 |
| `src/CodeVirtualize.Mcp/McpServer.cs` | `Get`에서 `ifNoneMatch`를 전달한다. part 오류 메시지를 갱신한다 |
| `src/CodeVirtualize.Cli/Commands/ResolutionCommands.cs` | `--if-none-match`와 part 오류 메시지를 추가한다. text 출력에 `NotModified true`와 `ContentHash` 줄을 추가하고, notModified이면 `Source` 블록을 생략한다 |
| `src/CodeVirtualize.Cli/Program.cs` | usage 97행의 part 목록과 `--if-none-match`를 갱신한다 |

`cv-diff` CLI와 `cv_diff` MCP 노출은 이 작업에 포함하지 않는다. 현재도 Core API뿐이며 gate 측정에 필요하지 않다(9절 U8).

### schema·문서

| 파일 | 변경 |
|---|---|
| `schemas/common.schema.json` | `sourceSlice`에 `contentHash`·`notModified`를 추가하고 notModified이면 `content: ""`, usage 0을 요구하는 `if/then`을 넣는다 |
| `schemas/diff.schema.json` | `evidence`에 4.3의 필드와 textMode별 `textualHunk` 조건을 넣는다. 최상위에 `evidenceTruncated`를 추가한다 |
| `docs/contracts/cli.md` | "source 반환 예산" 절에 `declaration`, `contentHash`, `ifNoneMatch`/`notModified`를 추가한다. 새 "Diff evidence와 lazy resolve" 절을 추가한다. "저장 레코드 의미"의 Diff 항목을 갱신한다 |
| `docs/prepare/architecture.md` | Interfaces 표의 `cv-resolve`(part·조건부) 행과 DiffEntry 행(evidence 필드)을 갱신한다 |

## 7. 테스트·검증 영향

| 테스트 파일 | 변경 |
|---|---|
| `tests/Core.Tests/Contracts/ContractTests.cs` | slice notModified 규칙, evidence textMode별 null 규칙, `evidenceTruncated`↔limitation 규칙, 알 수 없는 textMode 거부, round-trip |
| `tests/Core.Tests/Resolution/SourceResolverTests.cs` | `declaration` 범위를 검증한다. 일치하는 `ifNoneMatch` → notModified, 불일치 → 전체 반환, stale 파일 + 일치 hash → `SOURCE_STALE`(notModified 금지), 잘린 slice의 hash 의미 |
| `tests/Cli.Tests/ResolutionCliTests.cs` | `--part declaration`, `--if-none-match`, text 출력 |
| `tests/Integration.Tests/AgentAdapter/McpAgentAdapterTests.cs` | `tools/list`의 part enum과 `ifNoneMatch` |
| `tests/Integration.Tests/Diff/DiffTests.cs` | 67~71행의 `BaseSource`·전체 hunk 단언을 `DiffSourceResolver`·line hunk 단언으로 교체한다. 추가 사례: class-level 오탐 없음, 파일 줄 번호 hunk, added/deleted header_only와 omitted 줄 수, 예산 초과 시 명시적 잘림, 현재 파일이 달라도 base snapshot bytes 반환(DIFF-03), snapshot ID·SelectionKey 불일치 오류, member 순서 변경 보고, container 사이 주석 변경 보고, CRLF snapshot hunk |
| `tests/Integration.Tests/FailureInjection/FailureInjectionTests.cs` | 같은 길이 수정 후 `ifNoneMatch` 재조회가 stale을 숨기지 않는지(수정이 slice 안이면 전체 반환, digest 불일치면 오류) |

TASK-026 완료 기준은 plan과 같다: locked restore, Release build, Core/CSharp/CLI/Integration 테스트, fixture verifier 통과, stale·partial failure injection 회귀 0이다.

`benchmarks/runs/CvEvaluationRunner.cs`는 solution에 포함되지 않지만 `BaseSource`/`TargetSource`를 참조한다(`:609`, `:615`). 따라서 TASK-026 이후 runner 빌드가 깨진다. runner 수정과 새 계수 규칙 반영은 TASK-029 범위로 둔다(9절 U9).

## 8. 예상 효과 (모두 추정)

아래 수치는 fixture 원문으로 계산한 **추정**이다. 측정값이 아니며 small 수치에 맞춘 목표도 아니다. hunk bytes는 hunk 안 원문 줄 내용의 UTF-8 bytes로 셌고 `+`/`-`/공백 prefix, `@@`·`---`·`+++` header, 줄바꿈은 제외했다. 계수 규칙은 TASK-024가 확정한다.

### small corpus

| task | 현재 E | 권장안 E (추정) | B | B 대비 (추정) | 비고 |
|---|---:|---:|---:|---:|---|
| NAV (declaration + notModified) | 426 B | 42 B | 472 B | −91% | 복구 재조회 0 B |
| NAV (declaration, 조건부 미사용) | 426 B | 84 B | 472 B | −82% | 조건부 재조회 없이도 377.6 B 이하 |
| NAV (context 유지 + notModified) | 426 B | 426 B | 472 B | −9.75% | 주석 줄 수정이 slice 안이라 이득이 없다. part 선택이 핵심이다 |
| DIFF (D-B, context 1, DIFF-03 1회) | 1,272 B | 약 282 B | 74 B | +281% | 목록 212 B(VCS 141: V-01 70 + 헤더 41 + 30 / Session 71) + resolve 70 B |
| DIFF (D-B, context 0) | 1,272 B | 약 272 B | 74 B | +268% | V-01 hunk 60 B |

현재 계상 방식에서 small DIFF는 통과할 수 없다. B의 `git diff` 출력 원문 줄(402 B)을 대칭으로 세면 B는 약 476 B이고 통과선은 380.8 B가 되어, 권장안 추정치(272~282 B)가 그 안에 든다. 이 판단은 TASK-024의 계상 규칙에 달려 있다. 권장안의 효과는 −78% 수준의 과다 materialization 제거다.

### 일반 규모 (가정에 기반한 예시, 추정)

평균 35 B/줄, 1,200줄 class 안의 60줄 method에서 3줄을 교체하는 변경을 가정한다.

| 계약 | 반환 원문 | 대략 bytes |
|---|---|---:|
| 현재 | class before/after + method before/after = 2×1,200 + 2×60 줄 | 약 88 KB |
| D-A | method before/after = 120줄 | 약 4.2 KB |
| D-B | 삭제 3 + 추가 3 + 문맥 2 = 8줄 | 약 280 B |

60줄 method를 추가하면 현재 계약은 약 2.1 KB(+ class cascade)를 반환한다. D-B는 header 1줄(약 40 B)을 반환하고, 에이전트가 body를 요청할 때만 2.1 KB가 든다. 따라서 D-B의 반환량은 변경 줄 수에 비례하고 class·symbol 크기와 무관하다. medium/large corpus에서 B를 이길지는 B의 읽기 정책과 계수 규칙(10절 R1)에 달려 있어 여기서 판정하지 않는다.

## 9. 사용자 확인 필요 사항

| ID | 결정 | 권장 | 대안 |
|---|---|---|---|
| U1 | added/deleted evidence 기본값 | `header_only` + lazy resolve | 전체 원문 `line_hunks`. 에이전트 재조회가 줄지만 규모에 비례한다 |
| U2 | container type entry 억제(자기 텍스트 비교, 순서 변경은 fingerprint 보고) | 채택 | 현행 유지. class 전체 cascade와 오탐 2건이 남는다 |
| U3 | Core `SymbolDiffChange`의 원문 필드 제거(in-process API breaking) | 제거. 외부 소비자가 없는 prototype이다 | `[Obsolete]`로 남기고 null로 채움 |
| U4 | diff hunk 기본 `contextLines`와 예산 수치 | context 1(B의 `git diff --unified=1`과 같음), entry 8 KiB, 전체 64 KiB, line diff 상한 20,000줄 | context 3(git 기본) 등 |
| U5 | 조건부 재조회(`ifNoneMatch`/`notModified`) 도입 | 도입 | 제외. NAV small 추정은 84 B로 여전히 목표 이내다 |
| U6 | 기본 part | `header` 유지. `declaration`은 명시 요청 | 기본을 `declaration`으로 변경. type symbol에서 대량 반환 위험이 있다 |
| U7 | E 호출 패턴을 protocol rev2(ADR 005)에 사전 고정 | TASK-024가 새 corpus 결과를 보기 전에 "member 조회는 `part=declaration`, 재조회는 `ifNoneMatch` 사용, DIFF-03은 `DiffSourceResolver(Base, declaration)` 1회"를 고정한다 | runner가 현행 `context`/2를 유지하면 계약 개선 효과가 측정되지 않는다 |
| U8 | `cv-diff` CLI / `cv_diff` MCP 노출 | TASK-026 범위에서 제외(Core API만) | 포함. 범위와 리뷰 부담이 커진다 |
| U9 | runner(`CvEvaluationRunner.cs`) 컴파일 수정 시점 | TASK-029에서 새 계수 규칙과 함께 수정 | TASK-026이 측정 로직을 바꾸지 않는 최소 컴파일 수정만 함께 한다 |

## 10. 미해결·위험

| ID | 내용 | 영향 | 처리 |
|---|---|---|---|
| R1 | **B DIFF의 git diff 출력이 source bytes에서 빠져 있다**(`CvEvaluationRunner.cs:246-248`). E의 hunk를 세고 B의 diff 출력은 세지 않으면 비대칭이다. 현재도 E의 `TextualHunk`는 세지 않는다 | DIFF 비교가 한쪽으로 기운다 | TASK-024가 양쪽에 같은 규칙(둘 다 세거나 둘 다 제외하되 원문 줄은 셈)을 정한다 |
| R2 | E NAV만 stale 복구 재조회를 수행하고 B에는 대응 재조회가 없다 | NAV 비교가 E에 불리하다 | TASK-024가 판단한다. 이 설계는 R2를 유지해도 목표를 넘지 않게 했다 |
| R3 | 줄바꿈 비대칭: 기록값은 LF 기준이다. 현재 checkout(autocrlf)에서는 E가 NAV 440 B, DIFF 1,334 B로 늘지만 B는 `ReadAllLines`+`\n` join이라 그대로다 | 재측정 수치가 환경에 따라 달라진다 | TASK-024/029가 fixture `.gitattributes`(`eol=lf`) 또는 계수 정규화를 정한다 |
| R4 | E NAV 품질 판정이 `cv_get` content를 보지 않는다(`CvEvaluationRunner.cs:490`). NAV-03은 `BuildLabel` source를 요구하지만 E는 `Load`만 조회한다 | part를 줄여도 품질 판정이 바뀌지 않는 구조라 과소 조회를 잡지 못한다 | TASK-024가 E가 반환해야 할 원문을 task별로 명시한다 |
| R5 | `declaration`은 type symbol에서 member 전체를 포함한다 | 큰 type 조회는 여전히 크다(예산 잘림은 명시된다) | 이번 범위에서는 기본 part를 `header`로 유지한다. type outline 계약은 필요성이 확인되면 별도 과제로 한다 |
| R6 | container 억제가 index되지 않은 member 종류의 변경을 자기 텍스트 hunk로 보고한다 | entry는 남으므로 누락은 없지만 kind가 덜 구체적일 수 있다 | 테스트로 누락이 없음을 확인한다 |
| R7 | Myers 구현 오류가 잘못된 hunk를 만들 수 있다 | evidence 정확성 | 무작위 텍스트 쌍에 hunk를 적용해 target을 복원하는 property 테스트를 추가한다(TASK-026), TASK-028에서 집중 리뷰 |
| R8 | partial type의 여러 declaration hunk | declaration 수·경로가 바뀌면 짝짓기가 모호하다 | path+순서로 짝짓고, 짝이 없는 declaration은 header_only 추가·삭제로 표시한다. 테스트에 포함한다 |
| R9 | 권장 효과는 모두 small 추정이다 | medium/large에서 B 대비 20%를 보장하지 않는다 | TASK-029 재측정으로만 판정한다. TASK-026 담당에게 TASK-027 정답을 주지 않는다는 plan 규칙을 유지한다 |
