---
title: Code-Virtualize 아이디어 검토 — Claude 의견
category: harness
tags:
  - ai
  - claude-code
  - context-engineering
  - token-optimization
  - code-retrieval
  - review
source: https://github.com/jaywapp/wiki/blob/develop/ai/harness/code-virtualize.md
updated: 2026-09-17
---

# Code-Virtualize 아이디어 검토 — Claude 의견

> 문제의식은 맞지만, 지금 설계대로면 이미 있는 도구를 더 비싸게 다시 만들 위험이 크다. 토큰 절감보다 **grep이 통하지 않는 곳의 탐색·영향 범위 정확도**에 초점을 옮기고, 코드를 쓰기 전에 가설부터 측정하길 권한다.

- **검토 대상:** [Code-Virtualize](./code-virtualize.md) (`develop` 커밋 `30a222f` 기준)
- **검토자:** Claude (Opus 5), 2026-09-17
- **관점:** 비판적 검토. 직접 확인한 사실과 측정이 필요한 추정을 [마지막 절](#확인한-사실과-측정이-필요한-추정)에서 구분한다.

## 결론

큰 결함은 세 가지다.

1. 기존 도구(LSP, Serena 등)를 검토한 내용이 없다.
2. 인덱스가 **일부만 찾은 결과**를 정상 결과처럼 돌려줘도 알아챌 방법이 없다.
3. 가장 중요한 가설인 "토큰이 줄어든다"를 코드를 쓰기 전에 측정하지 않았다.

## 잘한 점

- 소스코드가 기준이고 `.cv`는 버려도 되는 인덱스라는 원칙, 실패 시 원본 검색으로 돌아가는 경로를 처음부터 넣었다.
- 벤치마크가 토큰만 보지 않고 성공률, 빌드 비용, cold/warm/long 세션, 고의 손상 테스트까지 다룬다.
- Claude 플러그인이 아니라 독립 엔진으로 두고 플러그인은 연동 계층으로 분리했다.

## 핵심 문제 (심각한 순)

### 1. 기존 도구 검토가 없다

`cv_find / cv_get / cv_impact`는 이미 있는 기능과 거의 1:1로 겹친다.

| Code-Virtualize | Claude Code 내장 `LSP` 도구 | Serena MCP |
|---|---|---|
| `cv_find` | `workspaceSymbol`, `documentSymbol` | `find_symbol`, 파일 심볼 개요 |
| `cv_get` / `cv-resolve` | `goToDefinition` + `Read` 범위 읽기 | `find_declaration` |
| `cv_impact` | `findReferences`, `goToImplementation`, `incomingCalls`/`outgoingCalls` | `find_referencing_symbols`, `find_implementations` |

- Claude Code의 `LSP` 도구는 C#·C++ 언어 서버를 붙이면 cv_find와 cv_impact 대부분을 대신한다.
- Serena는 LSP 기반이며 C#과 C/C++를 지원한다고 밝힌다.
- 그 밖에 Aider repo-map(tree-sitter 기반 심볼 맵), SCIP/Kythe, ctags가 같은 영역에 있다.

Claude Code를 만든 Boris Cherny는 초기 버전이 RAG와 로컬 벡터 DB를 썼지만 agentic search(grep/glob/read)가 대체로 더 잘 동작했고, 보안·프라이버시·**최신성(staleness)**·신뢰성 문제도 없었다고 밝혔다. 벡터 인덱스 이야기라 CV와 같지는 않지만, 인덱스를 최신으로 유지하는 비용과 신뢰성 문제는 CV에도 그대로 해당한다.

**권고:** 설계 문서에 "LSP나 Serena로는 왜 안 되는가" 절을 먼저 넣는다. 이 절이 비어 있으면 논의가 이미 있는 기능을 다시 설계하는 쪽으로 흐른다.

### 2. 일부만 찾은 결과는 자동 복구가 잡지 못한다

6절의 복구 경로는 인덱스에 없거나(MISS) 깨진(INVALID) 경우에만 원본 검색으로 넘어간다. 참조 10개 중 7개만 돌려주면 `HIT + VALID → fast path`로 그대로 통과한다.

- **grep**은 필요 이상으로 많이 찾는다. 오탐은 있어도 빠뜨리는 경우는 드물다.
- **정적 인덱스**는 조용히 빠뜨린다. 리팩토링과 영향 분석에서는 이쪽이 더 위험하다.

그리고 빠뜨림이 체계적으로 생기는 곳이 바로 목표 스택이다.

- **UE5:** `UCLASS`/`UPROPERTY`/`GENERATED_BODY` 매크로, UHT가 생성하는 `.generated.h`, Blueprint 참조, `FName` 문자열로 찾는 코드, `.ini` 설정
- **C#:** DI 등록, 리플렉션, XAML 바인딩, source generator, `partial` 타입

결국 `cv-impact`와 리뷰 범위 산출이 이런 누락을 "더 적은 컨텍스트로 리뷰했다"는 성과처럼 보이게 만든다. 벤치마크의 `Reference Recall`은 정답 참조 목록이 있어야 잴 수 있는데, 그 목록을 어떻게 만들지도 정해져 있지 않다.

**권고:** 참조 결과에 "정적 분석으로 찾을 수 없는 참조 유형이 있을 수 있음"을 명시하는 신뢰도 표시를 두고, 영향 분석에서는 인덱스 결과와 이름 기반 grep 결과의 차이를 함께 보여준다.

### 3. 세션마다 전체 빌드 후 폐기하면 대형 레포에서 불리하다

- 문서는 대형 레포에서 가치가 가장 크다고 하면서, 가장 비싼 작업을 세션마다 반복한다. Roslyn으로 솔루션을 불러오거나 clangd로 UE5를 인덱싱하는 작업은 규모에 따라 분 단위를 넘길 수 있다(측정 필요).
- clangd, Roslyn, Sourcegraph 같은 인덱서는 파일 해시로 캐시를 저장해 두고 바뀐 부분만 갱신해서 최신 상태를 유지한다. 매번 버리지 않는다.
- 14절의 "몇 번째 task부터 이득인가(break-even)" 지표는 이 설계 때문에 생긴 문제다.
- `current/` 폴더가 하나라서, 에이전트 팀처럼 여러 세션이 한 작업 공간을 쓰면 서로 덮어쓴다. manifest에 session ID가 있어도 폴더 구조가 받쳐주지 못한다.

**권고:** 세션 단위 폐기 대신 파일 해시로 키를 잡은 영속 캐시를 두고, 세션별 상태(작업 중 변경, diff 기준점)만 세션 폴더로 분리한다.

### 4. 최신 상태 유지에 빈틈이 있다

"편집 → DIRTY → 부분 재인덱싱" 흐름은 Claude의 Edit/Write 훅으로 편집을 감지한다는 전제다. 실제로는 다음 경로로도 파일이 바뀐다.

- Bash(sed, 포매터, 코드 생성)
- `p4 sync`, UHT와 빌드 산출물
- 사람이 IDE에서 동시에 한 수정

**권고:** 조회할 때마다 결과에 포함된 파일의 수정 시각·크기(필요하면 해시)를 확인한다. 이렇게 하면 DIRTY 상태 관리가 대부분 필요 없고 누락도 막을 수 있다.

### 5. "토큰이 줄어든다"는 가설을 아직 재지 않았다

- **캐싱 효과:** Anthropic API에서 캐시된 입력은 기본 입력 가격의 0.1배로 처리된다. "총 input token −42%"는 실제 비용과 많이 다를 수 있으니, 캐시를 반영한 비용과 최대 컨텍스트 사용량으로 재야 한다.
- **범위 읽기는 이미 된다:** `Grep -n`과 `Read offset/limit`으로 필요한 줄만 읽을 수 있다. 낭비가 도구가 없어서인지, 모델이 파일 전체를 읽는 습관 때문인지부터 가려야 한다.
- **조각만으로는 고치기 어렵다:** 메서드 본문만 받으면 필드, using, 주변 관례를 모른다. 결국 파일 전체를 다시 읽으면 절감은 사라지고 도구 호출만 늘어난다.
- **고정 비용:** 도구 정의, 스킬, 지시문이 매 턴 컨텍스트에 붙는다.

이건 코드 없이 지금 바로 잴 수 있다. 기존 Claude Code 세션 기록(JSONL)에서 다음 세 가지를 뽑는다.

- Read/Grep 결과가 컨텍스트에서 차지하는 비율
- 파일 전체를 읽은 비율
- 같은 파일을 다시 읽은 비율

비율이 낮으면 프로젝트의 전제가 무너진다.

### 6. 가설 검증 전에 설계가 너무 커졌다

- **L2/L3(지역 변수, 람다 캡처) 인덱싱:** 모델은 어차피 메서드 본문을 읽으므로 얻는 게 거의 없다.
- **공개 범위에 따라 나중에 인덱싱:** 비용은 파일을 파싱할 때 들고, 파싱한 뒤 private 멤버까지 뽑는 추가 비용은 거의 없다. 반대로 private을 빼면 버그가 자주 숨는 곳에서 인덱스가 비게 된다.
- **Materialization Priority의 "Current Task Relevance":** 결국 LLM이나 임베딩이 판단해야 해서 문서가 목표로 내건 결정적(deterministic) 탐색과 맞지 않는다. 가중치 없이 더하기만 한 식이라 아직 공식이라고 보기도 어렵다.
- **배포 계획:** npm 런처 + .NET 워커 + clang으로 런타임이 3종이다. 전역/작업 공간 설치, Codex 연동, 언어 공통 스키마까지 Phase 1 PoC보다 먼저 설계돼 있다.

### 7. Virtual-Diff의 기준점이 잘못됐다

비교 기준(CV₀)을 세션 시작 시점으로 잡으면, 이미 수정 중인 상태에서 세션을 시작했거나 CL 작업이 여러 세션에 걸칠 때 diff가 틀린다.

**권고:** 기준점은 CL의 기준 revision으로 잡는다. 이전 버전 파일은 `p4 print`나 `git show`로 필요할 때 파싱하면 되므로 스냅샷을 따로 보관할 필요가 없다.

### 8. 벤치마크 설계의 빈틈

- **순서:** "완성 후 평가"라서 진행 여부 판단(Go/No-Go)이 투자한 뒤에 온다.
- **비교군 부족:** A/B만으로는 CV 효과와 "적게 읽어라"는 지시문 효과를 구분할 수 없다. 최소한 두 조건을 추가한다.
  - 기존 방식 + 필요한 범위만 읽으라는 지시문
  - LSP 또는 Serena
- **통계:** 반복 횟수, 편차, 의미 있는 차이의 기준이 없다. 에이전트 실행 편차를 생각하면 예시의 94% → 95%는 우연히 생길 수 있는 차이다.
- **도구 사용률:** CV 조건에서 모델이 CV 도구를 실제로 얼마나 썼는지 재지 않으면 결과를 해석할 수 없다.
- **데이터 오염:** 공개 레포는 모델이 이미 학습했을 수 있고, 실제 대상인 UE5 프로젝트 코드를 대표하지도 못한다.

### 9. 기타

- 원문이 15절 마지막 `benchmark task/g`에서 잘려 있다(`30a222f` 기준).
- [Virtual Remark](https://github.com/jaywapp/wiki/blob/develop/ai/harness/lazy-comment-context-retrieval.md)를 `remark.cv`로 흡수했는데, 일반적인 코드에서 주석이 차지하는 토큰 비중은 측정 전이다. 5번 측정에 주석 비율도 같이 넣는 편이 좋다.

## 살릴 만한 부분

컨텍스트가 커지고 캐시 가격이 내려갈수록 토큰 절감의 가치는 줄어든다. 정확도의 가치는 줄지 않는다. 이 프로젝트만의 강점도 그쪽에 있다.

1. **grep으로는 결과가 너무 많은 곳:** UE5에서 `Tick`, `BeginPlay`, `Init`을 grep하면 결과가 수천 건 단위로 나온다. 다만 인덱스를 만들기 가장 어려운 곳도 여기다.
2. **심볼 단위 diff로 리뷰 범위 산출:** LSP나 Serena에 없는, 가장 독자적인 부분이다.
3. **Perforce CL 기준 연동, UE5 매크로·리플렉션 보완:** 기존 도구가 약한 영역이다.

프로젝트 소개도 "토큰 절감 엔진"보다 **"대형 C++/UE5에서 grep이 통하지 않는 곳의 탐색·영향 범위 정확도 도구"**가 맞다고 본다.

## 제안하는 진행 순서

| 단계 | 내용 | 중단·진행 기준 |
|---|---|---|
| Phase 0 (코드 없음) | 기존 세션 기록으로 5번의 비율(탐색 결과 비중, 전체 파일 읽기, 재읽기, 주석 비중)을 측정한다. | 탐색 결과 비중이 낮으면 중단한다. |
| Phase 1 (기존 도구 비교) | 대표 C# 작업 몇 개를 네 조건으로 비교한다: 기존 방식, 기존 방식 + 지시문, LSP, Serena. | 기존 도구로 이득 대부분이 나오면 엔진은 만들지 않고 얇은 연동만 둔다. |
| Phase 2 (차별점만 구현) | 심볼 diff 리뷰, UE5 보완, CL 기준 연동만 만든다. 캐시는 영속 캐시 + 조회 시점 검증으로 설계한다. | 누락률(참조 recall)과 리뷰 결함 발견률로 판단한다. |

## 확인한 사실과 측정이 필요한 추정

**직접 확인한 것**

- Claude Code 내장 `LSP` 도구의 operation 목록(`goToDefinition`, `findReferences`, `hover`, `documentSymbol`, `workspaceSymbol`, `goToImplementation`, `prepareCallHierarchy`, `incomingCalls`, `outgoingCalls`) — 2026-09-17 Claude Code 세션의 도구 정의에서 확인
- Serena의 심볼 도구 이름, LSP 기반 여부, C#·C/C++ 지원 — [oraios/serena](https://github.com/oraios/serena) README 기준
- Claude Code 초기 RAG 사용과 agentic search 전환 발언 — [Boris Cherny, X](https://x.com/bcherny/status/2017824286489383315)
- 원문 말미 잘림 — `develop` `30a222f`

**측정이 필요한 추정**

- Roslyn 솔루션 로드와 clangd UE5 인덱싱 소요 시간
- 실제 세션에서 Read/Grep 결과·주석이 차지하는 토큰 비중
- UE5 코드베이스에서 정적 인덱스의 참조 누락률
- `Tick`, `BeginPlay` 등 흔한 이름의 grep 결과 규모

## 기존 문서와의 관계

- [Code-Virtualize](./code-virtualize.md) — 검토 대상 원문. 위키 원본은 [wiki/ai/harness/code-virtualize.md](https://github.com/jaywapp/wiki/blob/develop/ai/harness/code-virtualize.md)다
- [Virtual Remark](https://github.com/jaywapp/wiki/blob/develop/ai/harness/lazy-comment-context-retrieval.md) — `virtual-remark` 계층의 출발점
- [Perforce·UE5 하네스 설계에 필요한 정보](https://github.com/jaywapp/wiki/blob/develop/ai/harness/perforce-ue5-harness-design-inputs.md) — 9번 입력(AI 실행기와 컨텍스트, 인덱스 기준 CL과 갱신 방식)이 이 검토의 3·4·7번과 연결된다
