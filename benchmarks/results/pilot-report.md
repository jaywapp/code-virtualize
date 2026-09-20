# 합성 smoke pilot 결과 (TASK-004)

실행일: 2026-09-20. 이 결과는 현재 저장소의 합성 C# fixture에서 계측 경로를 확인한 smoke pilot이다. 개인·기존 세션 로그와 외부 유료 모델/API를 사용하지 않았고, 제품 가치나 일반적인 성능을 증명하지 않는다.

## 실행 범위

- protocol: [protocol.md](../protocol.md), aggregate contract: [result.schema.json](../result.schema.json)
- repository 기준 commit: `b59670139de6d759ba79a315f893bb5241e9d79e`; 시작 worktree는 TASK-001~003 산출물 때문에 dirty 상태였다.
- corpus: `tests/fixtures/csharp/`의 NAV·DIFF 합성 task 두 개.
- seed: `20260920`; fixture digest: `b0fb86a76e3de47ffb38dc3541e0d613f34e0c2e54956622bd304fa1a9744a2e`.
- wall-time 상한 1,200초, 실제 harness wall time 0.785초. PowerShell 5.1.26100.9444에서 재현했다.
- token·provider/tool 비용·tool result token은 계측 수단이 없어 `null/unavailable`이다. bytes/lines를 token으로 환산하지 않았다.

재실행:

```powershell
& .\tests\fixtures\csharp\verify-fixtures.ps1
& .\benchmarks\runs\run-smoke.ps1
```

raw `pilot-raw.jsonl`과 `pilot-runtime.json`은 `benchmarks/runs/.gitignore`로 제외한다. 재현 가능한 합성 harness인 `run-smoke.ps1`과 집계 [pilot-manifest.json](pilot-manifest.json)만 추적한다.

## 조건과 결과

| 조건 | 실제 동작 | attempted | succeeded | infra_failed | 품질 통과 |
|---|---|---:|---:|---:|---:|
| A | 기본 `rg` 후보 검색 뒤 관련 fixture 전체 read | 2 | 2 | 0 | 2/2 |
| B | 후보를 좁힌 뒤 필요한 범위만 부분 read | 2 | 2 | 0 | 2/2 |
| C | C# LSP 탐색 | 2 | 0 | 2 | 0/0 |
| D | Serena 탐색 | 2 | 0 | 2 | 0/0 |
| 전체 | 모든 시작 셀 | 8 | 4 | 4 | 4/4 |

C는 `csharp-ls`, `OmniSharp`, `Microsoft.CodeAnalysis.LanguageServer`와 global dotnet tool에서 호출 가능한 서버를 찾지 못했다. D는 `serena`, `serena-mcp-server`와 활성 harness 도구에서 Serena를 찾지 못했다. 두 조건을 A로 대체하지 않고 `infra_failed / primary_tool_unavailable`로 attempted 분모에 포함했다. 따라서 complete pair는 0, incomplete pair는 2다.

실행 순서는 seed로 고정했다.

- NAV: D → B → C → A
- DIFF: B → D → A → C

## A/B 계측

| 조건 | tool calls | read | grep | other | full read | partial read | source bytes | source lines | wall 합계 |
|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|
| A | 13 | 10 | 1 | 2 | 10 | 0 | 2,650 | 152 | 83.602 ms |
| B | 9 | 3 | 4 | 2 | 0 | 3 | 459 | 27 | 139.883 ms |

B는 이 두 합성 task에서 A보다 source bytes 82.7%, source lines 82.2% 적게 읽었다. 반복 1회이고 process startup·OS page cache·순서 효과를 분리하지 않았으므로 latency 비교나 일반적 효율 결론으로 사용하지 않는다. token 계측이 없으므로 token 절감이라고 부르지 않는다.

## Raw 대조와 데이터 품질

- raw event 24건: `run_started` 8, `run_finished` 8, `quality_result` 4, harness 확장 `tool_summary` 4.
- started 8 = attempted 8; terminal 8 = succeeded 4 + infra_failed 4.
- A/B tool call 22, read 13 = full 10 + partial 3, source 3,109 bytes/179 lines가 runtime 집계와 일치한다.
- duplicate, conflicting duplicate, terminal 누락, invalid run은 0이다.
- `tool_summary`는 protocol 표준 event가 아니므로 `unknownEventCount=4`다.
- 네 A/B 셀의 token 관련 필드가 없어 `missingRequiredFieldCount=4`, `excludedMetricEventCount=4`다.
- raw에는 source/prompt/tool output 본문, 개인 로그, 세션 로그, 경로, credential이 없다.

## 판정

계측 harness와 실패 포함 denominator는 동작했다. A/B 모두 합성 정답을 통과했고 범위 읽기 지시가 읽은 bytes/lines를 줄일 가능성은 관찰됐다. C/D는 실행 환경이 없어 비교하지 못했다. 모델 token·비용·반복 분산이 없고 complete pair가 0이므로 제품 가치 판정은 `inconclusive`다.

독립 엔진 구현 여부는 이 smoke만으로 정당화하지 않는다. 후속 구현을 진행한다면 사용자의 명시적 전체 구현 지시를 별도 근거로 기록하고, TASK-016에서 CV 조건과 실제 token/cost를 측정할 수 있을 때 다시 평가한다.
