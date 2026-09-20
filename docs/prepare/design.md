# Design

## 문서 상태와 읽는 순서

- 작성일: 2026-09-19. 사용자 결정 반영: 2026-09-20.
- 분석 기준: `29374c6f969231d37937328005ee017c985f4d0f`의 `docs/ideas/` 전체 3개 문서.
- 상태: `b16df53`의 사용자 인터뷰 결정 반영본. 확정 선택과 후속 입력을 구분한다. 기술 세부 제안·UI 최종안·실험 수치는 아직 사용자 승인으로 간주하지 않는다.
- 읽는 순서: 본 문서 → [architecture.md](architecture.md) → [user-confirm.md](user-confirm.md) → [plan.md](plan.md).
- 이번 산출물은 기획·설계·검증 계획이다. 엔진, 플러그인, 벤치마크 실행 결과는 아직 없다.

## Overview

Code-Virtualize는 코드베이스를 심볼 중심으로 탐색하고 필요한 원문 범위만 읽도록 돕는 독립 Code Context Engine이다. `.cv`는 재생성할 수 있는 탐색용 인덱스이며, 소스코드가 유일한 진실의 원천이다. Claude 연동은 엔진을 호출하는 첫 번째 클라이언트다.

원안의 핵심 가설은 탐색 토큰·읽기량 감소다. 검토 의견은 기존 도구 대비 정확도와 차별성 검증을 먼저 요구한다. UC-001 A에 따라 **기존 탐색 방식 측정 → 기존 도구 비교 → Go/No-Go → C# PoC 또는 얇은 연동**이 확정됐다. 엔진 구축은 비교 결과 이후 선택한다. 첫 범위는 Windows + C# + Git이며 Perforce는 필수 후속 지원이다(UC-002).

## 근거와 결정의 구분

| 출처 | 읽은 범위 | 설계에 반영한 내용 | 취급 |
|---|---|---|---|
| [아이디어 목록](../ideas/README.md) | 전체 | 위키에서 이관한 시점과 문서 역할 | 출처 정보 |
| [아이디어 원안](../ideas/code-virtualize.md) | 1~15절 전체 | 원본 우선, `.cv`, CLI, 독립 엔진, C# PoC, 벤치마크 | 명시 원칙과 제안을 구분 |
| [Claude 검토 의견](../ideas/code-virtualize-feedback-claude.md) | 전체 | 기존 도구 비교, 부분 누락, 캐시·동시성, freshness, VCS 기준점 | 검토자의 권고이며 사용자 확정 아님 |

원안 15절은 실제로 `benchmark task/g`에서 끝난다. 잘린 내용을 복원하거나 위키의 이후 내용을 원안에 섞지 않았다. UC-010 B에 따라 원문 복구 전까지 누락 부분 관련 요구 확정을 보류한다. 로컬 위키 원본도 같은 지점에서 잘려 있음을 2026-09-20 확인했다.

| 주제 | 원안 | 검토 의견 | 현재 처리 |
|---|---|---|---|
| 가치·개발 순서 | 토큰 절감 엔진, C# Symbol PoC부터 | 측정 우선, 차별 기능 또는 얇은 연동 | UC-001 A: 측정 우선; UC-002 A 조건부: Perforce 필수 후속 |
| 캐시 | 세션 full build 후 폐기 | 영속 해시 캐시 + 세션 분리 | UC-004 A: 영속 JSON/JSONL cache와 세션 분리 |
| diff 기준점 | 세션 시작 snapshot | Git revision / Perforce CL 기준 | UC-005 C: VCS/Session 모두 지원 |
| 심볼 깊이 | visibility별 점진 생성, L0~L3 | private 포함, L2/L3 보류 | UC-006 A: L0/L1 모든 접근성 |
| runtime·배포 | npm launcher + 언어별 worker 검토 | PoC에 앞선 다중 runtime 지양 | UC-003 A: 엔진 선택 시 .NET |

## Problem

