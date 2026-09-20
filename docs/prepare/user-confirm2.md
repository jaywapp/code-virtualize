# 후속 사용자 확인서

작성일: 2026-09-20  
기준 문서: [plan.md](plan.md), [user-confirm.md](user-confirm.md), [ADR 002](../decisions/002-validation-outcome.md), [ADR 003](../decisions/003-next-step.md)

이 문서는 기존 `user-confirm.md`의 11개 확정 결정을 다시 묻지 않고, 전체 작업 결과와 아직 사용자 입력이 필요한 항목만 한 곳에 정리한다. `Pending` 항목은 답변이 없다는 이유로 승인된 것으로 해석하지 않는다.

## 현재 판정

TASK-001~017은 지정 모델의 Codex 서브에이전트로 구현·검증을 완료했다. TASK-016에서 기술 prototype의 품질은 기준을 통과했지만, 동결한 효율 gate를 충족하지 못해 최종 판정은 `No-Go`다. TASK-017은 prototype과 실험 산출물을 보존하고 독립 엔진의 자동 제품화를 중단하는 후속 결정을 기록했다.

TASK-018~021은 계획에 따라 `Blocked`다. 이 상태에서는 새 서브에이전트를 실행하지 않는다. 필요한 외부 입력과 새 결정이 확보되면 해당 FUP를 먼저 `Confirmed`로 갱신하고, 새 ADR과 계획 상태를 갱신한 뒤 작업을 재개한다.

## TASK 상태와 모델 배정

| 작업 | 모델 / 추론 수준 | 상태 | 결과 또는 차단 사유 |
|---|---|---|---|
| TASK-001 | `gpt-5.6-sol` / Medium | Complete | pilot 프로토콜·로그 집계 계약 |
| TASK-002 | `gpt-5.6-terra` / Medium | Complete | 독립 정답 fixture·corpus 후보 |
| TASK-003 | `gpt-5.6-luna` / Low | Complete | 기존 도구 capability 조사 |
| TASK-004 | `gpt-5.6-sol` / High | Complete | 합성 fixture smoke; 실제 로그는 미지정 |
| TASK-005 | `gpt-5.6-sol` / High | Complete | 본 실험 기준과 Go/No-Go gate 동결 |
| TASK-006 | `gpt-5.6-terra` / Medium | Complete | .NET solution·trust 경계 |
| TASK-007~009 | `gpt-5.6-sol` / High | Complete | schema·저장소·C# 구축·검색 |
| TASK-010 | `gpt-5.6-sol` / Medium | Complete | resolve·validate·inspect |
| TASK-011~014 | `gpt-5.6-sol` / High | Complete | lifecycle·장애/보안·주석/영향·diff |
| TASK-015 | `gpt-5.6-sol` / Medium | Complete | MCP stdio·Claude lifecycle adapter |
| TASK-016 | `gpt-5.6-sol` / High | Complete — No-Go | 효율 gate 실패; token은 inconclusive |
| TASK-017 | `gpt-5.6-sol` / Medium | Complete | 범위 전환·후속 결정 기록 |
| TASK-018 | `gpt-5.6-sol` / High | Blocked | No-Go 이후 공통 재개 증거·FUP-006·새 ADR 필요 |
| TASK-019 | `gpt-5.6-sol` / High | Blocked | 공통 재개 증거·FUP-007·실제 Perforce 환경 필요 |
| TASK-020 | `gpt-5.6-sol` / Medium | Blocked | FUP-005의 정확한 원문·출처 필요 |
| TASK-021 | `gpt-5.6-sol` / High | Blocked | FUP-004 실측 수치·GC 승인 필요 |

`TASK-018~021`의 모델은 계획상 배정값이며, `Blocked` 규칙에 따라 실행하지 않았다. 서브에이전트는 커밋·push·PR·병합을 수행하지 않는다.

## No-Go 근거

본 실험은 30개 측정 cell을 시도했다. 18개는 성공했고, C/D 비교 조건 12개는 실행 불가로 기록했다. E 조건은 다음을 확인했다.

