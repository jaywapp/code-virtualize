# ADR 004 — cache 정책: config 시스템과 generation GC

- 상태: Proposed — 2026-09-23 설계(사용자 확인 필요 11개 항목) 사용자 승인, 수치는 G4 확정 후 Accepted
- 작성일: 2026-09-23
- 적용 범위: FUP-004 cache 정책의 config·GC 설계(TASK-032). 구현은 설계 확인과 G4 수치 확정 뒤 TASK-021, 검증은 TASK-033·034
- 결정 요약(제안): **user·workspace 2단 JSON config + 명시 명령 `cv-gc` + writer lease 아래 fence·tombstone 삭제. 기본 `gc.enabled = false`이며 비활성 상태에서는 삭제 코드가 호출될 수 없는 구조로 둔다.** 수치는 모두 "G4에서 확정"으로 둔다.

## 맥락

FUP-004(2026-09-21 Confirmed)는 다음을 확정했다.

- GC mode는 `age`, `capacity`, `hybrid` 중 config에서 고른다.
- 활성 generation을 보호하고 dry-run을 지원한다.
- 기본값은 `gc.enabled = false`다. retention·용량 기본 수치는 실측(TASK-031) 뒤 사용자가 확정한다(G4).
- 파괴적 삭제는 이 cache GC 범위 안에서만 승인됐다.

[ADR 003](003-next-step.md)의 보존 규칙도 그대로 적용한다. active reader, session source, immutable base snapshot, pin된 generation, 사용자 설정은 삭제하지 않는다. 한 세션을 종료하면 그 세션의 pin만 해제된다.

### 현재 코드에서 확인한 사실 (2026-09-23, `docs/phase1-designs`)

| 항목 | 현재 상태 | 근거 |
|---|---|---|
| 설정 파일 | **없음**. `config.json`을 읽는 코드, 환경 변수 설정, `GetFolderPath` 사용이 없다(MCP 테스트용 `CODE_VIRTUALIZE_MCP_TEST_DELAY_MS`만 예외). | `src/` 전체 검색 |
| CLI 설정 | 공통 플래그는 `--store`, `--workspace`, `--format`이다. 명령별 동작 플래그로 `--trust-workspace`, `--writer-wait-ms`(update 기본 500 ms), `--configuration`이 있다. store 기본 위치는 `<workspace>/.code-virtualize`다. | `Cli/Commands/CliOptions.cs`, `UpdateCommand.cs`, `Program.cs` |
| `SemanticConfigFingerprint` | 분석 설정의 hash 계산일 뿐 설정 저장소가 아니다. | `Core/Storage/SemanticConfigFingerprint.cs` |
| writer lease | `<store>/writer.lock`을 `FileShare.None`으로 연다. OS 핸들 기반이라 프로세스가 죽으면 풀린다. host·PID·process start 시각은 기록만 한다. 대기는 0~1분이고, build는 기본 0이라 경합 시 바로 `BUSY`를 낸다. | `GenerationStore.AcquireWriterLease` |
| reader pin | reader는 `generations/<id>/.reader.pin`을 `FileAccess.Read, FileShare.Read`로 열어 둔 채 shard를 경로로 읽는다. Windows에서는 pin 삭제가 막히는 것을 테스트로 확인했다(`ReaderPinPreventsDeletionOnWindows`). | `GenerationStore.OpenGeneration`, `GenerationReader` |
| 원자적 교체 | generation은 `.tmp-<guid>`에 쓴 뒤 `Directory.Move`로 확정한다. pointer `current.json`은 `.current-<guid>.tmp` 다음 `File.Replace`로 교체한다. publish 뒤에도 이전 generation은 지우지 않는다. commit 뒤 pointer 교체 전에 crash가 나면 게시되지 않은 generation이 남는다(테스트로 확인). | `GenerationStore.Publish`, `GenerationStoreTests` |
| retention·GC | **코드 없음**. 삭제 코드는 publish 실패 시 자기 temp 정리, 세션 종료 시 자기 pin 삭제뿐이다. | `GenerationStore`, `SessionSnapshotStore.Close` |
| session | `sessions/<id>/`(`session.json`, `baseline.json`, `baseline.sha256`, `baseline-source/*.source`)와 `pins/sessions/<id>.pin`(`generationId` 포함)으로 구성된다. capture는 **writer lease 없이** `.tmp-<guid>`를 만든 뒤 move하고 그 **다음에** pin을 쓴다. | `SessionSnapshotStore.Capture` |
| VCS base | git에서 필요할 때 읽고 store에 영속하지 않는다. store 안의 immutable base snapshot은 session baseline뿐이다. | `Core/Vcs/GitBaselineProvider.cs` |
| 동일 ID 재빌드 | `GENERATION_EXISTS`가 나면 `<id>_<guid>`로 다시 게시한다. 삭제 중 남은 디렉터리가 있어도 build는 막히지 않는다. | `CSharpIndexBuilder`, `CSharpIncrementalIndexBuilder` |
| 응답 형식 | `cv-validate`와 `cv-inspect`는 `code-virtualize/response` envelope의 `results`에 결과 객체 하나를 넣는다. `cv-build`는 manifest, `cv-update`는 자체 객체를 출력한다. | `GenerationDiagnostics`, `ResolutionCommands` |
| 테스트 하네스 | `tests/Integration.Tests`는 xUnit이 아니라 `Program.cs`가 정적 `XxxTests.Run()`을 차례로 부르는 방식이다. 고장 주입은 `IStorageFaultInjector`/`StorageFaultPoint`로 한다. | `tests/Integration.Tests/Program.cs` |

architecture.md의 목표 배치(`cache/<workspace-key>/<analysis-key>/…`)와 실제 store 배치(`<store>/generations`, `sessions`, `pins`)는 다르다. 이 ADR은 **실제 코드 배치**를 기준으로 한다.

