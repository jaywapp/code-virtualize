# Claude Code 연동

Code-Virtualize의 Claude 연동은 stdio MCP 서버와 얇은 lifecycle hook으로 구성된다. 고정 client 검증을 위해 handshake 기반 MCP protocol `2025-06-18`을 사용하며 `initialize`, `notifications/initialized`, `ping`, `tools/list`, `tools/call`, `notifications/cancelled`를 지원한다. stdout에는 newline-delimited UTF-8 JSON-RPC만 쓰고 진단은 stderr로 분리한다.

노출 도구는 다음 세 개다.

- `cv_find`: 현재 generation에서 symbol 후보를 찾고 status, coverage, truncation을 함께 반환한다.
- `cv_get`: symbol의 header/body/context를 반환한다. 호출마다 source bytes의 digest와 UTF-16 span을 검증하므로 hook가 꺼져도 stale source를 반환하지 않는다.
- `cv_impact`: 정적 참조와 별도로 표시한 lexical candidate를 depth/result/source-file 예산 안에서 반환한다.

엔진 실패, timeout, cancellation은 성공으로 표시하지 않는다. tool execution 실패는 `isError: true`이고 기존 repository search/read 도구를 사용하라는 fallback을 반환한다. source, token, secret은 진단 로그에 기록하지 않는다.

## 빌드와 실행

```powershell
dotnet build src\CodeVirtualize.Mcp\CodeVirtualize.Mcp.csproj -c Release
dotnet src\CodeVirtualize.Mcp\bin\Release\net9.0\CodeVirtualize.Mcp.dll --workspace D:\work\sample --timeout-ms 30000
```

MCP 서버는 client가 subprocess로 시작한다. 직접 실행하면 stdin 요청을 기다린다.

## 설치와 dry-run

실제 사용자 홈은 자동 수정하지 않는다. 지정 workspace의 `.mcp.json`과 `.claude/settings.json`만 변경한다.

```powershell
powershell.exe -NoProfile -File integrations\claude\manage.ps1 `
  -Action Install -Workspace D:\work\sample `
  -McpDll D:\code-virtualize\src\CodeVirtualize.Mcp\bin\Release\net9.0\CodeVirtualize.Mcp.dll `
  -CliDll D:\code-virtualize\src\CodeVirtualize.Cli\bin\Release\net9.0\CodeVirtualize.Cli.dll `
  -DryRun
```

`-DryRun`을 제거하면 기존 MCP server, permission, hook을 보존해 병합하고 `.code-virtualize/claude-integration-backup.json`에 원본 bytes를 저장한다. SessionStart는 immutable snapshot을 만들고, PostToolUse는 파일 변경 힌트를 `cv-update`로 전달하며, SessionEnd는 session pin을 닫는다. hook 실패는 Claude 작업을 차단하지 않고 기존 search/read fallback을 안내한다. 변경 힌트는 freshness의 유일한 근거가 아니다.

## 제거와 복원

같은 명령에 `-Action Uninstall -DryRun`을 사용해 복원 대상을 확인한다. 확인 후 `-DryRun`을 제거하면 설치 전 `.mcp.json`과 `.claude/settings.json` bytes를 복원한다. backup이 없으면 설정을 추측해 삭제하지 않고 실패한다. 설치 뒤 두 파일을 별도로 편집했다면 uninstall 전에 그 변경을 보존해야 한다.

## 제한

- initialize가 없는 MCP `2026-07-28` stateless lifecycle은 이번 고정-client 범위에 포함하지 않았다.
- timeout은 응답 대기와 cancellation을 제한하지만 동기 Core 작업의 즉각적인 OS-level 강제 중단까지 보장하지 않는다.
- Claude Code 설정 schema는 실제 배포할 고정 client 버전에서 다시 확인해야 한다.
