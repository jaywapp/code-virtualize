# User Confirmation

## 사용 방법

아래 11개 선택은 사용자 인터뷰로 확정됐다. Options/Recommendation은 당시 검토 기록이며 최신 기준은 각 User Decision이다. 남은 구체 입력은 문서 끝의 FUP 목록과 [plan.md](plan.md)의 Blocked By를 따른다.

상태는 `Pending`(미결정), `Confirmed`(사용자 선택과 날짜·근거 기록), `Deferred`(사용자가 후속 단계로 보류)로 관리한다. 응답이 없었다는 이유로 Confirmed/Deferred로 바꾸지 않는다. Deferred도 해당 기능의 구현 승인은 아니다.

사용자는 예를 들어 `UC-001 A, UC-002 A, UC-003 A`처럼 선택할 수 있다. 조건부 선택·다른 대안도 그대로 기록한다. UC-007은 수치·corpus·예산 입력이 함께 필요하다. 2026-09-20 계속 작업에서 design/architecture/plan과 UC-011의 Web UI 시안을 동기화한다. 이미 답한 항목을 재질문하지 않는다.

## 결정 요약

| ID | 주제 | 사용자 선택 | 상태 | 남은 조건 |
|---|---|---|---|---|
| UC-001 | 제품 방향 | A: 측정·비교 후 Go/No-Go | Confirmed | FUP-003 결과 기반 경로 선택 |
| UC-002 | 지원 범위 | A 조건부: Windows/C#/Git 먼저, Perforce 필수 후속 | Confirmed | FUP-007 Perforce 계약·환경 |
| UC-003 | Core | A: 엔진 선택 시 C#/.NET CLI | Confirmed | 엔진 Go 이후 적용 |
| UC-004 | 저장·수명 | A: 영속 JSON/JSONL cache + 세션 metadata | Confirmed | FUP-004 retention·용량·GC 실측 |
| UC-005 | baseline | C: VCS와 Session 모두 제공 | Confirmed | 별도 추가 선택 불필요 |
| UC-006 | 심볼 | A: L0/L1 전체 접근성, 원문 lazy resolve | Confirmed | 상세 반환 예산은 구현 계약 |
| UC-007 | 실험 | A: 승인 비식별 로그 + 공개/합성 paired 실험 | Confirmed | FUP-001/002 실행 설정·최종 수치 |
| UC-008 | trust | A: syntax-only 기본, 명시 trust 시 semantic | Confirmed | workspace별 trust 적용 |
| UC-009 | 연동 | A: CLI 검증 후 MCP stdio + 얇은 훅 | Confirmed | 실제 설치는 별도 실행 범위 |
| UC-010 | 잘린 원문 | B: 복구 전 관련 요구 확정 보류 | Confirmed | FUP-005 원문 보완 |
| UC-011 | 검사 화면 | A+B: CLI text/JSON + 로컬 Web UI | Confirmed | FUP-006 시안·frontend 선택 |

## UC-001 — 제품 방향과 개발 순서

### Context

원안은 C# Symbol PoC부터 만들고 토큰·시간 효과를 평가한다. 검토 의견은 기존 세션 측정 및 LSP/Serena 비교 후 필요하면 차별 기능만 개발하자고 제안한다. 어느 제안도 다른 제안에 대한 사용자 승인 기록은 없다.

### Options

- **A. 측정 → 기존 도구 비교 → Go/No-Go → C# PoC 또는 얇은 연동.** 중복 투자 위험을 줄이지만 즉시 엔진 구현은 늦어진다.
- **B. C# 엔진 PoC를 바로 진행하며 비교 실험을 병행.** 핵심 기술 불확실성을 빨리 확인하나 효용이 없을 때 폐기 비용이 커진다.
- **C. UE5/Perforce·리뷰 차별점에 바로 집중.** 실제 차별 가설에 가깝지만 compiler·빌드·비공개 corpus 복잡도가 가장 높다.

### Recommendation

A. 비교 단계에서 엔진을 만들지 않는 결론도 허용한다. 토큰 절감과 정확도는 함께 측정하고 어느 하나를 관측 없이 제품 강점으로 확정하지 않는다.

### Impact

