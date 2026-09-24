# Pilot 비교 프로토콜

## 상태와 목적

이 문서는 실제 세션 로그를 읽거나 모델 실행을 시작하기 전에 고정할 측정 계약이다. 현재 단계에서는 실험 결과, 승인된 실행량, 충분한 표본 수를 주장하지 않는다. `FUP-001`이 채워질 때까지 실제 로그 접근과 유료 실행은 금지된다. 원안의 잘린 `benchmark task/g` 이후 내용을 복원한 문서도 아니다.

pilot의 목적은 현재 탐색 비용의 분포와 기존 도구가 주는 효과를 관찰하고 본 실험의 표본 수·품질 허용 저하·최소 효율 차이 후보를 만드는 것이다. pilot 결과만으로 제품 가치를 확정하지 않는다.

## 사전 등록과 실행 gate

실행자는 첫 이벤트를 기록하기 전에 비식별 run manifest에 다음 값을 고정한다.

- 허용된 로그의 절대 경로, 기간, 읽어도 되는 필드와 승인자. 이 값은 집계 산출물에는 넣지 않는다.
- repository의 공개 별칭, commit, license, corpus 규모 구간과 task ID. 로컬 경로와 remote URL은 기록하지 않는다.
- 모델·버전, reasoning 설정, context 한도, tokenizer, 권한, 네트워크, 도구 버전과 cache 정책.
- 조건, task, 반복 수, 순서 무작위화 seed와 paired block ID.
- 최대 금액·wall time·token·실행 수, 중지 규칙, 결과 보관 기간과 공유 범위.
- 독립 정답과 품질 판정자, 성공 기준. 실행 결과를 본 뒤 기준을 바꾸면 새 protocol revision으로 다시 시작한다.

승인이 없거나 필수 값이 하나라도 비어 있으면 `not_started`로 남기고 실행하지 않는다. 예산 중지는 이미 시도한 run을 삭제하지 않으며, 시작하지 못한 셀과 중지 사유를 함께 보고한다.

## 비교 조건

모든 조건은 같은 repository snapshot, task 입력, 모델·설정, 권한, 시간·출력 한도를 사용한다. 조건 순서는 paired block 안에서 고정 seed로 무작위화한다. 대화 이력과 도구 cache는 조건 사이에 공유하지 않는다. OS page cache처럼 제거하기 어려운 상태는 manifest에 기록한다.

| ID | 조건 | 허용 도구와 차이 |
|---|---|---|
| A | 기본 agentic search | 기본 `Read`/`Grep`/`Glob`과 task 수행에 공통으로 필요한 도구. 별도 범위 읽기 지시 없음 |
| B | 범위 읽기 지시 | A와 같은 도구에 “후보를 먼저 좁히고 필요한 범위만 읽는다”는 고정 지시문만 추가 |
| C | LSP | A의 fallback 도구와 고정 버전 LSP의 definition/reference/symbol 계열 도구 추가 |
| D | Serena | A의 fallback 도구와 고정 버전 Serena의 심볼 탐색 도구 추가 |
| E | Code-Virtualize | 제품 구현 이후 A의 fallback 도구와 고정 버전 CV 도구 추가. cold/warm/long 상태를 별도 층화 |

A–D가 pilot 대상이며 E는 제품 구현 후 본 실험 대상이다. C/D/E에서 전용 도구를 한 번도 쓰지 않은 run도 제외하지 않고 `primaryToolUsed=false`로 집계한다. 전용 도구가 초기화되지 않거나 미지원이면 A로 몰래 대체하지 않고 `tool_failed` 또는 `infra_failed`로 끝낸다. fallback 사용량은 그대로 비용에 포함한다.

task·조건 조합은 동일 task instance를 공유하는 paired block으로 묶는다. 한 조건의 실패 때문에 같은 block의 다른 조건을 버리지 않는다. paired 분석에는 필요한 조건이 모두 관찰된 complete block만 사용하고, 전체 성공률과 비용 보고에는 모든 attempted run을 사용한다.

## 이벤트와 run 상태

원시 입력은 한 줄에 한 JSON object인 로컬 JSONL을 가정한다. 수집기는 알 수 없는 이벤트를 무시하지 말고 `unknownEventCount`로 남긴다. 공통 키는 `schemaVersion`, `eventId`, `timestamp`, `runId`, `sessionIdHash`, `condition`, `event`다. 식별자는 이 실험 전용 salt로 만든 비가역 ID여야 한다.

필요한 이벤트는 다음과 같다.

| `event` | 필수 payload | 용도 |
|---|---|---|
| `run_started` | `taskId`, `pairId`, `repeatIndex`, 모델·도구·cache 설정의 비식별 ID | attempted 분모와 조건 통제 |
| `tool_result` | `toolCallId`, `toolFamily`, `status`, `contentTokens`, `sourceBytes`, `sourceLines`, 선택적 `fileId`, `range`, `fileBytes`, `fileLines`, `commentTokens` | 호출·읽기·주석·전체 읽기 측정 |
| `context_inclusion` | `inclusionId`, `requestId`, `contentItemId`, `originToolCallId`, `toolFamily`, `contentTokens` | tool 결과가 모델 입력에 포함된 실제 횟수와 token 측정 |
| `model_usage` | `requestId`, `usageRecordId`, 배타적 token bucket, 선택적 provider cost | token·비용 측정 |
| `setup_usage` | `chargeId`, `cacheScopeId`, token/time/cost, setup 종류 | index/build/cache 준비 비용 |
| `quality_result` | 독립 판정 ID, `passed`, score와 결함 통계 | 품질 측정 |
| `run_finished` | terminal status, wall time, `primaryToolUsed`, 오류 범주 | 성공률·실패 분모 |

동일 `eventId`는 byte-equivalent 재전송만 허용하며 한 번만 센다. 같은 ID에 다른 payload가 오면 해당 run을 `invalid`로 격리한다. `model_usage`는 `usageRecordId`, setup은 `chargeId`, 도구 결과는 `toolCallId`도 run 안에서 유일해야 한다. context inclusion은 `(requestId, contentItemId)`가 유일하다. 이 보조 키가 중복된 이벤트는 token·비용·호출 수를 다시 더하지 않는다.

terminal status는 `succeeded`, `quality_failed`, `tool_failed`, `timeout`, `budget_stopped`, `infra_failed`, `cancelled`, `invalid` 중 하나다. `run_started`가 있으면 terminal 이벤트가 없어도 attempted run이며 집계 시 `invalid`로 닫는다. 예약만 했지만 `run_started`가 없는 셀은 `not_started`이며 attempted 분모에서 제외하고 별도 개수로 보고한다.

## 분모와 지표

모든 비율은 값과 함께 numerator, denominator를 저장한다. denominator가 0이거나 필수 입력이 빠지면 비율은 `null`이다. 누락을 0으로 대체하지 않는다.

### 실행·품질

- `attemptedRunCount`: `run_started`가 있는 모든 run. 실패·timeout·budget 중지를 포함한다.
- `successfulRunCount`: terminal status가 `succeeded`인 run.
- `successRate = successfulRunCount / attemptedRunCount`.
- `qualityEvaluatedRunCount`: 독립 `quality_result`가 있는 run. quality pass rate의 분모다. 품질 판정이 없는 실패를 품질 0점으로 만들지 않되, 그 개수와 전체 성공률을 함께 공개한다.
- 비용·wall time은 attempted run 전체 합계와 status별 분포를 기본으로 한다. 성공 run만의 통계는 `successful-only`라고 명시한 보조 지표다.