1. 반복 검색·전체 파일 읽기는 관련 없는 코드를 컨텍스트에 넣을 수 있다. 실제 비중과 비용은 미측정이다.
2. 같은 이름의 심볼, overload, partial 선언을 문자열 검색만으로 구분하기 어렵다.
3. 오래된 인덱스는 잘못된 원문을 반환할 수 있고, 일부 참조만 반환하는 인덱스는 검증에 성공해도 영향 범위를 누락할 수 있다.
4. 기존 LSP·Serena와 기능이 겹친다. 별도 엔진이 필요한지는 비교 실험 전에는 알 수 없다.
5. 세션마다 구축하는 비용, 수정 추적, 여러 에이전트의 동시 접근이 절감 효과를 상쇄할 수 있다.

## Goals

- G-01: 탐색 비용과 품질을 재현 가능하게 비교하여 개발 투자 여부를 결정한다.
- G-02: 지원하는 C# 빌드 구성에서 결정적인 심볼 검색과 원문 resolve를 제공한다.
- G-03: stale·missing·partial·unsupported를 정상적인 완전 결과와 구분한다.
- G-04: index 실패 시 원문 탐색을 지속하고 복구 결과를 검증한다.
- G-05: 후속 단계에서 변경 심볼·영향 후보를 textual diff와 함께 제시한다.

## Non-Goals

- 이번 준비 작업에서 제품 코드, 플러그인 설치, 실제 세션 로그 수집, 유료 모델 실험을 수행하지 않는다.
- 초기 PoC에 C++/UE5·Blueprint 완전 분석을 넣지 않는다. Perforce는 1차 Git 검증 이후 필수 후속 작업으로 제공한다(UC-002).
- 인덱스만 보고 안전한 rename·수정·리뷰 완료를 선언하지 않는다. 소스 편집은 에이전트의 기존 도구가 담당한다.
- LLM/embedding 기반 관련도 점수, L3 expression 인덱스, 클라우드 인덱스 동기화를 초기 필수 기능으로 넣지 않는다.
- IDE/데스크톱 전용 앱과 source/graph 편집 기능은 제외한다. 로컬 Web UI의 읽기 중심 `.cv`·관계·diff 검사는 확정 범위다(UC-011 A+B).

## Target Users

| 사용자 | 해야 하는 일 | 성공의 증거 |
|---|---|---|
| C# 프로젝트를 탐색하는 개발자·에이전트 | 정확한 선언과 필요한 문맥 찾기 | 올바른 선언·원문 범위와 빌드 구성 식별 |
| 엔진·하네스 유지관리자 | 인덱스 상태, 비용, 누락 원인 진단 | `cv-inspect`와 구조화된 로컬 이벤트 |
| 변경 검토자 | 수정 심볼과 영향 후보 파악 | 실제 diff와 증거를 확인한 결함 탐지 |
| 이후 UE5/Perforce 사용자 | 매크로·CL 기반 탐색과 리뷰 | 별도 corpus에서 확인할 후속 가설 |

## Core Concept

`질문 → 후보 심볼 → 식별자·상태 확인 → 검증된 원문 → 기존 편집/리뷰` 흐름을 제공한다. 인덱스 유효성(freshness), 분석 범위(coverage), 결과 잘림(truncation)은 별개다. 파일 해시가 맞는다고 전체 참조를 찾았다는 뜻은 아니다.