TASK-005에서 비교 결과를 바탕으로 경로를 선택한다(FUP-003). 얇은 연동/중단이면 TASK-017에서 계획을 조정한다. Web UI와 필수 Perforce 요구는 변경 동의 없이 삭제하지 않는다.

### User Decision

- Status: Confirmed
- 선택: A
- 사용자 확인 근거·일자: 2026-09-19 인터뷰 — 측정 → 기존 도구 비교 → Go/No-Go → C# PoC 또는 얇은 연동.

## UC-002 — 첫 지원 범위

### Context

원안은 C# Roslyn PoC를 먼저 제시하고, 검토 의견은 C++/UE5·Perforce 영역의 차별성을 강조한다. 현재 저장소에는 테스트할 실제 프로젝트가 없다.

### Options

- **A. Windows의 C# + Git부터, UE5/Perforce와 다른 OS는 후속.** 작은 검증 범위를 갖지만 최종 관심 환경을 직접 증명하지 못한다.
- **B. C#은 기술 spike만 하고 첫 제품은 UE5/Perforce.** 목적에 가까운 검증이 가능하나 접근 가능한 프로젝트·툴체인·정답 corpus가 먼저 필요하다.
- **C. C#과 C++를 동시에 지원.** 공통 schema를 빨리 검증하지만 일정과 오류 원인 분리가 어려워진다.

### Recommendation

A. SDK, framework·solution 종류, generated code, 프로젝트 크기를 지원 표에 명시한다. C# 실험의 성공을 UE5 성공으로 일반화하지 않는다.

### Impact

1차 Windows/C#/Git을 TASK-002~016의 기준으로 삼고 Perforce는 TASK-019로 필수 후속 배정한다. C#/Git 실험을 UE5/C++ 효과로 일반화하지 않는다.

### User Decision

- Status: Confirmed
- 선택: A (조건부)
- 사용자 확인 근거·일자: 2026-09-19 인터뷰 — 1차는 Windows + C# + Git. Perforce 지원은 선택적 후보가 아니라 필수 후속 범위이며, C#/Git 결과를 UE5/C++로 일반화하지 않는다.

## UC-003 — Core 언어와 배포

### Context

npm은 원안에서도 launcher 후보다. 독립 엔진은 명시되어 있으나 Core 구현 언어·IPC·패키징은 미정이다.

### Options

- **A. C#/.NET Core + CLI, 로컬 실행부터.** Roslyn과 단일 runtime, PoC 단순화. .NET 설치 또는 self-contained 배포 고려가 필요하다.
- **B. TypeScript Core/npm launcher + .NET worker.** npm 배포와 다언어 worker 확장이 편하나 IPC·두 runtime 설치·버전 동기화가 추가된다.
- **C. 기존 도구를 호출하는 얇은 adapter.** 유지보수량이 적지만 기존 도구 계약·배포 정책에 의존한다.

### Recommendation

UC-001에서 엔진을 선택하면 A, 얇은 연동이면 C. `cv-*` 명령 이름은 보존하고 executable/subcommand alias 방식은 선택한 package 방식에 맞춰 확정한다.

### Impact

독립 엔진을 선택하면 TASK-006부터 .NET solution 구조를 적용한다. executable/alias·SDK 버전은 구현 계약으로 고정하며 실제 package 게시는 별도 요청이다.

### User Decision

- Status: Confirmed
- 선택: A
- 사용자 확인 근거·일자: 2026-09-19 인터뷰 — 독립 엔진 구현 시 C#/.NET Core + CLI를 사용한다. UC-001의 Go/No-Go 이후 적용한다.

## UC-004 — `.cv` 저장과 수명

### Context

원안은 full build 후 세션 종료 시 폐기한다. 검토 의견은 영속 cache를 추천한다. 공유 `current/` 한 개는 병렬 세션에서 덮어쓰기 위험이 있다.

### Options

- **A. 영속 content/config hash cache + 세션별 metadata, JSON manifest/JSONL `.cv` shards.** warm 비용과 검사 편의가 좋지만 GC·동시 접근·민감정보 보관을 관리해야 한다.
- **B. 세션마다 독립 구축·폐기, 같은 `.cv` 형식.** lifecycle이 단순하지만 cold 비용을 반복 지불한다. 세션별 디렉터리는 여전히 필요하다.
- **C. SQLite 기반 영속 cache + 세션 분리.** transaction·random lookup이 편하지만 schema migration·locking·inspection 비용이 생긴다.

