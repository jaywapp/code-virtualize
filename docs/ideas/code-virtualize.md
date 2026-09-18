# Code-Virtualize

> LLM에게 소스코드 전체를 읽히는 대신, 코드베이스를 탐색 가능한 가상 표현으로 변환하고 필요한 코드 조각만 지연 로드하는 **Code Context Virtualization Layer** 아이디어.

**상태:** Idea / 주말 Astra와 구체화 예정  
**목표:** Claude Code 기반 대규모 프로젝트 탐색 시 불필요한 파일 읽기와 토큰 소비를 줄이고, symbol 중심의 deterministic navigation을 제공한다.

## 1. 문제와 핵심 원칙

일반적인 코드 에이전트는 `검색 → 파일 Read → 추가 검색 → 관련 파일 Read → 주석/참조 탐색 → 작업`을 반복한다. Code-Virtualize는 이를 `질문 → Virtual Index 조회 → 관련 Symbol 식별 → 필요한 source/remark/reference만 resolve → 작업`으로 바꾼다.

```text
Codebase != Context
Virtual Index != Source of Truth
```

**소스코드는 Source of Truth이고 `.cv`는 현재 AI 세션의 탐색을 가속하는 disposable optimization/index layer다.**

## 2. `.cv` — Code-Virtualized Data

Code-Virtualize의 공식 데이터 확장자는 **`.cv`**다.

```text
.code-virtualize/
├─ config.json
├─ baseline/
│  ├─ manifest.cv
│  ├─ symbol.cv
│  ├─ remark.cv
│  └─ reference.cv
├─ current/
│  ├─ manifest.cv
│  ├─ symbol.cv
│  ├─ remark.cv
│  └─ reference.cv
└─ diff/
   └─ current.diff.cv
```

`manifest.cv`에는 session ID, 생성 시각, workspace/VCS 기준점, schema version 등을 둔다.

### Session Lifecycle

```text
SESSION START → Full .cv Build → VALID
                              ↓ source edit
                            DIRTY
                              ↓ changed-file incremental build
                            VALID
                              ↓
                         SESSION END
                              ↓
                           DISCARD
```

세션 시작마다 현재 source로 `.cv`를 재구축하고 세션 도중 파일 수정 시 변경 파일만 incremental re-index한다.

## 3. 기본 구조

```text
code-virtualize
├─ virtual-symbol
├─ virtual-remark
├─ virtual-reference
├─ virtual-diff
└─ resolver
```

향후 `virtual-dependency`, `virtual-history` 등의 계층도 검토한다.

## 4. Virtual-Symbol / Progressive Virtualization

프로젝트의 class/method/property/field/variable을 file + line range + signature 형태로 주소화한다.

```text
L0 Project    : public API / type
   ↓ resolve
L1 Type       : public / protected / private members
   ↓ resolve
L2 Method     : parameters / locals / local functions
   ↓ 필요 시
L3 Expression : lambda / capture / temporary dependency
```

| Symbol 범위 | 기본 생성 시점 | 저장 범위 |
|---|---|---|
| public/internal | Project indexing | Project Index / session |
| protected | Type indexing 또는 resolve | Type Index |
| private member | Type indexing 또는 접근 시 | Type Index / 선택적 |
| parameter/local | Method resolve | 임시 |
| lambda/local function | Method resolve | 임시 |

```text
Materialization Priority
 = Visibility
 + Symbol Kind
 + Reference Count
 + Call/Dependency Distance
 + Current Task Relevance
```

Visibility는 materialization을 결정하는 signal 중 하나일 뿐이다.

## 5. Virtual-Remark / Reference

`remark.cv`는 comment/XML docs를 별도 보관하고 필요할 때만 lazy resolve한다. `reference.cv`는 symbol별 사용 위치를 관리하여 반복적인 grep/rg를 줄이고 변경 영향 분석의 기반으로 사용한다.

## 6. Self-Healing / Fallback

`.cv`는 source of truth가 아니다.

```text
.cv 조회
 ├─ HIT + VALID → fast path
 ├─ MISS ───────┐
 └─ INVALID ────┴→ source grep/search/read
                     ↓
                  symbol 발견
                     ↓
                  .cv repair
                     ↓
                  작업 계속
```