LSP는 definition/reference/workspace symbol 관련 표준 기능을 제공하고, Serena는 LSP 기반 심볼 검색과 참조 탐색을 제공한다. 따라서 같은 이름의 기능을 보유했다는 사실만으로 차별성을 주장하지 않는다. [LSP 명세](https://microsoft.github.io/language-server-protocol/specifications/lsp/3.17/specification/), [Serena 공식 저장소](https://github.com/oraios/serena).

CV의 추가 가치는 **검증된 증분 캐시, 명시적인 coverage 계약, revision 기준 심볼 변화와 리뷰 증거 연결**에서 검증할 후보 가설이다. 기존 도구가 이를 충분히 해결하면 얇은 연동만 만들거나 개발을 중단할 수 있다.

## User Scenarios

### S-01 — 동일 이름의 메서드 탐색

에이전트가 `Load`를 검색한다. 엔진은 프로젝트·타입·매개변수·선언 위치를 포함한 후보를 정렬해 반환한다. 후보가 여러 개면 첫 항목을 임의 선택하지 않는다. 에이전트가 `symbolId`와 snapshot을 선택한 후 원문을 읽는다.

### S-02 — IDE에서 동시에 편집

사람이 도구 훅을 거치지 않고 파일을 바꾼다. resolve는 저장된 span을 바로 읽지 않고 원문 bytes와 fingerprint를 확인한다. 불일치 시 이전 줄 범위를 반환하지 않고 재파싱 또는 원문 fallback을 수행한다. 연속 편집으로 재시도 한도를 넘으면 명확하게 실패한다.

### S-03 — 불완전한 영향 분석

정적 참조와 이름 기반 검색 후보를 서로 다른 목록으로 보여준다. DI 문자열·리플렉션·XAML·생성 코드의 누락 가능성을 설명한다. `0 references`를 `no impact`로 해석하지 않는다. 후보는 직접 참조, 추정 후보, 미분석 범위로 구분한다.

### S-04 — 이미 수정된 작업 공간의 리뷰

사용자가 `VCS` 또는 `Session` 모드를 선택한다. VCS는 명시 revision/CL과 target을 비교해 세션 이전 변경도 포함한다. Session은 시작 당시 dirty 상태를 포함한 immutable snapshot과 현재를 비교하며 세션 이전 변경을 포함하지 않는다. 두 모드를 모두 구현하고 제목·API·내보내기에 모드를 유지한다. 필요한 base/session snapshot이 없으면 `BASE_REQUIRED`/`SESSION_BASE_MISSING`을 반환한다. 삭제된 심볼도 선택한 base의 원문을 읽는다.

### S-05 — 두 세션이 동시에 사용

같은 workspace의 두 에이전트는 다른 session ID를 갖는다. 읽기는 하나의 완성된 generation을 고정하며, update 도중 일부만 쓰인 파일을 읽지 않는다. 한 세션 종료가 다른 세션의 데이터를 지우지 않는다.

## User Flow

```mermaid
flowchart TD
    A[Workspace 선택 및 신뢰 설정] --> B[Session 연결]
    B --> C[구축 또는 캐시 검증]
    C --> D[cv-find 후보 검색]
    D --> E{freshness 및 coverage 확인}
    E -->|검증 가능한 후보| F[cv-resolve 원문 읽기]
    E -->|누락 또는 오래됨| G[제한된 원문 검색과 복구]
    G --> D
    E -->|미지원 또는 부분 분석| H[제약과 추가 탐색 방법 반환]
    F --> I[기존 도구로 수정 또는 리뷰]
    I --> J[변경 감지 및 cv-update]
    J --> D
```

## Features

| 단계 | 기능 | 진입 조건 |
|---|---|---|
| 조사 | 세션 탐색 비중, 범위 읽기·LSP·Serena 비교, 정답 corpus | 실험 방향 확정; pilot 실행 설정 FUP-001 |
| C# PoC 후보 | `cv-build`, `cv-find`, `cv-resolve`, `cv-inspect`, `cv-validate` | 비교 후 엔진 Go 결정 FUP-003 |
| 안정화 | `cv-update`, 동시성·failure injection·구조화 metrics | PoC 검증; GC 수치 FUP-004 |
| 참조·리뷰 | remark lazy resolve, `cv-impact`, VCS/Session `cv-diff`, Claude 연동 | 단계별 정확도 검증 |
| 로컬 Web UI | `.cv`·심볼 관계·diff 검사 | UC-011 확정; 시안 선택 FUP-006 |
| 필수 후속 | Perforce baseline·target·원문·diff | Git 검증 후 CL 계약·환경 입력 FUP-007 |
| 별도 후보 | C++/UE5, Codex 연동, npm 배포 | 필요성과 호환성을 별도 결정 |

## Functional Requirements

| ID | 요구사항 | 수용 조건 |
|---|---|---|
| FR-01 | 명시한 workspace·project·구성만 분석 | 범위 밖 경로 거부, 미로드 프로젝트 별도 집계 |
| FR-02 | 선언 검색과 식별자 반환 | overload·generic arity·partial·동명 프로젝트를 구분 |
| FR-03 | 원문 lazy resolve | snapshot과 같은 bytes에서 span 계산·내용 추출, UTF-16 및 줄 번호 계약 준수 |
| FR-04 | freshness 검증 | 같은 mtime·크기의 내용 변경도 digest 검증으로 탐지 |
| FR-05 | fallback·repair | 한정된 재시도, 원인·실제 수행 여부·복구 상태를 반환 |
| FR-06 | coverage 전달 | analyzed/excluded/failed/unknown/truncated를 응답에 유지 |
| FR-07 | 변경 적용 | 추가·수정·삭제·rename·프로젝트 설정 변경을 구분하고 semantic 의존성 무효화 |
| FR-08 | 동시 접근 | generation 단위 읽기, 단일 writer, 세션별 기준점 보존 |
| FR-09 | 진단·metrics | 원문을 기본 로그에 남기지 않고 비용·상태·오류 코드 기록 |
| FR-10 | 영향 후보 | 정적 참조와 텍스트 후보 분리, 동적 참조 완전성 미보장 |
| FR-11 | 변경 비교 | 명시 base/target, 추가·삭제·본문·signature 변화, textual diff 연결 |
| FR-12 | 연동 | 도구 장애가 기존 검색·원문 읽기를 막지 않으며 훅을 freshness의 유일한 근거로 삼지 않음 |
| FR-13 | 주석 lazy resolve | 현재 source에서 주석·XML 문서를 읽고 원문 위치·fingerprint 제공 |
| FR-14 | 로컬 Web UI | 검색→심볼→원문/관계/diff, freshness·coverage·baseline 구분, CLI와 결과 일치 |
| FR-15 | 필수 Perforce 후속 | read-only CL/revision provider, pending/shelved/submitted 의미·권한 오류 검증 |

## Non-Functional Requirements

- 결정성: 같은 source snapshot·config·query·schema이면 정렬과 ID 생성이 동일하다. 시각·request ID는 예외다.
- 성능: cold/warm/long 세션을 분리하고 build/update/fallback을 총시간에 포함한다. UC-007에 따라 pilot 설계·결과 후 최종 수치를 정하고 본 실험 전에 동결한다.
- 견고성: 잘못된 schema·부분 write·worker crash·파일 잠금에서 기존 generation을 보존한다.
- 관측성: 결과 없음, 부분 결과, 내부 오류, 미지원 상태를 구분하고 기계가 처리할 수 있어야 한다.
- 보안: 로컬 읽기 중심, workspace 경계 준수, 원문·시크릿의 자동 외부 전송 없음. 빌드·generator 실행 신뢰 정책은 UC-008.
- 호환성: Windows·PowerShell 사용 환경을 우선 검증한다. Windows는 확정 범위이며 구체적 SDK 버전은 구현 시 고정한다.

## Constraints

현재 저장소에는 아이디어·준비 문서와 비교용 UI 시안이 있다. 제품 `.sln`, 제품 테스트, 고정 runtime 버전은 없다. 준비 문서의 directory 구조와 명령은 제안 계약이며 설치 가능한 제품 명령이 아니다.

원안의 `.cv` 확장자·독립 엔진·CLI `cv-*` 접두사·원본 우선 원칙은 보존한다. 영속 JSON/JSONL 저장·L0/L1·CLI 후 MCP 연동은 확정됐다. 상세 schema·retention 수치·실험 실행 설정·최종 UI는 후속 확정 대상이다.

## Edge Cases

| 상황 | 기대 동작 |
|---|---|
| 솔루션 없음·빈 workspace | 지원 범위와 빈 결과 원인을 구분; 자동으로 전체 디스크 탐색하지 않음 |
| 복수 target framework·전처리 기호 | 분석한 구성 명시; 다른 구성까지 완전하다고 표시하지 않음 |
| partial·linked file·generated source | 선언 위치 목록 유지; 가상 document는 실제 파일 경로처럼 취급하지 않음 |
| reflection·DI·XAML 참조 | 동적 누락 위험 유지; 텍스트 후보와 확인된 참조 분리 |
| 결과 page 제한 | continuation과 truncated 표시; 다음 페이지에서 generation 변경 시 재검색 요구 |
| 취소·timeout·디스크 부족 | publish 중단, 기존 generation 보존, 자원 정리 |
| newline·한글·emoji·BOM | 원문 인코딩 유지, offset 단위 명확화, 변형 없는 범위 읽기 |
| branch switch·sync·설정 변경 | 파일 변경 외에 project graph·reference fingerprint 무효화 |
| 삭제·이동·공백 포함 파일명 | base/current 역할 구분, shell 문자열 결합 없이 인자 전달 |

## UI 필요 여부

UC-011 A+B에 따라 터미널 text/JSON과 로컬 Web UI를 모두 제공한다. `impeccable` Operate 기준과 `design-taste-frontend`의 적용 가능한 원칙으로 [시안 목록](samples/index.html)을 제공한다.

| 시안 | 정보 구조 | 사용 맥락 |
|---|---|---|
| [sample1](samples/sample1/index.html) | 검색 목록·원문·상태를 나란히 보는 분할 화면 | 반복 탐색·높은 정보 밀도 |
| [sample2](samples/sample2/index.html) | 심볼 선택 뒤 필요한 문맥을 단계적으로 펼침 | 낮은 인지 부담·원문 이해 |
| [sample3](samples/sample3/index.html) | 관계·변경 중심으로 원문 증거 확인 | 영향 범위·리뷰 |

시안은 합성 데이터로 조작 가능한 비교 prototype이며 engine 결과가 아니다. 최종 UI·frontend stack은 아직 선택되지 않았다(FUP-006). loading/empty/error/stale/partial, baseline 모드와 정적/추정 참조 구분을 공통 검토한다. 로컬 API·접근 제어는 architecture에서 정의한다.

## Success Criteria

### 준비 완료

모든 아이디어를 추적할 수 있고, 충돌은 UC로 연결되며, 각 구현 작업에 산출물·검증·Agent·Model·Reasoning Level·Blocked By가 존재한다. 사용자 승인 전에도 실행 가능한 조사 준비 작업과 승인 후 작업이 구분되어야 한다.

### 제품 진행 판단

1. 품질 보존: 독립 정답·build/test·리뷰 판정으로 평가한다. 모델 자기평가는 주 판정 근거가 아니다.
2. 순효율: 캐시 요금·도구 정의 토큰·구축·복구 비용을 포함한다.
3. 규모별 가치: small/medium/large의 크기·symbol·graph 지표를 기록한다.
4. 복구 안전성: stale source를 검증된 결과로 반환하는 사례와 조용한 부분 결과를 failure injection으로 찾는다.
5. 반복성: task·repository별 편차와 비교군의 실제 도구 사용률을 공개한다.

원안의 `-42%`, `94% → 95%`, `1.8s`는 예시이며 실제 결과·목표치로 재사용하지 않는다. UC-007의 실험 방향은 확정됐다. pilot 실행 범위·비용 상한은 실행 전에 지정하고(FUP-001), pilot 설계·결과로 본 실험 표본·품질 허용 저하·최소 효율 차이를 정한 뒤 고정한다(FUP-002). 누락 원문 관련 요구는 FUP-005로 보류한다.