### Recommendation

A를 PoC 후보로 두고 B와 cold/warm 차이를 측정한다. 저장 포맷은 최적화 전제이며 대규모 성능이 부족하면 C 전환을 별도 결정한다. `.cv` 확장자를 외부 export 계약으로 둘지 내부 format으로 둘지도 C 선택 시 명시한다.

### Impact

TASK-008/011에서 영속 cache·세션 분리·pin을 구현한다. retention/용량/GC 수치는 실측 후 TASK-021과 FUP-004로 확정하며 그 전 자동 파괴적 GC는 활성화하지 않는다.

### User Decision

- Status: Confirmed
- 선택: A
- 사용자 확인 근거·일자: 2026-09-19 인터뷰 — 영속 content/config hash cache + 세션별 metadata 분리. PoC는 JSON manifest/JSONL `.cv` shards로 시작하며 retention·용량·GC 수치는 실측 후 확정한다.

## UC-005 — diff와 리뷰 기준점

### Context

세션 시작 snapshot은 이미 수정된 workspace나 여러 세션에 걸친 변경 전체의 baseline이 아니다.

### Options

- **A. 명시 VCS base revision/CL + target.** 실제 작업 범위에 맞지만 revision·Perforce workspace mapping·dirty target 정의가 필요하다.
- **B. 세션 시작 snapshot만 비교.** 구현이 작지만 세션 이전 변경을 포함하지 않는다.
- **C. 두 모드 제공.** 목적별 선택이 가능하나 UX·테스트 조합이 늘어난다.

### Recommendation

A. 기본 base를 추측하지 않는다. B가 필요한 경우 `baselineKind=session`을 표시하는 보조 모드로 후속 추가한다.

### Impact

TASK-014에서 VCS/Session 두 모드를 모두 구현한다. Session은 시작 당시 source bytes를 보존하고, Perforce VCS 의미는 TASK-019/FUP-007에서 구체화한다.

### User Decision

- Status: Confirmed
- 선택: C
- 사용자 확인 근거·일자: 2026-09-19 인터뷰 — 명시 VCS baseline/target과 세션 시작 snapshot을 모두 지원하며 목적에 따라 선택한다.

## UC-006 — 심볼 깊이와 문맥 예산

### Context

원안의 visibility별 materialization과 L2/L3는 제안 단계다. private 멤버를 제외하면 탐색의 핵심 대상을 놓칠 수 있고, 메서드 body만 반환하면 주변 문맥이 부족할 수 있다.

### Options

- **A. L0/L1 모든 접근성의 선언 수집, 원문·주석은 lazy resolve.** 일관된 검색 범위를 제공하며 local/lambda ID는 초기 대상에서 제외한다.
- **B. 원안대로 visibility 기반 progressive 생성.** 초기 반환량을 줄일 수 있지만 지연 index·coverage 관리가 복잡하다.
- **C. L2/L3까지 선제 인덱싱.** 미세한 탐색이 가능하지만 측정되지 않은 저장·조회 복잡도가 커진다.

### Recommendation

A. exact/prefix/substring 결정적 검색과 pagination을 먼저 제공한다. header/using/containing type/body/full file은 선택적으로 확장하며 기본 반환 한도·전체 파일 재읽기율을 실험으로 조정한다.

### Impact

TASK-007/009/010/013의 schema·검색·원문/주석 resolve에 L0/L1 전체 접근성을 적용한다. source/result 예산은 계약에 후보를 기록하고 실험에서 검증한다.

### User Decision

- Status: Confirmed
- 선택: A
- 사용자 확인 근거·일자: 2026-09-19 인터뷰 — L0/L1 모든 접근성 선언을 인덱싱하고 원문·주석·body는 lazy resolve한다.

## UC-007 — 데이터 접근, 실험 예산과 Go/No-Go 기준

### Context

기존 세션 로그에는 원문·프롬프트·개인정보가 있을 수 있다. 실제 모델 실행은 비용을 발생시킨다. 원안에는 반복 횟수·효율 최소 차이·허용 품질 손실이 없고 예시 숫자만 있다.

### Options

