# 로그 집계 계약과 합성 검산

## 적용 범위

이 문서는 [protocol.md](protocol.md)의 이벤트를 비식별 집계로 바꾸는 규칙과 손계산 fixture를 정의한다. 아래 이벤트와 금액은 모두 합성값이며 실제 세션, 실제 제품 성능, 원안의 누락 문장을 나타내지 않는다.

집계기는 원시 source, prompt, comment, tool output 본문을 출력하지 않는다. 결과는 [result.schema.json](result.schema.json)을 통과해야 하며, 스키마 통과만으로 산술 정합성이 보장되는 것은 아니므로 아래 invariant도 검사한다.

## 비식별화와 보관

허용 목록 방식으로 필드를 수집한다. 이름·경로·본문을 받은 뒤 지우는 방식은 사용하지 않는다.

| 원본 정보 | 집계 입력 | 규칙 |
|---|---|---|
| session/run/user ID | `sessionIdHash`, `runId` | 실험별 무작위 salt를 사용한 HMAC 또는 무작위 매핑. salt와 매핑표는 집계 JSON과 분리하고 공유하지 않음 |
| repository | `corpusId`, size bucket, 공개 commit 별칭 | 로컬 경로, remote URL, 조직·사용자 이름 금지 |
| 파일 경로 | `fileId` | run 범위의 비가역 ID. 확장자도 승인 필드가 아니면 버림 |
| source/prompt/comment/tool output | token·byte·line 수와 boolean | 본문, 검색어, symbol 이름, diff, stack trace 금지 |
| 오류 | 열거형 `errorCategory` | 자유 형식 메시지·명령행·환경 변수 금지 |
| 시간 | UTC timestamp 또는 상대 duration | 기존 로그 분석에서 정확한 시각이 불필요하면 날짜 bucket으로 낮춤 |
| 모델·도구 | 승인된 version alias | 계정·endpoint·credential 제거 |

`.env`, credential, token, API key, 사용자 홈 경로, 절대 경로를 원시/집계 artifact 어느 쪽에도 복사하지 않는다. 원시 로그는 승인된 로컬 경로에서 read-only로 처리하고 외부 서비스에 업로드하지 않는다. 집계 전 임시 파일, mapping table, salt의 보관·삭제 시점은 FUP-001에 기록한다.

## 집계 절차

1. run manifest의 승인 범위와 protocol revision을 확인한다. 범위 밖 파일·기간은 열지 않는다.
2. JSONL을 streaming parse하고 필드 allowlist를 적용한다. 파싱 실패 line 수를 남기되 내용을 출력하지 않는다.
3. `(runId, eventId)`로 중복을 제거한다. 같은 키·같은 canonical payload는 `duplicateEventCount`만 올리고, payload 충돌은 `conflictingDuplicateCount`와 해당 run의 `invalid`를 올린다. context 포함량은 `(requestId, contentItemId)`로 한 번만 센다.
4. 이벤트별 필수 필드를 검사한다. 지표에 필요한 선택 필드가 없으면 해당 지표에서만 제외하고 `missingRequiredFieldCount`와 `excludedMetricEventCount`를 올린다. `run_started`, terminal status, usage 의미처럼 run 정합성에 필요한 값이 없으면 run을 `invalid`로 격리한다.
5. provider usage를 배타적 token bucket으로 정규화한다. `usageRecordId` 중복과 누적 snapshot 중복을 제거한 뒤 더한다.
6. 각 run의 terminal status, 도구 사용 여부, 비용, 품질을 계산하고 condition aggregate로 올린다.
7. 조건 합계와 overall 합계를 대조하고 산술 invariant를 검사한다.
8. schema validation 후 집계 JSON만 공유한다. 원시 로그 공유는 별도 승인 없이는 금지한다.

필수 invariant는 다음과 같다.

```text
sum(terminalCounts) == attemptedRunCount
successfulRunCount == terminalCounts.succeeded
successRate.denominator == attemptedRunCount
overall.attemptedRunCount == sum(condition.attemptedRunCount)
overall.notStartedRunCount == sum(condition.notStartedRunCount)
plannedRunCount == attemptedRunCount + notStartedRunCount
condition/cacheState 조합은 결과에서 최대 한 번만 등장
ratio.denominator == 0  => ratio.value == null
cacheAwareCost.complete == false => cacheAwareCost.total == null
```

부동소수 비율은 원래 numerator/denominator를 진실의 원천으로 보존하고 표시할 때만 반올림한다. 비용은 decimal 산술을 사용하며 가격표의 최소 통화 단위보다 한 자리 더 유지한 뒤 보고서에서 반올림한다.

## 합성 이벤트

다음 fixture는 조건 A의 계획 run 4개 중 3개가 시작된 상황이다. `r1`은 성공, `r2`는 timeout, `r3`은 성공했지만 일부 read metadata가 누락됐다. 네 번째 셀은 예산 중지 후 시작되지 않았다.