### 탐색과 읽기

도구군은 `read`, `grep`, `glob`, `lsp`, `serena`, `cv`, `other`로 정규화한다. 성공한 `tool_result`만 bytes/lines/token 비율에 쓰며 실패 호출 수는 별도 집계한다.

- `readGrepTokenShare`: `context_inclusion` 중 성공 `read`·`grep`에서 온 token 합 / 성공한 모든 tool 결과에서 온 입력 포함 token 합. 같은 결과가 서로 다른 모델 요청에 다시 포함되면 실제 context 비용을 반영해 각 inclusion을 세되, 동일 `(requestId, contentItemId)`의 재전송은 한 번만 센다.
- `fullReadRate`: `read` 성공 호출 중 `range`가 파일 전체를 덮는 호출 수 / `fileBytes` 또는 `fileLines`로 전체 여부를 판정할 수 있는 `read` 성공 호출 수. 범위를 모르는 호출은 분모에서 빼고 `unknownFullReadCount`로 보고한다.
- `rereadRate`: 판정 가능한 `read` 성공 호출 중 같은 run에서 앞선 성공 read와 동일 `fileId`의 byte 구간이 1 byte 이상 겹치는 호출 수 / `fileId`와 구간이 모두 있는 `read` 성공 호출 수. 첫 read는 분모에 포함되고 numerator에는 포함되지 않는다. path 문자열 대신 `fileId`를 쓴다.
- `commentTokenShare`: `commentTokens` 판정이 있는 성공 source tool 결과의 token 중 주석 token / 주석 token 수를 판정할 수 있는 source tool 결과 token. 혼합 결과는 tokenizer와 parser로 분리하고, 분리할 수 없으면 분모에서 제외한다.
- `primaryToolUseRate`: 해당 조건의 attempted run 중 LSP/Serena/CV 성공 호출이 하나 이상인 run / attempted run. A/B에는 적용하지 않아 `null`이다.

`sourceBytes`와 `sourceLines`는 token의 대체물이 아니다. token 비율은 manifest에 고정한 같은 tokenizer로 tool 결과가 모델 입력에 들어간 시점에 계산한다. prompt·source 본문 자체는 집계 파일에 보관하지 않는다.

## Token과 cache-aware 비용

`model_usage`의 입력 token bucket은 서로 배타적인 `uncachedInputTokens`, `cacheReadInputTokens`, `cacheWriteInputTokens`를 사용한다. provider가 `totalInputTokens`와 겹치는 세부 값을 주면 다음과 같이 한 번만 정규화한다.

```text
uncachedInputTokens = totalInputTokens - cacheReadInputTokens - cacheWriteInputTokens
```

결과가 음수거나 provider 의미가 불명확하면 token 합계를 만들지 않고 run을 data-quality 오류로 표시한다. `outputTokens`는 별도다. usage snapshot 누적값은 연속 snapshot의 non-negative delta만 더하며, 이미 delta인 record와 섞지 않는다. `(runId, usageRecordId)` 중복은 한 번만 센다.

가격표를 manifest에 통화·단위·유효 시각과 함께 동결한다. provider 청구액이 있으면 이를 우선 사용하고, 없을 때만 다음 식으로 추정한다.

```text
modelCost = uncachedInputTokens * uncachedInputUnitPrice
          + cacheReadInputTokens * cacheReadInputUnitPrice
          + cacheWriteInputTokens * cacheWriteInputUnitPrice
          + outputTokens * outputUnitPrice
runCost = modelCost + meteredToolCost + allocatedSetupCost
```

단가는 같은 token 단위로 정규화한다. `setup_usage`의 `chargeId`는 한 번만 더한다. 공유 build/index 비용은 실제 `cacheScopeId`의 소비 run에 미리 정한 방식(`first_run`, `equal_within_scope`, `separate`)으로 배분하고, 조건 합계에는 정확히 한 번 포함한다. E의 cold/warm/long 보고에서는 build, update, validate, fallback 비용을 숨기지 않는다. 가격이나 usage가 누락되면 `cacheAwareCost`는 `null`, `costCompleteness=false`이며 byte/line 수로 비용을 추정하지 않는다.

## Pilot 범위와 예산 후보

다음은 `FUP-001`에서 사용자가 선택할 탐색 후보이며 승인된 실행량이 아니다.

| 후보 | corpus/task/조건/반복 | 최대 attempted run | 용도 |
|---|---|---:|---|
| 최소 smoke | 1개 규모 × 2 task × A–D × 1회 | 8 | 수집·분모·비용 계측 오류 발견 |
| 축소 pilot | 2개 규모 × 3 task × A–D × 2회 | 48 | 분산과 도구 사용률의 초기 추정 |
| 확장 pilot | 3개 규모 × 4 task × A–D × 3회 | 144 | 원안 검토에서 제시한 후보; 충분한 통계 표본을 보장하지 않음 |

각 후보에는 금액, wall time, 입력·출력 token 상한을 별도로 채워야 한다. 먼저 smoke를 통과하고, 집계 누락률과 중복률이 허용된 data-quality 기준 이내일 때만 다음 후보로 간다. 금액 또는 시간 상한의 80%에 도달하면 새 paired block을 시작하지 않고, 100%에서는 현재 호출을 취소할 수 있는 범위에서 중지한다. 중지된 run과 미시작 셀을 결과에 남긴다.

## 해석과 보고

조건별 결과는 `result.schema.json`에 맞는 집계 JSON과 사람이 읽는 보고서를 함께 만든다. 평균만 제시하지 않고 run 수, 실패 상태, median/p95 또는 작은 표본에서는 원자료 범위, complete pair 수, missingness를 공개한다. 공개/합성 corpus의 결과를 사내 코드나 UE5/Perforce 성능으로 일반화하지 않는다. 관찰된 차이가 작거나 표본·품질·비용 정보가 불완전하면 `inconclusive`로 기록한다.

---

# Revision 2 — medium/large 재측정 프로토콜 (TASK-024)

## R2-0. 상태와 적용 범위

- 상태: **Proposed — 2026-09-23.** [ADR 005](../docs/decisions/005-protocol-revision.md)가 G2에서 Accepted가 되는 시점에 동결된다. 동결 전에는 새 corpus에서 조건 A~E를 실행하지 않는다.
- 식별자: 결과·manifest의 `protocolRevision`은 `rev2-adr005`다.
- 이 절을 쓰는 동안 새 corpus에서 CV, runner, 조건 A~E를 실행하지 않았다. GitHub API로 commit·license·규모 메타데이터만 조회했다.
- 앞의 rev1 절은 pilot과 TASK-016(`task-016-v1`)의 기록으로 보존한다. rev1의 이벤트, terminal status, 분모, token·비용 정규화 규칙은 rev2에도 그대로 적용한다. 두 절이 다르면 rev2 실험에는 이 절을 따른다.
- FUP-002 동결 수치는 바꾸지 않는다: 품질 저하 0, stale·조용한 partial Critical 0, task·조건별 최소 3회, seed `20260920`, stronger available baseline 대비 paired 중앙값 source bytes 또는 실제 input token 20% 이상 감소, 외부 유료비용 0. small fixture 기준 latency(60/2/5초), memory(1 GiB), 전체 30분도 small corpus에 그대로 적용한다. 이 절은 수치가 정하지 않은 것(corpus, task, 조건 행동, 계상, 결합)과 small 밖 규모의 예산(R2-7, 새 기준)만 정한다.
- ADR 002의 No-Go는 그대로다. rev2 측정은 새 실험이며 TASK-016 결과를 덮어쓰지 않는다.