## 결정 (제안)

### 1. config 시스템

#### 위치와 계층

| 계층 | 위치 | 신뢰 | 비고 |
|---|---|---|---|
| Built-in | 코드 상수 | 신뢰 | `gc.enabled = false`, `gc.trigger = "manual"`. 수치는 G4 전까지 `null`(미정) |
| User | Windows `%APPDATA%\code-virtualize\config.json`. Unix는 `$XDG_CONFIG_HOME/code-virtualize/config.json`, 없으면 `~/.config/…` (`Environment.SpecialFolder.ApplicationData`) | 신뢰 | `gc.enabled = true`는 여기서만 켤 수 있다 |
| Workspace | `<workspace>/.code-virtualize/config.json` | **untrusted** (architecture.md Configuration 절) | 더 보존적인 방향으로만 바꿀 수 있다(아래) |
| CLI | 명령 플래그 | 신뢰 | 수치 override는 `--dry-run`일 때만 허용 |

- store 수준의 별도 config는 두지 않는다. `--store`가 workspace 밖을 가리켜도 workspace config는 `--workspace` 기준으로 찾는다. `--store`만 줄 때는 workspace 계층을 건너뛴다.
- user config 경로는 CLI `--user-config <path>`로 바꿀 수 있다. **테스트는 항상 이 플래그나 Core API의 명시 경로를 써서 실제 사용자 홈을 읽거나 쓰지 않는다**(ADR 003: 실제 사용자 홈 설정 변경 금지).
- CV는 config 파일을 만들거나 고치지 않는다. 사용자가 직접 쓰는 읽기 전용 입력이다.
- architecture.md의 Session override 계층은 GC에 적용하지 않는다. GC는 store 전체 정책이므로 한 세션이 바꿀 수 없다.

#### 형식과 schema

기존 계약과 같은 엄격 JSON을 쓴다. `schema`/`schemaVersion`이 필수이고, 알 수 없는 속성·대소문자가 다른 속성·중복 속성·주석·trailing comma를 거부한다(`UnmappedMemberHandling.Disallow`). 기계 판독 schema는 TASK-021에서 `schemas/config.schema.json`(draft 2020-12)으로 추가한다.

```json
{
  "schema": "code-virtualize/config",
  "schemaVersion": 1,
  "gc": {
    "enabled": false,
    "mode": "hybrid",
    "trigger": "manual",
    "age": { "maxAgeHours": null },
    "capacity": { "maxBytes": null },
    "graceMinutes": null
  }
}
```

(값은 형식 예시다. `mode` 기본값과 모든 수치는 G4에서 정한다.)

| 키 | 타입·범위 | 의미 |
|---|---|---|
| `gc.enabled` | bool | false이면 삭제 경로 진입 불가. dry-run은 허용 |
| `gc.mode` | `age` \| `capacity` \| `hybrid` | 후보 선정 규칙 |
| `gc.trigger` | `manual` \| `after-publish` | 실행 시점(2절) |
| `gc.age.maxAgeHours` | 양의 정수 \| null | `age`·`hybrid`에서 필요 |
| `gc.capacity.maxBytes` | 양의 정수 \| null | `capacity`·`hybrid`에서 필요 |
| `gc.graceMinutes` | 양의 정수 \| null | 어떤 mode에서도 이 시간보다 어린 generation은 지우지 않는다. 0은 허용하지 않는다 |

단위는 사람이 쓰기 쉽고 파싱이 단순한 정수(시간, 바이트, 분)로 둔다. ISO 8601 duration 문자열도 검토했지만 파싱 규칙이 늘어나서 택하지 않았다.

#### 병합 규칙

1. Built-in → User → Workspace → CLI 순서로 키 단위 병합한다.
2. **Workspace 제한**: workspace 계층은 `gc.enabled`를 true로 바꿀 수 없다. 수치는 **더 많이 보존하는 방향**으로만 바꿀 수 있다(`maxAgeHours`·`maxBytes`·`graceMinutes`는 더 큰 값만). `mode`·`trigger`는 바꿀 수 없다. 이를 어긴 값은 무시하지 않고 `CONFIG_WORKSPACE_ESCALATION` 오류로 처리한다. 클론한 레포가 사용자 모르게 삭제를 켜거나 넓히지 못하게 하기 위해서다.
3. **CLI 제한**: `gc.enabled`를 켜는 플래그는 두지 않는다. `--mode`, `--max-age-hours`, `--max-bytes`, `--grace-minutes`는 `--dry-run`과 함께일 때만 받는다(TASK-031·G4의 수치 탐색용). 실제 삭제는 파일에 기록된 정책만 쓰므로 같은 config의 dry-run 결과와 실제 실행 결과를 비교할 수 있다.
4. 결과는 `EffectiveGcPolicy`(값과 키별 출처 `builtin|user|workspace|cli`)로 고정하고 GC 보고서에 그대로 싣는다.

#### 잘못된 config 처리

| 상황 | 코드 | `cv-gc` | `after-publish` 자동 GC | 다른 명령(build·find 등) |
|---|---|---|---|---|
| JSON·schema 오류, 알 수 없는 키, 범위 밖 값 | `CONFIG_INVALID` | error 응답, exit 2, 삭제 0 | 건너뛰고 stderr 한 줄, 원 명령 결과·exit code 유지 | 현재 config를 읽지 않으므로 영향 없음 |
| `enabled=true`인데 mode에 필요한 수치가 `null` | `CONFIG_INCOMPLETE` | error, exit 2, 삭제 0 | 건너뜀 | 영향 없음 |
| workspace 상향 시도 | `CONFIG_WORKSPACE_ESCALATION` | error, exit 2, 삭제 0 | 건너뜀 | 영향 없음 |
| 파일 없음 | — | 그 계층을 건너뜀 | 동일 | — |
| 크기 상한 초과, reparse point | `CONFIG_INVALID` | error | 건너뜀 | — |