- **A. 승인된 비식별 로그 + 고정 공개/합성 corpus + 다조건 paired 실험.** 재현성이 높지만 현실 프로젝트 대표성 한계가 있다.
- **B. 승인된 사내/개인 프로젝트를 로컬에서 추가 평가.** 현실성이 높지만 결과 공유·보관 범위를 엄격히 정해야 한다.
- **C. 합성 fixture와 프로토콜만 준비하고 실제 실험은 보류.** 비용은 작지만 제품 가치 판단은 할 수 없다.

### Recommendation

A를 기본으로 하고 B는 별도 지정한다. 원문 로그를 자동 수집하거나 외부 업로드하지 않는다. pilot 예시로 `3개 규모 × 4개 탐색/이해 task × 4개 기존 도구 조건 × 3회 = 144회`를 제안하되, 이는 승인된 실행량이나 충분한 통계 표본이 아니다. 비용이 크면 규모·task를 줄인 탐색 pilot부터 수행한다. CV 조건은 개발 후 추가한다.

### 반드시 채울 실행 설정

| 항목 | 사용자 결정/승인 전 상태 |
|---|---|
| 읽어도 되는 로그 경로·기간·필드 | 미정; 현재 실제 로그에 접근하지 않음 |
| repo/commit/라이선스·공개 가능한 결과 | 미정 |
| 모델·버전·reasoning·권한·cache 조건 | 미정 |
| 조건·task·반복 수, 무작위화 seed | 미정 |
| 최대 비용·wall time·중지 기준 | 미정 |
| 성공률·결함 recall 허용 저하 `deltaQuality` | 미정 |
| 비용/시간 개선의 최소 실용 차이 `minEfficiency` | 미정 |
| p95 latency·memory 상한 | 미정 |
| 결과 보관·삭제·공유 범위 | 미정 |

### Impact

실험 방향은 확정됐다. TASK-001에서 pilot을 설계하고 실제 로그/실행은 FUP-001 지정 후 TASK-004에서 수행한다. pilot 결과로 본 실험 수치(FUP-002)를 고정한 뒤 TASK-016에서 CV를 평가한다.

### User Decision

- Status: Confirmed
- 선택: A
- 사용자 확인 근거·일자: 2026-09-19 인터뷰 — 승인된 비식별 로그 + 공개/합성 corpus 기반 paired 실험. 반복 수·효율/품질 기준·예산은 pilot 설계/결과 후 확정한다.

## UC-008 — 프로젝트 로드와 실행 신뢰

### Context

Roslyn semantic 분석에 필요한 project evaluation·빌드 도구·generator는 단순 파일 읽기를 넘어설 수 있다. untrusted repository를 자동 실행하면 로컬 보안 경계가 달라진다.

### Options

- **A. 기본 syntax-only, 명시적으로 신뢰한 workspace에서 semantic load.** 실행 경계가 분명하나 초기 분석이 제한된다.
- **B. 모든 repository를 격리 worker에서 semantic load.** 격리 설계와 운영 비용이 추가되며 완전한 sandbox 검증이 필요하다.
- **C. 신뢰된 로컬 프로젝트만 제품 지원.** 범위가 단순하지만 일반 repository 탐색 용도가 제한된다.

### Recommendation

A. trust는 workspace 설정이 스스로 부여하지 못하게 한다. network restore/build/generator 실행 범위는 사용자 선택에 포함하며 syntax-only 결과는 semantic 완전 결과로 표시하지 않는다.

### Impact

TASK-006/009/012에서 syntax-only 기본과 명시 trust 경계를 구현·검증한다. workspace 설정이 스스로 trust를 부여할 수 없게 한다.

### User Decision

- Status: Confirmed
- 선택: A
- 사용자 확인 근거·일자: 2026-09-19 인터뷰 — 기본 syntax-only, 사용자가 명시적으로 신뢰한 workspace에서만 semantic load한다.

## UC-009 — Claude 연동과 설치 범위

### Context

원안은 MCP/tool/hook 노출을 미결정으로 남겼다. global 설치와 workspace 설정 변경은 별개다.

### Options

