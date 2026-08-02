# Работа над Nexus Shell

Nexus Shell находится в активной разработке. Перед изменениями прочитайте
`PRODUCT.md`, `DESIGN.md`, `docs/SECURITY_RULES.md` и
`docs/CLAUDE_HANDOFF.md`.

## Проверка изменения

```powershell
dotnet restore NexusShell.sln --configfile NuGet.Config -p:NuGetAudit=false
dotnet build NexusShell.sln -c Release --no-restore -p:NuGetAudit=false
dotnet run --project tests/Nexus.Core.Tests/Nexus.Core.Tests.csproj `
  -c Release --no-build --no-restore -p:NuGetAudit=false
dotnet run --project tests/Nexus.RecycleBin.SmokeTests/Nexus.RecycleBin.SmokeTests.csproj `
  -c Release --no-build --no-restore -p:NuGetAudit=false
```

`Nexus.Core.Tests` является консольным regression harness. Не заменяйте его
командой `dotnet test`.

## Правила

- одна задача — одно ограниченное изменение;
- тестовые файловые операции выполняются только в уникальном `%TEMP%`;
- нельзя следовать reparse points или ослаблять fail-closed проверки;
- изменения WinUI, Shell/COM, иконок и окон требуют реального GUI smoke-теста;
- `outputs`, `work`, `bin` и `obj` не коммитятся;
- установщик не публикуется, пока Release build и Core harness не проходят.

Лицензия проекта пока не выбрана. До появления `LICENSE` условия внешнего
распространения и вкладов необходимо согласовывать с владельцем проекта.