config 오류는 언제나 **삭제 0건 쪽**으로 실패한다. 오류 메시지에 config 값이나 절대 경로는 넣지 않고 계층 이름과 키 경로만 넣는다.

### 2. GC 실행 시점

- **`cv-gc`(alias `gc`) 명시 명령**이 기본이자 유일한 수동 경로다.
  `code-virtualize cv-gc (--workspace <path> | --store <path>) [--dry-run] [--user-config <path>] [--writer-wait-ms <n>] [--format text|json]`
  (`--dry-run`일 때만 수치·mode override 플래그 허용)
- **`gc.trigger = "after-publish"`**(선택): `cv-build`/`cv-update`가 **새 generation을 실제로 게시했고**(`Published = true`) `gc.enabled = true`일 때만, 같은 프로세스가 publish lease를 푼 뒤 GC를 한 번 실행한다. lease 대기는 0이다. `BUSY`면 건너뛴다. 결과는 run 로그와 stderr 한 줄로만 남긴다. stdout은 요청당 JSON 하나라는 계약을 지키기 위해 GC 보고서를 stdout에 섞지 않는다. GC 실패는 원 명령의 결과와 exit code를 바꾸지 않는다.
- MCP 도구로 GC를 노출하지 않는다(architecture.md: lifecycle 명령은 모델 도구 목록에 넣지 않는다). 모델이 삭제를 일으킬 수 있는 경로를 만들지 않는다.
- 백그라운드 daemon이나 예약 실행은 두지 않는다.

### 3. 후보 선정 알고리즘

모든 mode에서 **먼저 보호 집합(4절)을 계산**하고, 보호되지 않은 정상 generation만 후보로 본다. 나이는 manifest `createdAt`(UTC, schema 필수 필드) 기준이다. `createdAt`이 미래이면 나이 0으로 본다. 크기는 generation 디렉터리 안 파일의 실제 길이 합이다. 할당 크기가 아니며, reparse point는 따라가지 않는다.

| mode | 규칙 |
|---|---|
| `age` | `now - createdAt > maxAgeHours`인 비보호 generation을 모두 후보로 한다. |
| `capacity` | `generations/` 총 bytes(보호 포함) `> maxBytes`이면 비보호 generation을 `createdAt` 오름차순(같으면 generation ID ordinal)으로 하나씩 후보에 넣고, 예상 총량이 `maxBytes` 이하가 되면 멈춘다. |
| `hybrid` (권장 의미: 합집합) | 먼저 `age` 규칙으로 후보를 넣고, 남은 예상 총량이 `maxBytes`를 넘으면 `capacity` 규칙을 이어서 적용한다. |

- 비보호 후보를 모두 넣어도 `maxBytes`를 넘으면 `status = partial`, limitation `gc-capacity-unmet-protected`로 보고한다. 보호 대상은 절대 후보로 올리지 않는다.
- 선정은 결정적이다. 같은 store 상태, 같은 정책, 같은 시각이면 같은 후보 목록이 나온다. 테스트는 `TimeProvider`를 주입해 시각을 고정한다.
- 대안인 "마지막 사용 시각" 기준은 reader가 사용 기록을 써야 해서(reader가 writer가 됨) 택하지 않았다. "current에서 밀려난 시각" 기준은 현재 기록이 없어 새 메타데이터가 필요하므로 후속 과제로 남긴다.

### 4. 보호 집합

보호 집합은 **writer lease를 잡은 뒤**, 삭제를 하나라도 하기 전에 한 번에 계산한다. 계산 중 판단할 수 없는 입력이 있으면 **실행 전체를 중단하고 삭제 0건**으로 끝낸다(fail-closed). 항목별로 넘어가는 경우(skip)와 구분한다.

| # | 보호 대상 | 판별 방법 | 판별 불가 시 |
|---|---|---|---|
| P1 | current generation | `current.json`을 기존 `ValidatePointer` 규칙으로 읽는다 | pointer 없음(`NO_CURRENT_GENERATION`)이나 손상(`CORRUPT_GENERATION`)이면 **실행 중단** |
| P2 | session pin이 가리키는 generation | `pins/sessions/*.pin`의 `schema = code-virtualize/session-pin`, `generationId`를 읽는다 | 읽기·파싱 실패 pin이 하나라도 있으면 **실행 중단**(`GC_PROTECTION_UNKNOWN`) |
| P3 | 활성 세션의 generation (pin 쓰기 전 구간 포함) | `sessions/*/session.json` 중 `state = active`인 것, 그리고 `sessions/.tmp-*/session.json`이 있으면 그 `generationId`를 읽는다 | 읽기 실패 시 **실행 중단** |
| P4 | active reader가 있는 generation | 5절 fence 획득 실패 | 그 항목만 skip(`active_reader`) |
| P5 | grace 기간 안의 generation | `createdAt`과 디렉터리 생성 시각 중 **더 최근 값**이 `now - graceMinutes`보다 뒤 | 그 항목만 skip(`grace`) |
| P6 | immutable base snapshot, session source, 세션 데이터 전체 | **열거 대상이 아니다**. GC는 `sessions/`, `pins/`에서 읽기만 하고 삭제 경로를 만들지 않는다 | — |
| P7 | 사용자 설정·기타 파일 | **열거 대상이 아니다**. `config.json`, `current.json`, `writer.lock`, run 로그 등 삭제 허용 목록 밖은 건드리지 않는다 | — |
| P8 | 알 수 없거나 위험한 항목 | `generations/` 아래에서 `ValidateGenerationId`를 통과하지 못하고 `.tmp-` 형식도 아닌 이름, reparse point이거나 안에 reparse point가 있는 디렉터리, manifest를 읽을 수 없는 generation | 그 항목만 skip하고 보고(`unknown_entry`, `reparse_point`, `corrupt_generation`) |

