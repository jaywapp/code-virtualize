# Architecture

## 상태와 적용 조건

이 문서는 **C# Roslyn PoC를 진행할 경우의 추천 설계**다. UC-001에서 기존 도구 연동만 선택하면 해당 경로로 다시 줄여야 한다. 원안 대비 변경은 [user-confirm.md](user-confirm.md)에서 승인받으며, 승인 전 구현 기준으로 확정하지 않는다.

## Architecture Overview

```mermaid
flowchart LR
    H[CLI / Claude adapter] --> Q[Query service]
    H --> L[Lifecycle service]
    Q --> V[Freshness / Coverage validator]
    V --> C[Versioned CV store]
    V --> R[Source resolver]
    L --> P[CSharp Roslyn adapter]
    P --> W[Workspace source / project graph]
    R --> W
    P --> C
    Q --> F[Bounded fallback / repair]
    F --> P
    Q --> M[Local metrics]
    L --> M
    B[VCS baseline provider] --> D[Symbol diff]
    W --> D
    D --> Q
```

기본 프로세스 경계는 단일 .NET 실행 파일 + 라이브러리다(UC-003). CLI와 향후 MCP host가 동일한 Core 계약을 호출한다. 처음부터 npm launcher·C# worker IPC·C++ worker를 동시에 구현하지 않는 안을 추천한다. 외부 compiler backend를 도입할 때에만 worker protocol을 설계한다.

## Technology Stack

| 영역 | 추천안 | 이유·대안 | 결정 |
|---|---|---|---|
| Core·CLI | C#/.NET, `global.json`에 구현 시 지원 SDK 고정 | Roslyn과 한 runtime; TypeScript launcher는 배포 편의와 IPC 비용의 교환 | UC-003 |
| C# 분석 | Roslyn syntax/semantic/workspace APIs | 선언과 프로젝트 의미를 함께 다룸; LSP 재사용은 Phase 1 비교군 | UC-001/002 |
| 저장 | UTF-8 JSON manifest + JSONL `.cv` shards | PoC 검사·손상 주입이 쉬움; 대규모 random access 비용이 크면 SQLite 검토 | UC-004 |
| 검색 | snapshot별 정규화 이름·ID의 메모리 인덱스 | 결정적인 조회, 원문 body는 저장하지 않음 | UC-006 |
| 변경 감지 | 훅/파일 watcher + 조회 시 hash 검증 + inventory reconciliation | 이벤트 유실에 대비; 이벤트는 최적화 힌트 | UC-004 |
| 외부 노출 | CLI JSON, 이후 MCP stdio | 독립 Core 유지; 훅은 lifecycle만 담당 | UC-009 |
| 테스트 | .NET 테스트 프로젝트 + golden fixtures + integration process tests | 파서/저장/CLI/동시성 책임을 나눠 검증 | UC-003 |

Roslyn Workspaces는 solution/project/document 모델과 syntax tree·semantic model 접근을 제공한다. 이는 C# 선택의 기술적 근거일 뿐, 대형 솔루션의 속도나 정확도를 보증하지 않는다. [Microsoft 문서](https://learn.microsoft.com/en-us/dotnet/csharp/roslyn-sdk/work-with-workspace).

버전 번호와 NuGet/MCP package는 현 단계에서 임의로 고정하지 않는다. TASK-006에서 선택 SDK와 호환되는 안정 버전을 확인하고 lock·재현 가능한 restore를 포함한다.

## 기존 도구와의 경계

| 비교 대상 | 확인된 기능 | CV 검증 질문 |
|---|---|---|
| LSP | 심볼·정의·참조 관련 표준 요청 | 별도 CV store 없이 충분한가? 실제 서버 capability를 확인했는가? |
| Serena | LSP backend, C#·C/C++ 지원, 심볼·참조 탐색 | 얇은 lifecycle/출력 조정만으로 같은 이득을 얻는가? |
| Aider repo-map | 토큰 예산 안의 코드 구조 지도 | 대략적 맥락 제공만으로 원문 읽기량이 충분히 줄어드는가? |
| grep + 범위 읽기 | 현재 사용 도구로 구성할 실험 조건 | 낭비가 도구 부재보다 지시·사용 습관 때문인가? |