세션 시작 full build + edit 시 incremental update를 **1차 freshness 방어선**, validation/self-healing을 **2차 방어선**으로 둔다.

```text
USE → DETECT → FALLBACK → DISCOVER → REPAIR → REUSE
```

## 7. Virtual-Diff와 코드 리뷰

세션 시작 `CV₀`와 작업 후 `CV₁`을 비교해 textual diff와 별도의 semantic/symbol diff를 만든다.

```text
Perforce/Text Diff + Virtual Diff
              ↓
        Changed Symbols
              ↓
 Reference / Dependency Expansion
              ↓
          Review Scope
              ↓
필요한 실제 Source/Diff만 resolve
              ↓
          Review Agent
```

`.cv`는 review scope를 찾는 index이며 실제 source/diff가 최종 증거다.

```text
CL diff = textual change
CV diff = semantic/symbol change
```

## 8. Tool Architecture

CLI/tool prefix는 **`cv-*`**로 통일한다.

| 도구 | 역할 |
|---|---|
| `cv-build` | Source → `.cv` snapshot |
| `cv-find` | symbol 검색 |
| `cv-resolve` | symbol → 실제 source range |
| `cv-update` | changed-file incremental re-index |
| `cv-diff` | CV₀ ↔ CV₁ semantic diff |
| `cv-impact` | reference/dependency 영향 분석 |
| `cv-validate` | `.cv` ↔ source 검증 |
| `cv-inspect` | 사람이 보는 CV debugger |

Claude에게는 초기에는 `cv_find`, `cv_get`, `cv_impact` 정도만 노출하고 lifecycle 작업은 Harness가 자동 수행한다.

## 9. Workspace Integration

```text
workspace/
├─ CLAUDE.md
├─ .claude/
│  ├─ settings.json
│  ├─ skills/code-virtualize/SKILL.md
│  └─ hooks/
├─ .code-virtualize/
│  ├─ config.json
│  ├─ baseline/
│  ├─ current/
│  └─ diff/
└─ src/
```

`.claude/`는 AI Integration Layer, `.code-virtualize/`는 CV Engine runtime/data layer다. `.code-virtualize/`는 재생성 가능한 artifact이므로 VCS ignore 대상으로 본다.

## 10. Installation Model — Global + Workspace

```text
Code-Virtualize
├─ Engine
├─ Agent Integration
└─ Workspace Data (.cv)
```

전역 설치를 해도 `.cv` runtime data는 각 workspace에 존재한다.

```text
Built-in Default → Global Config → Workspace Config → Session Override
```

개념적 CLI:

```text
cv install --global
cv install --workspace
cv integrate claude --global
cv integrate claude --workspace
cv integrate codex --global
cv integrate codex --workspace
```

## 11. Distribution Architecture — CLI/Core + Agent Plugin

**Code-Virtualize 자체를 Claude Plugin으로 만들지 않는다.** 독립적인 AI Code Context Engine으로 만들고 Claude Plugin은 첫 번째 integration으로 둔다.

npm은 우선 배포/launcher 수단으로 검토한다.

```text
npm package / cv launcher
        │
        ├─ C# Adapter → Roslyn worker
        ├─ C++ Adapter → clang / tree-sitter
        └─ Generic Adapter → tree-sitter 등
```

Claude Plugin은 session lifecycle과 CV 호출 시점을 담당한다.

> **Code-Virtualize는 Claude Plugin이 아니다. 독립적인 AI Code Context Engine이고, Claude Plugin은 첫 번째 integration이다.**

## 12. Repository / Package 구조 초안

```text
code-virtualize/
├─ packages/
│  ├─ cli/
│  ├─ core/
│  ├─ adapters/
│  │  ├─ csharp/
│  │  └─ cpp/
│  └─ claude/
│     ├─ skills/
│     └─ hooks/
├─ docs/
└─ tests/
```

## 13. 개발 순서

```text
cv-build → cv-find → cv-resolve → cv-inspect → cv-update
         → virtual-reference / cv-impact → cv-diff → review integration
```

