# Code-Virtualize CV 포함 본 실험 결과

실행일: 2026-09-20  
protocol: `task-016-v1`  
최종 판정: **No-Go**

## 판정 요약

E(Code-Virtualize)는 합성 C# navigation·diff 정답의 품질과 안전성 기준을 통과했지만, 품질을 통과한 stronger available baseline B보다 source bytes를 20% 이상 줄이지 못했다. NAV는 9.75% 감소에 그쳤고 DIFF는 1,618.9% 증가했다. 따라서 ADR 001의 동결 기준에 따라 현재 평가 구성의 순효율 판정은 No-Go다.

실제 모델 input token과 provider 비용은 측정할 승인 로그·usage record가 없어 token 효율은 `inconclusive`다. source bytes를 token으로 환산하지 않았다. C# LSP(C)와 Serena(D)는 사용할 수 없어 각 셀을 `infra_failed`로 attempted 분모에 유지했다.

## 범위와 실행 통제

- corpus: 저장소의 합성 C# navigation·diff fixture, small 규모만 측정
- task: 기존 pilot과 같은 NAV·DIFF
- 조건: A/B/C/D/E, task·조건별 3회, 총 30개 planned/attempted cell
- 순서: seed `20260920`으로 paired block 안에서 회전 균형화했으며 실제 순서는 `cv-run-manifest.json`에 기록
- 권한: 네트워크·패키지 설치·유료 모델/API·개인 또는 기존 세션 로그 접근 없음
- 정답: `tests/fixtures/csharp/navigation/expected.md`와 `diff/expected.md`; CV 출력으로 정답을 만들지 않음
- raw source, prompt, 전체 tool output, secret은 결과 파일에 기록하지 않음
- 전체 harness wall time: 12.230초

medium/large corpus는 제공되지 않아 실행하지 않았고 0 결과로 대체하지 않았다. Perforce·UE5 성능은 이 결과에서 추론하지 않는다.

## 조건별 결과

| 조건 | 실제 동작 | attempted | succeeded | infra_failed | 품질 통과 | primary tool 사용률 |
|---|---|---:|---:|---:|---:|---:|
| A | pilot과 같은 grep + 관련 source 전체 read | 6 | 6 | 0 | 6/6 | 해당 없음 |
| B | pilot과 같은 후보 축소 + 필요한 범위 read | 6 | 6 | 0 | 6/6 | 해당 없음 |
| C | 고정 C# LSP 탐색 | 6 | 0 | 6 | 0/0 | 0/6 |
| D | 고정 Serena 탐색 | 6 | 0 | 6 | 0/0 | 0/6 |
| E | 실제 CV CLI·MCP·Core diff | 6 | 6 | 0 | 6/6 | 6/6 |

C는 `csharp-ls`, `OmniSharp`, `Microsoft.CodeAnalysis.LanguageServer`, D는 `serena`, `serena-mcp-server`의 호출 가능 명령을 찾지 못했다. fallback A 실행이나 0 비용 성공으로 바꾸지 않았다. 이 때문에 다섯 조건이 모두 성공한 complete pair는 0이고 incomplete pair는 6이다.

## E 실제 사용량

| 호출 | 횟수 |
|---|---:|
| `cv-build` | 9 |
| `cv-find` | 24 |
| `cv-get` | 9 |
| `cv-impact` | 3 |
| `cv-update` | 12 |
| symbol diff | 6 |
| MCP lifecycle/tool 요청 | 21 |

E의 CV primary tool 사용률은 6/6이다. MCP 사용률은 3/6이다. NAV 세 셀은 MCP `cv_find`·`cv_get`·`cv_impact`와 stale 검출·복구를 실행했고, DIFF 세 셀은 동일 workspace의 immutable base generation에서 update한 target generation을 Core diff로 비교했다.

## 품질과 안전성