### 실행 상태

| run | 상태 | 품질 판정 | 비고 |
|---|---|---|---|
| `r1` | `succeeded` | pass | 완전한 usage와 read metadata |
| `r2` | `timeout` | 없음 | 실패도 attempted 분모와 비용에 포함 |
| `r3` | `succeeded` | pass | 한 read의 `fileBytes` 누락, 전체 읽기율에서만 제외 |
| 예약 셀 | `not_started` | 없음 | attempted에서 제외하고 별도 보고 |

### 도구 결과 token과 읽기

중복 전 원시 이벤트에는 `r1`의 첫 read 이벤트가 동일 `eventId=e-read-1`로 두 번 들어 있다. 두 payload는 같으므로 한 번만 센다. 아래 각 유효 tool 결과는 합성 fixture에서 다음 모델 요청에 한 번씩 포함됐으며, 대응하는 `context_inclusion` token도 같은 표에 표시했다.

| 유효 이벤트 | run | 도구 | tool result token | 파일/구간 | 전체 read 판정 | 재읽기 판정 | 주석 token |
|---|---|---:|---:|---|---|---|---:|
| `e-read-1` | r1 | read | 100 | f1, bytes 0–999 / 1000 | yes | no | 20 |
| `e-grep-1` | r1 | grep | 50 | 해당 없음 | 해당 없음 | 해당 없음 | 0 |
| `e-read-2` | r1 | read | 40 | f1, bytes 200–399 / 1000 | no | yes | 10 |
| `e-lsp-1` | r1 | lsp | 60 | 해당 없음 | 해당 없음 | 해당 없음 | 판정 제외 |
| `e-read-3` | r2 | read | 30 | f2, bytes 0–99 / 500 | no | no | 판정 불가 |
| `e-read-4` | r3 | read | 80 | f3, bytes 0–799 / `fileBytes` 누락 | unknown | no | 20 |

손계산은 다음과 같다.

- Read/Grep context inclusion token: `100 + 50 + 40 + 30 + 80 = 300`.
- 모든 성공 tool result의 context inclusion token: `100 + 50 + 40 + 60 + 30 + 80 = 360`.
- `readGrepTokenShare = 300 / 360 = 0.8333333333`.
- 판정 가능한 read는 `e-read-1`, `e-read-2`, `e-read-3`의 3건이다. `e-read-4`는 분모에서 제외하고 `unknownFullReadCount=1`이다.
- `fullReadRate = 1 / 3 = 0.3333333333`.
- file ID와 range가 있는 read 4건 중 앞선 read와 겹치는 것은 `e-read-2` 한 건이다. `r2`, `r3`은 서로 다른 run이므로 다른 run의 read와 비교하지 않는다. `rereadRate = 1 / 4 = 0.25`.
- 주석 판정이 가능한 source 결과를 예시에서는 `e-read-1`, `e-read-2`, `e-grep-1`, `e-read-4`로 둔다. `commentTokenShare = (20 + 10 + 0 + 20) / (100 + 40 + 50 + 80) = 50 / 270 = 0.1851851852`.
- 중복 `e-read-1`은 token, read 호출, 주석 token 어디에도 두 번 더하지 않는다. `duplicateEventCount=1`이다.

### usage와 cache-aware 비용

가격은 손계산을 단순하게 하려고 합성 단가를 사용한다.

| bucket | 합성 단가/token |
|---|---:|
| uncached input | 0.002 |
| cache read input | 0.0002 |
| cache write input | 0.0025 |
| output | 0.004 |

| usage record | run | total input | cache read | cache write | 정규화 uncached | output |
|---|---|---:|---:|---:|---:|---:|
| `u1` | r1 | 1000 | 400 | 100 | 500 | 200 |
| `u2` | r2 | 300 | 0 | 0 | 300 | 20 |
| `u3` | r3 | 500 | 200 | 0 | 300 | 100 |

원시 입력에 `u1`이 같은 `usageRecordId`로 한 번 재전송되어도 한 번만 센다. 따라서 token 합계는 uncached `1100`, cache read `600`, cache write `100`, output `320`이다. 겹치는 `total input`을 다시 더하지 않는다.

```text
modelCost = 1100*0.002 + 600*0.0002 + 100*0.0025 + 320*0.004
          = 2.20 + 0.12 + 0.25 + 1.28
          = 3.85
```

합성 metered tool cost `0.30`, 한 번만 부과하는 setup charge `0.05`를 더하면 `cacheAwareCost.total = 4.20`이다. setup 이벤트가 재전송돼도 같은 `chargeId`이면 다시 더하지 않는다.

### 실패와 누락 필드 분모

