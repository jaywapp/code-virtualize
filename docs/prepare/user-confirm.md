# User Confirmation

## 사용 방법

아래는 문서 간 충돌이나 비용·범위·보안에 영향을 주는 결정이다. **추천안은 아직 사용자 결정이 아니다.** 이번 준비 문서 작성은 이 항목들의 응답을 기다리지 않고 완료하되, 후속 구현은 [plan.md](plan.md)의 `Blocked By`를 따른다.

상태는 `Pending`(미결정), `Confirmed`(사용자 선택과 날짜·근거 기록), `Deferred`(사용자가 후속 단계로 보류)로 관리한다. 응답이 없었다는 이유로 Confirmed/Deferred로 바꾸지 않는다. Deferred도 해당 기능의 구현 승인은 아니다.

사용자는 예를 들어 `UC-001 A, UC-002 A, UC-003 A`처럼 선택할 수 있다. 조건부 선택·다른 대안도 그대로 기록한다. UC-007은 수치·corpus·예산 입력이 함께 필요하다. 결정 후 영향받는 design/architecture/plan을 함께 갱신해야 한다.

## 결정 요약

| ID | 주제 | 추천 | 상태 | 차단 범위 |
|---|---|---|---|---|
| UC-001 | 제품 방향과 개발 순서 | 측정·기존 도구 비교 후 엔진 여부 결정 | Pending | 비교 결과 이후 구현 경로 |
| UC-002 | 첫 지원 언어·OS·VCS | C# + Windows + Git, UE5/Perforce 후속 | Pending | 제품·corpus 지원 범위 |
| UC-003 | Core 언어·배포 | 단일 .NET Core/CLI PoC | Pending | scaffold·package·배포 |
| UC-004 | 데이터 수명·저장·동시 세션 | 영속 해시 cache + 세션 분리, JSON `.cv` PoC | Pending | store·update·GC |
| UC-005 | 리뷰 baseline | 명시한 revision/CL과 target | Pending | diff·review |
| UC-006 | 심볼 깊이·검색·문맥 | L0/L1 전체 접근성, body lazy resolve | Pending | 검색·추출·resolve |
| UC-007 | 데이터·실험·진행 기준 | 사전 등록한 다조건 paired 실험 | Pending | 실제 로그 분석·모델 실험·Go/No-Go |
| UC-008 | 프로젝트 실행 신뢰 | syntax-only 기본, semantic load는 명시 trust | Pending | project loader·보안 정책 |
| UC-009 | Claude 연동 방식 | PoC CLI, 검증 후 MCP stdio + 얇은 훅 | Pending | 플러그인·설치·자동화 |
| UC-010 | 잘린 원문 추가 입력 | 현재 내용으로 진행, 추가 내용은 별도 반영 | Pending | 누락 내용을 전제한 기능만 |
| UC-011 | 사람용 검사 화면 | 터미널 text/JSON, GUI 후속 | Pending | GUI·시안·GUI 구현 |

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

design의 Goals/Features, architecture 전체 경로, TASK-005 이후 작업을 변경한다. A에서 기존 도구가 충분하면 TASK-006~016의 독립 엔진 경로를 실행하지 않고 TASK-017로 계획을 축소한다.

### User Decision

- Status: Pending
- 선택: 미정
- 사용자 확인 근거·일자: 없음

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

TASK-002~006의 범위와 architecture adapter·build matrix를 갱신한다. B/C 선택 시 C++ compile database·UHT·Blueprint·Perforce 권한과 baseline 정의를 별도 설계한 뒤 작업을 추가한다.

### User Decision

- Status: Pending
- 선택 및 지원 OS/VCS: 미정
- 사용자 확인 근거·일자: 없음

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

architecture의 Directory Structure/Build와 TASK-006~015의 예상 경로를 갱신한다. 최초 package registry 게시와 전역 설치는 별도 승인 대상이며 이 선택만으로 게시하지 않는다.

### User Decision