| 지표 | 결과 | 기준 | 판정 |
|---|---:|---:|---|
| B task 품질 | 6/6 | stronger baseline | 기준선 |
| E task 품질 | 6/6 | B 대비 저하 0 | PASS |
| E 정답 recall | 27/27 (100%) | 독립 정답 | PASS |
| 정답표 외 후보 | 9 | 공개 | 보고 |
| stale 원문 오반환 | 0 | 0 | PASS |
| 조용한 partial | 0 | 0 | PASS |

정답표 외 후보 9건은 반복별 NAV의 interface inheritance 위치 1건과 DIFF의 container-level 변화 2건이다. task 정답에 필요한 navigation 후보와 VCS/Session 변경은 모두 회수했으며, Session의 시작 전 dirty `Existing()` 변화는 변경 목록에서 제외했다. 삭제 symbol은 base source에서 복원했고 모든 기대 diff에 textual evidence가 있었다.

NAV recovery는 current source digest를 의도적으로 변경한 뒤 `cv_get`이 `SOURCE_STALE`로 명시 실패하는지 확인하고, `cv-update` 후 새 generation에서 원문을 다시 얻는 순서로 측정했다. stale content를 성공 응답으로 반환한 실행은 없었다.

## source bytes와 token

| task | stronger baseline | baseline 중앙값 | E 중앙값 | 변화 | 20% 기준 |
|---|---|---:|---:|---:|---|
| NAV | B | 472 B | 426 B | 9.75% 감소 | FAIL |
| DIFF | B | 74 B | 1,272 B | 1,618.9% 증가 | FAIL |

source bytes는 실제 반환·materialize한 source slice와 diff source evidence를 센 값이다. build index bytes나 JSON envelope를 source bytes에 섞지 않았다. 복구에 사용한 재조회도 E 비용에서 제외하지 않았다.

실제 model input/output/cache token, peak context, provider cost record는 없었다. schema가 요구하는 token 합계의 0은 token event가 없다는 합이며 “0 token agent run” 측정값이 아니다. 따라서 token 이득과 cache-aware model 비용은 `inconclusive`다. 외부 유료비용은 실제로 0 USD였다.

누적 sequence에서 E가 품질과 20% source-byte 기준을 함께 충족하는 지점이 없어 break-even은 `not_reached`다.

## 구축·갱신·복구·자원

| 지표 | 표본 | 중앙값 | p95 / 최대 | 상한 | 판정 |
|---|---:|---:|---:|---:|---|
| cold build | 6 | 249.379 ms | 268.308 ms | 60,000 ms | PASS |
| warm query/diff | 6 | 259.024 ms | 516.485 ms | 2,000 ms | PASS |
| incremental update | 6 | 267.518 ms | 273.602 ms | 5,000 ms | PASS |
| recovery | 3 | 314.599 ms | 321.319 ms | 별도 상한 없음 | 관측 |
| peak working set | 6 | 161,843,200 B | 216,129,536 B | 1 GiB | PASS |
| build index size | 6 | 29,167.5 B | 36,850 B | 사전 상한 없음 | 관측 |
| 전체 harness | 1 | 12.230 s | 12.230 s | 30분 | PASS |

recovery는 적용 가능한 NAV 세 셀에서 측정했다. peak working set은 runner peak와 순차 child process의 최대 peak 합계 근사이며, 전 시스템 sampling trace는 아니다.

## 결론과 한계

현재 합성 small corpus 구성은 품질·안전성·latency·memory·외부 비용 상한을 통과했지만 핵심 source-byte 효율 기준을 두 task 모두 통과하지 못했다. 그래서 현재 구성의 제품 가치 후보는 No-Go다.

이 결론은 실제 모델 token 효율, C/D semantic 도구와의 상대 효율, medium/large repository, 실제 승인 세션, Perforce 또는 UE5에 대한 결론이 아니다. 실제 input token과 대표 corpus가 확보되면 새 protocol revision과 decision으로 재평가해야 한다.

재현 가능한 집계는 [cv-result.json](cv-result.json), 실행 조건·분모·상세 지표는 [cv-run-manifest.json](cv-run-manifest.json)에 있다. 실행 명령은 `powershell -NoProfile -File benchmarks/runs/run-cv-evaluation.ps1`이다.