**삭제 허용 목록**(이 목록 밖은 어떤 모드에서도 삭제 코드에 전달되지 않는다):

1. `generations/<valid-generation-id>/` 중 후보로 선정된 디렉터리
2. `generations/.tmp-*`: writer lease를 잡고 있으면 진행 중인 publish가 있을 수 없으므로 crash가 남긴 고아다
3. `<store>/.current-*.tmp`: 같은 이유로 고아다
4. 이전 GC가 중단된 tombstone 디렉터리(6절)

`sessions/.tmp-*`는 capture가 lease 없이 만들기 때문에 진행 중인지 판별할 수 없다. 이 항목은 **보고만 하고 지우지 않는다**.

P3와 P5가 둘 다 필요한 이유가 있다. `StartSession`은 build(lease 안)로 generation G를 게시한 뒤 lease를 풀고, capture로 세션 디렉터리를 만든 **다음** pin을 쓴다. 그 사이에 다른 writer가 G′를 게시하면 G는 current도 아니고 pin도 없는 틈이 생긴다. P3는 디렉터리가 생긴 뒤를 막고, P5 grace는 디렉터리가 생기기 전을 막는다. 그래서 `graceMinutes`는 0을 허용하지 않는다. capture의 pin 쓰기를 lease 안으로 옮기는 코드 변경도 대안이지만 build·capture 경계를 바꾸므로 이 ADR에서는 택하지 않는다.

### 5. 동시성

| 참여자 | 잠금 | GC와의 관계 |
|---|---|---|
| writer (build/update publish) | `writer.lock` 배타 | GC도 같은 lease를 잡는다. GC 도중에는 publish와 pointer 교체가 없으므로 P1이 실행 내내 유효하다. |
| reader (query·resolve·MCP) | `.reader.pin` 공유 열기 | GC는 generation마다 **fence**를 잡는다. `.reader.pin`을 `FileShare.None`으로 연다. Windows에서는 공유 모드가 강제되고, Unix의 .NET은 `FileShare.None`을 `flock(LOCK_EX)`, 그 외를 `LOCK_SH`로 매핑한다. 그래서 pin을 연 reader가 있으면 fence가 실패하고(P4), fence를 잡은 뒤에는 새 reader의 pin 열기가 실패한다. |
| session capture | 없음 | P3·P5로 보호한다. |
| 다른 GC | `writer.lock` | 동시에 하나만 실행된다. 두 번째는 `BUSY`(exit 3). |

- lease 대기는 `--writer-wait-ms`(기존 update와 같은 의미, 0~60000)를 따른다. 기본값은 TASK-021에서 update와 같은 값으로 맞춘다.
- GC가 lease를 잡은 동안 build는 기본 대기 0이라 `BUSY`를 받는다. 그래서 한 번의 실행에서 lease를 쥐는 시간을 줄여야 한다. TASK-031이 측정할 scan 시간으로 필요성을 판단하고, 필요하면 TASK-021에서 실행당 삭제 개수 상한(G4 항목)을 둔다.
- Unix의 잠금은 advisory다. CV가 아닌 프로세스(예: 사용자가 연 편집기)의 읽기는 감지하지 못한다. 대상이 재생성 가능한 cache이므로 이 한계를 받아들이고 문서에 적는다.
- fence 획득과 reader의 pin 열기가 경합하면 reader는 IO 오류를 받는다. TASK-021에서 `OpenGeneration`이 pin 공유 위반을 기존 `BUSY`(retryable)로 매핑하도록 한다. 대상은 current가 아닌 generation뿐이라 드물다.
- crash한 writer나 GC가 남긴 `writer.lock`은 OS 핸들이 닫히면서 자동으로 풀린다. 파일이 남아 있는 것만으로 lease를 쥔 것으로 보지 않는다. 기록된 identity는 진단용으로만 보고서에 싣는다.

### 6. 삭제 절차와 crash 복구

`Directory.Delete(recursive)`를 바로 호출하지 않는다. Windows에서 reader가 pin만 쥐고 있으면 재귀 삭제가 shard를 먼저 지운 뒤 pin에서 실패해 **부분 삭제된 generation**이 남기 때문이다. 대신 generation마다 다음 순서를 따른다.

1. fence를 잡는다(실패하면 `active_reader`로 skip).
2. 디렉터리를 다시 검증한다. 이름·reparse point를 보고, current·pin 여부는 lease 아래라 바뀌지 않는다.
3. **tombstone** `.gc-tombstone`을 durable write로 만든다. 내용은 `schema = code-virtualize/gc-tombstone`, `runId`, `generationId`, `startedAt`이다.
4. `manifest.cv`를 가장 먼저 지운다. 이후 이 generation을 여는 reader는 manifest 누락으로 fail-closed(`CORRUPT_GENERATION`)가 되고, 반쯤 지워진 shard를 읽지 않는다.
5. shard와 하위 디렉터리를 지운다. reparse point는 따라가지 않고, 발견하면 멈추고 보고한다.
6. fence를 풀고 `.reader.pin`, `.gc-tombstone`, 빈 디렉터리를 지운다. 그 사이 reader가 pin을 열어 삭제가 실패하면 tombstone이 남은 채로 둔다.

**복구**: 다음 GC 실행(`enabled = true`, lease 아래)은 보호 집합을 계산한 뒤 `generations/` 안에서 tombstone이 있는 디렉터리를 찾아 4~6단계를 다시 한다. tombstone이 있는 디렉터리는 manifest가 없을 수 있으므로 P8 `corrupt_generation`으로 분류하지 않고 복구 대상으로 따로 분류한다. P1~P3가 tombstone 디렉터리를 가리키면(설계상 불가능) 복구하지 않고 `GC_PROTECTION_CONFLICT`로 실행을 중단한다. `enabled = false`이면 복구도 하지 않고 dry-run 보고서에 `pendingTombstones`로만 보여 준다. 남은 디렉터리는 build를 막지 않는다(`<id>_<guid>`로 다시 게시).

