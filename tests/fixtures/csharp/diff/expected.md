# VCS / Session diff 정답

이 fixture의 base와 target은 실제 Git 명령으로 만들지 않는다. 디렉터리별 source bytes가 계약상의 immutable snapshot을 나타낸다. 구현 테스트는 이를 임시 repository와 session storage에 복사할 수 있으나, 이 파일의 정답은 그 실행 결과로 갱신하지 않는다.

## 기준점

| Mode | Base source | Target source | 의미 |
|---|---|---|---|
| VCS | `vcs-base/ReviewTarget.cs` | `vcs-target/ReviewTarget.cs` | 세션 시작 전 dirty 변경 포함 |
| Session | `session-start/ReviewTarget.cs` | `session-current/ReviewTarget.cs` | 시작 당시 dirty bytes를 base로 보존; 시작 전 변경 제외 |

## 기대 변경

| ID | Mode | Symbol | Kind | Base 위치 | Target 위치 | 텍스트 근거 |
|---|---|---|---|---|---|---|
| V-01 | VCS | `ReviewTarget.Existing()` | body changed | `vcs-base/ReviewTarget.cs:5-8` | `vcs-target/ReviewTarget.cs:5-8` | `"base"` → `"dirty-before-session"` |
| V-02 | VCS | `ReviewTarget.AddedDuringSession()` | added | 없음 | `vcs-target/ReviewTarget.cs:10-13` | 새 method |
| V-03 | VCS | `ReviewTarget.Removed()` | removed | `vcs-base/ReviewTarget.cs:10-13` | 없음 | 삭제된 base method |
| S-01 | Session | `ReviewTarget.Existing()` | unchanged | `session-start/ReviewTarget.cs:5-8` | `session-current/ReviewTarget.cs:5-8` | 두 source 모두 `"dirty-before-session"` |
| S-02 | Session | `ReviewTarget.AddedDuringSession()` | added | 없음 | `session-current/ReviewTarget.cs:10-13` | 새 method |
| S-03 | Session | `ReviewTarget.Removed()` | removed | `session-start/ReviewTarget.cs:10-13` | 없음 | 삭제된 base method |

Session diff는 S-01을 변경 목록에 넣으면 안 된다. 삭제된 `Removed()`를 resolve할 때에는 `session-start/ReviewTarget.cs:10-13` 또는 선택한 VCS base bytes를 반환해야 하며, 현재 file로 대체하면 안 된다.
