# Verification

The current verified state of the repository. Earlier acceptance runs, with the defects each
one exposed, are kept in [history/](history/README.md).

## Current state

| | |
|---|---|
| Verified commit | `fa79883` (`main`) |
| Tests | **189 passed, 0 failed, 0 skipped** |
| Format | `dotnet format --verify-no-changes`: no changes |
| Build | Release, 0 warnings, 0 errors |
| CI | green on `c436c05`, the same application and test code: [run 37207068481](https://github.com/hidayetcolkusu/dotnet-reliable-integration-lab/actions/runs/37207068481), `ubuntu-latest`, 189/189 |
| Local | fresh clone, Windows 11 Pro (build 10.0.26200), PowerShell 7.6.6, Docker 29.6.1 |
| .NET SDK | 10.0.400, pinned in `global.json` with `rollForward: disable` |
| SQL Server | `mcr.microsoft.com/mssql/server:2022-latest@sha256:97b448857967be55e005424a660056fe6d51814435804dc07e8f79f028bab5fb` |
| RabbitMQ | `rabbitmq:4.3.5-management-alpine@sha256:b3b8b7f95f5382a19f9ea33540e604f30aad081d37ad9aba72255135765373a1` |

Detail for this run: [history/2026-10-04-race-fixes.md](history/2026-10-04-race-fixes.md).