- `attemptedRunCount=3`, terminal은 succeeded 2, timeout 1이다. `successRate = 2 / 3 = 0.6666666667`; timeout을 빼서 `2/2`로 만들지 않는다.
- 독립 품질 판정은 r1/r3 두 건이고 둘 다 pass다. `qualityPassRate = 2 / 2 = 1.0`. r2의 품질은 0점이 아니라 missing이며 전체 성공률과 함께 해석한다.
- 예약 셀 하나는 `notStartedRunCount=1`, 따라서 `plannedRunCount=4 = 3 + 1`이다.
- `e-read-4.fileBytes` 누락은 전체 읽기율만 제외한다. tool token, reread, comment 비율에 필요한 필드는 있으므로 그 지표에는 포함한다. 이 예시의 `missingRequiredFieldCount=1`, `excludedMetricEventCount=1`이다.

## 스키마 검증용 합성 집계

아래 문서는 위 손계산을 표현한다. A/B 조건에는 primary tool이 없으므로 `primaryToolUseRate`의 denominator를 0, value를 `null`로 둔다.

```json
{
  "schemaVersion": "1.0.0",
  "protocolRevision": "task-001-v1",
  "generatedAt": "2026-09-20T00:00:00Z",
  "experimentPhase": "synthetic_validation",
  "currency": "TST",
  "randomizationSeed": 17,
  "conditions": [
    {
      "condition": "A",
      "cacheState": "not_applicable",
      "attemptedRunCount": 3,
      "notStartedRunCount": 1,
      "terminalCounts": {
        "succeeded": 2,
        "quality_failed": 0,
        "tool_failed": 0,
        "timeout": 1,
        "budget_stopped": 0,
        "infra_failed": 0,
        "cancelled": 0,
        "invalid": 0
      },
      "successRate": { "numerator": 2, "denominator": 3, "value": 0.6666666667 },
      "qualityPassRate": { "numerator": 2, "denominator": 2, "value": 1.0 },
      "readGrepTokenShare": { "numerator": 300, "denominator": 360, "value": 0.8333333333 },
      "fullReadRate": { "numerator": 1, "denominator": 3, "value": 0.3333333333 },
      "rereadRate": { "numerator": 1, "denominator": 4, "value": 0.25 },
      "commentTokenShare": { "numerator": 50, "denominator": 270, "value": 0.1851851852 },
      "primaryToolUseRate": { "numerator": 0, "denominator": 0, "value": null },
      "unknownFullReadCount": 1,
      "tokens": {
        "uncachedInput": 1100,
        "cacheReadInput": 600,
        "cacheWriteInput": 100,
        "output": 320
      },
      "cacheAwareCost": {
        "model": 3.85,
        "tool": 0.30,
        "setup": 0.05,
        "total": 4.20,
        "complete": true
      }
    }
  ],
  "overall": {
    "plannedRunCount": 4,
    "attemptedRunCount": 3,
    "notStartedRunCount": 1,
    "completePairCount": 0,
    "incompletePairCount": 1
  },
  "dataQuality": {
    "duplicateEventCount": 2,
    "conflictingDuplicateCount": 0,
    "unknownEventCount": 0,
    "missingRequiredFieldCount": 1,
    "invalidUsageCount": 0,
    "excludedMetricEventCount": 1,
    "invalidRunCount": 0
  },
  "notes": [
    "모든 값은 집계 계약 검산용 합성 데이터다.",
    "duplicateEventCount는 tool event 1건과 usage record 1건의 재전송이다."
  ]
}
```

스키마 검증과 별도로 집계기는 모든 ratio에 대해 `abs(value - numerator/denominator)`가 허용 오차 이내인지 검사해야 한다. JSON Schema는 이 나눗셈과 terminal 합계를 표현하지 않으므로 parser의 invariant 검사가 필수다.

## 누락·오류 처리표

| 상황 | 처리 |
|---|---|
| 알 수 없는 event type | 집계에서 제외, `unknownEventCount` 증가; 비율 denominator에 넣지 않음 |
| 같은 event ID, 같은 payload | 한 번만 집계, `duplicateEventCount` 증가 |
| 같은 event ID, 다른 payload | run `invalid`, 충돌 수 증가, 수동 조사 전 결과 비교에서 제외 |
| `tool_result.contentTokens` 누락 | token share에서만 제외, 호출·range 지표는 가능한 경우 유지 |
| read의 `fileBytes/fileLines` 누락 | full-read 분모 제외, unknown count 증가 |
| read의 `fileId/range` 누락 | reread 분모 제외 |
| comment 판정 누락 | comment share 분모 제외 |
| usage bucket 의미 불명·음수 delta | 해당 usage 비용을 추정하지 않고 cost incomplete; `invalidUsageCount` 증가 |
| 가격 누락 | token은 보고, 비용 total은 `null`, `complete=false` |
| terminal 이벤트 누락 | 시작된 run을 `invalid`로 닫아 attempted 분모 유지 |
| quality 판정 누락 | quality 분모에서 제외하되 성공률·누락 개수 공개 |

이 규칙은 누락이 많은 조건을 유리하게 보이게 하지 않도록 metric coverage와 실패 상태를 항상 결과 옆에 둔다.