근거: [LSP](https://microsoft.github.io/language-server-protocol/specifications/lsp/3.17/specification/), [Serena](https://github.com/oraios/serena), [Aider repo-map](https://aider.chat/docs/repomap.html). 2026-09-19 확인. 기존 도구에 특정 기능이 절대로 없다는 주장은 하지 않는다. 실제 버전별 비교표와 성능 결과는 TASK-003/004의 산출물이다.

## System Components / Component Responsibilities

| 컴포넌트 | 책임 | 경계 |
|---|---|---|
| WorkspaceCatalog | root·project graph·file inventory·설정 fingerprint | 파일 존재만으로 semantic coverage 보장 금지 |
| CSharpAdapter | 선언 추출, semantic binding, capability·실패 범위 보고 | 신뢰 정책 없이 restore/build/generator 실행 금지 |
| SnapshotStore | immutable generation 작성·읽기·검증·GC | query와 저장 포맷을 결합하지 않음 |
| QueryService | 검색·paging·ID 조회·정렬·예산 | 원문 또는 임의 코드 실행 금지 |
| SourceResolver | 동일 bytes 검증·span resolve·context 확장 | 인덱스 줄 번호만 신뢰하지 않음 |
| UpdateCoordinator | 변경 병합, semantic 무효화, 단일 writer | 모든 변경을 해당 파일 하나만 갱신하면 끝난다고 가정하지 않음 |
| FallbackService | 원문 후보 검색, repair 요청, 제한·원인 보고 | 텍스트 일치 결과를 semantic 참조로 승격 금지 |
| ReferenceService | 정적 참조와 텍스트 후보의 구분·coverage | 전체 runtime 참조 완전성 보장 금지 |
| BaselineProvider / DiffService | 명시 VCS base 읽기, snapshot 비교, textual hunk 연결 | checkout·sync·source 수정 없이 동작 |
| AgentAdapter | CLI/MCP 매핑, lifecycle hook, 장애 시 기존 탐색 안내 | 엔진을 Claude 전용으로 종속시키지 않음 |
| MetricsWriter | 제한된 구조화 이벤트 | 원문·프롬프트·비밀 기본 기록 금지 |

## Data Flow

### 구축·발행

1. workspace root와 분석 구성을 정규화하고 session ID를 만든다.
2. 파일 목록, 내용 hash, project·reference·compiler option fingerprint를 수집한다.
3. 지원 범위 안에서 선언·semantic 결과를 계산하고 실패한 프로젝트를 기록한다.
4. 새 generation을 임시 디렉터리에 작성한다. manifest는 모든 shard의 digest·개수·schema를 참조한다.
5. 분석 중 input이 변했다면 해당 generation을 최신이라고 publish하지 않는다. 제한된 재시도 후 부분/오류 응답을 낸다.
6. writer lock 안에서 검증된 manifest pointer를 같은 볼륨의 원자적 교체로 갱신한다. 플랫폼별 동작은 통합 테스트로 확인한다.

### 조회·복구

1. query는 generation을 pin하고 지원 범위·schema·cursor를 확인한다.
2. `find`는 현재 파일 inventory와 project fingerprint를 reconciliation한다. 기존 결과 파일만 검사하면 새 파일의 심볼을 놓치므로 신규·삭제 파일도 확인한다.
3. `resolve`는 대상 파일을 한 번 읽어 그 bytes의 digest를 검증하고, 같은 buffer에서 source를 추출한다. 반환 후 파일 변경까지 막을 수 없으므로 fingerprint를 반환한다.
4. 불일치면 재파싱 후 심볼 identity를 다시 찾는다. 찾지 못하면 `STALE_SYMBOL`과 후보/추가 탐색 안내를 반환한다. 예전 줄 번호로 대체하지 않는다.
5. fallback의 검색 범위·시간·호출 수는 예산을 적용한다. repair는 원본을 고치지 않고 인덱스만 갱신한다.

mtime·size는 빠른 변경 힌트일 뿐이다. 전체 workspace hash 검증이 너무 비싸면 엄격한 검증 모드와 힌트 모드를 비교하되, 후자는 `workspaceFreshness=unknown`을 숨기지 않는다. 성능 때문에 정확성 계약을 조용히 바꾸지 않는다.

## Directory Structure

### 제품 저장소 제안

```text
code-virtualize/
  src/
    CodeVirtualize.Core/
    CodeVirtualize.CSharp/
    CodeVirtualize.Cli/
    CodeVirtualize.Mcp/            # UC-009 이후
  integrations/claude/            # UC-009 이후
  schemas/
  tests/
    Core.Tests/
    CSharp.Tests/
    Cli.Tests/
    Integration.Tests/
    fixtures/
  benchmarks/
    protocol.md
    corpus-manifest.json
    tasks/
    results/                     # 비밀 없는 승인된 집계만
  docs/prepare/
  global.json
  CodeVirtualize.sln
```

원안의 `packages/`는 runtime 선택 전 초안이었다. 이 구조는 .NET 선택 시 대안이며, TS 선택 시 UC-003에서 architecture/plan의 파일 경로를 같이 수정한다.

### Workspace runtime 제안 — UC-004 승인 조건

```text
.code-virtualize/
  config.json
  cache/<workspace-key>/<analysis-key>/
    generations/<generation-id>/
      manifest.cv
      symbol.cv
      remark.cv                  # 도입 단계 이후
      reference.cv               # 도입 단계 이후
    current.json                 # 검증된 generation pointer
    writer.lock
  sessions/<session-id>/
    session.json
    baseline.json                # 선택한 revision·snapshot 의미
    diff/current.diff.cv
  metrics/<session-id>.jsonl
```

영속 캐시 안을 세션과 분리하는 안이다. 세션 폐기를 선택하면 generation을 각 session 아래 두되 immutable publish·검증 계약은 유지한다. workspace key에는 실제 경로·worktree identity를 반영하여 서로 다른 checkout을 혼용하지 않는다. `.code-virtualize/`는 VCS 제외 대상이다. 삭제 가능한 cache와 사용자 config를 구분하여 cache GC가 config를 지우지 않게 한다.

## Data Model

| 레코드 | 필수 필드와 의미 |
|---|---|
| Manifest | `schemaVersion`, `generationId`, `workspaceKey`, `analysisKey`, `createdAt`, `adapterVersion`, `inputFingerprint`, `files[]`, `projects[]`, `shards[]`, `coverage` |
| File | `fileId`, root 상대 `path`, `contentHash`, `byteLength`, `encoding`, `newline`, `projectIds[]`; hash는 SHA-256 기준 제안 |
| Project | `projectId`, `targetFramework`, `configuration`, `defines[]`, `referencesFingerprint`, `loadStatus`, `analysisLevel` |
| Symbol | `symbolId`, `projectId`, `kind`, `name`, `qualifiedName`, `signature`, `accessibility`, `containerId`, `declarations[]` |
| Declaration | `fileId`, `contentHash`, `spanStart`, `spanLength`, `startLine`, `endLine`; 부분 선언은 배열로 유지 |
| Remark | `symbolId`, `fileId`, `span`, `kind`, `contentHash`; 원문은 resolve 시 읽음 |
| Reference | `targetSymbolId`, `sourceSymbolId` nullable, `location`, `kind`, `provenance`, `analysisKey` |
| DiffEntry | `kind`, `baseSymbolId`, `targetSymbolId`, `baseLocations[]`, `targetLocations[]`, `evidence`, `matchConfidence` |

`spanStart/spanLength`는 decode된 source의 0-based UTF-16 code unit, `startLine/endLine`은 1-based inclusive로 정의한다. byte offset과 혼용하지 않는다. source generator 문서는 `documentKind=generated`와 가상 URI로 구분하며, 디스크 경로처럼 외부 파일 읽기를 허용하지 않는다.

ID는 `project identity + 분석 구성 + symbol kind + qualified metadata signature`의 안정 hash로 제안한다. generic arity, parameter type/ref-kind, explicit interface 구현을 포함한다. partial 선언은 하나의 심볼·여러 location으로 합친다. rename/signature 변경 시 ID가 바뀔 수 있으며 revision 사이 영구 ID라고 보장하지 않는다. 지원하지 않는 문법이나 오류로 semantic identity를 못 얻으면 `identityQuality=syntactic`를 표기한다.

`analysisKey`는 **파일 hash만이 아니라** schema·adapter/compiler 버전·project graph·references·target framework·defines·분석 신뢰 모드를 포함한다. public API 변화, global using, reference 변화 등은 종속 프로젝트의 binding/reference 결과까지 invalidation한다.

### 공통 응답 계약

```json
{
  "schemaVersion": 1,
  "requestId": "req-demo-01",
  "generationId": "gen-demo-01",
  "status": "partial",
  "freshness": {"returnedFiles": "verified", "workspace": "unknown"},
  "coverage": {
    "scope": "static-csharp-selected-configuration",
    "level": "partial",
    "analyzedFiles": 8,
    "excludedFiles": 1,
    "failedProjects": ["GeneratedProject"],
    "limitations": ["dynamic-references-not-covered"]
  },
  "results": [],
  "truncated": false,
  "nextCursor": null,
  "fallback": {"attempted": false, "reason": null},
  "repair": {"status": "not-requested"},
  "errors": []
}
```

예시 데이터이며 실제 분석 결과가 아니다. `status=ok|partial|not_found|error`, `coverage.level=complete_within_scope|partial|unknown`. complete는 선언된 static scope에 한정된다. 0건 응답에도 scope·coverage는 필수다. `returnedFiles=verified`와 `workspace=unknown`은 동시에 가능하다. pagination cursor는 generation·query·offset에 묶어 바뀐 generation에 재사용하면 `CURSOR_EXPIRED`를 낸다.

## Interfaces

아래는 구현할 계약 예시이며 지금 실행 가능한 명령이 아니다. `cv-*` 이름을 기본으로 하고 단일 `cv` 실행 파일의 subcommand/alias 제공 방식은 UC-003에서 정한다.

| 명령 | 입력 | 결과·오류 |
|---|---|---|
| `cv-build` | workspace, project/solution, config, session | manifest/coverage; 부분 project load를 숨기지 않음 |
| `cv-find` | query, project/kind/path filter, limit, cursor | signature·ID·location·분석 범위, body 없음 |
| `cv-resolve` | symbol ID, generation, part, max bytes/lines | source/remark, fingerprint, 실제 range; ambiguity/changed ID 거부 |
| `cv-inspect` | session 또는 generation, format text/json | cache 상태·coverage·제약·last error, 기본 source 없음 |
| `cv-validate` | scope files/workspace, generation | mismatch·missing·schema 오류, 원문 수정 없음 |
| `cv-update` | changed paths 또는 reconcile, session | 새로운 generation, invalidated project 범위 |
| `cv-impact` | symbol IDs, depth, budget, text candidates | 정적 참조와 lexical 후보·한계·paging |
| `cv-diff` | base, target, baseline-kind | symbol 변경·textual hunk·매칭 근거 |

검색은 exact qualified name → exact simple name → prefix → substring 순, 동률은 project/path/line/ID의 ordinal 순을 제안한다. fuzzy·LLM relevance는 초기 계약에서 제외한다(UC-006). 기본 source 반환은 선언 header·containing type·using을 요청 가능한 section으로 나누고, body/full file 확장은 명시 요청과 반환 예산을 따른다.

MCP 후보: `cv_find → cv-find`, `cv_get → cv-resolve`, `cv_impact → cv-impact`. `cv_impact`는 reference 단계 완료 전 미지원 capability로 표시하거나 노출하지 않는다. lifecycle 명령을 모델의 탐색 tool 목록에 모두 넣지 않는다.

stdout은 요청당 JSON 하나, stderr는 진단이다. CLI exit code 제안: `0` 정상/정상 범위의 not_found, `2` 잘못된 입력, `3` 부분 결과·stale·미지원, `4` 내부/IO 오류, `5` timeout/cancel. agent adapter는 exit code만 읽지 않고 JSON status·coverage를 함께 처리한다.

### cv-inspect 터미널 출력 예시

```text
Code-Virtualize inspect — example data
Workspace     sample-csharp
Session       session-demo-01
Generation    gen-demo-01
Freshness     returned-files: verified / workspace: unknown
Coverage      partial / selected C# configuration
Projects      2 analyzed / 1 failed
Limitation    dynamic references are not covered
Next action   inspect failed project diagnostics; use source search
```

GUI 대신 사용할 비대화형 텍스트 계약이다. 색상만으로 상태를 표현하지 않으며 pipe·`--json`·좁은 터미널에서도 의미가 보존되어야 한다.

## External Dependencies

- .NET SDK/Roslyn·MSBuild 관련 package: 솔루션 로딩의 실제 지원 범위는 spike로 검증한다.
- Git: base source와 textual diff를 read-only로 읽는다. Perforce는 UC-002/005 후 별도 adapter다.
- `rg`: fallback 후보 도구. 설치 여부를 검사하고 없으면 파일 scan 대안 또는 `CAPABILITY_UNAVAILABLE`을 명시한다.
- Claude/MCP: 초기 연동 후보이며 Core 필수 의존성이 아니다. 공식 plugin 문서는 MCP/LSP와 lifecycle hook 연동을 설명한다. [Claude plugin reference](https://code.claude.com/docs/en/plugins-reference), [hook reference](https://code.claude.com/docs/en/hooks).
- 원격 서비스·벡터 DB·LLM API는 Core 실행에 필수로 두지 않는다.

## State Management

generation: `building → valid | partial | failed`. session: `starting → active → closing → closed`. source 변화는 현 generation의 freshness를 무효화하지만 immutable 파일 자체를 수정하지 않는다.

writer는 workspace/analysis key별 하나이며 competing writer는 bounded wait 또는 `BUSY`로 응답한다. reader는 generation을 pin하고 manifest가 가리킨 shard만 읽는다. GC는 active reader/session이 참조하는 generation을 제거하지 않는다. crash lock 회수는 PID만이 아니라 host/process start identity와 lease를 검사한다. 강제 종료·재부팅 후 복구를 테스트한다.

한 세션 종료는 자신의 pin만 해제한다. 영속 cache retention/용량은 UC-004 결정 후 설정한다. session mode에서도 다른 session 데이터와 사용자 설정은 삭제하지 않는다.

## Error Handling

| 코드 | 상황 | 처리 |
|---|---|---|
| `SCHEMA_UNSUPPORTED` | 알 수 없는 major schema | read 중단, 재구축 안내 |
| `STALE_SYMBOL` | 해시·identity 불일치 | 제한 재파싱, 실패 시 source fallback |
| `COVERAGE_PARTIAL` | 프로젝트 로드 실패·미분석 참조 | 결과+실패 범위, 완전 분석으로 위장 금지 |
| `BASE_REQUIRED` | 리뷰 base 미지정 | diff 실행 전 입력 요구 |
| `PATH_OUTSIDE_WORKSPACE` | traversal·symlink/junction 이탈 | 읽기 전에 거부 |
| `BUDGET_EXCEEDED` | timeout·result/source 제한 | 잘림·cursor·가능한 재시도 정보 |
| `BUSY` | writer lock 경합 | 기존 generation 조회 또는 명시 재시도 |
| `SOURCE_UNSTABLE` | 읽는 동안 반복 수정 | stale 원문 반환 없이 중단 |

고정 무한 재시도는 금지한다. fallback 예산 초과 시 실제로 수행하지 않은 검색·복구를 성공했다고 보고하지 않는다.

## Logging

기본 이벤트 필드: `timestamp`, `sessionId`, `requestId`, `event`, `durationMs`, `generationId`, `resultCount`, `sourceBytes`, `sourceLines`, `fallbackReason`, `coverageLevel`, `errorCode`. file path는 기본적으로 상대 경로 또는 비식별 ID를 사용한다.

source body·comment·prompt·환경 변수·토큰·credential은 로그에 넣지 않는다. 벤치마크 token 사용량은 외부 harness가 제공하며 엔진의 line/byte 수로 실제 input token이나 결제 비용을 단정하지 않는다. telemetry는 로컬, 원격 전송은 별도 동의가 없으면 없다.

## Configuration

우선순위는 원안대로 Built-in → Global → Workspace → Session override다. 설정에는 schema version, workspace scope, include/exclude, project configuration, trust mode, cache mode/limits, source/result limits, timeout, logging level을 둔다.

workspace 설정은 untrusted 입력이다. 전역 trust 정책을 완화하거나 임의 실행 명령·외부 경로·외부 telemetry 목적지를 강제할 수 없게 한다. 기본 제외 후보는 `.git`, `.env*`, credential 파일, binary, cache다. `obj`/generated source는 무조건 완전 무시했다고 숨기지 않고 분석 방식에 따른 제외 범위를 표기한다.

## Security Considerations

- 실제 root 경계를 resolve한 뒤 경로를 검증한다. junction/symlink, case normalization, UNC, drive 변경을 포함한다.
- build system·project evaluation·analyzer/source generator는 코드를 실행할 수 있는 경계로 취급한다. 신뢰되지 않은 프로젝트에서는 syntax-only와 명시적인 degraded coverage를 제공하는 안을 추천한다(UC-008).
- 자동 restore·network·build는 기본적으로 수행하지 않는 안이며, semantic workspace load 전에 trust와 필요 도구를 확인한다.
- `.cv`의 signature·경로·주석도 민감할 수 있다. disposable이라는 이유로 공개하거나 다른 workspace와 공유하지 않는다.
- cache 입력은 크기·중첩·record count·checksum을 검증한다. deserialization으로 코드나 타입을 임의 활성화하지 않는다.
- 소스와 `.cv` 내용은 데이터다. 주석 안의 지시문을 하네스 권한으로 승격하지 않는다.

## Testing Strategy

1. Golden fixtures: overload, generics, explicit interface, partial, private, record, property/event/indexer, nested types, file-scoped namespace.
2. 해석 경계: multi-target, defines, project reference 변화, linked/generated source, syntax errors, unsupported grammar.
3. resolve: CRLF/LF/BOM·한글·emoji·큰 파일·same mtime/size 변경·연속 수정에서 올바른 bytes를 반환하거나 명시 실패.
4. lifecycle: 신규/삭제/rename, hook 누락, branch switch, 손상 shard, schema mismatch, disk full, worker crash, 두 writer/reader/GC.
5. reference: 손으로 만든 정답 fixture와 독립적으로 검토한 위치 목록. static/dynamic 정답을 분리하고 CV 결과를 자기 정답으로 사용하지 않음.
6. diff: dirty start, 세션에 걸친 변경, 추가/삭제/rename, formatting-only, base 부재, baseline 변경, 삭제 파일 resolve.
7. security: 경계 이탈, 명령 인자 injection, untrusted generator, secret fixture의 로그·artifact 노출 검증.
8. benchmark: [plan.md](plan.md)의 실험 계약을 따른다. 제품의 속도·토큰 절감은 테스트 fixture 통과만으로 증명하지 않는다.

## Build / Deployment

UC-003 이후 단일 solution의 restore/build/test와 CLI subprocess smoke test를 CI로 구성한다. 의존성은 lock·SDK pin으로 재현하고 Windows를 첫 검증 환경으로 제안한다. 다른 OS는 UC-002에서 support matrix를 결정한다.

PoC는 로컬 산출물로 배포한다. 전역 설치·PATH 변경·agent 설정 수정·npm/.NET package 게시·release는 이번 준비 범위 밖이며 별도 작업이다. 설치 프로그램을 구현한다면 dry-run, 설정 병합, 중복 방지, uninstall 복원을 수용 조건으로 둔다.

## Technical Risks

| 위험 | 검증/대응 | 결정 또는 작업 |
|---|---|---|
| 기존 도구로 가치 대부분 충족 | 같은 corpus·모델·권한으로 비교 | UC-001, TASK-004/005 |
| semantic load 비용이 순이익 상쇄 | cold/warm/long 분리 | UC-007, TASK-016 |
| 정적 결과의 조용한 누락 | coverage 계약, independent ground truth | TASK-012/013 |
| 해시 cache key가 semantic 의존성 누락 | project/references/config invalidation | TASK-008/011 |
| 영속 cache 동시성·유출 | generation publish, scoped GC, 로그 검사 | UC-004/008, TASK-011/012 |
| 심볼 diff를 동작 의미 변화로 과장 | syntax/signature/body 변화로 명명, raw diff 유지 | TASK-014 |
| 원안 말미 누락 | 누락 정보 별도 결정 | UC-010 |

## 조사 범위의 한계

공식 문서는 기능 존재와 선택 이유를 확인하는 용도로 읽었다. 실제 benchmark, Roslyn 대형 솔루션 로드, Claude hook/MCP 호환성 실험, Perforce·UE5 검증은 수행하지 않았다. 원안의 가격·도구명·성능 추정은 현재 환경의 보장으로 승격하지 않았다. 실제 구현 시 지정 버전의 공식 문서와 capability를 다시 확인한다.
