# tapka

Менеджер переносимых пакетов для Windows на .NET 11. Tapka читает рецепты `packages/<id>/config.json` из официального каталога. Установленные пакеты хранятся в `%LOCALAPPDATA%\tapka\packages`, а их команды доступны через нативные shim-файлы в `%LOCALAPPDATA%\tapka\shims`. [Каталог Tapka](https://github.com/Tapka-pm/packages)

## Сборка

Для сборки нужны .NET 11 SDK и Visual Studio Build Tools с рабочей нагрузкой «Разработка классических приложений на C++» (MSVC x64 и Windows SDK). Скрипт ищет .NET SDK в `artifacts\tools` и `PATH`; параметр `-DotNet` принимает путь к `dotnet.exe`. Для компилятора shim сначала используется `cl.exe` из `PATH`, затем поиск через `vswhere.exe` и инициализация MSVC x64 через `vcvarsall.bat`. [.NET 11 SDK](https://dotnet.microsoft.com/download/dotnet/11.0), [Visual Studio Build Tools](https://visualstudio.microsoft.com/visual-cpp-build-tools/)

| Команда | Результат |
| --- | --- |
| `.\build.ps1` | `artifacts\release\tapka.exe` |
| `.\build.ps1 -Test` | `artifacts\test\tapka.exe` |

Скрипт собирает x64-клиент со встроенной средой выполнения .NET. Клиент выбирает `x64` или `arm64` в рецепте по архитектуре Windows.

## Установка пакетов

```powershell
.\artifacts\release\tapka.exe install git
.\artifacts\release\tapka.exe install powershell
```

`install` принимает имя пакета и устанавливает текущий релиз из каталога. Рецепт хранит его номер в поле `version`, предыдущие рецепты доступны в истории Git.

Tapka устанавливает зависимости первыми, проверяет SHA-256 или SHA-512 скачанных файлов и распаковывает ZIP либо запускает PowerShell-скрипт из рецепта. Для скриптов используется PowerShell 7. Если подходящий `pwsh.exe` не найден, Tapka устанавливает `powershell` из того же каталога.

Откройте новый сеанс PowerShell или `cmd.exe` из меню «Пуск», чтобы оболочка получила обновлённый пользовательский `PATH`. Если другая программа с тем же именем стоит выше в `PATH`, Tapka напечатает путь к ней.

Параметр `--repository` принимает базовый HTTPS URL каталога или путь к локальному клону. Для соседнего каталога `packages` выполните из папки клиента:

```powershell
.\artifacts\release\tapka.exe install git --repository ..\packages
```

Даже при использовании локального каталога Tapka скачивает дистрибутив по HTTPS из URL в рецепте. Используйте каталоги, которым доверяете, поскольку их рецепты могут запускать скрипты установки.

## Тестовая установка

```powershell
.\build.ps1 -Test
$testRoot = Join-Path $PWD 'artifacts\test-data\manual'
.\artifacts\test\tapka.exe install git --root $testRoot
$env:Path = (Get-Content "$testRoot\user-path.txt" -Raw) + ';' + $env:Path
git --version
```

Тестовый exe требует абсолютный путь в `--root` и помещает пакет и shim в этот каталог. Он хранит тестовый `PATH` в файле `user-path.txt`. Добавление пути в примере применяется к текущему сеансу PowerShell.

`.\test.ps1` запускает тестовый exe с новым каталогом, проверяет установку Git и повторный вызов с тем же именем пакета. Он также проверяет команды Git, передачу аргументов и кода завершения в новых процессах PowerShell и `cmd.exe`. Сценарию нужен доступ к GitHub для скачивания PortableGit. Для проверки локальных рецептов запустите `.\test.ps1 -Repository ..\packages`.
