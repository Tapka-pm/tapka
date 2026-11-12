# tapka

Менеджер пакетов для Windows x64 на .NET 11. Команда `tapka install git` устанавливает PortableGit 2.56.1 для текущего пользователя. Пакет хранится в `%LOCALAPPDATA%\tapka\packages\git`, а нативный `git.exe` в `%LOCALAPPDATA%\tapka\shims` запускает его из рабочей папки пользователя. URL и SHA-256 закреплены по официальному выпуску. [Git for Windows 2.56.1](https://github.com/git-for-windows/git/releases/tag/v2.56.1.windows.1)

## Сборка

Для сборки нужны .NET 11 SDK и Zig. Скрипт ищет их в каталоге проекта `artifacts\tools` и в `PATH`. Параметры `-DotNet` и `-Zig` принимают полные пути к инструментам. [.NET 11 SDK](https://dotnet.microsoft.com/download/dotnet/11.0), [Zig](https://ziglang.org/download/)

| Команда | Результат |
| --- | --- |
| `.\build.ps1` | `artifacts\release\tapka.exe` |
| `.\build.ps1 -Test` | `artifacts\test\tapka.exe` |

Оба файла запускаются на Windows x64 со встроенной средой выполнения .NET.

## Установка Git

```powershell
.\artifacts\release\tapka.exe install git
```

Откройте PowerShell или `cmd.exe` заново из меню «Пуск» и выполните `git --version`. Если другая установка Git стоит выше в `PATH`, tapka напечатает путь к ней.

## Тестовая установка

```powershell
.\build.ps1 -Test
$testRoot = Join-Path $PWD 'artifacts\test-data\manual'
.\artifacts\test\tapka.exe install git --root $testRoot
$env:Path = (Get-Content "$testRoot\user-path.txt" -Raw) + ';' + $env:Path
git --version
```

Тестовый exe требует абсолютный путь в `--root` и помещает пакет и shim в этот каталог. Он хранит тестовый `PATH` в файле `user-path.txt`. Добавление пути в примере применяется к текущему сеансу PowerShell.

`.\test.ps1` запускает тестовый exe с новым каталогом, проверяет повторную установку, выбор shim, передачу аргументов и кода завершения, а также команды Git в новых процессах PowerShell и `cmd.exe`. Сценарию нужен доступ к GitHub для скачивания PortableGit.
