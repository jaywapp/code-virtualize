# ADR 002 — CV 합성 본 실험 검증 결과

- 상태: Accepted
- 결정일: 2026-09-20
- 적용 범위: TASK-016 합성 C# small corpus의 A/B/C/D/E 비교와 독립 엔진 제품 가치 재평가
- 결정: **No-Go**

## 맥락

ADR 001은 독립 C#/.NET engineering prototype 구현을 승인하면서 제품 가치 판단을 TASK-016으로 분리했다. 본 실험의 동결 기준은 stronger available baseline 대비 품질 저하 0, stale 원문·조용한 partial Critical 0, task·조건별 최소 3회, seed `20260920`, source bytes 또는 실제 input token 20% 이상 감소, cold/warm/update 60/2/5초, peak working set 1 GiB, 외부 유료비용 0, 전체 30분이다.

실제 승인 세션 로그와 실제 모델 token·비용 record는 제공되지 않았다. 따라서 합성 fixture와 로컬 제품 실행만 사용했으며 token 효율은 source bytes로 대체하지 않았다.

## 관찰 결과

- NAV·DIFF × A/B/C/D/E × 3회로 30개 셀을 모두 시작했다.
- A, B, E는 각각 6/6 성공했다. C# LSP(C)와 Serena(D)는 unavailable이어서 각각 6/6 `infra_failed`로 분모에 남겼다.
- E는 독립 정답 27/27을 회수했고 task 품질 6/6, stale 원문 오반환 0, 조용한 partial 0이었다.
- E primary tool 사용률은 6/6, MCP 사용률은 3/6이었다.
- stronger available baseline은 두 task 모두 B였다.
- NAV source bytes 중앙값은 B 472 B, E 426 B로 9.75% 감소했다.
- DIFF source bytes 중앙값은 B 74 B, E 1,272 B로 1,618.9% 증가했다.
- cold build, warm query, update 최대값은 각각 268.308 ms, 516.485 ms, 273.602 ms였다.
- peak working set 최대값은 216,129,536 B, 외부 유료비용은 0 USD, 전체 harness 시간은 12.230초였다.
- 실제 model token·peak context·provider 비용은 미측정이다. token 효율은 `inconclusive`다.
- source-byte 기준과 품질을 함께 만족하는 누적 break-even은 관측되지 않았다.

세부 결과와 분모는 [CV 평가 보고서](../../benchmarks/results/cv-report.md), [schema 집계](../../benchmarks/results/cv-result.json), [run manifest](../../benchmarks/results/cv-run-manifest.json)에 있다.

## 결정

현재 평가한 Code-Virtualize 구성은 **No-Go**로 판정한다. 품질과 안전성은 stronger available baseline보다 낮지 않았지만, 사전 동결한 20% source-byte 효율 기준을 NAV와 DIFF 모두 충족하지 못했다. 모든 기준을 통과해야 Go라는 ADR 001 규칙에 따라 latency·memory 통과로 효율 실패를 상쇄하지 않는다.

No-Go는 현재 합성 small C# corpus와 측정한 출력 계약에 대한 판단이다. 실제 모델 token 이득, C# LSP·Serena 대비 우위, medium/large corpus, Perforce·UE5 성능을 부정하거나 추론하는 결정은 아니다. 해당 근거가 새로 확보되면 결과를 덮어쓰지 않고 새 protocol revision과 ADR로 재평가한다.

## 결과

- 독립 엔진 prototype과 재현 가능한 benchmark artifact는 보존한다.
- 현재 결과를 token 절감 또는 기존 semantic 도구 대비 우위의 근거로 사용하지 않는다.
- 제품화 재개 전에는 diff evidence의 source-byte 비용을 줄이고, 실제 모델 input token과 대표 corpus에서 효율을 다시 측정해야 한다.
- C/D unavailable 결과를 성공·0 비용으로 재해석하지 않는다.
- 실제 승인 로그·모델 비용, medium/large corpus, Perforce·UE5는 미완 입력으로 남는다.