# KadrStudio AI Server

Самостоятельный HTTP/CUDA-сервер для KadrStudio. Он владеет API v2, Python workers,
моделями, runtime, AI-кэшами и бинарными артефактами. Desktop-приложение обращается
к нему только по HTTP и не запускает модели или Python локально.

## Запуск

```powershell
.\scripts\install-ai-server-local.ps1
.\scripts\setup-ai-workers.ps1 -InstallDependencies
.\scripts\install-production-models.ps1
.\scripts\run-ai-server.ps1
.\scripts\test-ai-server-connection.ps1
```

Runtime и веса находятся в `.kadr-ai` внутри этой папки и никогда не добавляются
в Git. Конфигурация endpoint и Bearer key описана в `docs/REMOTE_AI_SERVER.md`.

## Сборка и тесты

```powershell
dotnet build .\KadrStudioAiServer.sln
dotnet test .\KadrStudioAiServer.sln
```
