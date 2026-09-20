# CRLF·emoji 원문 정답

`CrLfEmoji.cs`는 UTF-8 (BOM 없음), CRLF 줄바꿈이어야 한다. 이 조건은 `verify-fixtures.ps1`가 byte 단위로 검사한다.

| ID | 대상 | 위치 | 기대 원문 |
|---|---|---|---|
| E-01 | `CrLfEmoji` | `CrLfEmoji.cs:3` | `public static class CrLfEmoji` |
| E-02 | `Greeting` | `CrLfEmoji.cs:5`, UTF-16 column 25-43 | `public const string Greeting = "안녕 👋";` |
| E-03 | `👋` | `CrLfEmoji.cs:5`, UTF-16 column 40-41 | surrogate pair 두 code unit |
| E-04 | `GetGreeting()` | `CrLfEmoji.cs:7` | `public static string GetGreeting()` |
| E-05 | `Greeting` reference | `CrLfEmoji.cs:9` | `return Greeting;` |

열은 1-based UTF-16 code unit이다. 따라서 emoji 하나는 표시상 문자 하나지만 E-03처럼 두 열을 차지한다.