Phase 1은 C# + Roslyn/AST 기반 Symbol PoC, Phase 2는 incremental update, Phase 3은 Reference/Review 연동으로 진행한다.

## 14. Benchmark / Value Validation Strategy

Code-Virtualize 완성 후의 평가는 단순히 "토큰이 줄었다"가 아니라 **비용, 속도, 정확도, 작업 품질, 운영 오버헤드의 trade-off**를 함께 측정한다.

### 핵심 비교 원칙: A/B Paired Benchmark

동일한 repository snapshot, 동일한 task, 동일한 모델/설정에서 두 조건을 반복 실행한다.

```text
A — Baseline
Claude Code + 기존 grep/search/read

B — CV
Claude Code + Code-Virtualize

같은 Repo / Commit / Prompt / Model / Tool 권한
```

가능하면 task 순서 효과와 모델 변동을 줄이기 위해 여러 회 반복하고 A/B 실행 순서를 교차한다. 결과 평가는 모델의 자기평가가 아니라 build/test, 정답 위치, diff, 독립 review 등 외부 기준을 우선한다.

### Benchmark Task Set

단순 symbol lookup만 테스트하지 않고 실제 개발 작업을 난이도별로 구성한다.

| 유형 | 예시 | 주로 검증하는 것 |
|---|---|---|
| Navigation | 특정 method/class 위치와 역할 찾기 | `cv-find`, token/read 감소 |
| Understanding | 기능 동작 흐름 설명 | reference/context 정확도 |
| Bug Fix | 알려진 버그 수정 | 탐색 + 실제 작업 품질 |
| Feature Change | 기존 기능 변경/확장 | dependency 탐색 |
| Refactoring | method/class 구조 변경 | reference completeness |
| Impact Analysis | 변경 영향 파일/symbol 찾기 | `cv-impact` |
| Code Review | 준비된 CL/commit 리뷰 | `cv-diff`, review scope |
| Stale/Missing CV | 일부 `.cv` 고의 손상 | fallback/self-healing |

### Repository Scale Matrix

효과는 코드베이스 크기에 따라 달라질 가능성이 높으므로 최소 세 단계로 나눈다.

```text
Small   : CV overhead가 이득보다 큰지 확인
Medium  : 일반 프로젝트에서 break-even 확인
Large   : CV의 핵심 가치 검증
```