| 항목 | 결과 | 판정 |
|---|---:|---|
| 품질 기준 | 6/6 | 통과 |
| 참조 recall | 27/27 | 통과 |
| Critical stale·silent partial | 0 | 통과 |
| NAV source bytes | 9.75% 감소 | 20% gate 미달 |
| DIFF source bytes | 1,618.9% 증가 | gate 실패 |
| 실제 model input token | 측정되지 않음 | `inconclusive` |
| latency·memory·로컬 예산 | 상한 내 | 통과 |

따라서 품질·안전성은 prototype 보존 근거가 되지만, 제품화 Go 근거는 아니다. source-byte gate를 다시 판단하려면 대표 corpus, 실제 token 측정, 비교 기준 개선과 새 결정 기록이 필요하다.

## 후속 확인이 필요한 FUP

| ID | 현재 상태 | 사용자 또는 외부에서 제공할 내용 | 재개되는 작업 |
|---|---|---|---|
| FUP-001 | Partial | 실제 승인 로그의 경로·기간·필드, 실제 모델·token·비용 측정 설정. 지정 전 실제 로그를 읽거나 유료 실행하지 않음 | 실제 데이터 평가 |
| FUP-002 | Complete / Frozen | 품질 저하 0, Critical 0, 최소 3회, seed `20260920`, 20% 효율 후보, 30분·1 GiB·latency 상한 | TASK-005·016 기준 유지 |
| FUP-003 | Complete | 독립 C#/.NET prototype 완료; TASK-016 No-Go 후 자동 제품화 중단 | TASK-017 후속 결정 유지 |
| FUP-004 | Pending | retention 기간, cache 용량, GC 트리거와 삭제 승인 범위에 대한 실측 수치·승인 | TASK-021 |
| FUP-005 | Pending | `benchmark task/g` 뒤에 이어지는 정확한 원문과 출처(파일·리비전·URL 등) | TASK-020 |
| FUP-006 | Pending | 세 UI 시안 중 최종 방향과 frontend stack. CLI+Web 제공 결정 자체는 이미 확정 | TASK-018 |
| FUP-007 | Pending | submitted/shelved/pending CL별 base·target 계약, 실제 Perforce server/client/CL, 허용 read-only 명령 | TASK-019 |

### 입력 양식

아래 형식으로 필요한 항목만 채워서 회신할 수 있다. 비어 있는 항목은 계속 `Pending`으로 둔다.

```text
FUP-004
- retention: <기간>
- cache capacity: <용량 또는 산정식>
- GC trigger: <조건>
- destructive delete approved: <yes/no 및 범위>

FUP-005
- source: <파일·리비전·URL>
- missing text: <원문 또는 보완 문서>

FUP-006
- selected mock: <시안 식별자>
- frontend stack: <framework / language / build>

FUP-007
- server/client/workspace/CL: <값>
- base-target contract: <submitted/shelved/pending별 규칙>
- allowed read-only commands: <목록>
```

## 안전한 현재 기본값

- 자동 파괴적 GC는 승인 전까지 비활성화하고 삭제하지 않는다.
- FUP-005가 없으면 잘린 원문을 추정하지 않는다.
- `p4` 실행 환경과 실제 Perforce 계약이 없으면 adapter를 만들거나 성공으로 표시하지 않는다.
- TASK-016 결과만으로 Web UI·Perforce 제품화를 자동 재개하지 않는다.
- 실제 로그·원문·시크릿은 외부로 보내지 않으며, 보고서에는 승인된 비식별 집계만 남긴다.

## 검증 및 작업 트리

다음 검증은 완료됐다.

- locked restore와 Release build
- Core/CSharp/CLI/Integration 실행형 테스트
- storage·lifecycle·failure·security·diff·MCP 통합 검증
- fixture verifier 및 Windows PowerShell 5.1 검증
- benchmark verify-only 재실행

현재 feature branch에는 구현·문서 산출물이 작업 트리로 남아 있다. 이 확인 문서 작성만으로 커밋·push·PR·병합을 수행하지 않는다.
