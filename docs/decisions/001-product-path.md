# ADR 001 — 독립 .NET 엔진 engineering prototype 경로

- 상태: Accepted
- 결정일: 2026-09-20
- 적용 범위: TASK-006 이후 독립 엔진 구현과 TASK-016 본 실험

## 맥락

[합성 smoke pilot](../../benchmarks/results/pilot-report.md)에서 A와 B는 독립 fixture 정답을 모두 통과했다. B는 A보다 source bytes 82.7%, source lines 82.2% 적게 읽었지만 task별 1회뿐이며 token·비용을 측정하지 못했다. C# LSP(C)와 Serena(D)는 환경에서 사용할 수 없어 모두 `infra_failed`였다. 따라서 이 결과의 제품 가치 판정은 `inconclusive`이며 token 절감, latency 우위, 기존 도구 대비 우위를 증명하지 않는다.

사용자는 2026-09-20에 `plan.md`의 모든 작업을 지정 모델 서브에이전트로 마무리하라고 지시했다. 이 지시는 실험 결과에 따른 제품 가치 승인과 구분되는 engineering prototype 구현 승인으로 취급한다.

## 결정

FUP-003은 **독립 C#/.NET 엔진 경로 Go**로 확정한다. TASK-006부터 단일 .NET solution, Core·C# adapter·CLI를 구현하고 이후 정확성 gate와 의존 순서를 따른다.

- 로컬 Web UI는 UC-011과 TASK-018의 필수 제품 범위로 유지한다. 시안·frontend stack 선택은 FUP-006이 계속 차단한다.
- Perforce adapter는 UC-002와 TASK-019의 필수 후속 범위로 유지한다. 실제 CL 계약과 read-only 환경 입력은 FUP-007이 계속 차단한다.
- 구현 진행은 제품 가치가 증명됐다는 뜻이 아니다. TASK-016에서 아래 동결 기준으로 다시 평가하며 결과가 불충분하면 `inconclusive`로 기록한다.
- 실제 승인 로그, 실제 모델 token·비용 평가는 FUP-001의 미완 범위다. 합성 smoke 결과로 이를 대체하지 않는다.

## FUP-002 동결 기준

아래 기준은 합성 fixture와 승인된 로컬 corpus에서 실행하는 본 실험에 적용한다. 변경하려면 결과를 보기 전에 새 decision으로 이유와 날짜를 기록한다.

| 항목 | 동결 값 | 판정 규칙·이유 |
|---|---:|---|
| 품질 허용 저하 | `0` | 독립 fixture의 정답, build/test, 필수 task 성공 여부에서 stronger available baseline보다 한 건도 낮아지지 않아야 한다. 작은 표본에서 품질 손실을 평균 효율로 상쇄하지 않는다. |
| Critical 안전 결함 | `0` | stale 원문 오반환과 조용한 partial/coverage 누락은 한 건이라도 있으면 실패다. |
| 반복 | task·조건별 최소 `3회` | 1회 smoke의 순서·startup·cache 영향을 줄이면서 30분 로컬 예산 안에서 재현할 수 있는 최소 반복이다. |
| 무작위화 | 고정 seed `20260920` | 조건 순서와 manifest에 동일 seed를 기록해 재현한다. |
| 효율 후보 기준 | `20% 이상` 감소 | 품질을 통과한 stronger available baseline 대비 측정 가능한 `sourceBytes` 또는 실제 `inputTokens`의 paired 중앙값 감소율을 각각 판정한다. 두 지표를 합치지 않는다. |
| token 주장 | 실제 계측 시에만 허용 | input token이 미측정이면 token 이득을 주장하지 않고 token 판정은 `inconclusive`다. bytes/lines를 token으로 환산하지 않는다. |
| cold build latency | 실행별 `60초 이하` | 작은 fixture와 CI에서 restore를 제외한 clean index build wall time 상한이다. 느린 초기화나 hang을 잡되 일반 CI 변동을 허용한다. |
| warm query latency | 실행별 `2초 이하` | 준비된 generation의 find/resolve 요청 wall time 상한이다. 작은 fixture의 상호작용 가능한 응답과 CI 재현성을 함께 본다. |
| incremental update latency | 실행별 `5초 이하` | 한 fixture 변경의 update·publish 완료 wall time 상한이다. watcher debounce를 포함해 runaway 재분석을 잡는다. |
| peak memory | 프로세스 합계 `1 GiB 이하` | engine과 harness 자식 프로세스의 peak working set 합계다. 작은 fixture/CI에서 비정상 graph·cache 확장을 탐지하기 위한 넉넉한 상한이다. |
| 유료 외부 비용 | `0` | 로컬 도구와 무료 합성/로컬 입력만 사용한다. provider 과금이 필요한 조건은 실행하지 않고 unavailable로 기록한다. |
| 로컬 wall time | 전체 `30분 이하` | fixture 검증, 모든 조건·반복, 집계를 포함한다. 초과하면 중단하고 attempted/timeout을 denominator에 남긴다. |

`stronger available baseline`은 A/B/C/D 중 동일 task에서 품질 gate를 통과하고 실제로 실행 가능한 조건 가운데 사전에 선택한다. 우선순위는 실제 input token이 측정되면 input token이 가장 적은 조건, 그렇지 않으면 source bytes가 가장 적은 조건이다. 동률이면 기능이 더 강한 semantic 조건 D, C, B, A 순으로 고정한다. unavailable 조건을 0 비용·0 읽기로 대체하지 않는다.

모든 품질·효율·latency·memory 기준을 통과해야 제품 가치 후보를 `Go`로 재평가할 수 있다. metric이 없거나 비교군이 불완전하면 해당 주장은 `inconclusive`다.

## 결과

TASK-005는 완료되고 TASK-006은 실행 가능해진다. 이후 구현은 독립 엔진 경로로 진행하되 Web UI와 Perforce를 계획에서 제거하지 않는다. 실제 모델 비용, 기존 semantic 도구와의 완전한 비교, 중대형 corpus 일반화는 이 결정으로 해결되지 않는다.