- **A. PoC CLI부터 검증 후 MCP stdio + lifecycle 훅.** Core를 독립 검증할 수 있지만 연동 단계가 따로 생긴다.
- **B. 초기부터 Claude plugin/MCP에 통합.** 실제 agent 사용을 빨리 관찰하지만 오류 원인·토큰 고정비가 늘어난다.
- **C. CLI + 사용 지침만 제공.** 연동이 작지만 lifecycle 자동화는 부족하다.

### Recommendation

A. `cv_find`, `cv_get`부터, reference 구현 후 `cv_impact`를 노출한다. 훅 실패·timeout 시 기존 도구 사용을 막지 않는다. source freshness는 훅 성공 여부와 독립 검증한다.

### Impact

TASK-015에서 CLI 검증 후 MCP stdio·얇은 lifecycle hook을 구현한다. 기존 설정 병합·dry-run·uninstall 복원을 검증하고 실제 설치 범위는 실행 시 지정한다.

### User Decision

- Status: Confirmed
- 선택: A
- 사용자 확인 근거·일자: 2026-09-19 인터뷰 — PoC CLI를 먼저 검증한 뒤 MCP stdio + 얇은 lifecycle hook으로 Claude Code와 연동한다.

## UC-010 — 원문 말미 누락

### Context

원안 15절이 `benchmark task/g`에서 잘려 있다. 검토 의견에도 같은 사실이 기록되어 있다.

### Options

- **A. 현재 3개 문서를 기준으로 준비를 진행하고 누락 내용을 추후 별도 반영.** 작업을 계속할 수 있지만 미전달 요구가 있을 수 있다.
- **B. 원문 작성자가 보완한 후 관련 요구를 확정.** 의도를 보존하지만 해당 부분은 대기한다.

### Recommendation

A. 누락 내용을 임의로 완성하지 않는다. 현재 작성된 benchmark 설계는 이 문서의 추천안이며 원문 복원본이 아니다.

### Impact

TASK-020에서 복구 근거를 확인한다. FUP-005는 누락 부분 관련 요구만 차단하며 기존에 명시된 설계·시안·프로토콜 준비를 막지 않는다.

### User Decision

- Status: Confirmed
- 선택: B
- 사용자 확인 근거·일자: 2026-09-19 인터뷰 — 잘린 원문을 복구하기 전까지 누락 부분과 관련된 요구사항 확정을 보류한다.

## UC-011 — cv-inspect의 화면 형태

### Context

아이디어의 사람용 기능은 `cv-inspect` debugger다. 웹/데스크톱 GUI 요구는 없다. 현재 준비 문서는 text/JSON CLI로 해석하고 그래픽 UI 시안을 생략했다.

### Options

- **A. 초기에는 터미널 text/JSON만.** 현재 CLI 흐름과 맞고 구현 범위가 작다.
- **B. 로컬 웹 검사 화면 포함.** graph·diff를 탐색하기 편하지만 서버·경로 접근·frontend 테스트 범위가 늘어난다.
- **C. IDE/데스크톱 화면 포함.** 사용 맥락은 좋지만 플랫폼·배포·연동 결정을 추가해야 한다.

### Recommendation

A. B/C를 선택하면 승인된 화면 범위를 바탕으로 정보 밀도 중심·순차 탐색 중심·영향 관계 중심의 시안 3종을 먼저 제시한다.

### Impact

이번 prepare에서 세 시안을 생성한다. 제품 로컬 Web UI는 TASK-018, 최종 시안/frontend 선택은 FUP-006에 연결한다. CLI는 TASK-010, IDE/데스크톱 UI는 제외한다.

### User Decision

- Status: Confirmed
- 선택: A+B
- 사용자 확인 근거·일자: 2026-09-19 인터뷰 — 터미널 text/JSON과 로컬 Web UI를 모두 제공한다. CLI는 자동화/AI 연동, Web UI는 `.cv`·심볼 관계·diff의 사람용 탐색/검사에 사용한다. IDE/데스크톱 UI는 제외한다.

## 후속 입력과 실행 조건

2026-09-20 동기화: 위 11개 결정은 모두 Confirmed이며 다시 선택할 필요가 없다. 아래 표는 이미 선택한 방향을 실행하기 위한 구체 입력 또는 결과 기반 판단이다. 합성 smoke 완료와 실제 승인 로그·모델 비용 평가를 구분하며, 미입력 상태를 승인으로 바꾸지 않는다.

