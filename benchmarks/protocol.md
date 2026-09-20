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
