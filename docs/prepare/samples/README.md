# UI 비교 시안

UC-011의 CLI + 로컬 Web UI 결정에 따라 만든 **선택 전 프로토타입 3종**이다. 최종 화면과 frontend 스택은 미정이다. 실제 제품 코드·엔진·서버·Git 연동은 포함하지 않는다. 모든 source, hash, revision, snapshot, 관계, diff는 합성 예제다.

## 실행

`index.html`을 브라우저에서 열면 갤러리가 표시된다. 빌드와 패키지 설치는 필요 없다. 각 시안은 classic script와 로컬 CSS를 사용하므로 `file://`에서도 실행할 수 있다.

HTTP로 확인하려면 Python이 설치된 환경에서 다음을 실행한다. 서버는 loopback에만 바인딩한다.

```powershell
Set-Location D:\station\repos\code-virtualize\docs\prepare\samples
python -m http.server 8766 --bind 127.0.0.1
```

브라우저에서 `http://127.0.0.1:8766/`를 연다. 중지는 실행한 터미널에서 `Ctrl+C`다. 이 서버는 시안 파일만 제공한다.

## 비교 기준

| 시안 | 정보 구조와 주요 동작 | 검토할 점 |
|---|---|---|
| [sample1](sample1/index.html) | 심볼 목록 / 원문 / 참조 근거의 3열 구조 | 선언을 반복 탐색할 때 시선 이동과 밀도가 적절한가 |
| [sample2](sample2/index.html) | 원문 → 주석 → 변경의 읽기 단계와 접이식 참조 근거 | 낮은 노출 정보량이 이해를 돕는가, 관계를 찾기 쉬운가 |
| [sample3](sample3/index.html) | 선택 가능한 기하 관계 그래프와 diff 검사 패널 | 관계/변경 주변을 탐색할 때 근거와 불확실성을 구분할 수 있는가 |

## 직접 확인할 흐름

1. `Checkout`에서 source, XML 주석, diff를 전환한다. `Write`는 private 선언이며 주석이 없는 예제다.
2. 이름/파일 경로로 검색하고, 없는 검색어에서 검색 초기화를 실행한다. 검색은 심볼 목록을 좁히며 현재 선택한 원문은 유지한다.
3. VCS `4f2c7a1`과 세션 시작 snapshot을 전환한다. Checkout의 VCS diff는 가격 정책과 감사 호출, 세션 diff는 감사 호출만 보여준다. Calculate는 세션 baseline 이후 변경이 없다.
4. 관계 범위를 선택 심볼 1-hop / 전체 workspace로 전환한다. graph 노드와 참조 목록에서 심볼을 선택한다.
5. 상태 선택에서 stale, partial, empty, error를 재현하고 해당 복구 동작을 실행한다. 550ms 복구 중은 합성 loading 상태이며 실제 분석은 없다.
6. 라이트/다크 테마를 전환한다. 모바일에서는 ‘심볼 목록’으로 sidebar를 펼친다. Tab/Enter/Space로 native controls와 노드를 조작한다.

## 데이터와 상태 의미

- 기본 상태도 **원문 최신 / 참조 coverage 부분**이다. 최신 hash와 완전한 참조 분석을 혼동하지 않는다.
- 정적 관계는 synthetic semantic fixture에서 대상 ID를 해석했다고 가정한 예제다. 실제 trust 부여나 프로젝트 실행은 하지 않는다. 실제 제품의 기본 syntax-only 정책을 변경하지 않는다.
- `Checkout → Write`는 이름 일치만 있는 텍스트 후보다. private 접근성과 receiver를 확인하지 않았으므로 실제 호출로 확정하지 않는다. dashed edge와 텍스트 배지로 구분한다.
- stale은 최신 검증 전 source/remark/diff/graph 반환을 막는다. 부분 분석은 원문을 유지하고 실패한 PricePolicy 관련 참조를 숨긴다. empty는 인덱스 없음, error는 원문 읽기 실패 상황이다.
- `fixture-current`는 암호학적 hash가 아닌 시안 라벨이다. 숫자와 관계 수는 이 합성 fixture 범위에만 해당한다.
- source는 합성 코드의 line slice를 가정한다. span remapping, 실제 hashing, diff 엔진, graph layout 엔진, 분석 모드 전환, trust UI는 후속 구현 범위다.
- 복구/검색/테마는 현재 페이지 메모리에서만 동작하며 파일을 수정하거나 외부로 전송하지 않는다.

구현용 스택을 이 static HTML/CSS/JS 형태로 확정한 것은 아니다. 최종안과 기술을 결정한 뒤 `plan.md`의 UI 구현 작업에서 별도로 정한다.

## 검증 기록

[검증 결과](verification.md)와 [실제 화면](screenshots/)에서 조작 확인 및 독립 리뷰 결과를 확인할 수 있다.
