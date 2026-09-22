# Implementation Plan

## 기준과 현재 상태

작성일: 2026-09-20. 모델 배정과 서브에이전트 실행 방식 갱신: 2026-09-20. [design.md](design.md), [architecture.md](architecture.md), [user-confirm.md](user-confirm.md)의 확정 결정과 [UI 시안 3종](samples/index.html)을 종합했다. 기준은 사용자 인터뷰 커밋 `b16df53`, [ADR 001](../decisions/001-product-path.md), [ADR 002](../decisions/002-validation-outcome.md), [ADR 003](../decisions/003-next-step.md)이다. TASK-001~016 구현·검증과 TASK-017의 최종 No-Go 후속 결정을 반영했다. 2026-09-21/22에 FUP-004~007 확정 사항과 TASK-018~021 상태를 [user-confirm.md](user-confirm.md)·[user-confirm2.md](user-confirm2.md) 기준으로 동기화했다.

- UC-001~011은 모두 Confirmed다. 다시 선택을 요구하지 않는다.
- TASK-006~016의 Windows/C#/Git 기술 prototype, Core·CLI·MCP·Claude adapter와 benchmark artifact는 보존한다.
- TASK-016에서 E 품질 `6/6`, recall `27/27`, Critical `0`을 확인했지만 source bytes가 NAV `9.75% 감소`, DIFF `1,618.9% 증가`로 동결된 `20% 이상 감소` gate에 실패했다.
- 실제 model token 효율은 `inconclusive`이며 source bytes/lines와 분리한다. C/D는 unavailable이다.
- 제품화 자동 진행은 중단한다. 로컬 Web UI와 Perforce 요구는 폐기하지 않고 재개 조건부 `Blocked`로 보존한다.
- 자동 GC는 FUP-004 확정 정책(`age`/`capacity`/`hybrid` 중 config 선택, 활성 generation 보호, dry-run)을 따르며 기본값은 `gc.enabled = false`다. 삭제 없는 측정·정책 제안은 TASK-021에서 착수할 수 있고 retention·용량 수치를 실측·승인받기 전에는 자동 삭제를 켜지 않는다. 잘린 원문 복구 요구는 FUP-005로 폐기했고 TASK-020은 종결(Won't do)이다.

## 실행 상태와 gate

`Complete`는 산출물과 검증이 끝난 작업, `Ready`는 현재 실행 가능한 작업, `Blocked`는 외부 입력·새 결정이 필요한 작업이다. TASK-001~017은 완료됐다. TASK-018·019는 아래 재개 조건이 남은 `Blocked` 상태이고, TASK-020은 종결(Won't do), TASK-021은 삭제 없는 측정·정책 제안까지는 착수 가능하며 GC 구현과 자동 삭제 활성화는 수치 확정 후로 `Blocked`다.

| Gate | 결과·조건 | 영향 |
|---|---|---|
| Pilot 실행 | 합성 smoke 완료; 실제 로그·model token/비용은 FUP-001 미완 | TASK-004 합성 범위 Complete |
| Prototype 구현 | 별도 전체 구현 지시로 TASK-006~015 완료 | 기술 artifact 보존; 제품 가치 Go 주장 금지 |
| 본 실험 | 품질 6/6·recall 27/27·Critical 0, source-byte 20% gate 실패, token inconclusive, C/D unavailable | TASK-016 Complete — No-Go |
| 후속 결정 | 기술 prototype 보존, 독립 엔진 제품화 자동 진행 중단 | TASK-017 Complete |
| UI 제품화 | FUP-006 Confirmed(Sample 1 + Blazor Server); 공통 재개 증거·새 ADR 필요 | TASK-018 Blocked |
| 필수 Perforce | FUP-007 Confirmed/Environment Unavailable(계약 확정, 실환경 미제공); 공통 재개 증거·새 ADR 필요 | TASK-019 Blocked |
| 원문 복구 | FUP-005 Obsolete — 복구 요구 폐기 | TASK-020 종결 (Won't do) |
| 자동 GC | FUP-004 Confirmed(정책 확정); retention·용량 수치는 실측 후 확정, 승인 전 no-delete | TASK-021 측정 착수 가능 |

```mermaid
flowchart TD
    P[TASK-001~005 준비 / 경로 선택] --> F[TASK-006~015 engineering prototype]
    F --> B[TASK-016 본 실험]
    B -->|No-Go| N[TASK-017 최종 disposition]
    N --> A[prototype과 benchmark 보존]
    N --> U[TASK-018 Web UI Blocked / FUP-006 Confirmed]
    N --> V[TASK-019 Perforce Blocked / FUP-007 Confirmed]
    N --> K[TASK-021 측정 착수 가능 / 구현·자동 삭제는 수치 승인 후]
    O[TASK-020 종결 Won't do / FUP-005 Obsolete]
    R[NAV·DIFF source-byte 계약 개선 + protocol revision / ADR 005 동결 후 ADR 006 재측정 결과] --> U
    R --> V
```

TASK-001/002/003은 독립적인 준비 작업이었다. 완료된 prototype과 재현 artifact는 보존한다. 원문 복구가 전체 작업의 불필요한 선행 조건이 되지 않게 하며, No-Go 후속 제품 작업은 새 증거·입력·결정을 갖추기 전 실행하지 않는다.
## Agent / Model 배정

계획의 모든 TASK는 표에 지정한 모델과 추론 수준을 명시한 Codex 서브에이전트에 배정한다. 사용 모델은 `gpt-5.6-sol`, `gpt-5.6-terra`, `gpt-5.6-luna`다. 복합 설계·검토·통합 판단은 Sol High, 명확한 구현·분석은 Sol/Terra Medium 또는 High, 제한된 출처 수집은 Luna Low다. 기존 상위 모델 배정은 검증 범위를 유지한 채 Sol High로 통일했다. 불필요하게 높은 추론 수준을 쓰지 않는다.

전역 역할 규칙의 Claude 설계·교차 리뷰 선호는 유지한다. 이 세션에서 특정 Claude 모델의 실행 가능성이 확인되지 않았으므로 계획에 호출 불가능한 모델명을 기입하지 않았다. 실제 Claude 환경을 확인하면 해당 작업을 동등 역할로 재배정하고 모델·추론 수준을 갱신한다. 모델 배정은 계획이며 유료 agent 실행 권한·실험 예산의 대체물이 아니다.

## 서브에이전트 실행 방식

- 메인 에이전트는 dispatcher와 integrator 역할을 맡는다. TASK의 `Dependencies`, `Blocked By`, `Status`를 확인하고 실행 가능한 TASK만 서브에이전트에 전달한다.
- TASK 하나를 기본 작업 단위로 사용한다. 서브에이전트 프롬프트에는 Goal, Dependencies, Scope, Files, Validation, 확정된 UC/FUP 입력, 브랜치와 금지 범위를 포함한다.
- 각 서브에이전트 호출에는 TASK 표의 `Model`과 `Reasoning Level`을 명시한다. 모델을 상속에 맡기거나 실행 중 임의로 낮추지 않는다.
- 의존성이 없고 파일·외부 상태를 공유하지 않는 TASK만 병렬 실행한다. TASK-001/002/003처럼 독립적인 준비 작업은 함께 실행할 수 있다. 같은 schema·브랜치·실험 데이터·생성 파일을 수정하는 TASK는 순차 실행하거나 격리된 worktree를 사용한다.
- 서브에이전트는 자신의 TASK 산출물과 검증 결과를 반환한다. 메인 에이전트가 diff, 요구사항 추적, 테스트, 보안 경계와 다음 TASK의 준비 상태를 직접 확인한 뒤 통합한다.
- `Blocked`와 아직 gate를 통과하지 않은 `Conditional` TASK는 스폰하지 않는다. 필요한 FUP 입력이나 Go/No-Go 결정이 확보되면 문서 상태를 먼저 갱신한다.
- 서브에이전트는 커밋·push·PR·병합을 수행하지 않는다. 통합 검증이 끝난 뒤 메인 에이전트가 사용자 승인 범위에서 Git 작업을 처리한다.

## 실험 프로토콜

### 조건과 통제

| 조건 | 구성 | 목적 |
|---|---|---|
| A | 기존 grep/search/read | baseline |
| B | A + 필요한 범위만 읽는 고정 지침 | 지시문 효과 분리 |
| C | 같은 하네스 + 사용 가능한 C# LSP | 표준 semantic 탐색 |
| D | 같은 하네스 + Serena | 기존 agent 도구 |
| E | 같은 하네스 + CV, 구현·정확성 gate 이후 | CV 추가 가치 |

repo/commit, task prompt, 모델 version/reasoning, 원본 접근 권한, timeout을 고정한다. 조건별 도구 정의·지침의 차이와 token 비용을 기록한다. 같은 작업의 조건 순서를 seed로 무작위화 또는 균형 교차하며 깨끗한 worktree·새 agent context를 사용한다. 실패·timeout·CV 미사용 실행도 전체 결과에서 제외하지 않는다. 설치 불가 비교군은 unavailable로 표시하고 0 비용/0 결과로 대체하지 않는다.

### Corpus와 정답

small/medium/large를 LOC뿐 아니라 file/symbol/project/reference 수로 정의한다. 초기 navigation·understanding·bug fix·feature change, 후속 impact/refactoring/review·stale/missing을 capability에 맞춰 평가한다. 공개 repo는 commit·license·대표성·학습 노출 가능성을 기록하고 UE5/개인 프로젝트를 대표한다고 단정하지 않는다.

합성 source fixture에서 선언·호출·동적 후보의 정답을 직접 검토한다. static과 dynamic ground truth를 분리하고 CV 또는 동일 query의 출력을 자체 정답으로 사용하지 않는다. tuning task와 held-out task를 분리하며 판정 후 데이터를 변경하면 새 실험으로 기록한다.

### Pilot와 본 실험의 구분

UC-007에 따라 TASK-001이 프로토콜을 고정하고 TASK-004에서 합성 fixture 전용 smoke를 실행했다. A/B는 품질을 통과했고 C/D는 unavailable이었으며 token·비용은 미측정이다. 실제 승인 로그·실제 모델 token/비용 평가는 FUP-001의 미완 범위이며 합성 결과로 대체하지 않는다.

본 실험 기준은 FUP-002와 ADR 001에 동결했다: 품질 저하 0, stale/조용한 partial Critical 0, task·조건별 최소 3회, seed `20260920`, stronger available baseline 대비 source bytes 또는 실제 input token 20% 이상 감소, cold/warm/update 60/2/5초, peak working set 1 GiB, 외부 유료비용 0, 전체 30분이다. token이 미측정이면 token 이득은 `inconclusive`다.

### 측정과 판정

- cold는 index/worker startup 포함, warm은 실제 구축 후 탐색(준비 비용 별도), long은 여러 task/edit·update를 누적 측정한다.
- input/output·cache read/write token, 최대 context, source bytes/lines, 도구 호출/실제 CV 사용률, fallback/repair, build/update/wall time, peak memory를 기록한다.
- provider token 분류의 중복 여부를 확인하고 측정일 가격표로 비용을 계산한다. cache token을 input에 이중 합산하지 않는다. usage가 없으면 추정/미측정이며 line 수를 실제 token으로 부르지 않는다.
- build/test·정답 위치·독립 리뷰로 성공과 defect recall/false positive를 판정한다. 모델 자기평가는 주 기준이 아니다.
- task별 paired 차이, 중앙값/p95/분산, 품질 차이의 불확실성 구간을 보고한다. 적은 반복 pilot이 충분한 통계 검증이라는 주장을 하지 않는다.
- TokenSaving = (A_input - E_input) / A_input. baseline 0이면 정의하지 않는다.
- ReferenceRecall = 정답과 일치하는 참조 수 / 정답 참조 수. 정답 0건을 100%로 채우지 않는다.
- ResolveAccuracy = 정확히 resolve한 범위 / 시도 수. unsupported·명시 실패도 별도 집계한다.
- 승인된 품질 비열등 기준과 최소 효율 차이를 함께 판단한다. E는 A뿐 아니라 사전 규칙으로 정한 강한 비교군 B/C/D와 비교한다.
- break-even은 같은 sequence의 누적 비용 차이가 이득으로 바뀌어 유지되는 첫 task다. 관측되지 않으면 not reached다.
- token·초·bytes를 임의로 더한 단일 점수로 손해를 숨기지 않는다. stale 원문 오반환·조용한 부분 결과는 평균 절감으로 상쇄하지 않는다.
- 원문/시크릿/전체 세션 로그는 로컬 비공개로 관리한다. 보고·commit에는 승인된 비식별 집계만 포함한다. 이번 prepare에서 실제 로그 분석이나 유료 실험은 실행하지 않았다.

## TASK-001 — pilot 프로토콜과 로그 집계 계약

### Goal
실제 로그를 읽기 전에 비교 조건·측정 분모·집계 형식을 정의한다.

### Dependencies
없음

### Scope
Read/Grep 비중·전체 읽기율·재읽기율·주석 비율, cache-aware 비용, pilot 범위·예산 후보와 비식별화 규칙을 작성한다. 원문 복원으로 표시하지 않는다.

### Files
`benchmarks/protocol.md` / `benchmarks/result.schema.json` / `benchmarks/log-analysis.md`

### Validation
합성 이벤트로 분모·중복 token·누락 필드·실패 실행 집계를 손으로 검산한다.

| 배정 | 값 |
|---|---|
| Agent | Codex subagent |
| Model | gpt-5.6-sol |
| Reasoning Level | Medium |
| Reason | 측정 계약과 집계의 오류를 검토하면서 작은 산출물로 나눈다. |

### Blocked By
없음

### Status
Complete — 2026-09-20

## TASK-002 — 독립 정답 fixture와 corpus 후보

### Goal
CV 결과에 의존하지 않는 C# 선언·참조·diff 정답을 준비한다.

### Dependencies
없음; TASK-001과 병렬 가능

### Scope
overload/generic/partial/private/linked file·동적 참조·CRLF/emoji, VCS/session dirty-start·삭제 snapshot fixture를 만든다. 공개 repo는 후보와 commit/license만 조사한다.

### Files
`tests/fixtures/csharp/` / `benchmarks/tasks/` / `benchmarks/corpus-candidates.md`

### Validation
source와 정답 위치를 독립 대조한다. CV 또는 같은 Roslyn query를 정답 생성기로 사용하지 않는다.

| 배정 | 값 |
|---|---|
| Agent | Codex subagent |
| Model | gpt-5.6-terra |
| Reasoning Level | Medium |
| Reason | 기준이 명확한 테스트 자료 제작이다. |

### Blocked By
없음; 실제 corpus 실행은 FUP-001

### Status
Complete — 2026-09-20

## TASK-003 — 기존 도구 capability 조사

### Goal
LSP·Serena·범위 읽기와 CV의 중복·차별 가설을 확인한다.

### Dependencies
없음

### Scope
공식 문서의 지원 기능·runtime·버전·호출 방법·제약을 정리한다. 문서에 기능이 있다는 사실을 실측 성능으로 바꾸지 않는다.

### Files
`benchmarks/tool-matrix.md`

### Validation
각 claim에 공식 출처·확인일·버전/미확인 표시가 있는지 검토한다.

| 배정 | 값 |
|---|---|
| Agent | Codex subagent |
| Model | gpt-5.6-luna |
| Reasoning Level | Low |
| Reason | 한정된 기능 목록과 출처 수집이다. |

### Blocked By
없음; 실제 설치·모델 실행 제외

### Status
Complete — 2026-09-20

## TASK-004 — 승인된 데이터로 pilot 실행

### Goal
현재 탐색 비용과 기존 도구 효과의 분포를 측정한다.

### Dependencies
TASK-001, TASK-002, TASK-003

### Scope
지정 로그 경로·기간만 집계하고 고정 corpus에서 A/B/C/D pilot을 수행한다. 모델·권한·시작 상태·조건 순서를 통제한다. 예산 초과 시 중단하며 원시 로그를 공개하지 않는다.

### Files
`benchmarks/runs/ (로컬 비공개)` / `benchmarks/results/pilot-report.md` / `비식별 run manifest`

### Validation
원시 usage와 집계 대조, 실패 포함 denominator, 실제 도구 사용률·비용 상한을 확인한다. pilot을 확정적 가치 증명이라고 보고하지 않는다.

| 배정 | 값 |
|---|---|
| Agent | Codex subagent |
| Model | gpt-5.6-sol |
| Reasoning Level | High |
| Reason | 실험 조건·개인정보·비용 집계를 함께 통제한다. |

### Blocked By
없음 — 합성 smoke 완료; 실제 승인 로그·모델 token/비용 평가는 FUP-001 미완
### Status
Complete — 2026-09-20 (합성 smoke 범위)


## TASK-005 — Go/No-Go와 본 실험 기준 확정

### Goal
pilot 결과를 사용자와 검토해 엔진/얇은 연동/중단 경로를 선택한다.

### Dependencies
TASK-004

### Scope
불확실성·유지보수·기존 도구 우위를 검토하고 최종 품질/효율 기준과 본 실험 예산을 기록한다. 엔진 선택 시 승인된 .NET 설계를 적용한다. 누락 원문 요구는 끼워 넣지 않는다.

### Files
`docs/decisions/001-product-path.md` / `docs/prepare/design.md` / `docs/prepare/architecture.md` / `docs/prepare/user-confirm.md` / `docs/prepare/plan.md`

### Validation
사용자 선택·근거·날짜를 기록하고 기준을 본 실험 전에 동결한다. 기존 도구 경로에서도 Web UI·필수 Perforce 요구의 처리 방법이 남아 있어야 한다.

| 배정 | 값 |
|---|---|
| Agent | Codex subagent |
| Model | gpt-5.6-sol |
| Reasoning Level | High |
| Reason | 제품 가치와 실험 불확실성을 통합하는 판단이다. |

### Blocked By
없음 — FUP-002/003 완료
### Status
Complete — 2026-09-20


## TASK-006 — .NET solution과 trust 경계

### Goal
Windows/C# Core·CLI·adapter의 빌드 가능한 기반을 만든다.

### Dependencies
TASK-005에서 독립 엔진 선택

### Scope
지원 SDK·package 버전을 고정하고 Core/CSharp/CLI/test 프로젝트와 CI를 만든다. .code-virtualize와 raw benchmark 데이터를 ignore한다. syntax-only 기본과 명시 trust 경계를 둔다.

### Files
`CodeVirtualize.sln` / `global.json` / `Directory.Build.props` / `.gitignore` / `src/` / `tests/` / `.github/workflows/ci.yml`

### Validation
restore/build/test와 CLI help를 실행한다. trust 없는 프로젝트의 generator·build sentinel이 실행되지 않아야 한다.

| 배정 | 값 |
|---|---|
| Agent | Codex subagent |
| Model | gpt-5.6-terra |
| Reasoning Level | Medium |
| Reason | 확정한 단일 runtime 구조의 정형 구현이다. |

### Blocked By
없음 — FUP-003 완료
### Status
Complete — 2026-09-20


## TASK-007 — .cv schema와 CLI 응답 계약

### Goal
ID·span·coverage·오류·paging·baseline 의미를 고정한다.

### Dependencies
TASK-006, TASK-002

### Scope
Manifest/Symbol/Declaration/Reference/Diff/Response schema, UTF-16 span·1-based line, partial/generic ID, generation cursor와 source 반환 예산을 정의한다.

### Files
`schemas/` / `src/CodeVirtualize.Core/Contracts/` / `tests/Core.Tests/Contracts/` / `docs/contracts/cli.md`

### Validation
round-trip·unknown schema 거부, not_found/partial/truncated/error 필드, cursor 세대 불일치, 두 baseline 직렬화를 검증한다.

| 배정 | 값 |
|---|---|
| Agent | Codex subagent |
| Model | gpt-5.6-sol |
| Reasoning Level | High |
| Reason | 모든 후속 작업에 영향을 미치는 계약 정합성을 결정한다. |

### Blocked By
없음; 선행 제품 경로 gate 적용

### Status
Complete — 2026-09-20

## TASK-008 — Immutable generation store

### Goal
부분 쓰기를 노출하지 않는 영속 store를 구현한다.

### Dependencies
TASK-007

### Scope
JSON manifest/JSONL shards, digest·schema·크기 검증, 임시 generation publish, reader pin·writer lock, semantic config fingerprint를 구현한다. 자동 파괴적 GC는 비활성 상태다.

### Files
`src/CodeVirtualize.Core/Storage/` / `tests/Core.Tests/Storage/` / `tests/Integration.Tests/Storage/`

### Validation
손상 shard·partial write·publish crash·disk full에서 이전 generation 유지 또는 명시 오류를 확인한다.

| 배정 | 값 |
|---|---|
| Agent | Codex subagent |
| Model | gpt-5.6-sol |
| Reasoning Level | High |
| Reason | 원자적 발행·복구의 오류는 데이터 정확성에 직접 영향을 준다. |

### Blocked By
없음; 자동 GC 수치는 FUP-004로 별도

### Status
Complete — 2026-09-20

## TASK-009 — C# 구축·검색

### Goal
cv-build/cv-find로 L0/L1 전체 접근성의 선언을 탐색한다.

### Dependencies
TASK-008

### Scope
Roslyn syntax/semantic 경로, partial 선언 병합, overload/generic ID, inventory·project coverage·제외 구성, 결정적 정렬·filter·paging을 구현한다.

### Files
`src/CodeVirtualize.CSharp/` / `src/CodeVirtualize.Core/Search/` / `src/CodeVirtualize.Cli/Commands/` / `tests/CSharp.Tests/`

### Validation
독립 fixture의 선언·위치·동명 후보·새 파일·빈 workspace·project load 실패를 검증한다. syntax-only 결과에 semantic 완전성을 표시하지 않는다.

| 배정 | 값 |
|---|---|
| Agent | Codex subagent |
| Model | gpt-5.6-sol |
| Reasoning Level | High |
| Reason | 파서·binding과 coverage를 함께 다루는 구현이다. |

### Blocked By
없음; workspace semantic load는 명시 trust 적용

### Status
Complete — 2026-09-20

## TASK-010 — 원문 resolve·validate·터미널 inspect

### Goal
동일 bytes를 검증해 원문·상태를 정확히 반환한다.

### Dependencies
TASK-009

### Scope
cv-resolve/cv-validate/cv-inspect text/JSON, header/body/context 선택, stdout/stderr·exit code, stale·ambiguity·budget 오류를 구현한다.

### Files
`src/CodeVirtualize.Core/Resolution/` / `src/CodeVirtualize.Cli/Commands/` / `tests/Core.Tests/Resolution/` / `tests/Cli.Tests/`

### Validation
CRLF/LF/BOM·한글/emoji·same mtime/size 변경·오래된 span에서 정확한 원문 또는 명시 실패를 확인한다. pipe에서도 상태 의미를 유지한다.

| 배정 | 값 |
|---|---|
| Agent | Codex subagent |
| Model | gpt-5.6-sol |
| Reasoning Level | Medium |
| Reason | 계약이 정해진 원문 추출·CLI 표시 작업이다. |

### Blocked By
없음; Web UI 선택과 독립

### Status
Complete — 2026-09-20

## TASK-011 — 증분·복구·동시 세션과 시작 snapshot

### Goal
외부 편집과 병렬 사용에서 freshness와 session base를 보존한다.

### Dependencies
TASK-008, TASK-010

### Scope
cv-update·신규/삭제/rename reconciliation, semantic 종속 invalidation, bounded fallback/repair, writer/reader lease를 구현한다. session 시작 source bytes·inventory·config를 immutable 저장하고 pin한다.

### Files
`src/CodeVirtualize.Core/Lifecycle/` / `src/CodeVirtualize.Core/Fallback/` / `src/CodeVirtualize.Core/Snapshots/` / `tests/Integration.Tests/Lifecycle/`

### Validation
두 session 동시 update/resolve·hook 누락·branch switch·crash와 full/incremental 결과를 비교한다. 시작 dirty 파일이 삭제돼도 base bytes를 복원하고 다른 session을 지우지 않는다.

| 배정 | 값 |
|---|---|
| Agent | Codex subagent |
| Model | gpt-5.6-sol |
| Reasoning Level | High |
| Reason | 동시성·semantic invalidation·원문 보존의 복합 구현이다. |

### Blocked By
없음; 자동 GC는 TASK-021

### Status
Complete — 2026-09-20

## TASK-012 — 장애 주입·보안·정확성 검증

### Goal
최적화가 원문 정확성과 coverage를 훼손하지 않는지 검증한다.

### Dependencies
TASK-011

### Scope
entry/range 손상·update 누락·rename·크래시·timeout·취소·경계 이탈/junction·명령 인자·secret log·untrusted generator 사례를 검증한다.

### Files
`tests/Integration.Tests/FailureInjection/` / `tests/Integration.Tests/Security/` / `docs/verification/poc-gate.md`

### Validation
JSON 결과를 실제 source/독립 정답과 대조한다. stale source 오반환·조용한 coverage 누락은 해결 또는 사용자 수용 전 통과로 기록하지 않는다.

| 배정 | 값 |
|---|---|
| Agent | Codex subagent |
| Model | gpt-5.6-sol |
| Reasoning Level | High |
| Reason | 작성자의 성공 경로와 독립적으로 안전성·정확성 가정을 검토한다. |

### Blocked By
없음; Critical/High 미해결 결함은 다음 단계 차단

### Status
Complete — 2026-09-20 (PoC gate PASS)

## TASK-013 — 주석과 영향 후보

### Goal
주석 lazy resolve와 불완전성을 명시하는 cv-impact를 제공한다.

### Dependencies
TASK-012 통과

### Scope
remark 위치·hash·원문, 정적 참조·caller expansion, lexical 후보 provenance, depth/result budget, reflection/DI/XAML/generated 한계를 구현한다.

### Files
`src/CodeVirtualize.Core/Remarks/` / `src/CodeVirtualize.Core/Impact/` / `src/CodeVirtualize.CSharp/References/` / `tests/CSharp.Tests/References/`

### Validation
독립 정답에서 recall을 계산하며 0건·partial·truncated를 구분한다. 주석 수정 후 이전 span이나 텍스트를 반환하지 않는다.

| 배정 | 값 |
|---|---|
| Agent | Codex subagent |
| Model | gpt-5.6-sol |
| Reasoning Level | High |
| Reason | 참조 누락과 잘못된 완전성 주장을 막는 semantic 구현이다. |

### Blocked By
없음; 측정 예산은 UC-007 후속 조건 적용

### Status
Complete — 2026-09-20

## TASK-014 — VCS/Session 심볼 diff

### Goal
Git revision과 세션 시작 snapshot 두 모드의 diff를 구현한다.

### Dependencies
TASK-011, TASK-012; 영향 연결은 TASK-013

### Scope
baseline-kind를 명시하고 added/removed/signature/body/remark와 textual hunk를 연결한다. base source를 read-only로 조회하며 rename 불확실성은 delete/add 또는 후보로 표시한다.

### Files
`src/CodeVirtualize.Core/Diff/` / `src/CodeVirtualize.Core/Vcs/` / `tests/Integration.Tests/Diff/`

### Validation
dirty-start 변경은 VCS에 포함·Session에서 제외됨을 검증한다. 삭제 symbol base 복원·SESSION_BASE_MISSING·BASE_REQUIRED·모드 전환 stale 응답 방지를 검증한다.

| 배정 | 값 |
|---|---|
| Agent | Codex subagent |
| Model | gpt-5.6-sol |
| Reasoning Level | High |
| Reason | revision 의미와 identity 변화·원문 증거의 정확성이 핵심이다. |

### Blocked By
없음; Perforce는 TASK-019

### Status
Complete — 2026-09-20

## TASK-015 — Claude MCP·lifecycle 연동

### Goal
CLI 검증 후 최소 도구와 얇은 훅으로 엔진을 연결한다.

### Dependencies
TASK-010, TASK-011, TASK-012; impact는 TASK-013

### Scope
cv_find/cv_get 및 구현된 cv_impact를 stdio로 노출하고 세션 시작/종료·변경 힌트·timeout·기존 탐색 fallback을 연결한다. 설정 병합·dry-run·uninstall을 준비한다.

### Files
`src/CodeVirtualize.Mcp/` / `integrations/claude/` / `tests/Integration.Tests/AgentAdapter/` / `docs/integrations/claude.md`

### Validation
고정 client 버전에서 실제 tool 결과·timeout·worker crash·기존 설정 보존·복원을 검증한다. 훅을 꺼도 freshness 확인이 동작해야 한다.

| 배정 | 값 |
|---|---|
| Agent | Codex subagent |
| Model | gpt-5.6-sol |
| Reasoning Level | Medium |
| Reason | 확정 Core 계약을 외부 client와 연결하는 작업이다. |

### Blocked By
실제 client 설치 범위·UC-007 실행 예산 지정

### Status
Complete — 2026-09-20

## TASK-016 — CV 포함 본 실험·가치 재평가

### Goal
품질·구축·갱신·복구 비용을 포함한 순효율을 평가한다.

### Dependencies
TASK-004, TASK-012, TASK-013, TASK-014, TASK-015

### Scope
A/B/C/D에 E(CV)를 추가하고 cold/warm/long·규모별 실험을 수행한다. actual CV 사용률·cache-aware cost·peak context·break-even·review recall/false positive를 보고한다.

### Files
`benchmarks/results/cv-report.md` / `비식별 집계 JSON/run manifest` / `docs/decisions/002-validation-outcome.md`

### Validation
사전 기준·고정 seed·실패 denominator·독립 품질 판정을 점검한다. 미지원 Perforce/UE5 성과를 추론하지 않으며 불확실하면 inconclusive로 보고한다.

| 배정 | 값 |
|---|---|
| Agent | Codex subagent |
| Model | gpt-5.6-sol |
| Reasoning Level | High |
| Reason | 정확도·비용·통계적 불확실성을 통합해 판단한다. |

### Blocked By
없음 — TASK-012~015 완료; 합성/로컬 corpus로 실행하고 실제 로그·token 미측정은 결과에 명시
### Status
Complete — 2026-09-20 (No-Go; token efficiency inconclusive)


## TASK-017 — 범위 전환·진행 또는 종료 기록

### Goal
TASK-016 No-Go에 맞춰 다음 작업을 완결된 계획으로 정리한다.

### Dependencies
TASK-016 결과, ADR 001/002, 사용자 확정 결정

### Scope
TASK-016의 최종 No-Go를 적용한다. 완료된 TASK-006~016과 재현 가능한 artifact는 기술 prototype으로 보존하고 신규 독립 엔진 제품화는 중단한다. 얇은 MCP/Claude 연동이 Web UI와 Perforce 계약을 충족하지 않는 gap을 명시하고, 대표 corpus·실제 model token·source-byte 계약 개선·새 ADR 및 FUP-006/007을 재개 조건으로 고정한다. 잘린 원문과 retention 차단, session/base source 보존을 유지한다.

### Files
`docs/decisions/003-next-step.md` / `docs/prepare/architecture.md` / `docs/prepare/plan.md`

### Validation
선택과 남은 작업, 링크와 수치가 일치하고 11개 User Decision 원문이 바뀌지 않았는지 검토한다. source bytes와 token을 분리하고 데이터 삭제·게시·설치를 수행하지 않는다.

| 배정 | 값 |
|---|---|
| Agent | Codex subagent |
| Model | gpt-5.6-sol |
| Reasoning Level | Medium |
| Reason | 확정된 실험 근거를 terminal 계획으로 정리하는 문서 작업이다. |

### Blocked By
없음 — TASK-016 No-Go와 ADR 003 terminal disposition 반영 완료

### Status
Complete — 2026-09-20 (No-Go: prototype 보존, 제품화 자동 진행 중단)

## TASK-018 — 선택한 로컬 Web UI 제품 구현

### Goal
선택된 시안을 실제 `.cv` 조회 API에 연결한다.

### Dependencies
TASK-010, TASK-013, TASK-014; TASK-017의 제품화 재개 결정; FUP-006 Confirmed(Sample 1 + Blazor Server)

### Scope
.NET loopback host + Blazor Server(InteractiveServer)/ASP.NET Core frontend(FUP-006 확정: Sample 1 3열 심볼 탐색기), 검색→source/remark/관계/diff, freshness/coverage·VCS/Session 표시, paging·취소·오래된 응답 폐기, 키보드·mobile·theme를 구현한다. 조회 로직과 원문은 loopback 호스트 프로세스에 두고 브라우저로 내리지 않는다. 로컬 접근 인증·Host/Origin·CSP·no-store를 적용한다. 현재 얇은 MCP/Claude 연동에는 이 사람 중심 화면과 로컬 Web API/보안 경계가 없어 UC-011을 충족하지 않는다. 계약과 확정된 UI 방향은 보존하지만 No-Go 상태에서 구현하지 않는다.

### Files
`src/CodeVirtualize.Web/` / `tests/Integration.Tests/Web/` / `docs/ui/` / `선택 frontend build 설정`

### Validation
CLI와 같은 query/generation의 결과가 일치해야 한다. 실제 source escape·remote Origin/Host·XSS·junction·session 종료·예산을 검증한다. static 시안 통과만으로 제품 API 통과를 주장하지 않는다.

| 배정 | 값 |
|---|---|
| Agent | Codex subagent |
| Model | gpt-5.6-sol |
| Reasoning Level | High |
| Reason | 선택된 UX와 민감한 로컬 source 접근 경계를 함께 구현한다. |

### Blocked By
TASK-016 No-Go; 대표 corpus·실제 model token과 NAV·DIFF source-byte 계약 개선(protocol revision)을 검증한 새 ADR(005 동결 → 006 결과)

### Status
Blocked — 제품화 자동 진행 중단; FUP-006 확정(Sample 1 + Blazor Server)으로 UI 방향은 정해졌으나 공통 재개 조건(ADR 006) 충족 전 실행하지 않음

## TASK-019 — 필수 Perforce adapter

### Goal
필수 후속 Perforce source/baseline/diff를 제공한다.

### Dependencies
TASK-012, TASK-014; TASK-017의 제품화 재개 결정; FUP-007 Confirmed/Environment Unavailable(계약 확정, 실제 p4 환경 미제공); baseline provider 추상화 선행

### Scope
submitted/shelved/pending별 base/target, server/client/depot mapping, have/head 차이, rename/delete/binary·권한/오프라인을 구현한다. 착수 전 `src/CodeVirtualize.Core/Vcs/GitBaselineProvider.cs`(현재 인터페이스 없는 concrete sealed class)에서 baseline provider 추상화를 추출하는 작업이 선행돼야 한다. 실제 server/client/CL이 제공되지 않는 동안은 FUP-007이 확정한 합성 CLI 응답 fixture 범위까지만 구현·검증하고 실환경 대조는 보류한다. 현재 얇은 연동은 CL별 bytes/baseline과 mapping 의미를 제공하지 않아 UC-002를 충족하지 않는다. 계약은 보존하지만 No-Go 상태에서 구현하지 않는다.

### Files
`src/CodeVirtualize.Perforce/` / `tests/Integration.Tests/Perforce/` / `docs/integrations/perforce.md`

### Validation
file revision과 반환 bytes·diff를 독립 대조한다. sync/submit/revert/shelve를 호출하지 않고 ticket·원문을 로그에 누출하지 않는다. CL 유형별 미지원은 명시한다.

| 배정 | 값 |
|---|---|
| Agent | Codex subagent |
| Model | gpt-5.6-sol |
| Reasoning Level | High |
| Reason | VCS 의미·workspace mapping·접근 경계를 다루는 필수 통합이다. |

### Blocked By
TASK-016 No-Go; 대표 corpus·실제 model token과 NAV·DIFF source-byte 계약 개선(protocol revision)을 검증한 새 ADR(005 동결 → 006 결과); 실제 p4 환경 제공

### Status
Blocked — 제품화 자동 진행 중단; FUP-007로 계약은 확정했으나 실제 p4 환경 미제공과 공통 재개 조건(ADR 006) 충족 전 실행하지 않음

## TASK-020 — 잘린 원문 복구와 요구 보완

### Goal
작성자 원문이 확보되면 누락된 요구를 정확히 복구한다.

### Dependencies
현재 원안과 로컬 위키도 잘린 사실 확인 완료

### Scope
출처·revision·보완 내용을 대조하고 기존 확정 결정과 충돌을 식별한다. 새 요구를 design/architecture/plan에 반영한다. 정보가 없으면 임의 복원하지 않는다.

### Files
`docs/ideas/ (복구된 원문 제공 시)` / `docs/prepare/design.md` / `docs/prepare/architecture.md` / `docs/prepare/user-confirm.md` / `docs/prepare/plan.md`

### Validation
실제 보완 출처와 변경 내용이 대응하고, AI 추정을 원문으로 기록하지 않았는지 확인한다.

| 배정 | 값 |
|---|---|
| Agent | Codex subagent |
| Model | gpt-5.6-sol |
| Reasoning Level | Medium |
| Reason | 원문 증거와 확정 요구의 충돌을 제한된 문서 범위에서 검토한다. |

### Blocked By
FUP-005 Obsolete — 복구 원문 요구 자체가 폐기됨

### Status
종결 (Won't do) — FUP-005로 복구 요구를 폐기했고 원문을 추정해 복구하지 않음

## TASK-021 — 실측 기반 cache 한도·GC 확정

### Goal
사용자 결정대로 실측 후 retention/용량/GC 정책을 정한다.

### Dependencies
TASK-011 측정 자료와 TASK-016 No-Go; FUP-004 정책 확정(모드 선택은 완료); GC 구현과 자동 삭제 활성화는 실측 retention·용량 수치의 승인이 필요

### Scope
크기·session 수·warm 이득·disk 압박을 기록해 retention·용량 수치 후보를 제시한다. 수치가 승인되면 FUP-004 확정에 따라 `age`/`capacity`/`hybrid` 모드를 config로 선택하는 GC와 dry-run을 구현하며 기본값은 `gc.enabled = false`다. 수치 승인 전에는 삭제 없는 측정과 정책 제안까지만 진행한다. 승인 후만 자동 GC를 활성화하고 active reader/session/base snapshot을 보호한다. 승인 전 terminal disposition은 자동 삭제를 수행하지 않는 no-delete이며 prototype 보존 데이터를 임의 정리하지 않는다.

### Files
`docs/decisions/004-cache-policy.md` / `src/CodeVirtualize.Core/Storage/` / `tests/Integration.Tests/Storage/`

### Validation
용량·나이 경계·동시 reader·crash lease·pinned base·config 보존을 검증한다. 승인 수치가 없으면 자동 삭제 기능을 활성화하지 않는다.

| 배정 | 값 |
|---|---|
| Agent | Codex subagent |
| Model | gpt-5.6-sol |
| Reasoning Level | High |
| Reason | 실측 정책과 삭제·동시성 안전성의 결합이다. |

### Blocked By
GC 구현과 자동 삭제 활성화는 FUP-004의 실측 retention·용량 수치와 명시적 승인이 필요; 삭제 없는 측정·정책 제안은 착수 가능

### Status
측정 착수 가능 — 삭제 없는 측정·정책 제안까지 진행; 자동 GC는 승인 전 비활성/no-delete이며 active reader·session·base snapshot 보존
## 요구사항과 결정 추적

| 요구 | 작업 |
|---|---|
| 가치 검증 / UC-001, UC-007 | TASK-001~005, TASK-016~017 |
| FR-01 scope·trust / UC-002, UC-008 | TASK-006, TASK-009, TASK-012 |
| FR-02 심볼 / UC-006 | TASK-007, TASK-009 |
| FR-03 resolve | TASK-010, TASK-012 |
| FR-04 freshness | TASK-010~012 |
| FR-05 fallback·repair | TASK-011~012 |
| FR-06 coverage | TASK-007, TASK-009, TASK-012~013 |
| FR-07 update | TASK-011~012 |
| FR-08 동시성 / UC-004 | TASK-008, TASK-011~012, TASK-021 |
| FR-09 inspect·metrics | TASK-001, TASK-010~011, TASK-015~016 |
| FR-10 impact | TASK-013, TASK-016 |
| FR-11 두 baseline / UC-005 | TASK-011, TASK-014, TASK-019 |
| FR-12 agent 연동 / UC-009 | TASK-015~016 |
| FR-13 remark | TASK-013 |
| FR-14 Web UI / UC-011 | 시안 3종 보존, FUP-006 Confirmed(Sample 1 + Blazor Server), TASK-018 Blocked — 공통 재개 조건(ADR 006) 필요 |
| FR-15 필수 Perforce / UC-002 조건 | 계약 보존, FUP-007 Confirmed/Environment Unavailable, TASK-019 Blocked — 공통 재개 조건(ADR 006)과 실제 p4 환경 필요 |
| UC-003 .NET | TASK-006 이후 |
| UC-010 원문 복구 요구 폐기 | TASK-020 종결 (Won't do) |

FUP 상태와 입력 내용은 [user-confirm.md](user-confirm.md)의 후속 입력 표가 기준이다. FUP-004는 GC 정책(모드 선택)을 확정했고 기본값 `gc.enabled = false`의 no-delete 상태에서 TASK-021의 삭제 없는 측정·정책 제안을 허용한다. FUP-005는 Obsolete로 복구 요구 자체를 폐기해 TASK-020을 종결(Won't do)로 만든다. FUP-006/007은 Confirmed지만 공통 제품화 재개 증거와 새 ADR(005 동결 → 006 결과)을 대신하지 않는다. 이미 확정된 11개 선택을 다시 Pending으로 되돌리지 않는다.

## Prepare 완료 검증

- 아이디어 3개 전체를 분석했고 원안의 잘림과 검토 의견을 구분했다.
- 사용자 인터뷰의 11개 User Decision 원문을 보존하고 design/architecture에 동기화했다.
- CLI+Web UI 범위에 맞는 조작 가능한 시안 3종과 실행 방법을 제공했다.
- [시안 검증 기록](samples/verification.md): 3종 기능 확인, desktop/mobile/dark 화면, JavaScript 오류 0, 페이지 가로 넘침 없음. 독립 reviewer의 경미 수정 2건 모두 resolved.
- 시안의 원문·hash·revision·관계·diff는 합성이며 실제 제품 분석 결과가 아니다.
- 이 plan은 기획·설계·결정·시안·검증 기록 이후 마지막에 작성했다.
- 모든 TASK는 목표·의존·범위·예상 파일·검증·Agent·Model·Reasoning Level·배정 이유·Blocked By를 갖는다.
- TASK-001~017을 완료했다. TASK-006~015 기술 prototype과 TASK-016 재현 실험을 보존하며, TASK-016은 source-byte gate 실패로 No-Go다.
- 실제 model token·비용과 C/D 비교는 미완이며 source-byte 결과로 대체하지 않는다.
- TASK-018·019는 구체적인 재개 조건이 있는 Blocked다. TASK-020은 FUP-005 Obsolete로 종결(Won't do)했고, TASK-021은 FUP-004 정책 확정으로 삭제 없는 측정·정책 제안까지 착수 가능하다. 자동 GC는 승인 전 비활성이고 데이터·session/base snapshot을 삭제하지 않는다.

현재 plan의 terminal disposition은 기술 prototype 보존과 제품화 자동 진행 중단이다. Blocked TASK는 새 근거·입력·결정을 문서화하기 전 스폰하거나 구현하지 않으며 미제공 입력을 성공으로 가장하지 않는다.