고장 주입을 위해 `IStorageFaultInjector`와 같은 방식의 GC 지점(`BeforeTombstone`, `AfterTombstone`, `AfterManifestDelete`, `DuringShardDelete`, `BeforeDirectoryRemove`)을 둔다. 기존 `StorageFaultPoint`를 바꾸지 않도록 별도 enum `GcFaultPoint`로 추가한다.

### 7. `gc.enabled = false`에서 삭제 0건을 보장하는 구조

런타임 `if` 하나에 기대지 않고 세 겹으로 막는다.

1. **계획과 실행 분리**: `GcPlanner.Plan(...) → GcPlan`은 읽기 전용이다. 삭제 API를 호출하는 코드는 `GcExecutor` 한 곳에만 둔다(TASK-033이 검색으로 확인할 수 있는 단일 지점).
2. **권한 객체**: `GcExecutor.Execute(GcPlan, GcDeletionAuthorization)`의 권한 객체는 config 해석기만 만들 수 있다(`internal` 생성자). 조건은 `EffectiveGcPolicy.Enabled == true`, 수치 완비, dry-run 아님이다. `enabled = false`이거나 `--dry-run`이면 권한 객체를 만들 수 없으므로 executor를 호출할 수 없다.
3. **executor 진입 재검증**: 권한 객체가 가리키는 정책과 plan의 정책 fingerprint가 다르면 즉시 예외를 던진다. tombstone 복구(6절)와 고아 temp 정리도 executor 안에서만 한다. 그래서 비활성 상태에서는 **고아 정리를 포함한 어떤 삭제도** 일어나지 않는다.

dry-run은 활성 여부와 관계없이 같은 `GcPlanner`를 쓴다. 따라서 "dry-run 후보 = 실제 삭제 대상"은 active reader 경합과 삭제 중 오류를 뺀 나머지에서 성립한다.

### 8. 출력 계약

`cv-validate`·`cv-inspect`와 같이 **`code-virtualize/response` envelope + `results`에 보고서 객체 하나** 형태로 낸다.

- `generationId`: P1 current generation. current가 없거나 손상되면 `status = error`로 응답하고 실행을 중단한다.
- `coverage.scope = "cache-gc-generations"`. 모든 항목을 평가하고 skip이 없으면 `complete_within_scope`다. skip·용량 미달·보고만 한 항목이 있으면 `partial`이고 `limitations`에 `gc-active-reader-skipped`, `gc-corrupt-generation-skipped`, `gc-capacity-unmet-protected`, `gc-session-temp-reported` 등을 넣는다.
- `status`: `ok` / `partial` / `error`. exit code는 기존 계약(0/2/3/4)을 따르고 `BUSY`는 3이다.
- `freshness`: `not_applicable` / `unknown`. `fallback`: `attempted = false`. `repair`: `not_requested`.

보고서 객체(`results[0]`):

```json
{
  "runId": "gc_…",
  "dryRun": true,
  "enabled": false,
  "evaluatedAt": "…",
  "policy": { "mode": "…", "trigger": "…", "maxAgeHours": null, "maxBytes": null, "graceMinutes": null },
  "policySources": { "gc.enabled": "builtin", "gc.mode": "user" },
  "totals": { "generationCount": 0, "generationBytes": 0, "sessionBytes": 0, "projectedGenerationBytes": 0 },
  "protected": [ { "generationId": "…", "bytes": 0, "reasons": ["current", "session_pin", "active_session", "active_reader", "grace"] } ],
  "candidates": [ { "generationId": "…", "createdAt": "…", "bytes": 0, "reason": "age_expired" } ],
  "deleted": [],
  "skipped": [ { "entry": "…", "code": "active_reader" } ],
  "orphans": [ { "kind": "generation_temp", "entry": ".tmp-…", "bytes": 0, "action": "report" } ],
  "pendingTombstones": [],
  "writerLease": { "waitedMs": 0 }
}
```

- 항목은 store 기준 상대 이름(generation ID, temp 이름)만 쓴다. 절대 경로, source 내용, config 원문은 넣지 않는다. 세션은 ID와 이유 코드만 쓴다.
- `candidate.reason`: `age_expired` | `capacity`. `orphans[].action`: 실제 실행이면 `deleted`, 아니면 `report`.
- 실제 실행은 같은 형태에 `dryRun = false`로 `deleted`를 채운다. 실패 항목은 `skipped`에 코드와 함께 넣는다.
- text 형식은 기존 `inspect`처럼 `Label        value` 줄 형식이다. 후보·보호·skip을 한 줄에 하나씩 쓴다.

### 9. metrics와 로그

- 실행마다 `<store>/gc/runs.jsonl`에 요약 한 줄을 남긴다. 필드는 `runId`, `startedAt`, `durationMs`, `dryRun`, `trigger`, `mode`, 개수와 bytes(후보·삭제·보호·skip), `errorCodes[]`다. dry-run도 기록한다. TASK-031·G4의 근거 자료가 된다. 경로·세션 원문·config 값은 넣지 않는다.
- 이 파일은 GC 삭제 허용 목록 밖이다. 스스로 커지지 않도록 보존할 run 수 상한을 두고, 상한은 G4 항목이다. 넘으면 오래된 줄부터 원자적 교체로 잘라 낸다(writer lease 아래).
- stderr에는 architecture.md Logging 절의 규칙대로 진단만 쓴다.

### 10. 실패 모드 요약