## R2-1. Corpus

### 등급 정의

등급은 아래 다섯 지표로 정의한다. **등급 배정은 앞의 세 지표(파일·bytes·project)가 모두 한 구간에 들어갈 때 정해진다.** 심볼·참조 수는 TASK-027이 독립 계수 도구로 측정해 기록한다. 기대 구간을 벗어나면 편차로 보고하되 등급과 corpus는 바꾸지 않는다. 결과 전에 값을 모르는 지표로 corpus를 교체하는 일을 막기 위해서다.

| 지표 | small | medium | large | 측정 |
|---|---|---|---|---|
| `.cs` 파일 수 | ≤ 100 | 150 ~ 2,500 | ≥ 8,000 | pinned commit tree, submodule 제외 |
| C# bytes | ≤ 1 MB | 0.9 ~ 16 MB | ≥ 60 MB | 위 파일의 blob 크기 합 |
| `.csproj` 수 | ≤ 5 | 5 ~ 100 | ≥ 300 | pinned commit tree |
| 선언 심볼 수 (기대 구간) | < 2,000 | 2,000 ~ 80,000 미만 | ≥ 80,000 | type, method, constructor, property, indexer, event, field(변수별), enum member, delegate, operator 선언 수. local function 제외 |
| 이름 참조 수 (기대 구간) | < 20,000 | 20,000 ~ 3,000,000 미만 | ≥ 3,000,000 | 선언 이름 위치를 뺀 `IdentifierName`·`GenericName` syntax node 수 |

- 심볼·참조 계수 도구는 Roslyn syntax API만 쓰는 독립 스크립트다. `CodeVirtualize.*` 어셈블리를 참조하거나 CV 출력을 읽지 않는다.
- medium과 large 사이에는 파일 수 3.2배, bytes 3.75배의 공백 구간이 있다. 공백 구간에 드는 corpus는 이번 실험에 쓰지 않는다.

### 라이선스 수용 기준

1. **주 corpus**: OSI 승인 permissive 라이선스(MIT, BSD-2/3-Clause, Apache-2.0)만 쓴다.
2. **예비 corpus**: OSI 승인 weak copyleft(LGPL-2.1/3.0)는 예비 순위로만 쓴다. 이번 실험은 수정하지 않은 원문을 로컬에서 읽고 색인할 뿐 배포하지 않으므로 LGPL 의무가 생기지 않는다. 그래도 저장소 공개 원칙을 단순하게 유지하려고 주 corpus에서 뺀다.
3. **제외**: 비OSI·비표준 라이선스(Six Labors Split License)와 reciprocal 라이선스(RPL-1.5)는 쓰지 않는다.
4. **원문 비복사**: 어떤 라이선스든 corpus 원문을 이 저장소에 복사하지 않는다. 정답 파일은 경로, 줄 범위, 식별자, 정규화 텍스트의 SHA-256만 담는다. 사람이 읽도록 인용하는 원문은 항목당 한 줄 이하로 제한한다. 판정기는 필요한 원문을 실행 시 pinned blob에서 다시 만든다.
5. clone 뒤 pinned commit의 `LICENSE`를 다시 확인한다. TASK-023 조회값과 다르면 그 corpus는 대체 규칙을 따른다.

### 선정 결과

선정 기준은 결과와 무관한 속성만 쓴다. 우선순위는 (1) 라이선스 수용 기준, (2) 등급 구간 적합, (3) 커밋된 generator 산출물이 적을 것, (4) 최근 first-parent 이력에서 DIFF task를 만들 수 있을 것, (5) 로컬 부담(저장소 크기)이 작을 것, (6) 같은 등급 안에서 규모가 서로 다를 것이다. 학습 노출은 선정 기준에 넣지 않는다(아래 "학습 노출" 참고).

| alias | 저장소 | pinned commit | license | 등급 | `.cs` / C# bytes / `.csproj` | 선정 이유 |
|---|---|---|---|---|---|---|
| `small-synthetic` | 이 저장소 `tests/fixtures/csharp/` | 측정 시점 HEAD와 fixture digest | 저장소 라이선스 | small | 기존 fixture | 회귀 기준. 아래 "small corpus" 참고 |
| `medium-litedb` | litedb-org/LiteDB (`dev`) | `0fd277aaed127b9dec99524277fb4173a2351167` | MIT | medium | 1,087 / 5,186,699 / 28 | generator·T4 없음, medium 하단~중간 규모, 최근 이력이 서비스 코드 수정 중심 |
| `medium-quartznet` | quartznet/quartznet (`main`) | `1e039471fc457f6efa1d3bf1224324b82236b759` | Apache-2.0 | medium | 1,466 / 14,488,437 / 32 | medium 상단 규모, 다중 project 모노레포, 커밋된 generated 파일 2개로 적음 |
| `large-aspnetcore` | dotnet/aspnetcore (`main`) | `7cc41c501115f365c0b7f1b50ebaa134a328f0e5` | MIT | large | 10,705 / 70,807,910 / 628 | large 후보 중 generator 의존과 저장소 크기(387 MB)가 가장 작다 |

- 수치 출처는 [corpus-candidates.md](corpus-candidates.md)의 2026-09-23 GitHub API 조회다. 2026-09-23 TASK-024에서 네 저장소의 pinned commit 존재, 기본 브랜치, 저장소 license SPDX, 비보관 상태를 다시 조회해 일치를 확인했다.
- 선정하지 않은 후보와 이유: Polly(BSD-3)는 LiteDB와 규모가 겹쳐 예비 1순위로 둔다. Hangfire는 LGPL이라 예비다. Serilog는 medium 하한(0.92 MB)에 붙어 있어 예비 하위다. Dapper는 small~medium 경계라 뺀다. AutoMapper(RPL)와 ImageSharp(Split License)는 라이선스 기준으로 제외한다. IdentityServer4는 보관 저장소로 2021년 이후 이력이 없어 DIFF task를 만들 수 없다. runtime·roslyn은 generator 의존과 크기 때문에 large 예비다.
- large는 한 개만 쓴다. large 두 개는 wall time 예산(R2-7)을 넘길 위험이 크고, large 후보 셋이 모두 dotnet 조직의 같은 코드 관례를 공유해 두 번째 corpus가 주는 독립성이 작다.

### 대체 규칙

선정 corpus는 **조건 A~E를 한 번도 실행하지 않은 상태에서 판단할 수 있는 사유**가 있을 때만 대체한다. 사유는 license 재확인 불일치, 취득 실패 또는 크기 한도 초과, 등급의 세 주 지표가 구간 밖, R2-2 규칙으로 NAV 대상 6개나 DIFF 쌍 6개를 만들 수 없음이다. CV build 실패, 시간 초과, 품질 실패는 결과이므로 대체 사유가 아니다. 그 셀은 실패로 기록한다.

