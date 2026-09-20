# TASK-012 PoC 정확성·보안 gate

검증일: 2026-09-20
범위: TASK-012 — 장애 주입·보안·정확성 검증
최종 판정: **PASS**

## 판정 기준

- 실제 source bytes와 독립 fixture에서 만든 expected를 JSON manifest·symbol·response와 대조한다.
- stale source 오반환 또는 조용한 coverage 누락은 Critical이다. 하나라도 미해결이면 gate를 통과시키지 않는다.
- 기존 성공 경로의 결과를 expected 생성기로 사용하지 않는다. expected path, source text, SHA-256 digest와 UTF-16 위치는 fixture source에서 직접 계산한다.
- CLI 검증은 별도 프로세스의 인자, exit code, stdout, stderr를 함께 확인한다.
- junction과 가능한 환경의 directory symlink를 검증한다. 환경 제약으로 만들 수 없는 link는 runner가 명시적으로 `SKIP`을 출력한다.

## 검증 매트릭스

| 영역 | 독립 판정 근거 | 결과 | 상태 |
|---|---|---|---|
| 손상 range | fixture source 길이와 JSON span 직접 비교 | source 미반환, `SOURCE_SPAN_INVALID` | PASS |
| 손상 entry/shard | digest·schema·JSONL 검증 및 기존 current 확인 | read/publish 거부, 이전 generation 유지 | PASS |
| update 누락 | hook hint에 없는 새 파일의 source·symbol 직접 확인 | inventory 발견, fallback/repair 명시 | PASS |
| rename | 실제 filesystem move와 JSON declaration path 비교 | rename 명시, 새 path만 반환 | PASS |
| publish/read crash | shard/manifest/generation/pointer fault point별 current 확인 | 이전 generation 유지 또는 명시 오류 | PASS |
| disk full 모사 | pointer write fault와 기존 current 확인 | 이전 generation 유지 | PASS |
| timeout/cancellation | stale source fixture와 bounded repair | stale source 미반환, `BUDGET_EXCEEDED` | PASS |
| traversal/rooted path | workspace 밖 fixture | source 미반환, `PATH_OUTSIDE_WORKSPACE` | PASS |
| Windows junction | workspace 내부 junction에서 외부 source 지시 | reparse point에서 읽기 전 거부 | PASS |
| directory symlink | 실제 symlink 생성 시도 | 현재 환경에서 `IOException`, runner가 명시적으로 skip | SKIP (환경 제약) |
| CLI 인자 | 별도 프로세스와 `ProcessStartInfo.ArgumentList`로 공백·메타문자 전달 | shell 실행 없음, malformed build/analyze exit 2, stdout 비어 있음 | PASS |
| CLI stdout/stderr | JSON parse, exit code와 stderr 직접 검사 | 계약과 일치 | PASS |
| secret log | stale validation의 stdout/stderr 검사 | token·source 본문 없음 | PASS |
| untrusted generator | `Directory.Build.targets` sentinel을 별도 CLI process로 구축 | syntax-only, sentinel 미실행 | PASS |
| 독립 fixture verifier | source text에서 직접 계산한 digest·UTF-16 위치와 직렬화 JSON 비교 | manifest/symbol JSON 일치 | PASS |
| runner 연결 | suite의 `Run()`과 test 호출을 정적 대조 | 59개 명시 호출 확인 | PASS |

## 발견 결함과 조치

| Severity | 결함 | 재현 | 최소 수정 | 최종 상태 |
|---|---|---|---|---|
| Critical | 증분 재사용 시 변경되지 않은 syntax-error 파일의 `coverage.failedFiles`가 1에서 0으로 감소 | 실패 파일을 유지한 채 다른 파일만 수정·update | 이전 `syntax-errors:<path>` limitation을 재사용할 때 실패 파일 집계도 보존 | RESOLVED |
| High | indexed path가 workspace 내부 junction을 통과하면 hash가 맞는 외부 source를 반환 | 외부 source를 향한 Windows junction과 독립 manifest fixture | root부터 대상까지 기존 경로 요소의 reparse point를 읽기 전에 거부 | RESOLVED |
| Medium | 값 없는 `--workspace`가 다음 옵션을 값으로 소비해 입력 오류가 아닌 경로/IO 오류가 됨 | 별도 CLI process에서 `--workspace --format json` 및 malformed `analyze` | 공통 option reader와 top-level dispatch의 입력 오류 경계 보강 | RESOLVED |
| Medium | `BoundedWriterWaitAcquiresAfterRelease` 테스트가 정의됐지만 `Run()`에서 호출되지 않음 | runner 호출 정적 감사 | 기존 test suite의 `Run()`에 호출 추가 | RESOLVED |

미해결 Critical/High 결함은 없다. stale source를 반환한 사례도 최종 suite에는 없다.

## 실행 결과

다음 명령을 저장소 root에서 실행했다.

```powershell
dotnet restore CodeVirtualize.sln --locked-mode
dotnet build CodeVirtualize.sln -c Release --no-restore
dotnet run --project tests\Core.Tests\Core.Tests.csproj -c Release --no-build --no-restore
dotnet run --project tests\CSharp.Tests\CSharp.Tests.csproj -c Release --no-build --no-restore
dotnet run --project tests\Cli.Tests\Cli.Tests.csproj -c Release --no-build --no-restore
dotnet run --project tests\Integration.Tests\Integration.Tests.csproj -c Release --no-build --no-restore
dotnet run --project tests\Integration.Tests\Integration.Tests.csproj -c Release --no-build --no-restore -- --fixture-verifier
git diff --check
```

- locked restore: PASS
- Release build: PASS, warning 0, error 0
- Core executable tests: PASS
- CSharp executable tests: PASS
- CLI executable tests: PASS
- Integration failure-injection/security tests: PASS
- 독립 fixture JSON/source verifier: PASS
- runner invocation audit: PASS, 59개 호출
- `git diff --check`: 최종 실행 결과를 아래 검증 기록과 함께 유지한다.

## 남은 위험과 환경 제약

- 이 Windows 환경에서는 directory symlink fixture 생성이 `IOException`으로 실패했다. Windows junction은 실제 생성해 같은 reparse 경계를 검증했고 통과했다. symlink 생성 권한이 있는 CI/개발자 모드 환경에서 해당 분기를 다시 실행해야 한다.
- timeout/cancellation은 bounded repair API에서 검증했다. 현재 TASK-012 범위의 CLI에는 timeout/cancellation 옵션이 없으므로 process-level timeout exit 5 경로는 후속 adapter/CLI 계약이 이를 노출할 때 추가 검증해야 한다.
- fault injector의 process crash와 disk full은 결정적 fault point 예외로 모사했다. 실제 OS 강제 종료와 실제 volume exhaustion은 이번 로컬 gate에서 수행하지 않았다.
- syntax-only generator sentinel은 검증했다. 명시적으로 신뢰한 semantic mode는 현재 in-process compilation만 사용하며 MSBuild/generator를 실행하지 않는 계약이다.