| 실패 | 동작 | 삭제 |
|---|---|---|
| config 오류·불완전·workspace 상향 | error, exit 2 | 0 |
| writer lease 경합 | `BUSY`, exit 3 | 0 |
| current pointer 없음·손상 | error, exit 3/4 | 0 |
| pin·session.json 판독 불가 | `GC_PROTECTION_UNKNOWN`, 실행 중단 | 0 |
| 보호 집합과 tombstone 충돌 | `GC_PROTECTION_CONFLICT`, 실행 중단 | 0 |
| active reader | 해당 항목 skip, partial | 그 항목 0 |
| reparse point·알 수 없는 항목·손상 manifest | 해당 항목 skip, partial | 그 항목 0 |
| 삭제 중 IO 오류 | 해당 항목에 tombstone 남김, skip, 다음 항목 진행 | 부분(복구 대상) |
| 프로세스 crash | lease 자동 해제, tombstone으로 다음 실행이 복구 | 부분(복구 대상) |
| 디스크 가득 참(tombstone 쓰기 실패) | 그 항목 skip. tombstone이 없으면 삭제를 시작하지 않는다 | 그 항목 0 |

## 대안과 트레이드오프

| 쟁점 | 추천 | 대안 | 추천 이유 |
|---|---|---|---|
| config 위치 | User + Workspace 2단, CLI 최상위 | (a) store 수준 `config.json` 하나 (b) CLI 플래그만 | architecture.md의 Built-in→Global→Workspace 원안과 맞다. (a)는 store가 workspace 안에 있어 untrusted 입력이 삭제 정책을 정하게 된다. (b)는 FUP-004의 "config에서 선택"을 충족하지 못한다. |
| 실행 시점 | 수동 `cv-gc` 기본, `after-publish` 선택 | (a) 수동만 (b) daemon·예약 | FUP-004의 "자동 삭제를 켠다"를 config 한 줄로 지원하면서 기본은 수동으로 둔다. (b)는 상주 프로세스·스케줄러 의존이 생겨 범위를 넘는다. |
| active reader 판별 | 기존 `.reader.pin` 잠금 probe(fence) | (a) PID·heartbeat reader 등록 파일 (b) OS 프로세스 스캔 | 이미 모든 reader가 쥐는 잠금을 그대로 쓰므로 새 상태가 없다. (a)는 reader가 쓰기를 해야 하고 stale 판정이 필요하다. (b)는 플랫폼 의존이 크고 어떤 generation을 읽는지 모른다. |
| 삭제 방식 | fence + tombstone + manifest 선삭제 | (a) 휴지통 디렉터리로 rename 후 삭제 (b) 재귀 삭제 | (b)는 Windows에서 부분 삭제 위험이 있다. (a)는 Windows에서 안쪽 파일이 열려 있을 때 디렉터리 rename 성공 여부가 핸들 공유 모드에 따라 달라 확신하기 어렵고, Unix에서는 reader가 열어 둔 채 rename돼 경로 기반 shard 읽기가 깨진다. 추천안은 두 OS에서 같은 의미를 갖는다. |
| 나이 기준 | manifest `createdAt` | 마지막 사용 시각, current에서 밀려난 시각 | 현재 데이터만으로 결정적으로 계산된다. 나머지는 새 쓰기 경로나 메타데이터가 필요하다. |
| 출력 | Response envelope + 보고서 객체 1개 | 새 최상위 schema `code-virtualize/gc-report` | validate·inspect와 같은 client 처리 경로를 쓴다. 대가로 coverage 필드를 GC 의미로 해석해야 한다(scope로 명시). |
| enabled 보장 | 계획/실행 분리 + 권한 객체 + 재검증 | 런타임 `if (enabled)` 한 곳 | 리뷰(TASK-033)가 단일 삭제 지점과 권한 생성 지점만 보면 되고, 테스트로 호출 0건을 증명할 수 있다. |

## 보안·호환성

- 삭제는 `PathBoundary`로 store 루트 안인지 확인하고 reparse point를 거부한다. junction을 통해 store 밖을 지우지 않는다.
- workspace config는 untrusted다. 삭제를 켜거나 넓힐 수 없다(1절).
- 보고서·로그에 source, 절대 경로, config 원문, 시크릿을 넣지 않는다.
- 하위 호환: 기존 명령의 동작·출력·exit code는 바뀌지 않는다. `after-publish`를 켜지 않으면 build·update는 config를 읽지 않는다. 새 오류 코드(`CONFIG_INVALID`, `CONFIG_INCOMPLETE`, `CONFIG_WORKSPACE_ESCALATION`, `GC_PROTECTION_UNKNOWN`, `GC_PROTECTION_CONFLICT`)는 추가만 한다. `OpenGeneration`의 pin 공유 위반을 `STORAGE_IO_ERROR`에서 `BUSY`로 바꾸는 것은 current가 아닌 generation을 fence 중일 때에만 생기는 좁은 변화다.
- 기존 store에는 마이그레이션이 필요 없다. GC는 현재 배치를 그대로 읽는다.

## 검증 설계 (TASK-034 대응)

모든 테스트는 기존 `Integration.Tests`의 정적 `Run()` 하네스에 `Storage/CacheGcTests.cs`, `Storage/ConfigTests.cs`로 추가한다. temp store, 주입한 `TimeProvider`, 명시한 user config 경로만 쓴다. 실제 `%APPDATA%`는 읽지도 쓰지도 않는다. 기대값은 구현 코드가 아니라 테스트가 직접 만든 store 상태(생성 시각·크기를 정한 generation)에서 계산한다.