| ID | 상태 | 필요한 정보/근거 | 영향 TASK |
|---|---|---|---|
| FUP-001 | Partial | 합성 fixture smoke는 2026-09-20 완료. 실제 승인 로그 경로·기간·필드와 실제 모델 token/비용 평가는 미완이며 별도 지정 전 실행하지 않음 | TASK-004 합성 범위 완료; 실제 데이터 평가 |
| FUP-002 | Complete / Frozen — 2026-09-20 | 품질 저하 0, Critical 0, task·조건별 최소 3회, seed `20260920`, 20% 효율 후보, 유료비용 0, 30분 및 자원 상한. 아래 동결 표와 ADR 001 적용 | TASK-005, TASK-016 |
| FUP-003 | Complete — 2026-09-20 | 독립 C#/.NET engineering prototype 구현은 완료. TASK-016은 source-byte gate 실패로 최종 No-Go이며 기술 artifact 보존·제품화 자동 진행 중단 | TASK-005~017 완료; TASK-018/019/021 재개 조건부 Blocked |
| FUP-004 | Pending | 실측에 근거한 retention·용량·GC 수치. 초기에는 자동 파괴적 GC 비활성 | TASK-021 |
| FUP-005 | Pending | 작성자가 보완한 잘린 원문의 정확한 내용과 출처 | TASK-020, 누락 내용을 전제하는 요구 |
| FUP-006 | Pending | 시안 3종 중 최종 방향과 frontend stack. CLI+Web 제공 자체는 재논의하지 않음 | TASK-018 |
| FUP-007 | Pending | Perforce submitted/shelved/pending CL별 base/target 계약과 실제 server/client/CL·허용 read-only 명령 | TASK-019의 실서버 연동 |

### FUP-002 동결 수치

| 항목 | 동결 값 |
|---|---|
| 품질 | 독립 fixture 기준 stronger available baseline 대비 허용 저하 `0` |
| 안전성 | stale 원문 오반환·조용한 partial/coverage 누락 Critical `0` |
| 반복·순서 | task·조건별 최소 `3회`, 고정 seed `20260920` |
| 효율 후보 | 품질 통과 stronger available baseline 대비 paired 중앙값 source bytes 또는 실제 input token `20% 이상` 감소 |
| token 판정 | 실제 input token 미측정이면 token 이득 주장 금지, `inconclusive` |
| 작은 fixture latency | cold build `60초`, warm query `2초`, incremental update `5초` 이하 |
| 작은 fixture memory | engine+harness 자식 프로세스 peak working set 합계 `1 GiB` 이하 |
| 예산 | 외부 유료비용 `0`, fixture 검증·전 조건·집계를 포함한 로컬 wall time `30분` 이하 |

선정·측정 규칙과 상한의 이유는 [ADR 001](../decisions/001-product-path.md)을 따른다. unavailable인 C/D를 0 비용·0 읽기로 대체하지 않으며 실제 승인 로그·실제 모델 비용 평가는 FUP-001의 미완 범위다.

FUP-005 확인: 저장소 원안과 로컬 위키 원본을 2026-09-20 확인했으며 둘 다 `benchmark task/g`에서 끝난다. 복구됐다고 간주하지 않는다. 누락과 무관하게 명시된 요구·시안·프로토콜 설계는 진행한다.

### 동기화 기록

- 반영 기준: 사용자 인터뷰 기록 커밋 `b16df53`과 2026-09-20 전체 계획 구현 지시.
- 이번 작업에서 위 11개 `User Decision` 블록의 선택·근거·날짜와 본문은 변경하지 않았다. FUP 표·동결 수치·동기화 기록만 갱신했다.
- 제품 경로 결정: [ADR 001](../decisions/001-product-path.md)의 prototype 승인, [ADR 002](../decisions/002-validation-outcome.md)의 본 실험 No-Go, [ADR 003](../decisions/003-next-step.md)의 prototype 보존·제품화 자동 진행 중단을 순서대로 적용한다.
- 최신 적용 문서: [design.md](design.md), [architecture.md](architecture.md), [plan.md](plan.md).
- UI 비교 자료: [시안 목록](samples/index.html). 시안의 데이터와 동작은 합성이며 제품 구현 결과가 아니다.
