# Product

<!-- impeccable:product-schema 1 -->

## Platform

web

## Stack

제품 Core/CLI는 C#/.NET으로 확정됐다(UC-003). 로컬 Web UI의 최종 프레임워크는 미확정이다. 이번 prepare의 시안은 브라우저로 확인할 수 있는 정적 HTML/CSS/JavaScript 프로토타입이며 제품 stack 선택으로 간주하지 않는다.

## Users

개발자와 리뷰어가 로컬 workspace의 `.cv`, 심볼 관계와 diff를 탐색·검사한다. CLI text/JSON은 자동화와 AI 연동에 사용한다(UC-011).

## Product Purpose

원본 source를 진실의 원천으로 유지하면서 심볼 중심 탐색과 필요한 원문 범위 조회를 돕는다. 효율과 품질은 실험으로 검증하며 미측정 절감 수치를 주장하지 않는다.

## Operating Context

1차 Windows + C# + Git. Perforce는 필수 후속 지원이다. 로컬 Web UI와 터미널을 모두 제공하고 IDE/데스크톱 전용 UI는 제외한다. VCS baseline/target과 세션 시작 snapshot을 모두 지원하며 사용자가 목적에 따라 고른다.

## Capabilities and Constraints

- 영속 content/config hash cache와 세션 metadata 분리, PoC는 JSON/JSONL `.cv`.
- L0/L1의 모든 접근성 선언 수집, source·주석·body lazy resolve.
- 기본 syntax-only, 명시적으로 신뢰한 workspace에서 semantic load.
- 원안의 누락 부분과 관련된 요구 확정은 복구 전 보류.
- 실제 로그 범위, pilot 예산, 품질/효율 threshold, cache retention 수치는 아직 미정.
- 3종 시안은 선택할 대안이며 어느 안도 사용자 선택이 끝난 최종 화면이 아니다.

## Evidence on Hand

[user-confirm.md](user-confirm.md)의 2026-09-19 인터뷰 11개 결정을 근거로 작성했다. 추가 인터뷰로 이미 답한 제품 사실을 다시 묻지 않는다. [design.md](design.md), [architecture.md](architecture.md), `../ideas/` 전체가 상세 근거다. 제품 실행 데이터와 기존 UI·브랜드 자산은 없다. 시안의 source·관계·diff는 합성 데이터임을 화면에 표시한다.

## Product Principles

원문 정확성, freshness와 coverage의 분리, baseline 의미의 명시, 로컬 읽기 중심, 사용자가 확인 가능한 증거를 우선한다.