| 영역 | 테스트 |
|---|---|
| 비활성 삭제 0 | `enabled = false`에서 `cv-gc`, `cv-gc --dry-run`, `after-publish` build/update를 실행한 뒤 store 전체 파일 목록·크기·hash가 실행 전과 같다. 고아 `.tmp-*`와 tombstone도 그대로 남는다. deleter spy 호출 0 |
| 권한 구조 | `enabled = false`나 `--dry-run`에서 `GcDeletionAuthorization`을 만들 수 없다. 정책 fingerprint가 다른 plan은 executor가 거부한다 |
| age 경계 | 나이가 `maxAgeHours` 직전·같음·직후인 generation(엄격한 `>`), 미래 `createdAt` |
| capacity 경계 | 총량이 `maxBytes`와 같음(삭제 없음)·1 byte 초과, 가장 오래된 것부터 삭제, 동률은 ID ordinal, 보호분만으로 초과 시 partial |
| hybrid | age로 삭제한 뒤 capacity로 추가 삭제, 두 규칙의 합집합 |
| grace | grace 안의 generation은 모든 mode에서 제외한다. `graceMinutes = 0`이나 null이면 enabled 실행이 `CONFIG_*` 오류 |
| 보호 | current, pin된 generation, `state = active`이면서 pin이 없는 세션(pin 쓰기 전 구간 재현), `sessions/.tmp-*/session.json`이 가리키는 generation, 손상 pin이면 실행 전체 중단·삭제 0 |
| pinned base | 세션을 시작하고 update를 여러 번 한 뒤 GC해도 `sessions/<id>/baseline*`과 pin된 generation이 남고 `SessionBaselineProvider.Capture`가 성공한다. 세션 종료 뒤에는 그 generation만 후보가 되고 다른 세션 pin은 유지된다 |
| 동시 reader | 다른 스레드와 가능하면 다른 프로세스가 비current generation을 `Open`해 쥔 동안 GC를 실행하면 skip(`active_reader`)이고 shard가 모두 온전하다. reader가 닫힌 뒤 다시 실행하면 삭제된다. fence 도중 reader는 `BUSY`를 받는다 |
| writer 경합 | 외부에서 `writer.lock`을 쥐고 있으면 `BUSY`·삭제 0, bounded wait 뒤 획득 |
| crash lease | lease를 쥔 자식 프로세스를 강제 종료한 뒤 GC가 lease를 얻는다 |
| crash 중 삭제 | 각 `GcFaultPoint`에서 예외를 주입하면 tombstone이 남고 manifest가 없어 reader가 fail-closed다. 다음 활성 실행이 복구하고, 비활성 실행은 `pendingTombstones`로만 보고한다 |
| config 보존 | user·workspace `config.json`, `current.json`, `writer.lock`, `sessions/`, `pins/`, `gc/runs.jsonl`이 GC 뒤 byte 단위로 같다 |
| config 해석 | 우선순위, 키별 출처, 알 수 없는 키·중복 키·대소문자 불일치 거부, workspace가 enabled를 켜거나 수치를 줄이려 하면 오류, CLI override는 `--dry-run` 없이 거부 |
| 경로 안전 | `generations/` 아래 junction, 알 수 없는 이름은 skip하고 대상 밖 파일은 그대로다 |
| dry-run 일치 | 같은 config·시각에서 dry-run `candidates`와 실제 실행 `deleted`가 reader 경합이 없을 때 같다 |
| 출력 계약 | envelope `Validate()` 통과, 절대 경로·source 미포함, exit code 매핑 |

## TASK-031 측정 제안 (수치 결정 근거, 삭제 없음)

TASK-023의 공개 corpus(small·medium·large)에서 FUP-002 규칙대로 최소 3회, seed `20260920`으로 측정한다. 측정은 temp store에서 하고 삭제를 하지 않는다. 수치는 TASK-021 구현 전이므로 파일시스템 집계 스크립트나 `cv-inspect`로 수집한다. 구현 뒤에는 `cv-gc --dry-run`으로 같은 값을 다시 확인한다.

| 측정 항목 | 방법 | 결정할 값 |
|---|---|---|
| generation 크기 분포(manifest+shard bytes, 파일·심볼 수 대비) | corpus별 cold build 뒤 `generations/<id>` 파일 길이 합 | `maxBytes` 하한 = 보호가 필요한 generation 수 × P95 크기 |
| update당 증가량과 full/incremental 차이 | 대표 편집 시퀀스를 N회 update하며 `generations/` 총량 추이를 본다(generation마다 전체 shard를 새로 씀) | 시간당 증가율 → `maxBytes`와 `maxAgeHours`의 관계 |
| 세션 길이·세션당 update 횟수·동시 세션 수 | benchmark harness의 세션 replay 기록. 실제 사용자 로그는 FUP-001 승인 전에는 쓰지 않는다 | `maxAgeHours` ≥ 세션 길이 P95, `maxBytes` ≥ 동시 pin 수 × 크기 |
| 세션 snapshot 크기 | `sessions/<id>` 합계(삭제 대상 아님) | 사용자 안내용 총 디스크 예상치 |
| warm 이득 | 이전 generation이 남아 있을 때와 없을 때의 `cv-update` 시간, 비current generation 조회(`--generation`) 빈도 | 오래된 generation을 오래 둘 가치가 있는지 → mode 기본값 |
| `StartSession`의 build 게시 → pin 기록 지연 P99 | 단계별 timestamp | `graceMinutes` 하한(여유를 두고) |
| 보호 집합 계산과 scan 시간(generation 수 10/100/1000 합성 배치) | dry-run 상당의 열거만 | lease 보유 시간 → 실행당 삭제 상한 필요 여부 |
| 디스크 여유 | 측정 환경 볼륨의 여유 공간 기록 | 절대 bytes 상한으로 충분한지(여유율 기반 상한은 이번 범위 밖) |

결과는 `benchmarks/results/cache-measurement.md`에 절차·환경·반복 수와 함께 기록한다. 비공개 레포 수치는 싣지 않는다.

## G4에서 확정할 값

