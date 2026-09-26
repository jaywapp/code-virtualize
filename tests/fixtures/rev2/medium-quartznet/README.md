# medium-quartznet rev2 정답 — 사람이 읽는 요약

`expected.json`을 읽기 전에 [`tests/fixtures/rev2/CONVENTIONS.md`](../CONVENTIONS.md)를 먼저 읽는다. 이 문서는 그 규약을 반복하지 않고 이 corpus에서만 나온 내용만 적는다. 원문은 인용하지 않는다.

## 대상

- corpus: `quartznet/quartznet` (`main`) @ `1e039471fc457f6efa1d3bf1224324b82236b759`
- NAV 6개(`NAV-01`이 tuning), DIFF 6쌍(`DIFF-01`이 tuning)
- 정답은 `D:\worktrees\corpus\medium-quartznet`의 pinned checkout을 사람이 직접 읽어 작성했다. CV, runner, `rg`/`git diff`의 출력을 그대로 정답에 복사하지 않았다 — 후보 수집에만 썼다.

## NAV 표본 진행

`benchmarks/corpora/rev2-manifest.json`의 12개 후보를 순서대로 평가했다. 6개를 채택하려고 12개 모두 평가해야 했다(정적 참조 0건으로 6개를 건너뜀). 자세한 채택/건너뜀 사유는 `expected.json`의 `nav.candidateSelection`에 있다. 요약:

- 채택(6): 순서 0(tuning, self-`nameof` 참조 1건), 3, 5, 6, 8, 11
- 건너뜀(6, 정적 참조 0건): 순서 1, 2, 4, 7, 9, 10 — 대부분 interface·base 타입 receiver를 통한 호출만 있어 그 선언이 아니라 interface/base 선언에 귀속되는 경우였다.

가장 반복적으로 쓴 판정 규칙: **receiver의 정적 타입이 대상 선언 타입(또는 대입 호환 타입)일 때만 정적 참조로 센다.** interface나 base class 타입으로 선언된 receiver를 통한 호출은 그 interface/base 선언의 참조이지, 구현(override)의 참조가 아니다. small fixture의 N-06/R-04 선례와 같다.

## DIFF 쌍 진행

6쌍 모두 첫-parent 이력에서 뽑힌 실제 커밋이다. 특이사항 (2026-09-25 `CONVENTIONS.md` 개정 반영):

- **DIFF-05(pair 4)**: `build/*.cs`(NUKE 빌드 스크립트)를 다루는 유일한 쌍이다. 이 쌍은 삭제된 member도, D3 자격이 있는 body/signature 변경도 없다 — 유일한 "변경"(D05-D1-01, `Build` partial class)은 `[GitHubActions(...)]` attribute 인자 변경(`signatureChanged`)과 선언 범위 밖 `//` 주석 변경뿐이고, class 선언 줄 자체(`public partial class Build;`)는 byte 단위로 동일하다. `CONVENTIONS.md`가 attribute·remark·범위 밖 주석만 바뀐 member는 D3 대상이 아니라고 명시하고(2026-09-25 개정), 이런 쌍은 D3를 아예 판정하지 않기로 했다(2026-09-25 사용자 결정, ADR 005 보완) — `expected.json`의 `diff.pairs[].d3_resolve`가 `null`이고 `d3NotApplicable: true`다.
- **6쌍 모두 삭제된 member가 0건이다.** D3는 "선언 범위 안 텍스트가 p·c'에서 실제로 다른 첫 번째 member" 규칙으로 결정된다(DIFF-05만 예외 — 그런 member가 아예 없음). **DIFF-06의 D3 대상은 원래 `OneOffThroughputPostgresBenchmark.Profile`이었으나, 그 변경이 attribute·remark뿐이라 2026-09-25 개정으로 `OneOffFiringStatementCountSqliteTest.CreateEmptyDatabase()`(진짜 body 변경)로 바뀌었다.**
- attribute 변경(`signatureChanged`)과 XML doc comment 변경(`remarkChanged`)을 분리했다 — 둘 다 선언 범위 밖에서 일어나는 변경이라 D3 자격이 없지만, D1·D2에는 그대로 남는다.
- DIFF-03(pair 2)이 가장 크다(D1 25항목) — `InternalsVisibleTo`를 통한 생성 클래스 이름 충돌 방지 기능 추가.

## 교차 확인

`expected.json`의 `crossCheckPlan`에 표본 크기(32/126)를 적어 두었다. 실제 표본 뽑기와 확인은 다른 세션의 몫이며 모든 `crossCheck` 필드는 `null`이다.

## corpus 안의 AI 에이전트 지시 파일

루트에 `AGENTS.md`, `CLAUDE.md`, `.aider.conf.yml`, `.fallout/`, `.gemini/`, `.squad/`가 있다. 모두 이 저장소(Quartz.NET) 자체의 개발을 돕기 위한 평범한 코딩 컨벤션·내부 roleplay형 멀티에이전트 워크플로 도구이고, 벤치마크 채점자를 겨냥한 지시는 없었다. 자세한 내용은 `expected.json`의 `corpusAnomalies`에 있다.