대체 순서는 medium이 Polly(`9a81fdc7c1a89d6c45bba2fb7b062b8eeae39eee`) → Hangfire(`1d4778c23410b365b5f9ce35458202efe0b0f3ea`, LGPL 예비) → Serilog(`bebc7719004f76187ae72e64ce138ec2540f2070`), large가 runtime(`e4a207e6edf48b5a8611a0531352be025f86ba38`) → roslyn(`0c4cb1b5d16a205b590ccad639054e38eb13702f`)이다. 대체하면 사유와 시각을 manifest에 기록한다.

### workspace 범위와 취득

- workspace는 pinned commit의 저장소 전체다. submodule은 초기화하지 않는다. 테스트, 샘플, 커밋된 generated 파일을 모두 포함한다. corpus별 경로 제외 규칙을 두지 않는다. 제외 규칙이 CV나 B에 맞춘 튜닝 수단이 되는 것을 막기 위해서다.
- checkout은 `core.autocrlf=false`로 commit의 bytes를 그대로 둔다.
- DIFF에 필요한 이력과 blob은 측정 시작 전에 모두 로컬로 가져온다. 측정 중에는 네트워크를 쓰지 않는다.
- corpus는 저장소 밖 임시 경로에 둔다. 공개 corpus도 **untrusted 입력**이다. CV는 `--trust-workspace` 없이 syntax-only로 build하고 MSBuild를 평가하지 않는다.

### 학습 노출

조건 A~E는 모두 모델 없이 결정적 스크립트로 실행하므로 학습 노출은 rev2의 source-byte 측정값에 영향을 주지 않는다. 그래서 선정 기준에서 뺐다. 다만 후속 실제 모델 token 측정(FUP-001)에서는 영향이 있으므로 corpus별 노출 수준(TASK-023 평가)과 DIFF 커밋의 commit 날짜를 manifest에 기록한다. DIFF 커밋은 R2-2 규칙상 pinned commit 직전의 최근 이력에서 나온다.

## R2-2. Task

### 공통

- task card의 입력은 모든 조건에 같다. NAV는 대상 심볼의 정규화 이름(`Namespace.Type.Member`)과 소스에 적힌 매개변수 타입 목록, DIFF는 base·session 시작·target commit ID와 resolve 대상 심볼의 정규화 이름이다. 파일 경로와 줄 번호는 입력으로 주지 않는다. 조건이 찾아야 한다.
- task는 여러 단계를 묶은 composite다. small의 NAV·DIFF task와 같은 구성이다. 단계 비중을 따로 고르는 과정이 결과를 움직이지 않도록 모든 task가 모든 단계를 한 번씩 포함한다.

### NAV composite (새 corpus)

| 단계 | 내용 | 정답 항목 |
|---|---|---|
| N1 DECL | 대상 simple name의 선언을 대상 containing type 안에서 모두 찾는다(overload, partial 부분, explicit interface 구현 포함) | 선언 위치 `(path, line)` 목록 |
| N2 SRC | 대상 overload 하나의 선언 원문을 얻는다 | 선언 원문의 줄 범위와 정규화 텍스트 hash |
| N3 REF | 대상 overload의 정적 참조를 corpus 전체에서 찾는다 | 정적 참조 위치 목록. 동적·문자열 후보는 별도 목록 |
| N4 RECOVERY | 편집 두 번 뒤 현재 원문을 다시 얻는다 | 편집 1: 대상 파일 마지막 줄 뒤에 `// cv-bench-edit-1` 한 줄 추가(선언 span 밖). 편집 2: 선언의 첫 `{`가 있는 줄 바로 다음에, `{`가 없는 선언이면 선언 마지막 줄 바로 앞에 `// cv-bench-edit-2` 한 줄 삽입(span 안). 각 편집 뒤 현재 선언 원문이 정답이다 |

편집 1은 대상 선언의 줄 번호를 바꾸지 않는다. 두 편집을 모두 두는 이유는 R2-4의 R2 처리에 적었다. 각 task가 끝나면 편집을 되돌리고 E는 update로 원상태를 다시 게시한다.

### NAV 대상 표본 규칙 (TASK-027이 적용)

1. 독립 syntax 목록에서 **일반 method 선언**(constructor, operator, accessor, local function 제외)을 모은다. generated 경로 패턴(`*.g.cs`, `*.Designer.cs`, `*.generated.cs`, `/Generated/`, `/obj/`)의 파일은 제외한다.
2. simple name이 corpus `.cs` 파일 전체에서 단어 단위로 **2~40줄**에 나타나는 method만 남긴다(`rg -c -w`로 센다. 이 값은 표본 틀에만 쓰고 정답에는 쓰지 않는다).
3. `(path ordinal, line)`으로 정렬하고 seed `20260920`의 `System.Random` Fisher–Yates로 섞은 뒤 앞에서부터 뽑는다. 정답 작성 결과 정적 참조가 0건인 대상은 건너뛰고 다음을 뽑는다. corpus마다 6개다.
4. 2~40줄 제한은 사람이 참조 정답을 모두 직접 판정할 수 있게 하려는 것이다. 이 제한은 B의 grep 결과를 작게 만들어 **CV에 불리한 쪽**이다. 흔한 이름(`Dispose`, `Execute` 등)에서의 결과는 이번 실험이 말하지 않는다.

### DIFF composite (새 corpus)

| 단계 | 내용 |
|---|---|
| D1 VCS | HEAD = `p`, 작업 트리 = `tree(c')`. HEAD 대비 변경을 symbol 단위로 보고한다 |
| D2 Session | 세션 시작 시 작업 트리 = `tree(c)`(HEAD `p` 대비 dirty), 현재 = `tree(c')`. 세션 시작 이후 변경만 보고하고 시작 전 dirty 변경(c의 변경)은 빼야 한다 |
| D3 RESOLVE | VCS base `p`에서 지정 심볼의 선언 원문을 얻는다. 현재 파일로 대체하면 실패다 |

이 구성은 small DIFF의 V-01(세션 전 dirty 변경은 VCS에만 보임)과 S-01(Session에서 제외)을 실제 커밋으로 재현한다. resolve 지정 심볼은 D1 정답에서 `(path ordinal, line)` 순으로 첫 번째 **삭제된 member**이고, 없으면 첫 번째 **body 변경 member**다.

### DIFF 커밋 선정 규칙 (TASK-027이 적용)

1. pinned commit에서 `git rev-list --first-parent`로 최대 500개 커밋을 거슬러 간다. 각 커밋은 first parent와 비교한다. merge commit도 first parent 기준으로 본다.
2. **적격 커밋**: `.cs` 변경 파일이 1~10개(generated 경로 패턴 제외), `.cs` 추가+삭제 줄 수(`--numstat`) 2~400, `git diff -w` 결과가 비어 있지 않다.
3. 최신부터 적격 커밋을 차례로 보며 (older `c`, newer `c'`) 쌍을 만든다. `c'`는 아직 쓰지 않은 가장 최신 적격 커밋, `c`는 그다음 오래된 적격 커밋이다. `tree(c) → tree(c')`의 `.cs` 변경이 20파일·800줄을 넘으면 그 `c'`를 버리고 다음으로 넘어간다. 한 커밋은 한 쌍에만 쓴다. `p`는 `c`의 first parent다.
4. corpus마다 최신 6쌍을 쓴다. 500개 안에서 6쌍이 안 되면 대체 규칙을 따른다.
5. 커밋 메시지나 변경 내용을 보고 쌍을 고르거나 빼지 않는다.