| 값 | 현재 | 비고 |
|---|---|---|
| `gc.mode` 기본값 | G4에서 확정 | 비활성 동안에는 영향 없음 |
| `gc.age.maxAgeHours` 기본값 | G4에서 확정 | built-in `null` |
| `gc.capacity.maxBytes` 기본값 | G4에서 확정 | built-in `null` |
| `gc.graceMinutes` 기본값 | G4에서 확정 | 0은 금지 |
| 실행당 삭제 개수 상한(필요 시) | G4에서 확정 | scan 측정 결과로 필요 여부부터 판단 |
| `gc/runs.jsonl` 보존 run 수 | G4에서 확정 | |
| `gc.enabled` 기본값 | `false` 유지 | FUP-004. 켜는 것은 사용자가 user config로 한다 |

## 사용자 확인이 필요한 결정

1. **config 계층**: User(`%APPDATA%`) + Workspace(`.code-virtualize/config.json`) 두 단계로 하고 store 수준 config는 두지 않는다.
2. **workspace 신뢰 규칙**: workspace config는 GC를 켤 수 없고 수치를 더 보존적인 방향으로만 바꿀 수 있다.
3. **비활성 시 수동 삭제 금지**: `gc.enabled = false`이면 `cv-gc`도 dry-run만 한다. 고아 temp·tombstone 정리도 하지 않는다.
4. **CLI override 범위**: 수치·mode 플래그는 `--dry-run`에서만 받고, `enabled`를 켜는 CLI 플래그는 두지 않는다.
5. **자동 실행**: `gc.trigger = "after-publish"`를 선택 사항으로 제공하고 기본은 `manual`로 둔다.
6. **`hybrid` 의미**: 합집합(나이 초과는 삭제하고, 그래도 용량을 넘으면 오래된 순서로 추가 삭제)으로 한다. 교집합(용량 초과 시 나이 초과분만 삭제)과 다른 결과가 나온다.
7. **나이 기준**: manifest `createdAt`으로 한다.
8. **용량 계산 범위**: `generations/` 총량(보호분 포함)으로 비교하고, 삭제는 비보호분에서만 한다. 세션 snapshot은 계산에서 빼고 따로 보고한다.
9. **세션 데이터 범위**: 종료된 세션의 snapshot(`sessions/<id>`)도 이번 GC 대상에서 뺀다. 지우려면 별도 승인과 ADR 개정이 필요하다.
10. **손상 generation**: manifest를 읽을 수 없는 generation은 지우지 않고 보고만 한다.
11. **GC의 MCP 노출 금지**: 모델 도구로 GC를 제공하지 않는다.

## TASK-021 구현 분해 (설계 확인·G4 뒤)

| 순서 | 작업 | 주요 파일 | 병렬 |
|---|---|---|---|
| 1 | config 모델·loader·병합·검증, `schemas/config.schema.json` | `src/CodeVirtualize.Core/Configuration/*`(신규), `schemas/config.schema.json` | 2와 병렬 가능 |
| 2 | GC planner(보호 집합·후보 선정), 보고서 계약, `GcFaultPoint` | `src/CodeVirtualize.Core/Storage/CacheGc*.cs`(신규), `StorageContracts.cs` | 1과 병렬 가능 |
| 3 | executor(fence·tombstone·복구), `AcquireWriterLease`를 `internal`로 공개, pin 공유 위반을 `BUSY`로 매핑 | `GenerationStore.cs`, `CacheGcExecutor.cs` | 2 이후 |
| 4 | `cv-gc` 명령, `--user-config`, help, `after-publish` 연결 | `Cli/Commands/GcCommand.cs`(신규), `CliOptions.cs`, `BuildCommand.cs`, `UpdateCommand.cs`, `Program.cs` | 1·3 이후. `Program.cs`는 TASK-026과 순서대로 병합 |
| 5 | 문서 반영 | `docs/contracts/cli.md`(config·GC 절), `docs/prepare/architecture.md`(State Management·Configuration 동기화), 이 ADR의 상태 갱신 | 4 이후 |

TASK-033(리뷰)은 `GcExecutor` 단일 삭제 지점, 권한 객체 생성 경로, fence와 lease의 경합, dry-run과 실제 실행의 일치를 본다. TASK-034(테스트)는 위 검증 설계 표를 구현과 독립된 기대값으로 작성한다.

## 결과

이 ADR이 승인되기 전에는 코드·schema·계약 문서를 바꾸지 않는다. 승인 뒤에도 G4 수치가 확정되기 전에는 built-in 수치가 `null`이다. 따라서 `gc.enabled = true`로 실행해도 `CONFIG_INCOMPLETE`로 삭제가 일어나지 않는다. 사용자가 user config에 수치를 직접 쓴 경우만 예외다. 자동 GC의 terminal disposition은 계속 no-delete이고, ADR 003의 보존 대상은 구조적으로 삭제 허용 목록 밖에 있다.

## 참고

- .NET의 Unix `FileShare` 에뮬레이션(`FileShare.None` → `LOCK_EX`, 그 외 → `LOCK_SH`, advisory): [dotnet/runtime PR #134060](https://github.com/dotnet/runtime/pull/134060), [dotnet/runtime issue #52700 — Linux에서 `File.Delete`는 `FileShare.None`을 따르지 않음](https://github.com/dotnet/runtime/issues/52700)
- `Environment.SpecialFolder.ApplicationData`의 Unix 매핑(`$XDG_CONFIG_HOME` 또는 `~/.config`): [.NET 8 breaking change: GetFolderPath behavior on Unix](https://learn.microsoft.com/en-us/dotnet/core/compatibility/core-libraries/8.0/getfolderpath-unix)
- Windows 이름 변경은 DELETE 권한 열기를 거치므로 열린 핸들의 공유 모드에 영향을 받는다: [The Old New Thing — Renaming a file is a multi-step process](https://devblogs.microsoft.com/oldnewthing/20211022-00/?p=105822)
