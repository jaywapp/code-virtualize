# ADR 005 — source-byte gate 재측정 protocol revision 2

- 상태: Proposed — G2(사용자 승인) 뒤 Accepted
- 작성일: 2026-09-23
- 적용 범위: TASK-027(새 corpus 정답), TASK-029(runner 확장·재측정), TASK-030(ADR 006 판정). 운영 규칙은 [protocol.md의 Revision 2 절](../../benchmarks/protocol.md#revision-2--mediumlarge-재측정-프로토콜-task-024)에 있다
- 결정 요약(제안): **medium 2개(LiteDB, Quartz.NET)·large 1개(aspnetcore)와 small 회귀 기준으로, B와 E를 모두 이상적인 범위 읽기로 고정해 측정한다. 받은 원문 텍스트는 도구와 무관하게 모두 세고, 품질은 센 텍스트로만 판정한다. corpus × {NAV, DIFF} 8개 셀이 모두 20% 이상 감소해야 Go다.** FUP-002 동결 수치는 바꾸지 않는다. medium·large의 latency·memory·wall time은 새 기준으로 제안한다.

## 맥락

- [ADR 002](002-validation-outcome.md)는 small 합성 corpus에서 E가 품질·안전성을 통과했지만 source bytes가 NAV 9.75% 감소, DIFF 1,618.9% 증가로 20% gate에 실패해 No-Go로 판정했다. 이 결정은 Accepted 그대로 둔다. 이번 측정은 새 실험이다.
- [ADR 003](003-next-step.md) 재개 조건 1·2는 medium/large corpus, 개선한 반환 계약, 결과 전에 고정한 protocol을 요구한다. 2026-09-23 사용자는 재개 경로(G1)를 "NAV·DIFF source-byte 계약 개선 + medium/large 공개 C# corpus protocol revision"으로 확정했다.
- [TASK-025 계약](../contracts/source-byte-contract-proposal.md)은 승인됐다(`part=declaration`, `contentHash`/`ifNoneMatch`/`notModified`, context 1 line hunk, added/deleted `header_only`, `DiffSourceResolver` lazy resolve). E 호출 패턴 고정(U7)과 측정 비대칭 R1~R4 처리는 이 ADR로 넘어왔다.
- [TASK-023](../../benchmarks/corpus-candidates.md)이 후보 12개를 조사했다. 조사 측 추천은 medium Hangfire·LiteDB·Quartz.NET, large aspnetcore였다.

### 코드에서 확인한 측정 비대칭 (`benchmarks/runs/CvEvaluationRunner.cs`)

| ID | 사실 | 근거 |
|---|---|---|
| R1 | B DIFF는 품질 판정(`DiffExpected`)에 `git diff` 두 번의 출력을 쓰면서 source bytes에는 세션 base 10~13행 slice만 넣었다. 같은 구조로 B NAV도 판정에 쓴 `rg -n` 출력 4개를 세지 않았다. E DIFF는 `BaseSource`·`TargetSource` 전체를 셌다 | `:227-237`, `:246-266`, `:614-615` |
| R2 | stale 검출·update·재조회 시나리오가 E NAV에만 있고 재조회 213 B를 E에 더했다. B에는 대응 단계가 없다 | `:429-484` |
| R3 | 기록값은 LF 기준이다. CRLF checkout에서는 E만 커진다(NAV 440 B, DIFF 1,334 B). B는 `ReadAllLines` 뒤 `\n` join이라 그대로다 | `:232-235`, `:260-263`, TASK-025 2.1절 |
| R4 | E NAV 품질은 `cv_get`의 `!IsError`만 본다. NAV-03이 요구하는 `BuildLabel` 원문을 E는 조회하지 않았다 | `:490` |
| U7 | E NAV는 `cv_get part=context contextLines=2`를 두 번 호출했다. 계약 개선 효과는 호출 패턴을 고정해야 측정된다 | `:398-405`, `:472-479` |

이 ADR을 쓰는 동안 새 corpus에서 CV, runner, 조건 A~E를 실행하지 않았다. GitHub API로 pinned commit 존재, 기본 브랜치, license SPDX, 보관 여부만 다시 조회했다.

## 결정 (제안)

세부 규칙은 protocol R2 절이 정본이다. 여기에는 결정과 이유를 적는다.

### 1. corpus 선정 (R2-1)

| alias | 저장소 @ commit | license | 등급 |
|---|---|---|---|
| `small-synthetic` | 이 저장소 fixture | — | small (회귀 기준) |
| `medium-litedb` | litedb-org/LiteDB @ `0fd277aaed127b9dec99524277fb4173a2351167` | MIT | medium |
| `medium-quartznet` | quartznet/quartznet @ `1e039471fc457f6efa1d3bf1224324b82236b759` | Apache-2.0 | medium |
| `large-aspnetcore` | dotnet/aspnetcore @ `7cc41c501115f365c0b7f1b50ebaa134a328f0e5` | MIT | large |

- **선정 기준은 결과와 무관한 속성만 쓴다**: 라이선스, 등급 구간, 커밋된 generator 산출물의 양, 최근 이력, 로컬 부담, 같은 등급 안의 규모 차이. LiteDB(5.2 MB)와 Quartz.NET(14.5 MB)은 medium 구간의 아래와 위를 덮는다. aspnetcore는 large 후보 중 generator 의존과 크기가 가장 작다.
- **조사 측 추천과 다른 점**: Hangfire를 주 corpus에서 빼고 예비로 돌렸다. LGPL-3.0은 수정하지 않은 원문을 로컬에서 읽기만 하는 이번 사용에 의무가 생기지 않지만, 공개 저장소 규칙을 permissive 하나로 단순하게 두려는 것이다. 빈 자리는 Quartz.NET으로 채웠다.
- **라이선스 수용 기준**: 주 corpus는 OSI permissive(MIT·BSD·Apache)만, LGPL은 예비만, 비OSI(Six Labors Split)와 reciprocal(RPL-1.5)은 제외한다. 어떤 corpus든 원문을 저장소에 복사하지 않고 정답은 좌표와 hash로 저장한다.
- **대체**: 조건을 실행하기 전에 판단할 수 있는 사유(license 불일치, 취득 실패, 등급 구간 밖, task 부족)로만 대체한다. CV의 build 실패·시간 초과·품질 실패는 결과이므로 대체 사유가 아니다. 대체 순서를 미리 정했다(medium: Polly → Hangfire → Serilog, large: runtime → roslyn).
- **학습 노출**: 조건 A~E가 모두 모델 없는 결정적 스크립트라 source-byte 측정에 영향이 없다. 그래서 선정 기준에서 뺐고 기록만 한다. 후속 실제 모델 token 측정(FUP-001)에서는 한계로 다룬다.
- **large는 1개**: 두 번째 large는 wall time 한도를 넘길 위험이 크고, 후보 셋이 모두 dotnet 조직의 같은 코드 관례를 공유해 독립성이 작다.

### 2. 등급 정의 (R2-1)

파일 수, C# bytes, `.csproj` 수, 선언 심볼 수, 이름 참조 수로 정의한다. **등급 배정은 앞의 세 지표로 한다.** 심볼·참조는 clone 없이 알 수 없어 TASK-023에서 모두 `미확인`이었다. 결과 전에 모르는 값으로 corpus를 바꾸는 일을 막기 위해 두 지표는 기대 구간과 비교해 편차만 보고한다. 계수 도구는 `CodeVirtualize.*`를 참조하지 않는 독립 Roslyn syntax 스크립트다.

### 3. task 설계 (R2-2)

- **composite task**: NAV는 한 대상 심볼에 대해 DECL → SRC → REF → RECOVERY(편집 두 번), DIFF는 한 커밋 쌍에 대해 VCS → Session → base resolve를 묶는다. small의 NAV·DIFF task 구성과 같다. 단계 유형별 task 수를 따로 정하면 그 비율이 결과를 움직이므로, 모든 task가 모든 단계를 한 번씩 갖게 했다.
- **개수**: 새 corpus마다 NAV 6, DIFF 6. 그중 NAV 1·DIFF 1이 tuning, 나머지 5·5가 held-out이다. 셀 중앙값을 5개 task로 계산한다.
- **NAV 대상**: 독립 syntax 목록의 일반 method 중 이름이 corpus에서 2~40줄에 나타나는 것을 seed `20260920`으로 뽑는다. 40줄 상한은 사람이 참조 정답을 모두 직접 판정할 수 있게 하려는 것이다. 이 제한은 B의 grep 결과를 작게 만들어 CV에 불리한 쪽이다.
- **DIFF 커밋**: pinned commit에서 first-parent 이력을 거슬러 적격 커밋(`.cs` 1~10파일, 2~400줄, 공백만의 변경 아님)을 찾고 최신부터 (c, c') 쌍 6개를 만든다. HEAD는 c의 parent, 세션 시작 작업 트리는 `tree(c)`, 현재는 `tree(c')`다. 이렇게 하면 small의 "세션 전 dirty 변경은 VCS에만 보이고 Session에서는 빠진다"(V-01/S-01)를 실제 커밋으로 재현한다. 커밋 내용을 보고 고르지 않는다.
- **독립 정답(TASK-027)**: 사람이 pinned 원문을 읽어 판정한다. CV 출력과 runner 조건의 명령 출력을 정답에 쓰지 않는다. static·dynamic·unresolved를 분리한다. 25%(corpus당 최소 10개) 무작위 표본을 다른 세션이 원문으로 교차 확인한다.

### 4. 조건 정의 (R2-3)

- **B와 E를 모두 "이상적인 범위 읽기"로 정의한다.** B는 단어 grep 한 번으로 선언·참조 후보를 얻고, 선언은 brace 균형 읽기로 정확히 그 범위만 읽고, 삭제된 선언이 이미 diff에 온전히 있으면 다시 읽지 않는다. E는 계약이 허용하는 가장 작은 반환(`declaration`, `ifNoneMatch`, `header_only`, resolve 1회)을 쓴다. 실제 에이전트보다 낭비가 적은 쪽으로 양쪽을 같게 맞춰, 한쪽의 약한 행동이 결과를 만들지 않게 했다. B 행동이 실제 에이전트보다 강하므로 이 정의는 CV에 보수적이다.
- A는 grep + 결과 파일 전체 read, C/D는 rev1과 같은 탐색 후 unavailable 처리다.
- 모든 조건 행동은 task card 입력만으로 결정되는 알고리즘이고 task별 수동 조정이 없다. 파라미터는 tuning 결과로도 바꾸지 않는다.

### 5. U7 — E 호출 패턴 고정

protocol R2-3의 E 표로 고정한다. 요점은 다음과 같다.

- member 원문은 `cv_get part=declaration`(`maxBytes=65536`, `maxLines=2000`). 기본 part(`header`)는 계약대로 바꾸지 않되 runner가 명시한다.
- 편집 뒤 재조회는 첫 호출에서 `SOURCE_STALE`을 확인하고, `cv-update` 뒤 `ifNoneMatch=<이전 contentHash>`로 한다.
- 영향 후보는 `cv_impact` depth 1, 동적 후보 포함, corpus 전체 파일을 예산으로 주고 모든 페이지를 받는다. 예산 잘림으로 E recall이 떨어지는 일을 막는 동시에 그 비용은 latency 예산이 잡는다.
- diff는 `DiffEvidenceOptions` 기본값(context 1, entry 8 KiB, 전체 64 KiB)과 `DiffSourceResolver(Base, declaration)` 1회. VCS는 `GitBaselineProvider`, Session은 `SessionSnapshotStore` 경로를 쓴다. small의 generation-pair 근사는 쓰지 않는다.
- 이유: 계약 개선 효과는 호출 패턴을 고정해야 측정되고(TASK-025 N3), 결과를 본 뒤 패턴을 고르면 선택 편향이 된다.

### 6. 계상 규칙과 R1~R4 (R2-4)

**원칙을 채택한다: 도구와 무관하게 조건이 받은 소스 유래 텍스트는 모두 source bytes로 세고, 품질 판정은 센 텍스트와 위치 메타데이터만 쓴다.**

| ID | 결정 | 이유 |
|---|---|---|
| R1 | A·B의 `rg -n` hit 줄 내용과 `git diff` hunk 본문 줄, E의 hunk·`header_only` 본문 줄을 같은 규칙으로 센다. diff 헤더와 `path:line:` 접두는 모든 조건에서 세지 않는다 | "둘 다 제외"는 E의 diff evidence도 빼야 해서 E가 받는 원문 대부분이 사라진다. 이 경우가 E에 유리하다. 판정에 쓴 텍스트를 계상에서 빼는 일이 구조적으로 불가능해지는 쪽은 "둘 다 셈"뿐이다 |
| R1 보완 | E 결과의 `signature`(와 remark 원문)도 센다. `qualifiedName`·경로·span·id는 세지 않는다 | `signature`는 선언 줄에서 만든 코드 텍스트이고 B의 grep 줄과 같은 역할을 한다. 세지 않으면 E의 DECL·REF 단계가 정의상 0 B가 된다 |
| R2 | 편집 두 번(span 밖 1회, span 안 1회)을 A·B·E 모두에 주고 각 조건의 재조회 비용을 모두 센다 | 복구를 빼면 CV의 조건부 재조회 이득과 staleness 안전성 측정이 사라지고, E에만 두면 E에 불리하다. span 밖 편집만 두면 `notModified`가 늘 이득을 내 E에 유리하고, span 안만 두면 반대다. 둘을 한 번씩 둔다 |
| R3 | 줄 단위 출력은 줄마다 내용 + LF 1 byte, CV 문자열은 CRLF·CR→LF 뒤 UTF-8 bytes. BOM 제외. raw 값도 기록 | checkout 설정이 판정을 바꾸지 않게 한다 |
| R4 | N2·N4·D3는 받은 텍스트에 정답 선언이 들어 있어야 통과다. small NAV-03은 E가 `BuildLabel` 원문을 실제로 받아야 통과다 | 반환을 줄이는 계약에서 과소 조회를 품질이 잡아야 한다 |

`toolOutputBytes`(JSON envelope 포함 전체 출력)는 보고만 한다. FUP-002의 효율 지표는 source bytes 또는 실제 input token이므로 전체 출력 bytes를 gate로 추가하지 않는다. 대신 ADR 006은 두 값을 나란히 적고, source bytes 통과로 context·token 절감을 주장하지 않는다.

### 7. 판정 규칙 (R2-8)

- task별 baseline은 ADR 001 규칙(품질 통과 조건 중 source bytes 중앙값 최소, 동률은 D·C·B·A)대로 고른다.
- **B 실패 보호**: B가 품질 실패한 task는 efficiency 계산에서 뺀다. B 실패 때문에 전체 read인 A와 비교되어 E가 이기는 경우를 막는다.
- task 값은 반복별 paired 감소율의 중앙값, 셀 값은 held-out task 값의 중앙값이다. 셀은 `(corpus, NAV|DIFF)` 8개다.
- **Go는 8개 셀 모두 20% 이상, 품질 저하 0, Critical 0, 모든 측정이 등급 예산 이하, 유료비용 0, 계획 셀 570개 모두 attempted일 때만이다.** 하나라도 실패하면 No-Go, 실패 없이 빠진 값이 있으면 Inconclusive(재개 불가)다.
- **small과 새 corpus의 결합**: small은 TASK-025 계약이 small 수치를 보고 설계됐으므로 오염된 회귀 기준이다. small 통과는 Go 근거가 되지 않지만 small 실패는 Go를 막는다. 셀 사이 평균·상쇄는 없다. 어느 셀이 통과했는지 보고 결합 방식을 고를 여지가 없다.
- token은 FUP-001 입력이 없으면 `inconclusive`이고 Go는 "B 대비 source-byte Go, token inconclusive, C/D unavailable"로 한정한다.

### 8. 반복·seed·순서와 C/D (R2-6)

- task·조건별 3회, seed `20260920`, paired block `(corpus, task, repeat)` 안에서 rev1과 같은 shuffle·rotate로 조건 순서를 정한다.
- C/D는 승인된 adapter가 없으므로 이번 revision에서 만들지 않고 모든 셀을 attempted `infra_failed`로 분모에 남긴다(ADR 003 재개 조건 3: 계속 unavailable이면 어떤 available baseline에 대한 판단인지 명시).

### 9. medium/large 예산 — 새 기준 (R2-7)

small 값(60 s / 2 s / 5 s / 1 GiB / 30 min)은 small fixture의 hang 탐지용이라 규모에 비례해 늘릴 근거가 없다. medium·large 값은 제품 사용 조건에서 정했고 어떤 CV 측정값도 보지 않았다.

| 항목 | medium | large | 근거 |
|---|---:|---:|---|
| cold build | 300 s | 900 s | 처음 한 번 색인이 5분·15분을 넘으면 세션 도구로 쓰기 어렵다. medium 상단 14.5 MB에서 약 48 KB/s, large 70.8 MB에서 약 79 KB/s의 최소 처리량이다 |
| warm lookup (`cv_find` 한 페이지, `cv_get`, resolve) | 2 s | 2 s | 색인 조회가 규모에 비례해 느려지면 그 자체가 결함이다. small 값을 유지한다 |
| warm analysis (`cv_impact` 한 페이지, diff compare) | 10 s | 30 s | 참조 분석·line diff는 규모에 따라 늘어나는 작업이다. 대화 한 단계의 대기로 허용할 범위다 |
| incremental update | 10 s | 30 s | 편집 뒤 재조회까지의 대기 |
| peak memory | 2 GiB | 4 GiB | 16 GiB 개발 PC에서 보조 도구가 4분의 1을 넘지 않는다 |
| wall time 한도 | corpus당 120 min | 360 min | 제품 기준이 아닌 실행 한도. 전체 11시간 이하, 80%에서 새 block 중지 |

latency·memory 예산은 ADR 001의 "모든 품질·효율·latency·memory 기준 통과" 규칙에 따라 Go 조건이다.

### 10. 후속 TASK에 넘길 구현 요구

**TASK-027 (qa-verifier, sonnet)**

1. `benchmarks/corpora/rev2-manifest.json`: alias, 저장소, commit, clone 뒤 `LICENSE` 재확인, 등급 지표 다섯 개와 독립 계수 도구 버전, 대체 발생 시 사유.
2. 독립 syntax 목록 스크립트(`CodeVirtualize.*` 비참조)로 NAV 표본 틀과 심볼·참조 수를 만든다. 표본 순서와 DIFF 쌍을 R2-2 규칙대로 기록하고 tuning/held-out을 지정한다.
3. task card는 입력만 담는다. 정답(`expected.json`)은 좌표·hash 기반으로, static/dynamic/unresolved, `nonSymbolChange`, Session 제외 확인 줄을 분리한다. 원문을 복사하지 않는다.
4. 25% 교차 확인 기록을 남긴다. CV를 실행하거나 CV 출력을 보지 않는다. TASK-026 담당에게 정답을 넘기지 않는다.

**TASK-029 (performance-engineer, opus)**

1. runner를 rev2로 확장한다: corpus 취득(측정 전 네트워크 사용 완료), R2-3 조건 알고리즘, R2-4 계상기(줄 단위 parser, diff hunk parser, CV 필드 계상), R2-5 판정기, R2-6 순서·timeout, R2-7 예산 비교와 wall time 중지 규칙, R2-8 `--evaluate-gates`, `--verify-only` 재집계.
2. TASK-026으로 깨진 `BaseSource`/`TargetSource` 참조를 새 계약(`DiffSourceResolver`, `textualHunk`)으로 바꾼다(TASK-025 U9).
3. 계상기·판정기·B 알고리즘을 small fixture의 손 계산 기대값으로 단위 검증한 뒤 tuning을 실행한다. tuning 뒤에는 결함 수정만 하고 `runnerChangeLog`에 기록한다.
4. held-out 실행 전에 runner를 code-reviewer가 R2 절과 대조 리뷰한다(행동 파라미터 일치, 계상 대칭). Critical·High가 남으면 held-out을 실행하지 않는다.
5. corpus별 결과는 기존 `result.schema.json` 형식을 그대로 쓰고 corpus 차원과 보조 지표는 run manifest에 둔다. schema 변경이 필요하면 held-out 전에 따로 승인받는다.
6. 메인 에이전트가 `--verify-only`로 다시 집계해 수치가 일치해야 완료다.

**TASK-030 (architect, opus)**

`gate-evaluation.json`의 분류를 그대로 옮긴다. 셀별 값, 분모, `baseline-degraded` 수, 예산 초과, `toolOutputBytes` 비교, token·C/D 한정 문구를 적는다. 이 ADR의 규칙 밖 판단을 더하지 않는다.

## 대안과 트레이드오프

| 쟁점 | 추천 | 대안 | 추천 이유 |
|---|---|---|---|
| R1 계상 | 둘 다 셈(원칙) | (a) 둘 다 제외 (b) 현행 유지 | (a)는 E의 diff evidence도 빠져 E에 유리하다. (b)는 B가 판정에 쓴 원문을 무상으로 받는다. 원칙만이 "판정에 쓴 텍스트 = 센 텍스트"를 보장한다 |
| `signature` 계상 | 센다 | 메타데이터로 보고 제외 | 제외하면 E의 DECL·REF가 정의상 0 B라 NAV 셀이 계상 정의로 기운다 |
| R2 복구 | 양쪽에 편집 2회(span 밖·안) | (a) 복구 제거 (b) E만 유지(현행) (c) span 밖 1회만 | (a)는 안전성과 조건부 재조회가 측정에서 사라진다. (b)는 E에 불리하다. (c)는 늘 `notModified`가 이득이라 E에 유리하다 |
| B 행동 | 이상적 범위 읽기(brace 균형, diff 재사용) | (a) 고정 창(예: hit ±40줄) 읽기 (b) 여러 B 변형 중 품질 통과한 최소값(oracle) | (a)는 창 크기 선택이 결과를 좌우하고 B를 약하게 만들 수 있다. (b)는 정답을 아는 선택이라 비현실적이고, E에도 같은 oracle을 줘야 대칭이 된다. 추천안은 양쪽 모두 결정적이고 CV에 보수적이다 |
| 셀 결합 | 8개 셀 모두 통과 | (a) 등급별 task 풀링 중앙값 (b) 새 corpus만 판정, small은 보고 | (a)는 한 corpus의 큰 이득이 다른 corpus의 실패를 가린다. (b)는 small 회귀 실패를 무시한다. 추천안은 가장 엄격하고 선택의 여지가 없다. 대가는 셀 하나의 실패도 No-Go라는 점이다 |
| 셀 통계 | task 감소율 중앙값(FUP-002의 paired 중앙값) | task 전부 20% 이상 | FUP-002가 중앙값으로 동결했다. task별 통과 비율은 보고만 한다 |
| medium 수 | 2개(LiteDB, Quartz.NET) | Hangfire 포함 3개 | 3개면 셀이 늘고 wall time이 늘지만 이득은 작다. LGPL은 예비로 둔다 |
| large 수 | 1개 | 2개(aspnetcore + runtime) | wall time과 독립성(같은 조직 관례) 문제 |
| tuning 분리 | corpus당 NAV 1·DIFF 1 | tuning 없음 | tuning 없이 held-out에서 runner 결함이 나오면 held-out을 소진한다. 행동 파라미터는 tuning으로도 바꾸지 않는다 |
| medium/large 예산 | 새 기준으로 gate | (a) 관측만(gate 아님) (b) small 값 그대로 | (a)는 ADR 001의 전부 통과 규칙과 어긋나 규모 문제를 가린다. (b)는 small hang 탐지값을 규모에 적용하는 근거가 없다 |
| DIFF E 준비 | 반복 1에서 준비 build, 복사한 store로 반복 2·3 | 반복마다 준비 build | large에서 준비 build 18회는 wall time 한도를 넘길 수 있다. cold build 지연은 NAV build 3회와 준비 build 6회로 충분히 표본을 얻는다 |

## 보안·호환성

- 공개 corpus도 untrusted 입력이다. CV는 `--trust-workspace` 없이 syntax-only로 build하고 MSBuild를 평가하지 않는다. 측정 중 네트워크를 쓰지 않는다.
- corpus 원문, prompt, 로컬 절대 경로를 결과·정답 파일에 넣지 않는다. 정답은 좌표·hash로 저장한다.
- rev1 결과(`task-016-v1`)와 ADR 002는 바꾸지 않는다. rev2 결과는 `benchmarks/results/rev2/`에 따로 둔다.
- `result.schema.json`은 바꾸지 않는 것을 기본으로 한다.

## 결과

- 이 ADR이 Accepted가 되기 전에는 TASK-027·029를 시작하지 않는다(G2).
- 승인 뒤 rev2 절은 동결된다. held-out 결과를 본 뒤의 변경은 새 revision과 새 표본이 필요하다.
- rev2 결과가 Go여도 그 범위는 "공개 C# corpus 세 개와 small 합성, stronger available baseline B 대비 source bytes"다. 실제 모델 token, C#LSP·Serena 대비 우위, Perforce·UE5·비공개 코드로 일반화하지 않는다.

## G2 — 사용자 확인이 필요한 결정

1. **corpus**: medium `medium-litedb`·`medium-quartznet`, large `large-aspnetcore`(위 commit 고정), 대체 순서(medium Polly → Hangfire → Serilog, large runtime → roslyn).
2. **라이선스 기준**: 주 corpus는 permissive만, LGPL(Hangfire)은 예비, RPL-1.5·Six Labors Split은 제외, 원문 비복사(정답은 좌표·hash).
3. **등급 정의**: 파일·bytes·project 세 지표로 배정하고 심볼·참조는 편차만 보고.
4. **task 구성**: corpus당 NAV 6·DIFF 6 composite, tuning NAV 1·DIFF 1, NAV 대상 표본을 이름 출현 2~40줄로 제한(CV에 보수적, 흔한 이름은 이번 실험 밖).
5. **계상 원칙**: 받은 소스 유래 텍스트는 도구와 무관하게 모두 센다(R1), `signature`도 센다, 줄바꿈 정규화(R3), `toolOutputBytes`는 보고만.
6. **R2 처리**: 편집 두 번(span 밖·안)을 모든 조건에 주고 재조회 비용을 모두 센다.
7. **R4·품질 판정**: 센 텍스트만으로 내용 판정, E의 non-succeeded run은 품질 실패로 본다.
8. **조건 행동**: B와 E를 모두 이상적 범위 읽기로 고정(B는 실제 에이전트보다 강함 — CV에 보수적). U7 E 호출 패턴 표.
9. **판정 결합**: 8개 셀 전부 20% 이상, small은 오염된 회귀 기준(통과는 근거가 아니고 실패는 Go를 막음), B 실패 task는 efficiency에서 제외, 빠진 값이 있으면 Inconclusive.
10. **새 예산**: medium cold 300 s / warm lookup 2 s / warm analysis 10 s / update 10 s / memory 2 GiB, large cold 900 s / 2 s / 30 s / 30 s / 4 GiB. 이 값들을 Go 조건(gate)으로 쓴다.
11. **실행 한도**: small 30 min, medium corpus당 120 min, large 360 min, 전체 11시간 이하, 외부 유료비용 0.
12. **C/D**: 이번 revision에서 adapter를 만들지 않고 unavailable로 분모에 남긴다. Go는 "B 대비"로 한정한다.

## 참고

- [protocol.md Revision 2](../../benchmarks/protocol.md#revision-2--mediumlarge-재측정-프로토콜-task-024) — 운영 규칙 정본
- [corpus-candidates.md](../../benchmarks/corpus-candidates.md) — 후보·commit·license 조회 근거(2026-09-23 GitHub API)
- [source-byte-contract-proposal.md](../contracts/source-byte-contract-proposal.md) — 승인된 반환 계약, U7·R1~R4 원문
- [cv-report.md](../../benchmarks/results/cv-report.md), [ADR 001](001-product-path.md) FUP-002 동결 기준, [ADR 002](002-validation-outcome.md), [ADR 003](003-next-step.md)