LOC뿐 아니라 file count, symbol count, reference graph 크기, language(C#/C++), mono/multi-project 여부를 함께 기록한다.

### Primary Metrics

**1. Input Token** — 작업 완료까지 모델에 전달된 총 input token. 가장 핵심적인 효율 지표다.

```text
Token Saving % = (BaselineToken - CVToken) / BaselineToken × 100
```

**2. Source Read Volume** — LLM이 실제로 읽은 source line/byte 수와 Read 호출 횟수. 토큰 변화의 원인을 설명하는 지표다.

**3. Tool Calls** — grep/search/read/find/resolve 등 탐색 관련 호출 수. CV가 탐색 왕복을 얼마나 줄였는지 본다.

**4. Time to Completion** — prompt 입력부터 완료까지 wall-clock time. `cv-build` startup 비용을 반드시 포함한 **cold session**과 이미 구축된 **warm session**을 분리한다.

**5. Task Success Rate** — build 성공, test 통과, 요구사항 충족 등 deterministic 기준으로 평가한다. 토큰을 줄이면서 성공률이 떨어지면 가치가 없다.

### CV 자체의 품질 지표

```text
Symbol Hit Rate
Reference Recall
Resolve Accuracy
Fallback Rate
Stale Detection Rate
Repair Success Rate
```

특히 `Fallback Rate`가 높다면 CV가 실제 탐색을 충분히 대체하지 못하고 있다는 신호다. 반대로 fallback을 무리하게 낮추다가 accuracy가 떨어져서도 안 된다.

### Cost Accounting

CV의 비용도 반드시 포함한다.

```text
Net Value
 = LLM 탐색 비용 절감
 + 작업시간 절감
 - cv-build 비용
 - incremental update 비용
 - storage/memory 비용
 - fallback/repair 비용
```

따라서 `cv-build`가 10초 걸리고 Claude 탐색을 2초 줄였다면 해당 작업에서는 손해다. 반대로 한 세션에서 여러 task를 수행한다면 build 비용을 session 전체에 amortize해서 계산한다.

### Cold / Warm / Long Session

세 가지 실행 모드를 별도로 측정한다.

```text
Cold Session
cv-build 비용 포함 → 첫 task까지 총비용

Warm Session
.cv 구축 완료 → 순수 탐색 효율

Long Session
여러 edit/task 반복 → incremental update와 build 비용 amortization
```

Code-Virtualize가 **몇 번째 task부터 break-even에 도달하는지**를 중요한 제품 지표로 본다.

### Review Benchmark

미리 정답이 알려진 bug/issue가 포함된 commit 또는 changelist를 준비한다.

```text
Baseline Review
Text Diff → Claude Review

CV Review
Text Diff + CV Diff + Impact → Claude Review
```

측정 항목:

- 실제 defect 발견률
- false positive 수
- review input token
- review source read volume
- review 시간
- 영향 symbol/file recall

CV Review가 더 적은 context로 동일하거나 더 높은 defect recall을 보이는지를 확인한다.

### Failure Injection Benchmark

Self-Healing 설계를 별도로 검증한다.

```text
1. symbol.cv entry 삭제
2. 잘못된 line range 삽입
3. source 수정 후 cv-update 누락
4. reference 일부 누락
5. 파일 rename/move
```

각 상황에서 `오류 감지 → source fallback → repair → 정상 작업 지속` 여부와 추가 token/time 비용을 기록한다.

### Telemetry / Benchmark Artifact

CV Engine 자체가 benchmark에 필요한 telemetry를 남기도록 설계한다.

```text
.code-virtualize/metrics/
└─ {session-id}.jsonl
```

예시 이벤트:

```json
{"event":"cv_find","durationMs":4,"hit":true,"results":1}
{"event":"cv_resolve","lines":37,"valid":true}
{"event":"fallback","reason":"stale_symbol"}
{"event":"cv_update","files":1,"durationMs":31}
```

LLM token/tool-call 정보와 결합하여 한 세션의 CV 효율을 자동 리포트할 수 있도록 한다.

### 최종 결과 표현

단일 점수보다 trade-off를 그대로 보여준다.

```text
Code-Virtualize Benchmark

Input Tokens       -42%
Source Lines Read  -61%
Search/Read Calls  -48%
Wall Time          -18%
Task Success       94% → 95%
CV Build            1.8s
Fallback Rate       3.1%
Break-even          2.4 tasks/session
```

위 수치는 예시이며 실제 목표치가 아니다.

### Go / No-Go 판단 원칙

구현 전에 임의의 높은 목표 수치를 확정하기보다 PoC에서 baseline distribution을 확보한다. 이후 다음 질문으로 가치를 판단한다.

1. **Quality Preservation:** CV 사용 시 task success/review quality가 baseline보다 실질적으로 악화되지 않는가?
2. **Net Efficiency:** build/update/fallback 비용까지 포함해 token 또는 시간에서 순이익이 있는가?
3. **Scale Benefit:** 프로젝트가 커질수록 이점이 증가하는가?
4. **Operational Stability:** stale/missing data 상황에서 자동 fallback/repair가 안정적인가?
5. **Repeatability:** 특정 task 한두 개가 아니라 여러 repository/task에서 효과가 반복되는가?

이 다섯 조건을 만족할 때 Code-Virtualize의 실질적인 도입 가치가 있다고 판단한다.

## 15. Astra와 구체화할 항목

- npm을 launcher/distribution으로 사용할지
- Core 구현 언어와 package 경계
- `.cv` serialization/schema
- Roslyn / LSP / compiler API / tree-sitter 비교
- C# / C++ / UE5 공통 schema
- Progressive Symbol Virtualization depth
- session start full-build latency
- changed-file incremental indexing
- Perforce baseline/freshness 검증
- `cv-impact` dependency expansion
- Claude Code MCP/tool/hook 노출 방식
- benchmark용 공개 C#/C++ repository 선정
- benchmark task/g