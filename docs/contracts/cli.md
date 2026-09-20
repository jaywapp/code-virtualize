# CLI 및 `.cv` 계약 v1

이 문서는 `.cv` 저장 레코드와 CLI JSON 응답의 버전 1 계약을 정의한다. 소스 파일이 진실의 원천이며 `.cv`는 재생성 가능한 색인이다. 구현 모델은 `src/CodeVirtualize.Core/Contracts/`, 기계 판독 schema는 `schemas/`에 있다.

## 버전과 역직렬화

모든 최상위 Manifest, Symbol, Declaration, Reference, Diff, Response 객체에는 `schema`와 정수 `schemaVersion`이 필수다. 버전 1의 schema 이름은 다음과 같다.

| 레코드 | `schema` |
|---|---|
| Manifest | `code-virtualize/manifest` |
| Symbol | `code-virtualize/symbol` |
| Declaration | `code-virtualize/declaration` |
| Reference | `code-virtualize/reference` |
| Diff | `code-virtualize/diff` |
| Response | `code-virtualize/response` |

reader는 기대한 schema 이름과 정확히 일치하는 버전 1만 읽는다. 이름 또는 버전이 다르거나 누락되면 `SCHEMA_UNSUPPORTED`로 중단한다. 대소문자가 다른 속성, 알 수 없는 속성, 중복 JSON 속성, 숫자로 표현한 enum도 거부한다. 알 수 없는 major/minor를 일부 필드만 읽어 계속 사용하지 않는다.

## span과 위치

`span.start`와 `span.length`는 decode된 .NET 문자열의 0-based UTF-16 code unit 수다. Unicode scalar 또는 UTF-8 byte offset이 아니다. 예를 들어 BMP 밖 문자인 emoji 하나는 길이 2다. `span.startLine`과 `span.endLine`은 1-based inclusive다. 각 span은 이를 JSON에서도 명시하도록 다음 고정 필드를 가진다.

```json
{
  "start": 10,
  "length": 4,
  "startLine": 2,
  "endLine": 2,
  "offsetUnit": "utf16_code_unit",
  "lineBase": 1,
  "endLineInclusive": true
}
```

위치는 `fileId`, `contentHash`, `span`과 함께 실제 source의 workspace 상대 `path` 또는 generated document의 가상 `uri` 중 정확히 하나만 가진다. 저장 span으로 source를 읽기 전에 같은 bytes의 SHA-256 fingerprint를 검증해야 한다.

## 결정적 symbol ID

`symbolId`는 `sym_`와 64자리 lowercase SHA-256 hex로 구성한다. hash 입력은 길이 prefix를 붙인 UTF-8/binary canonical sequence이며 순서는 다음과 같다.

1. project identity
2. analysis key
3. symbol kind
4. qualified metadata name
5. generic arity
6. explicit interface identity 또는 빈 문자열
7. parameter 수
8. 선언 순서의 parameter type identity와 ref-kind

parameter 이름, source 경로, 줄 번호, 선언 순서는 identity에 넣지 않는다. 따라서 같은 partial symbol의 여러 선언은 한 ID와 여러 declaration으로 합쳐진다. generic arity, overload parameter type/ref-kind, explicit interface, project 또는 분석 구성이 달라지면 ID가 달라진다. semantic identity를 얻지 못한 symbol은 `identityQuality=syntactic`과 원인을 `limitations`에 기록한다. rename/signature 변경 전후의 영구 ID를 보장하지 않는다.

## coverage와 limitation

freshness, coverage, truncation은 서로 다른 축이다. 파일 hash가 일치해도 전체 workspace 또는 동적 참조의 분석이 완전하다는 뜻은 아니다.

`coverage`는 아래 필드를 항상 포함한다.

- `scope`: 완전성 주장이 적용되는 정적 언어·프로젝트·구성 범위
- `level`: `complete_within_scope`, `partial`, `unknown`
- `analyzedFiles`, `excludedFiles`, `failedFiles`, `unknownFiles`
- `failedProjects`
- `limitations`: scope 밖 동적 참조, syntax-only 분석, load 실패 등
- `truncated`: coverage 집계 또는 결과가 예산 때문에 잘렸는지 여부

`complete_within_scope`는 선언한 scope 안에서만 완전하다는 뜻이며 failed/unknown/truncated 항목이 없어야 한다. `partial`과 `unknown`은 비어 있지 않은 `limitations`로 이유를 설명한다. 참조 0건도 coverage를 생략할 수 없고, lexical candidate를 확인된 static reference로 표시하지 않는다.

## 응답 상태와 오류

Response의 `status`는 `ok`, `partial`, `not_found`, `error`다. `truncated`는 별도 boolean이며 true이면 `status=partial`과 `coverage.truncated=true`여야 한다.

| 상태 | 의미 |
|---|---|
| `ok` | 선언된 scope 안에서 완전한 정상 결과 |
| `partial` | 분석 범위 누락, recoverable error 또는 예산 잘림이 있는 결과 |
| `not_found` | 정상 조회 결과가 0건이며 `results`와 `errors`가 비어 있음 |
| `error` | 요청을 정상 결과로 처리하지 못했으며 `errors`가 하나 이상 있음 |

