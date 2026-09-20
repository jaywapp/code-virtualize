# 공개 corpus 후보

이 표는 실험 실행 승인이나 clone 지시가 아니다. 2026-09-20에 remote ref를 조회해 고정한 후보·commit·license만 기록한다. 실제 선택은 FUP-001에서 repo, 공개 범위, 모델, 예산과 함께 승인한다.

| 후보 | 고정 commit | License | 사용 가설 | 현재 상태 |
|---|---|---|---|---|
| [dotnet/roslyn](https://github.com/dotnet/roslyn) | `5a9f1b4bb88ec57c776fd9be0c8693eafb375b10` | MIT | 대형 다중 프로젝트 C# compiler/workspace 탐색 | 후보; 실행·clone 미수행 |
| [dotnet/runtime](https://github.com/dotnet/runtime) | `3312bedc3acd5a10909362870db36b7de8c57998` | MIT | 대형 runtime/library의 선언·참조·diff task | 후보; 실행·clone 미수행 |
| [dotnet/aspnetcore](https://github.com/dotnet/aspnetcore) | `1fcd7ef305697a1888f3ede076010350ae9f4f8d` | MIT | 중대형 web/framework C# navigation task | 후보; 실행·clone 미수행 |

후보는 대표성 주장이 아니다. UE5/Perforce·개인 프로젝트·비공개 로그를 대체하거나 그 결과를 일반화하지 않는다. clone 뒤에는 commit과 해당 revision의 `LICENSE`를 다시 검증하고, corpus·task·tuning/held-out 분리와 공개 가능한 집계 범위를 FUP-001에 기록한다.