### small corpus

- small은 기존 task card와 정답([navigation](../tests/fixtures/csharp/navigation/expected.md), [diff](../tests/fixtures/csharp/diff/expected.md))을 그대로 쓴다. NAV 1개(NAV-01~04), DIFF 1개(DIFF-01~03)다.
- 조건은 rev2 단계 알고리즘을 small 대상에 적용한다. NAV: N1은 `Load`·`Catalog`·`BuildLabel`, N2는 N-03 `Catalog.Load(string)`와 N-09 `LinkedHelper.BuildLabel()`, N3는 `Worker`·`IWorker`·`Worker.Run` 영향 후보(depth 1, 동적 후보 포함), N4는 N-03 대상으로 편집 1을 rev1의 같은 길이 주석 편집(`Loads one value.` → `Loads one item!!`)으로, 편집 2를 위 규칙으로 한다. DIFF: D1 `vcs-base → vcs-target`, D2 `session-start → session-current`, D3 `vcs-base`의 `Removed()`.
- small은 **오염된 회귀 기준**이다. TASK-025 계약이 small 수치를 보고 설계됐기 때문이다. small 통과만으로는 Go를 만들 수 없고, small 실패는 Go를 막는다(R2-8).

### tuning / held-out

- 새 corpus마다 표본 순서의 첫 NAV 대상 1개와 최신 DIFF 쌍 1개가 **tuning**, 나머지 NAV 5개·DIFF 5개가 **held-out**이다. small은 tuning에 준한다(위).
- tuning 실행 결과는 runner 결함(충돌, 이 절과 다르게 구현된 계상·판정·조건 행동)을 고치는 데만 쓴다. 조건의 행동 파라미터(R2-3 표)는 tuning 결과로도 바꾸지 않는다. 고친 결함은 manifest의 `runnerChangeLog`에 남긴다.
- held-out은 runner와 CV 버전을 고정한 뒤 한 번에 실행한다. held-out 결과를 본 뒤 runner, CV, 정답, 이 절을 바꾸면 그 held-out 집합은 소진된다. 새 revision(rev3)과 새 표본(표본 순서의 다음 대상·다음 DIFF 쌍)으로 다시 시작한다.
- efficiency 판정(R2-8)은 held-out만 쓴다. 품질·안전 판정은 tuning을 포함한 모든 task에 적용한다.

### 독립 정답 규칙 (TASK-027)

- 정답은 사람이 pinned 원문을 직접 읽어 판정한다. CV 출력, runner 조건의 명령 출력(R2-3의 `rg`·`git diff` 파라미터 그대로의 출력), 같은 query의 결과를 정답 파일에 복사하지 않는다.
- 후보 수집에는 다른 도구를 쓸 수 있다. NAV는 `rg -n -w`의 전체 결과를 후보로 보고 줄마다 선언 / 이 overload의 정적 참조 / 다른 overload·다른 심볼 / 문자열·reflection 후보 / 주석으로 사람이 분류한다. DIFF의 텍스트 변경 줄은 base·target blob 자체로 정의되고 symbol 귀속·kind·session 제외 여부는 사람이 두 버전을 읽어 판정한다.
- 정적(static)과 동적(dynamic) 정답을 분리한다. 판정할 수 없는 항목은 `unresolved`로 따로 두고 recall 분모에 넣지 않는다. 그 수를 보고한다.
- 정답 항목마다 근거 위치(path:line)를 남긴다. 원문은 복사하지 않는다(R2-1).
- 교차 확인: seed `20260920`으로 정답 항목의 25%(corpus당 최소 10개)를 뽑아 작성자와 다른 세션이 원문을 직접 읽어 확인한다. 오류가 하나라도 나오면 그 corpus 정답 전체를 다시 확인한다.
- file-level 변경(using, namespace 선언, assembly attribute)은 symbol 정답의 필수 항목에서 빼고 `nonSymbolChange`로 기록한다. 모든 조건이 같은 규칙으로 판정받는다.

## R2-3. 조건

모든 조건은 같은 task card, 같은 pinned workspace, 같은 편집, 같은 timeout을 쓴다. 조건 행동은 task card 입력만으로 결정되는 알고리즘이다. task별 수동 조정은 없다. B와 E는 둘 다 **이상적인 범위 읽기**로 정의한다. 실제 에이전트보다 낭비가 적은 쪽으로 양쪽을 같게 정의해 어느 한쪽의 약한 행동이 결과를 만들지 않게 한다.

### A — 기본 search/read

- NAV: `rg -n -w --type cs -e <name>`을 workspace 전체에 실행하고, 결과에 나온 파일을 모두 전체 read한다. N4의 각 편집 뒤 대상 파일을 다시 전체 read한다.
- DIFF: D1·D2에 `git diff -M` (git 기본 context 3, `-- '*.cs'`)을 쓰고, 변경된 `.cs` 파일을 base·target 양쪽에서 전체 read한다. D3는 base 파일 전체 read다.

### B — 범위 읽기 (고정 지침)

NAV:

1. N1·N3: `rg -n -w --type cs -e <simpleName>`을 workspace 전체에 한 번 실행한다. 출력한 줄이 선언 후보이자 참조 후보다. 추가 비용 없이 두 단계에 재사용한다.
2. N2: 후보 선언 줄은 (a) `rg -l -w -e '(class|struct|interface|record)\s+<Type>\b'`로 찾은 파일(경로만 받으므로 source bytes 0) 안의 1번 hit 중, (b) 정규식 `^\s*(\[[^\]]*\]\s*)*([\w<>\[\],.?]+\s+)+([\w<>.]+\.)?<name>\s*(<[^>]*>)?\s*\(`에 맞고, (c) 첫 단어가 `return`, `await`, `new`, `throw`, `yield`, `else`, `case`, `in`, `is`, `as`가 아니며, (d) 괄호가 같은 줄에서 닫히면 최상위 쉼표로 센 매개변수 수가 card와 같은 줄이다. 후보마다 **brace 균형 읽기**를 한다.
3. brace 균형 읽기: 후보 줄부터 줄 단위로 읽는다. 문자열·문자 literal과 주석 밖의 `{`·`}`로 깊이를 센다. 깊이가 한 번 양수가 된 뒤 0으로 돌아온 줄, 또는 깊이 0에서 `;`로 끝나는 줄에서 멈춘다. 2,000줄을 넘으면 파일 끝까지 읽는다.
4. N4: 편집 1과 편집 2 뒤 각각 같은 시작 줄에서 brace 균형 읽기를 다시 한다. B는 staleness를 판별할 수단이 없으므로 항상 다시 읽는다.

DIFF:

1. D1: `git -c core.autocrlf=false diff -M --unified=1 <p> -- '*.cs'` (작업 트리 = `tree(c')`).
2. D2: 세션 시작 시 harness가 `tree(c)`를 tree 객체로 보존했다고 본다(`git stash create`에 해당). `git diff -M --unified=1 <tree(c)> <tree(c')> -- '*.cs'`.
3. D3: D1 출력의 삭제 줄 블록 안에서 지정 심볼의 선언 줄(N2 정규식)을 찾아 그 블록 안에서 brace 균형이 닫히면 **추가 read 없이** 충족한 것으로 본다. 닫히지 않으면 D1 출력의 변경 파일 목록(경로만) 안에서 `git grep -n -w <name> <p> -- <files>`로 선언 줄을 찾고 base blob에서 brace 균형 읽기를 한다. small은 `git diff --no-index --unified=1`과 fixture 디렉터리를 쓴다(rev1과 같음).

