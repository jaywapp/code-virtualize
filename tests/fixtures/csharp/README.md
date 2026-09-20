# C# 독립 정답 fixture

이 디렉터리는 Code-Virtualize 또는 Roslyn 질의 결과로 정답을 생성하지 않는다. 각 `expected.md`의 선언, 참조, 후보, diff 판정은 source를 사람이 직접 읽어 기록한 것이다. 이후 구현 테스트는 도구 출력과 이 정답을 대조해야 하며, 출력으로 이 파일을 갱신하면 안 된다.

위치 표기 규칙은 다음과 같다.

- 경로는 fixture 루트 기준 상대 경로다.
- 줄과 열은 모두 1-based다. 열은 UTF-16 code unit 기준이며, 범위의 끝은 inclusive다.
- `static`은 컴파일러가 직접 바인딩할 수 있어야 하는 참조다. `dynamic`은 문자열, DI 등록, reflection 등으로 별도 후보로만 제시한다.
- `Session` base는 `session-start`의 immutable bytes다. VCS base는 `vcs-base`의 immutable tree다.

`verify-fixtures.ps1`는 source의 사람이 지정한 줄과 encoding/newline 불변식을 점검한다. 이는 parser나 symbol query를 실행하지 않는 fixture 무결성 검사다.
