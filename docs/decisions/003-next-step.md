# ADR 003 — TASK-016 No-Go 이후 prototype 보존과 제품화 중단

- 상태: Accepted
- 결정일: 2026-09-20
- 적용 범위: TASK-016 결과 이후 TASK-017~021의 실행 상태와 재개 조건
- 최종 disposition: **기술 prototype 보존, 독립 엔진 제품화 자동 진행 중단**

## 맥락

[ADR 001](001-product-path.md)은 합성 smoke의 제품 가치 판정을 `inconclusive`로 유지하면서 독립 C#/.NET engineering prototype 구현을 승인했고, 모든 동결 기준을 통과해야 제품 가치 후보를 Go로 판정하도록 했다. 이 구현 승인은 제품 가치 Go가 아니며 TASK-016 재평가를 위한 기술 증거 생성이었다.

[ADR 002](002-validation-outcome.md)와 [CV 평가 보고서](../../benchmarks/results/cv-report.md)의 TASK-016 결과는 다음과 같다.

- E task 품질은 `6/6`, 독립 정답 recall은 `27/27`, stale 원문 오반환과 조용한 partial을 포함한 Critical 결함은 `0`이었다.
- 품질을 통과한 stronger available baseline B 대비 source bytes는 NAV `9.75% 감소`, DIFF `1,618.9% 증가`였다. 두 task 모두 동결한 `20% 이상 감소` 기준을 충족하지 못했다.
- 실제 model input/output/cache token과 provider 비용 record가 없어 token 효율은 `inconclusive`다. source bytes를 token으로 환산하지 않는다.
- C# LSP(C)와 Serena(D)는 unavailable이었다. 이를 성공이나 0 비용 비교군으로 해석하지 않는다.

따라서 현재 합성 small C# corpus와 출력 계약에 대한 최종 판정은 **No-Go**다. 품질·안전성 통과가 효율 실패를 상쇄하지 않는다.

## 결정

TASK-006~016에서 만든 Core, CLI, MCP, Claude lifecycle adapter, 테스트와 재현 가능한 benchmark artifact는 기술 prototype으로 보존한다. 완료된 작업을 되돌리거나 결과를 삭제하지 않는다. 동시에 현재 독립 엔진의 신규 제품화와 그 경로에만 필요한 후속 구현은 자동으로 진행하지 않는다.

- TASK-018 로컬 Web UI는 `Blocked`다. 얇은 MCP/Claude 연동은 사람 중심 검색·source/remark·관계·VCS/Session diff 화면, freshness/coverage 표시, 로컬 Web 접근 경계를 제공하지 않으므로 UC-011을 충족하지 않는다. 요구와 시안은 보존하지만 지금 구현하지 않는다.
- TASK-019 Perforce adapter는 `Blocked`다. 얇은 연동은 submitted/shelved/pending CL별 base/target bytes, depot/client mapping, have/head 차이와 read-only 실패 계약을 제공하지 않으므로 UC-002의 필수 후속을 충족하지 않는다. 요구와 adapter 계약은 보존하지만 지금 구현하지 않는다.
- TASK-020은 FUP-005의 복구 원문이 없으므로 `Blocked`를 유지한다. 잘린 내용을 추정해 복구하지 않는다.
- TASK-021은 FUP-004의 retention·용량 수치와 사용자 승인이 없으므로 `Blocked`다. 자동 GC는 비활성인 no-delete 상태를 terminal disposition으로 삼으며 active reader, session source, immutable base snapshot과 사용자 설정을 보존한다.

이 중단은 UC-011 Web UI와 UC-002 Perforce 요구를 폐기하는 결정이 아니다. 현재 No-Go에서 이를 구현하지 않는 이유와 미충족 gap을 기록한 것이다. 실제 사용자 홈 설정 변경, 데이터 삭제, 설치, 게시, release 또는 외부 전송을 승인하지 않는다.

## 제품화 재개 조건

TASK-018/019 또는 자동 GC 제품 작업은 다음 조건을 충족하고 결과를 보기 전에 protocol·기준을 고정한 새 ADR이 승인된 뒤에만 재개한다.

1. medium/large를 포함한 대표 corpus와 실제 승인 세션 또는 동등하게 재현 가능한 실제 model input token 측정을 확보한다. token이 계속 미측정이면 token 효율 주장을 하지 않는다.
2. source-byte 반환 계약, 특히 diff evidence의 과다 materialization을 개선하고 stronger available baseline 대비 동결 기준을 통과한다. 기준을 변경한다면 재실험 전에 이유·수치·protocol revision을 별도 기록한다.
3. 가능한 C/D 비교 환경을 확보하거나, 계속 unavailable이면 그 제한을 유지한 채 어떤 available baseline에 대한 판단인지 명시한다.
4. TASK-018은 위 공통 조건과 함께 FUP-006의 최종 UI 방향·frontend stack 입력을 받는다.
5. TASK-019는 위 공통 조건과 함께 FUP-007의 Perforce CL별 계약·승인된 read-only 실환경 입력을 받는다.
6. TASK-021은 FUP-004의 실측 retention·용량 수치와 명시적 승인을 받는다. 제품화가 재개되지 않아도 보존 한도를 정해야 한다면 삭제 없는 측정·정책 제안까지만 별도 승인 범위로 수행한다.

FUP-001의 실제 로그·token, FUP-006, FUP-007은 서로를 대신하지 않는다. source bytes/lines와 model token은 별도 지표로 유지한다.

## 결과

TASK-017은 이 최종 No-Go disposition을 architecture와 plan에 동기화함으로써 완료된다. TASK-018~021은 위 조건이 충족되기 전 실행하지 않는다. Session baseline과 VCS base의 원문 bytes, pin된 generation, 잘린 원문과 관련된 미확정 요구, retention 보호는 그대로 유지한다.