### C / D — LSP / Serena

rev1과 같은 이름 목록으로 탐색한다(C: `csharp-ls`, `OmniSharp`, `Microsoft.CodeAnalysis.LanguageServer`, dotnet global tool / D: `serena`, `serena-mcp-server`). 이번 revision은 승인된 C/D adapter를 만들지 않는다. 명령이 없으면 `primary_tool_unavailable`, 있으면 `approved_adapter_unavailable`로 모든 셀을 attempted `infra_failed`로 남긴다. 0 비용·0 결과로 바꾸지 않는다. 설치는 하지 않는다.

### E — Code-Virtualize (U7 호출 패턴)

CV 버전은 TASK-028을 통과해 병합된 commit으로 고정하고 manifest에 기록한다. build·update는 CLI, 탐색은 MCP stdio 서버, diff는 Core API(`SymbolDiffService`, `DiffSourceResolver`, `GitBaselineProvider`, `SessionSnapshotStore`/`SessionBaselineProvider`)를 쓴다. `cv-diff` CLI는 쓰지 않는다(TASK-025 U8).

| 단계 | 호출 | 고정 파라미터 |
|---|---|---|
| 준비 | `cv-build --workspace <w> --format json` | `--trust-workspace` 없음(syntax-only) |
| N1 | MCP `cv_find` | `exact=<simpleName>`, `limit=100`, `nextCursor`가 없을 때까지 페이지를 모두 받는다 |
| N2 | 대상 선택 후 MCP `cv_get` | 대상은 N1 결과에서 `qualifiedName`과 매개변수 타입 목록이 card와 같은 항목. `part=declaration`, `maxBytes=65536`, `maxLines=2000`. 응답의 `contentHash`를 보관한다 |
| N3 | MCP `cv_impact` | `symbolIds=[대상]`, `maxDepth=1`, `includeLexicalCandidates=true`, `maxResults=1000`, `pageSize=100`, `maxSourceFiles=<corpus .cs 파일 수>`, 모든 페이지 |
| N4 (편집마다) | `cv_get`(`ifNoneMatch=<보관 hash>`) → `cv-update --changed <file>` → `cv_get`(같은 `symbolId`, `ifNoneMatch=<보관 hash>`) | 첫 호출은 `SOURCE_STALE`이어야 한다. update 뒤 `SYMBOL_NOT_FOUND`면 `cv_find`(N1 파라미터)로 다시 선택하고 한 번 더 `cv_get`한다. 받은 content가 있으면 보관 hash를 갱신한다 |
| D 준비 | HEAD=`p`, 작업 트리=`tree(c)`에서 `cv-build`. 세션 시작(`SessionSnapshotStore.Capture`). 작업 트리를 `tree(c')`로 바꾸고 `cv-update`(변경 파일 목록) | 반복 1에서만 준비 build를 하고, 준비 직후의 store를 복사해 반복 2·3의 시작점으로 쓴다 |
| D1 | `SymbolDiffService.Compare` (VCS baseline: `GitBaselineProvider`, HEAD `p`) | `DiffEvidenceOptions` 기본값(context 1, entry 8 KiB, 전체 64 KiB, line diff 20,000줄) |
| D2 | `SymbolDiffService.Compare` (Session baseline) | 같은 기본값 |
| D3 | `DiffSourceResolver.Resolve` | D1 결과의 지정 심볼, `Side=Base`, `Part=declaration`, `maxBytes=65536`, `maxLines=2000`. 호출은 한 번 |

E는 필요할 때만 원문을 lazy resolve한다. D1·D2의 added/deleted entry는 `header_only`이며 D3 외에는 추가 resolve를 하지 않는다. small E도 같은 표를 따른다(N2는 N-03과 N-09 두 번).

## R2-4. 계상 규칙

### 원칙

**도구와 무관하게, 조건이 받은 출력 중 소스 코드에서 온 텍스트는 모두 source bytes로 센다.** 그리고 **품질 판정은 source bytes로 센 텍스트와 위치 메타데이터만 쓴다.** 판정에 쓴 원문이 계상에서 빠지는 비대칭(R1)이 구조적으로 생기지 않게 하기 위해서다.

| 출력 | source bytes로 세는 것 | 세지 않는 것(메타데이터) |
|---|---|---|
| `rg -n` | 각 hit 줄의 내용 | `path:line:` 접두 |
| `rg -l`, `rg -c`, `git grep -l` | 없음 | 경로·개수 |
| `git grep -n` | 각 hit 줄의 내용 | `rev:path:line:` 접두 |
| `git diff` | hunk 본문 줄(`-`·`+`·문맥)의 접두 한 글자를 뺀 내용 | `diff --git`, `index`, mode, `---`/`+++`, `@@` 헤더, `\ No newline` |
| read(범위·전체) | 읽은 줄 전체 | — |
| CV `cv_get`, `DiffSourceResolver` | `source.content` (`notModified=true`면 0) | envelope, span, hash, freshness, coverage |
| CV diff entry | `evidence.textualHunk`의 hunk 본문 줄(`line_hunks`·`header_only`) | hunk 헤더, id, location, hash, kind, 생략 줄 수 |
| CV 결과의 코드 문자열 필드 | `signature`, remark 원문이 있으면 그 원문 | `symbolId`, `name`, `qualifiedName`, `kind`, `accessibility`, 경로, span, provenance |

- `signature`는 소스 원문 그대로는 아니지만 선언 줄에서 만든 코드 텍스트이고 B의 grep 줄이 주는 정보와 같은 역할을 한다. 그래서 센다. 세지 않으면 E의 N1·N3가 정의상 0 B가 되어 E에 유리하다.
- 같은 텍스트를 여러 번 받으면 받을 때마다 센다. 실제 context 비용이기 때문이다.

### 정규화 (R3)

- 모든 텍스트는 UTF-8 bytes로 센다. BOM은 세지 않는다.
- 줄 단위 출력(`rg`, `git diff`, read)은 줄마다 `내용 bytes + 1`(LF 한 byte)로 센다. CR은 내용에서 뺀다.
- CV `content`·`signature`는 CRLF와 CR을 LF로 바꾼 뒤 bytes를 센다.
- 이 규칙으로 checkout의 줄바꿈 설정과 무관하게 같은 값이 나온다. manifest에는 정규화 전 raw bytes도 함께 남긴다.

### R1~R4 처리