- Status: Pending
- 선택·실행 파일 형태·배포 대상: 미정
- 사용자 확인 근거·일자: 없음

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

TASK-008/011/012/016, architecture Data Model/State Management/Directory Structure. 선택 시 retention 기간, 최대 용량, 활성 session pin, 수동 purge 범위를 함께 정한다. GC가 config·원본·다른 session을 삭제해서는 안 된다.

### User Decision

- Status: Pending
- 선택·retention·용량·purge 범위: 미정
- 사용자 확인 근거·일자: 없음

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

TASK-014/016, architecture BaselineProvider와 design S-04. Perforce 선택 시 submitted/shelved/pending CL별 base file revision과 target 읽기 방법을 구현 전에 따로 확정한다.

### User Decision

- Status: Pending
- 선택·base/target 의미: 미정
- 사용자 확인 근거·일자: 없음

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

TASK-007/009/010/013, schema·검색 정렬·context expansion 계약. default limit·max bytes/lines·timeout은 TASK-007에서 후보를 제시하고 UC-007 실험 전 고정한다.

### User Decision

- Status: Pending
- 선택·필수 심볼 종류·문맥 정책: 미정
- 사용자 확인 근거·일자: 없음

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

TASK-001은 protocol/집계 schema만 준비할 수 있고 실제 로그 접근은 TASK-004의 차단 조건이다. TASK-004/005/016/017 및 성공 기준을 갱신한다. 근거가 부족하면 결론을 `inconclusive`로 내며 작은 성공률 차이를 확정 이득으로 주장하지 않는다.

### User Decision

- Status: Pending
- 선택 및 위 실행 설정: 미정
- 사용자 확인 근거·일자: 없음

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

TASK-006/009/012와 architecture Security/Configuration. build output/generated code가 없어지는 coverage 영향도 문서화한다.

### User Decision

- Status: Pending
- 선택·신뢰 부여 위치·허용 실행/네트워크: 미정
- 사용자 확인 근거·일자: 없음

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

TASK-015, integration contract, package/설치 계획. 전역 설정 변경은 dry-run·병합·uninstall 계약을 준비한 뒤 실제 설치 시 별도로 요청한다. 이번 선택은 설치 실행 권한이 아니다.

### User Decision

- Status: Pending
- 선택·global/workspace 설치 선호: 미정
- 사용자 확인 근거·일자: 없음

## UC-010 — 원문 말미 누락

### Context

원안 15절이 `benchmark task/g`에서 잘려 있다. 검토 의견에도 같은 사실이 기록되어 있다.

### Options

- **A. 현재 3개 문서를 기준으로 준비를 진행하고 누락 내용을 추후 별도 반영.** 작업을 계속할 수 있지만 미전달 요구가 있을 수 있다.
- **B. 원문 작성자가 보완한 후 관련 요구를 확정.** 의도를 보존하지만 해당 부분은 대기한다.

### Recommendation

A. 누락 내용을 임의로 완성하지 않는다. 현재 작성된 benchmark 설계는 이 문서의 추천안이며 원문 복원본이 아니다.

### Impact

준비 문서·synthetic corpus·기존 도구 조사는 차단하지 않는다. 새 정보가 들어오면 TASK-005에서 영향 분석하고 design/architecture/plan을 개정한다. 누락된 요구가 이미 결정됐다고 가정하는 구현은 금지한다.

### User Decision

- Status: Pending
- 선택·추가 내용: 미정
- 사용자 확인 근거·일자: 없음

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

현재의 CLI 계약·TASK-001~017을 막지 않는다. GUI는 TASK-018이 차단되며 B/C 결정 후 `impeccable`과 `design-taste-frontend`를 읽고 design/architecture/plan에 신규 UI 범위를 반영한다. 현재 시안이 존재하거나 GUI가 확정됐다고 보고하지 않는다.

### User Decision

- Status: Pending
- 선택: 미정
- 사용자 확인 근거·일자: 없음
