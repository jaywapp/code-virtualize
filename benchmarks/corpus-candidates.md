# 공개 corpus 후보

이 표는 실험 실행 승인이나 clone 지시가 아니다. 2026-09-23에 GitHub API로 remote ref·규모·라이선스를 조회해 고정한 후보·commit·license만 기록한다. 최종 선정은 TASK-024(ADR 005)에서 한다.

## 요약 표

| 후보 | 고정 commit (조회 2026-09-23) | License (SPDX) | .cs 파일 | .csproj | C# bytes | LOC(추정, bytes/35) | 학습 노출 | 등급 제안 | repo 크기 |
|---|---|---|---:|---:|---:|---:|---|---|---:|
| [dotnet/roslyn](https://github.com/dotnet/roslyn) | `0c4cb1b5d16a205b590ccad639054e38eb13702f` | MIT | 18,174 | 408 | 256,248,122 | ~7,321,000 | 매우 높음 | large | 2.63 GB |
| [dotnet/runtime](https://github.com/dotnet/runtime) | `e4a207e6edf48b5a8611a0531352be025f86ba38` | MIT | 27,649+ (truncated) | 3,260+ (truncated) | 356,528,322+ | ~10,186,000+ | 매우 높음 | large | 1.21 GB |
| [dotnet/aspnetcore](https://github.com/dotnet/aspnetcore) | `7cc41c501115f365c0b7f1b50ebaa134a328f0e5` | MIT | 10,705 | 628 | 70,807,910 | ~2,023,000 | 매우 높음 | large | 387 MB |
| [App-vNext/Polly](https://github.com/App-vNext/Polly) | `9a81fdc7c1a89d6c45bba2fb7b062b8eeae39eee` | BSD-3-Clause | 801 | 21 | 4,479,791 | ~128,000 | 보통 | medium | 55.5 MB |
| [serilog/serilog](https://github.com/serilog/serilog) | `bebc7719004f76187ae72e64ce138ec2540f2070` (dev) | Apache-2.0 | 216 | 6 | 921,041 | ~26,300 | 보통 | medium(하한 근접) | 74.6 MB |
| [quartznet/quartznet](https://github.com/quartznet/quartznet) | `1e039471fc457f6efa1d3bf1224324b82236b759` | Apache-2.0 | 1,466 | 32 | 14,488,437 | ~414,000 | 보통 | medium(상한 근접) | 45.2 MB |
| [HangfireIO/Hangfire](https://github.com/HangfireIO/Hangfire) | `1d4778c23410b365b5f9ce35458202efe0b0f3ea` | LGPL-3.0 (multi-license) | 498 | 10 | 3,151,851 | ~90,000 | 보통 | medium | 40.6 MB |
| [SixLabors/ImageSharp](https://github.com/SixLabors/ImageSharp) | `f80843e609e8f3df9a10cf14192756610ec8c086` | Six Labors Split License 1.0 (비표준, 라이선스 위험) | 2,107 | 5 | 13,892,095 | ~396,900 | 보통 | medium(상한 근접) | 134 MB |
| [litedb-org/LiteDB](https://github.com/litedb-org/LiteDB) | `0fd277aaed127b9dec99524277fb4173a2351167` (dev) | MIT | 1,087 | 28 | 5,186,699 | ~148,200 | 보통 | medium | 58.5 MB |
| [DapperLib/Dapper](https://github.com/DapperLib/Dapper) | `8becae8d0e2b360165ae03c0d5d1330b0273473d` | Apache-2.0 | 157 | 11 | 1,170,550 | ~33,400 | 높음 | small~medium 경계 | 48.8 MB |
| [LuckyPennySoftware/AutoMapper](https://github.com/LuckyPennySoftware/AutoMapper) | `6e8697bc44f02fb54ef6a556a7d134e63485299e` | RPL-1.5 (비permissive, 라이선스 위험) | 513 | 6 | 2,166,862 | ~61,900 | 높음 | medium | 124.5 MB |
| [DuendeArchive/IdentityServer4](https://github.com/DuendeArchive/IdentityServer4) (`archive` 브랜치) | `5e40bd18fbf030d0158a15532eb8381c454b53da` | Apache-2.0 | 1,115 | 78 | 3,809,880 | ~108,900 | 높음 | medium | 20.7 MB |

후보는 대표성 주장이 아니다. UE5/Perforce·개인 프로젝트·비공개 로그를 대체하거나 그 결과를 일반화하지 않는다. clone 뒤에는 commit과 해당 revision의 `LICENSE`를 다시 검증하고, corpus·task·tuning/held-out 분리와 공개 가능한 집계 범위를 TASK-024/027에 기록한다.

## 측정 근거와 한계

- 파일 수·C# bytes·commit SHA·license는 GitHub API로 직접 조회했다: `repos/<owner>/<repo>`, `/languages`, `/license`, `/contents/<LICENSE>`, `/commits/<branch>`, `git/trees/<sha>?recursive=1`. clone은 하지 않았다.
- LOC는 C# bytes를 35 bytes/line으로 나눈 **추정**이다. 심볼(타입·메서드 선언)과 참조 규모는 clone 없이 측정할 수 없어 모든 후보에서 `미확인`이다.
- dotnet/runtime은 tree API가 `truncated`를 반환해 `.cs`·`.csproj` 수가 하한값이다.
- source generator 의존도는 `.tt`·`Generated` 경로 이름으로만 판단했다. 빌드 시점에만 생성되는 incremental generator 사용 여부는 확인하지 않았다.
- `NOASSERTION`으로 감지된 Hangfire·AutoMapper·ImageSharp·Dapper는 LICENSE 원문을 직접 확인했다. Hangfire는 LGPL-3.0, AutoMapper는 RPL-1.5다.

## 후보별 근거

### large (기존 후보 재평가)

- **dotnet/roslyn**: `Syntax.xml` 기반 generator 산출물 292개가 git에 커밋돼 있다. syntax-only 파싱은 가능하지만 대량 boilerplate가 심볼 밀도를 왜곡할 수 있다. DIFF 예시 `0c1dd5b7d7`(Enum.HasFlag boxing 회피, 여러 파일의 1~2줄 본문 수정). 2.63 GB라 sparse·shallow clone이 필요하다.
- **dotnet/runtime**: `Generated` 144, `.tt` 12로 generator 의존이 셋 중 가장 높다. DIFF 예시 `e4a207e6ed`(NativeAOT `Array.GetEnumerator` inline, 1줄 추가).
- **dotnet/aspnetcore**: `Generated` 18, `.tt` 3으로 셋 중 generator 의존이 가장 낮고 로컬 부담(387 MB)도 가장 작다. DIFF 예시 `8e5048ee97`(QuickGrid virtualize 스크롤 버그 수정, C# 파일 6개).

### medium (신규)

- **App-vNext/Polly**: generator·T4 없음. 최근 이력에 의존성 bump가 많아 DIFF 커밋을 골라야 한다. DIFF 예시 `482bdf824f`(`FaultGenerator` 1줄 본문 수정 + 테스트).
- **serilog/serilog**: generator·T4 없음. 규모가 medium 하한에 가깝다. DIFF 예시 `bebc7719`(`MessageTemplateParser` alignment overflow 수정).
- **quartznet/quartznet**: `Generated` 2, `.tt` 0. DB provider·대시보드·CLI를 포함한 모노레포다. 최근 커밋 메시지가 서술형이라 task 카드 작성 시 커밋 내용을 직접 확인해야 한다. DIFF 예시 `2718eee576`(`AdoExecutionHistoryStore` 본문 수정).
- **HangfireIO/Hangfire**: `.tt` 1, `Generated` 0. LGPL-3.0은 permissive가 아니지만 표준 OSS 라이선스다. DIFF 예시 `0b6ef5043c`(`SqlServerStorageOptions`에 옵션 멤버 추가 — 멤버 추가형 DIFF).
- **SixLabors/ImageSharp**: `.tt` 22, `Generated` 51로 신규 후보 중 generator 의존이 가장 크다. Six Labors Split License는 OSI 미승인이고 상업 사용 임계값이 있다. DIFF 예시 `dccac8959b`(grayscale ICC 변환 버그 수정).
- **litedb-org/LiteDB**: generator·T4 없음. 최근 이력이 트랜잭션·락 서비스 수정 중심이라 메서드 단위 다중 파일 diff가 많다. DIFF 예시 `e4ce3b8992`(reader ownership 버그 수정, 서비스 파일 6개).
- **DapperLib/Dapper**: generator·T4 없음. 157 파일로 small에 더 가까울 수 있다. DIFF 예시 `41d76c783c`(`DynamicParameters.AddParameters` 신규 메서드).
- **LuckyPennySoftware/AutoMapper**: generator·T4 없음. RPL-1.5는 수정본 소스 공개 의무가 있는 reciprocal 라이선스라 벤치마크 공개 배포 원칙과 맞지 않을 수 있다. DIFF 예시 `d57b329d09`(`TypeMapPlanBuilder` 조건 전달 로직 수정).
- **DuendeArchive/IdentityServer4** (`archive` 브랜치): 기본 브랜치는 안내 placeholder라 `archive` 브랜치를 써야 한다. `src`의 마지막 실질 커밋이 2021-04-07이라 최근 이력이 없다. star 수는 archive 이관으로 초기화돼 실제 노출을 반영하지 않는다.

## 조사 측 참고 의견 (선정 아님)

- medium 상위: Hangfire, LiteDB, Quartz.NET. 백업: Serilog(작은 medium), Polly(DIFF 커밋 선별 필요).
- large 상위: aspnetcore(generator 의존·로컬 부담 최소), runtime, roslyn. 세 후보 모두 학습 노출이 매우 높다.
- 등급 경계 제안: medium은 `.cs` 약 150~2,200개·C# 0.9~14.5 MB·`.csproj` 5~80개, large는 `.cs` 10,000개 이상·C# 70 MB 이상·`.csproj` 400개 이상. 두 구간 사이에 파일 수·bytes 모두 5배 이상의 간극이 있다. 최종 경계는 TASK-024가 심볼·참조 지표와 함께 정한다.