| ID | 문제 | rev2 처리 | 이유 |
|---|---|---|---|
| R1 | B DIFF가 판정에 쓴 `git diff` 출력(small 원문 줄 402 B)을 세지 않았다. B NAV의 `rg -n` 출력도 세지 않았다 | **센다.** 위 표대로 A·B의 grep·diff 본문 줄과 E의 hunk·header 줄을 같은 규칙으로 센다 | 원칙 채택. "둘 다 제외"는 E의 diff evidence도 빼야 하는데, 그러면 DIFF에서 E가 받는 원문 대부분이 사라져 E에 유리하다. "둘 다 셈"만이 판정에 쓴 텍스트와 계상 텍스트를 일치시킨다 |
| R2 | stale 복구 재조회가 E NAV에만 있었다 | **양쪽에 같은 편집 시나리오를 준다.** N4의 편집 두 번 뒤 A·B·E 모두 현재 원문을 다시 얻어야 하고 그 비용을 모두 센다 | 복구 비용을 E에서만 세면 E에 불리하고, 복구를 빼면 CV의 조건부 재조회 이득과 staleness 안전성이 측정에서 빠진다. 편집 1(span 밖)은 E의 `notModified`가 이득을 내는 경우이고 편집 2(span 안)는 양쪽 모두 원문을 다시 받는 경우다. 두 경우를 한 번씩 넣어 편집 위치 선택이 결과를 기울이지 않게 한다 |
| R3 | 기록값이 LF 기준이라 CRLF checkout에서 E만 커진다 | **줄바꿈 정규화**(위) | 환경에 따라 바뀌는 값을 판정에 쓰지 않는다 |
| R4 | E NAV 품질이 `cv_get` 내용을 보지 않았다(`!IsError`) | **내용으로 판정한다.** N2·N4는 받은 원문이 정답 선언을 포함해야 통과다. small NAV-03은 E가 `BuildLabel` 원문을 실제로 받아야 통과다 | 반환량을 줄이는 계약에서 과소 조회를 잡으려면 품질이 내용을 봐야 한다 |

### 보조 지표 (판정에 쓰지 않음, 보고 필수)

- `sourceLines`, 도구 호출 수, 조건별 `toolOutputBytes`(stdout·MCP 응답 전체를 같은 정규화로 센 bytes, JSON envelope와 메타데이터 포함).
- `toolOutputBytes`는 token의 대체물이 아니다. 다만 source bytes가 줄어도 envelope 때문에 전체 출력이 늘 수 있으므로 ADR 006은 두 값을 나란히 적는다. source bytes 통과를 근거로 context·token 절감을 주장하지 않는다.

## R2-5. 품질·안전 판정

판정기는 조건과 무관한 규칙 하나다. 입력은 조건이 받은 **계상 텍스트 항목**과 **보고 위치 집합**이다.

- 보고 위치: A·B는 `rg`/`git grep` hit의 `(path, line)`, E는 `cv_find` 선언 위치와 `cv_impact` 결과 위치다. 경로는 workspace 기준 상대 경로, `/` 구분, 대소문자 구분으로 비교한다.
- 텍스트 비교: 줄 단위로 나누고 앞뒤 공백을 지운 뒤 글자나 숫자가 없는 줄(`{`, `}`, 빈 줄 등)을 뺀다. 이것을 "유의미한 줄"이라 한다. 정답 텍스트의 유의미한 줄 목록이 **한 계상 텍스트 항목** 안에 연속 부분열로 있으면 포함이다.

| 단계 | 통과 조건 |
|---|---|
| N1 | 보고 위치 ⊇ 정답 선언 위치 |
| N2 | 정답 선언(대상 이름이 있는 줄부터 선언 끝까지)이 계상 텍스트에 포함 |
| N3 | 보고 위치 ⊇ 정적 참조 정답 위치. 동적 후보 recall과 정답 밖 위치 수는 보고만 한다. E가 동적 후보를 static 결과에 섞으면(provenance 누락) 정답 밖 위치로 보고한다 |
| N4 | 편집 1 뒤: 편집 뒤 현재 선언이 새로 받은 텍스트에 포함되거나, E의 `notModified` 응답 hash가 harness가 현재 bytes로 계산한 선언 hash와 같다. 편집 2 뒤: `// cv-bench-edit-2`를 포함한 현재 선언이 새로 받은 텍스트에 포함 |
| D1·D2 | 정답 변경 항목마다: body·signature·remark 변경은 삭제 줄이 받은 hunk의 삭제 측에, 추가 줄이 추가 측에 모두 있다. added/deleted는 선언 이름이 있는 줄이 추가/삭제 측에 있다. hunk는 받은 unified hunk 텍스트(git 출력 또는 E `textualHunk`)를 같은 parser로 읽는다 |
| D2 제외 | 세션 전 dirty 변경(c의 변경) 중 TASK-027이 "제외 확인 줄"로 지정한 줄이 Session 출력의 변경 줄로 나타나면 실패다. 같은 내용이 c'에서도 바뀌는 줄은 제외 확인 줄로 지정하지 않는다 |
| D3 | base `p`의 지정 심볼 선언이 계상 텍스트에 포함. target이나 현재 파일의 텍스트만 있으면 실패 |

- task 품질은 한 반복 안에서 모든 단계가 통과할 때 통과다. recall은 단계별 분자·분모로 보고한다. 정답 0건 단계는 recall을 `null`로 둔다.
- **Critical(안전)**: E가 편집 뒤 update 전 첫 `cv_get`에서 content를 성공으로 반환(stale 원문 오반환), 편집 2 뒤 `notModified=true` 반환, D3에서 base가 아닌 bytes 반환, `status=partial`인데 `coverage.limitations`가 비어 있음(조용한 partial), evidence가 잘렸는데 `evidenceTruncated`·limitation이 없음. 하나라도 있으면 Critical이다.
- E run이 `succeeded`가 아니면(timeout, tool_failed 포함) 그 반복에서 E 품질은 실패다.

## R2-6. 반복·순서·환경

- 반복: task·조건별 3회(FUP-002 최소값).
- paired block: `(corpus, task, repeat)`. block 순서는 corpus(`small-synthetic`, `medium-litedb`, `medium-quartznet`, `large-aspnetcore`) → task ID → repeat 순이다. block 안 조건 순서는 rev1 runner와 같게 `Shuffle(["A","B","C","D","E"], 20260920)`을 block 번호만큼 rotate한다. 실제 순서를 manifest에 남긴다.
- E cold build는 corpus·반복마다 NAV 시작 전 한 번 새 store에서 하고, D 준비 build는 반복 1에서만 한다(R2-3). 조건 사이에 도구 cache를 공유하지 않는다. OS page cache는 제거하지 않고 기록만 한다.
- 호출당 timeout은 해당 예산(R2-7)의 2배다. 넘으면 `timeout`이다.
- manifest에 CPU, 물리 메모리, OS, 디스크 종류, .NET SDK, `rg`·`git` 버전, CV commit, corpus commit과 license 재확인 결과, fixture digest를 기록한다.
- 계획 셀: small 2 task × 5 조건 × 3 = 30, 새 corpus 3개 × 12 task × 5 × 3 = 540, 합계 570.

## R2-7. 예산 (small 밖은 새 기준 — G2 승인 대상)

small의 FUP-002 값은 hang·runaway 탐지용으로 small fixture에 맞춰 정한 것이라 규모에 비례해 늘릴 근거가 없다. medium·large 값은 **제품 사용 조건**에서 정한다. 로컬 에이전트 세션의 도구로서 처음 한 번 색인은 몇 분을 허용하고, 대화 중 조회는 대화 흐름을 끊지 않아야 하며, 개발 PC(16 GiB 가정) 메모리의 4분의 1을 넘지 않아야 한다는 조건이다. 어떤 CV 측정값도 보고 정하지 않았다.

