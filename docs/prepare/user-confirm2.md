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
| FUP-004 | Confirmed | 기간/용량 기반 GC를 모두 제공하고 config에서 `age`/`capacity`/`hybrid`를 선택. 활성 generation 보호·dry-run 지원. 기본 retention/용량 수치는 실측 후 확정 | TASK-021 |
| FUP-005 | Complete / Obsolete | 잘린 원문 복구 요구를 폐기. 누락 사실과 사용자 결정을 코멘트로 보존하고 현재 benchmark 설계·구현을 기준으로 진행 | TASK-020 |
| FUP-006 | Confirmed | Sample 1 + Blazor/ASP.NET Core. CLI+Web을 유지하고 로컬 Web UI는 .NET stack으로 통일 | TASK-018 |
| FUP-007 | Confirmed / Environment Pending | CL 중심 read-only adapter. submitted/pending/shelved CL 및 workspace 상태를 지원하고 변경 명령은 자동 실행하지 않음. 실제 server/client/CL은 통합 테스트 시 제공 | TASK-019 |

### 입력 양식

아래 형식으로 필요한 항목만 채워서 회신할 수 있다. 비어 있는 항목은 계속 `Pending`으로 둔다.

```text
FUP-004 — Confirmed (2026-09-21)
- modes: age / capacity / hybrid
- retention: config로 관리; 기본값은 실측 후 확정
- cache capacity: config로 관리; 기본값은 실측 후 확정
- GC trigger: 선택한 mode에 따라 기간/용량/혼합 기준
- safety: 활성 generation 보호, dry-run 지원
- destructive delete approved: 위 cache GC 범위 내에서만 허용

FUP-005 — Complete / Obsolete (2026-09-21)
- decision: 잘린 `benchmark task/g...` 원문은 복구하지 않는다.
- comment: 원문 누락 사실과 복구하지 않기로 한 사용자 결정을 추적 정보로 남긴다.
- baseline: 현재 benchmark 설계·구현을 이후 기준으로 사용한다.

FUP-006 — Confirmed (2026-09-21)
- selected mock: Sample 1
- frontend stack: Blazor / ASP.NET Core / .NET
- intent: CLI 자동화 인터페이스와 로컬 Web 검사 UI를 함께 제공한다.

FUP-007 — Confirmed / Environment Pending (2026-09-21)
- server/client/workspace/CL: 실제 통합 테스트 시 제공
- base-target contract: CL 중심. submitted CL은 depot revision 기준, pending CL은 해당 CL의 opened workspace 파일, shelved CL은 shelf revision, 현재 workspace는 have revision + local opened 상태를 사용
- allowed read-only commands: `p4 info`, `p4 client`, `p4 opened`, `p4 changes`, `p4 describe`, `p4 files`, `p4 fstat`, `p4 print`, `p4 have`, `p4 where` 등 조회 계열
- prohibited automatic mutations: `sync`, `edit`, `revert`, `submit`, `unshelve` 등 workspace/depot 변경 명령
```

## 안전한 현재 기본값

- GC는 config의 `age`/`capacity`/`hybrid` 정책으로 동작하며 활성 generation을 보호하고 dry-run을 제공한다. 기본 retention/용량 수치는 실측 후 확정한다.
- 잘린 `benchmark task/g...` 원문은 복구하지 않으며, 누락 사실과 해당 결정을 코멘트로 보존한다.
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
