# C# diff task 카드

고정 fixture `tests/fixtures/csharp/diff/`에서 명시한 baseline mode로만 변경을 판정한다. 정답은 [diff expected](../../tests/fixtures/csharp/diff/expected.md)의 표를 따른다.

| ID | 작업 | 성공 조건 |
|---|---|---|
| DIFF-01 | VCS mode에서 base와 target을 비교한다. | V-01~03을 반환하고 세션 시작 전 dirty 변경 V-01을 포함한다. |
| DIFF-02 | Session mode에서 start와 current를 비교한다. | S-02~03만 변경으로 반환한다. S-01은 unchanged다. |
| DIFF-03 | 삭제된 `Removed()`의 base source를 resolve한다. | 선택한 baseline source의 10~13행을 반환하며 current source를 대신 반환하지 않는다. |

task 실행자는 baseline kind, base ID, target ID 또는 session ID를 결과에 기록해야 한다. base가 없으면 `BASE_REQUIRED`, session snapshot이 없으면 `SESSION_BASE_MISSING`으로 명시 실패해야 한다.