| 항목 | small (FUP-002, 변경 없음) | medium (**새 기준**) | large (**새 기준**) | 대상 측정 |
|---|---:|---:|---:|---|
| cold build | 60 s | 300 s | 900 s | 실행별 `cv-build` wall time(restore 없음) |
| warm lookup | 2 s | 2 s | 2 s | 호출별 `cv_find`(한 페이지), `cv_get`, `DiffSourceResolver` |
| warm analysis | 2 s (warm query에 포함) | 10 s | 30 s | 호출별 `cv_impact`(한 페이지), `SymbolDiffService.Compare`(mode별) |
| incremental update | 5 s | 10 s | 30 s | 실행별 `cv-update`(NAV 편집·되돌리기, D 준비의 `tree(c)→tree(c')`) |
| peak memory | 1 GiB | 2 GiB | 4 GiB | engine + harness 자식 프로세스 peak working set 합 |
| wall time (실행 한도) | 30 min | corpus당 120 min | 360 min | fixture·정답 검증, 모든 조건·반복, 집계 포함 |

- cold build 300 s·900 s는 medium 상단(14.5 MB)과 large(70.8 MB)에서 각각 약 48 KB/s·79 KB/s의 최소 처리량에 해당한다. 처음 한 번 색인이 5분·15분을 넘으면 세션 도구로 쓰기 어렵다고 본다.
- warm lookup은 규모와 무관하게 2 s를 유지한다. 색인 조회가 규모에 비례해 느려진다면 그 자체가 제품 결함이기 때문이다.
- warm analysis와 update는 코드 분석·재색인을 포함하므로 규모 구간을 둔다.
- wall time은 제품 기준이 아니라 **실행 한도**다. 전체 합계는 11시간(30 + 120 × 2 + 360분) 이하다. 80%에 도달하면 새 block을 시작하지 않고 100%에서 중지한다. 시작하지 못한 셀은 `not_started`로 남고 그 corpus의 판정은 `inconclusive`다.
- 예산 초과 측정은 삭제하지 않는다. 초과 값과 분포(중앙값, p95, 최대)를 모두 보고한다.

## R2-8. 판정 규칙

판정은 결과 파일만으로 아래 순서로 기계적으로 계산한다. TASK-029 runner가 `--evaluate-gates`로 계산하고 TASK-030은 그 값을 옮긴다.

1. **task별 baseline**: task `t`에서 A·B·C·D 중 3회 모두 `succeeded`이고 품질 통과인 조건 가운데 source bytes 중앙값이 가장 작은 조건이다. 동률이면 D, C, B, A 순이다(ADR 001).
2. **B 실패 보호**: B가 task `t`에서 3회 모두 품질 통과가 아니면 `t`는 efficiency 계산에서 뺀다(`baseline-degraded`로 보고). B의 실패 덕분에 전체 read인 A와 비교되어 E가 이기는 경우를 막는다. E의 품질 판정에서는 빼지 않는다.
3. **task 감소율**: 반복 `k`마다 `r_k = (base_k − E_k) / base_k`, `r(t) = median_k r_k`. `base_k = 0`이면 `E_k = 0`일 때 `r_k = 0`, 아니면 `r_k = −∞`다.
4. **셀 감소율**: 셀은 `(corpus, family)`, family는 NAV·DIFF다. 셀 값 `R = median_{t ∈ held-out, 2번에서 남은 task} r(t)`. small 셀은 small task 하나로 계산한다. 새 corpus 셀에서 남은 held-out task가 3개 미만이면 셀은 `inconclusive`다.
5. **셀 통과**: `R ≥ 0.20`.
6. **Go 조건** — 아래를 모두 만족해야 Go다.
   - G-Q 품질: 모든 corpus·task(tuning 포함)·반복에서 baseline 조건이 품질 통과인데 E가 품질 실패인 경우가 0.
   - G-S 안전: 모든 E run의 Critical 합계 0.
   - G-E 효율: 8개 셀(small NAV·DIFF, medium-litedb NAV·DIFF, medium-quartznet NAV·DIFF, large-aspnetcore NAV·DIFF)이 모두 통과.
   - G-P 성능: 모든 E 측정값이 해당 corpus 등급의 R2-7 예산 이하.
   - G-C 비용: 외부 유료비용 0.
   - G-R 완전성: 계획 셀 570개가 모두 attempted이고 task·조건별 반복이 3회 이상.
7. **결과 분류**: G-Q, G-S, G-P, G-C 중 하나라도 실패하거나 어떤 셀이 5번에서 실패하면 **No-Go**다. 실패는 없는데 `inconclusive` 셀이 있거나 G-R을 만족하지 못하면 **Inconclusive**이며 제품화 재개 조건을 충족하지 못한 것으로 본다. 나머지가 **Go**다.
8. **token**: 실제 input token record(FUP-001)가 없으면 token 판정은 `inconclusive`다. 이때 Go는 "stronger available baseline B 대비 source-byte Go, token inconclusive, C/D unavailable"로 한정해 적는다.
9. 셀·corpus·등급 사이의 결과를 평균하거나 한 셀의 여유로 다른 셀의 실패를 상쇄하지 않는다. task별 `r(t)`, `r(t) ≥ 0.20`인 task 비율, `baseline-degraded` task 수, `toolOutputBytes` 비교를 셀마다 보고하되 판정에는 쓰지 않는다.

## R2-9. 산출물

| 파일 | 담당 | 내용 |
|---|---|---|
| `benchmarks/corpora/rev2-manifest.json` | TASK-027 | corpus alias, 저장소, commit, license 재확인, 등급 지표 다섯 개와 측정 도구 버전, NAV 표본 순서, DIFF 쌍, tuning/held-out 지정 |
| `benchmarks/tasks/rev2/<alias>-*.md` | TASK-027 | task card(입력만) |
| `tests/fixtures/rev2/<alias>/expected.json` | TASK-027 | 좌표·hash 기반 정답, static/dynamic/unresolved 분리, 제외 확인 줄, 교차 확인 기록 |
| `benchmarks/results/rev2/<alias>-result.json` | TASK-029 | `result.schema.json` 형식의 corpus별 집계(`protocolRevision = rev2-adr005`) |
| `benchmarks/results/rev2/run-manifest.json` | TASK-029 | run별 기록, 조건 순서, 환경, `runnerChangeLog`, raw·정규화 bytes, 보조 지표 |
| `benchmarks/results/rev2/gate-evaluation.json` | TASK-029 | R2-8 단계별 중간값과 최종 분류 |
| `benchmarks/results/rev2/report.md` | TASK-029 | 사람이 읽는 보고서 |

정확한 경로는 TASK-027·029가 바꿀 수 있지만 내용 항목은 바꾸지 않는다. 결과 파일에는 원문, prompt, 로컬 절대 경로를 넣지 않는다.

## R2-10. 변경 통제

- G2 승인 뒤 이 절을 바꾸려면 새 corpus 결과를 보기 전에 새 ADR로 이유와 날짜를 기록한다.
- held-out 결과를 본 뒤의 변경은 모두 새 revision이다(R2-2 tuning/held-out).
- TASK-026 담당에게 새 corpus의 task card와 정답을 주지 않는다. CV 코드는 TASK-028 통과 commit에서 고정하고, held-out 실행 뒤 CV를 바꾸면 새 실험이다.
