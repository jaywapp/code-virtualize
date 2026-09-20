# C# navigation task 카드

고정 fixture `tests/fixtures/csharp/navigation/`에서 다음 작업을 수행한다. 정답은 [navigation expected](../../tests/fixtures/csharp/navigation/expected.md)의 대조표로만 판정한다.

| ID | 작업 | 성공 조건 |
|---|---|---|
| NAV-01 | `Load` 선언을 모두 찾아 overload와 generic/private를 구분한다. | N-03, N-04, N-05를 각각 반환하고 N-06의 explicit implementation을 별도 선언으로 보인다. |
| NAV-02 | `Catalog`를 찾아 partial 위치를 보인다. | `Contracts.cs:8`과 `Catalog.Partial.cs:3` 두 위치를 모두 반환한다. |
| NAV-03 | `LinkedHelper.BuildLabel`의 source를 찾는다. | physical path와 linked project path를 혼동하지 않고 N-08~09 위치를 제시한다. |
| NAV-04 | `Worker.Run`의 영향 후보를 설명한다. | static과 dynamic 후보를 분리하며 D-01~03의 provenance와 한계를 밝힌다. |

이 task는 모델·도구·반복 수를 정하지 않으며 실제 corpus 실행이나 token 측정을 수행하지 않는다.