`freshness.returnedFiles=verified`와 `freshness.workspace=unknown`은 동시에 가능하다. `fallback.attempted`가 true이면 실제로 수행한 이유를 기록하고, 수행하지 않은 fallback을 성공으로 표시하지 않는다. `repair.status`는 `not_requested`, `not_needed`, `succeeded`, `failed` 중 하나다. 오류에는 안정적인 `code`, 사람용 `message`, `retryable`, 비밀이나 source를 포함하지 않는 문자열 `details`를 둔다.

CLI의 종료 코드는 JSON을 대체하지 않는다. `0`은 정상 및 정상 범위의 `not_found`, `2`는 입력 오류, `3`은 partial/stale/unsupported, `4`는 내부/IO 오류, `5`는 timeout/cancel에 사용한다. client는 종료 코드와 JSON status/coverage를 함께 검사한다.

## paging cursor

cursor payload는 cursor schema version, `generationId`, 정규화 query의 SHA-256 fingerprint, 다음 0-based offset을 포함한 base64url JSON이다. cursor는 불투명 값으로 취급한다.

- 요청 generation이 payload와 다르면 `CURSOR_EXPIRED`를 반환하고 처음부터 재조회한다.
- query fingerprint가 다르거나 payload/schema가 잘못되면 `CURSOR_INVALID`를 반환한다.
- `nextCursor`가 있으면 `truncated=true`다. 예산 초과 결과라도 재개 가능한 단위가 없으면 cursor가 null일 수 있다.

cursor는 인증 토큰이 아니며 보안 경계를 제공하지 않는다.

## source 반환 예산

source 요청은 양의 `maxBytes`와 `maxLines`를 가진다. Response의 선택적 `source`는 실제 `content`, UTF-16 `span`, 요청 `budget`, 실제 `usage`, `truncated`, `exhaustedBy`를 함께 반환한다.

`usage.bytes`는 JSON escaping 전 `content`의 UTF-8 byte 수다. 원본 파일 byte offset이나 전체 HTTP/CLI response 크기가 아니다. `usage.lines`는 CRLF, LF, CR을 각각 하나의 line break로 센 논리 line 수이며 빈 문자열은 0줄이다. 반환 content는 두 한도를 넘을 수 없다. 잘리지 않았으면 `exhaustedBy=none`, 잘렸으면 `bytes`, `lines`, `bytes_and_lines` 중 정확한 이유를 기록한다. 잘린 source를 완전한 declaration body로 표시하지 않는다.

## VCS와 Session baseline

두 baseline은 동등한 명시 모드이며 자동으로 서로 대체하지 않는다. 공통 필드는 `kind`, `provider`, `baseId`, `targetId`, `sessionId`, `capturedAt`, `inputFingerprint`다.

| 모드 | 계약 |
|---|---|
| `vcs` | `baseId`는 명시한 immutable commit/tree 또는 provider revision 집합이다. `targetId`는 명시 revision 또는 안정화한 working-tree snapshot이다. `sessionId`는 null이다. 세션 시작 전 변경도 포함한다. |
| `session` | provider는 `session`, `sessionId`는 필수다. `baseId`는 시작 당시 dirty bytes·inventory·config를 보존한 immutable content-addressed snapshot이고 `targetId`는 안정화한 현재 snapshot이다. 시작 당시 이미 있던 변경은 이 diff에서 제외한다. |

VCS base가 없으면 `BASE_REQUIRED`, 보존한 Session base가 없으면 `SESSION_BASE_MISSING`을 반환한다. 현재 파일로 누락된 base를 재구성하지 않는다. Session snapshot은 source bytes를 포함하므로 해당 session의 접근 범위와 수명에 묶고 metrics나 export에 복사하지 않는다.

## 저장 레코드 의미

- Manifest는 generation, workspace/analysis key, input fingerprint, 파일·프로젝트·shard, generation state와 coverage를 고정한다.
- Symbol은 L0/L1 모든 접근성 선언의 identity와 declaration 배열을 가진다. body와 remark 원문은 넣지 않는다.
- Declaration은 symbol ID, content fingerprint, source/generated 위치와 UTF-16 span을 가진다.
- Reference는 static과 lexical candidate를 구분하고 provenance, analysis key, coverage와 limitation을 보존한다.
- Diff는 baseline을 반드시 포함하고 added/deleted/renamed/rename-candidate/signature/body/remark/formatting 변화와 textual/fingerprint evidence를 가진다. rename candidate는 확정 rename으로 승격하지 않으며 이는 동작 의미 변화의 보장이 아니다.

JSON Schema는 draft 2020-12를 사용한다. `common.schema.json`의 공통 정의와 여섯 최상위 schema의 `$ref`는 같은 `schemas/` 디렉터리를 기준으로 해석한다.
