# UnityBundleRecompress — анализ и план доработок

Дата анализа: 2026-10-09. Ветка: `arena/2e19dd96-unitybundlerecompresscli`.

## 0. Что проверено и что нет

**Прочитано полностью:** `Unity bundle recompress launcher.cmd`, `batch_lzma_cli_pwsh.ps1` (1025 строк),
`Program.cs` (worker, 1681 строка), `Bootstrap/Program.cs`, оба `*.csproj`, `.github/workflows/build.yml`, `.gitignore`.

**Проверено отдельно:**
- `.gitignore` содержит правило `bin/`. `git check-ignore` подтверждает, что файл `bin/x.ps1` игнорируется.
- Upstream `AssetsTools.NET` (github.com/nesrak1/AssetsTools.NET), target `netstandard2.0`, не имеет PackageReference-зависимостей. Значит, `AssetsTools.NET.dll` самодостаточен.

**Не проверено (нет Windows/.NET Framework/pwsh в песочнице, NuGet API недоступен):**
- сборка через CI, runtime-поведение на Windows;
- что SHA-256 в CI соответствует пакету 3.0.5 на NuGet;
- полный список DLL, которые нужны `System.Text.Json` в `bin/` (ожидаемо: `System.Text.Json`, `Microsoft.Bcl.AsyncInterfaces`, `System.Buffers`, `System.Memory`, `System.Numerics.Vectors`, `System.Runtime.CompilerServices.Unsafe`, `System.Threading.Tasks.Extensions`, `System.Text.Encodings.Web`). Список нужно уточнить по содержимому `bin/` после сборки.

---

## 1. Архитектура (как сейчас)

```
launcher.cmd  ──►  batch_lzma_cli_pwsh.ps1  ──(пул до N потоков, ThreadJob)──►  BundleRecompressCli.exe  (Bootstrap)
                                                                                   │  проверка/загрузка AssetsTools.NET (NuGet)
                                                                                   ▼
                                                                          BundleRecompressCli.Worker.exe  (реальная перепаковка)
```

- **Bootstrap** (`Bootstrap/`) запускается на каждый файл. При каждом запуске проверяет обновление DLL.
- **Worker** (корневой `Program.cs`) делает перепаковку. В нём же лежит **второй, мёртвый экземпляр** логики обновления.

---

## 2. Критичные проблемы (блокируют поставку или дают неверный результат)

### K1. Пакет по заданной структуре не будет работать — отсутствует `BundleRecompressCli.Worker.exe`
Bootstrap запускает `BundleRecompressCli.Worker.exe` из своей папки (`Bootstrap/Program.cs`, `workerPath`). В структуре из ТЗ этого файла нет. Без него любой файл завершится ошибкой «Не найден основной файл утилиты».

**Исправление:** добавить `bin\BundleRecompressCli.Worker.exe` в структуру поставки.

### K2. Не упакованы зависимости `System.Text.Json`
`Bootstrap.csproj` и корневой `csproj` ссылаются на `System.Text.Json 8.0.5` (net48, copy-local). Workflow упаковывает только 3 файла: Bootstrap exe, Worker exe и `AssetsTools.NET.dll`. Bootstrap на чистой машине, скорее всего, упадёт с `FileNotFoundException` (исключение из-за отсутствующей сборки уходит в `catch` → код 10).

**Исправление:** упаковывать все файлы из `bin/Release/net48`, кроме служебных, либо явный список с зависимостями. Проверить на Windows.

### K3. Офлайн-режим или сбой NuGet убивает каждый файл (exit code 10)
В `Bootstrap/Program.cs`, `EnsureLibrary`: `await GetLatestVersion()` не обёрнут в try. Исключение уходит в `Main` → код 10. Из-за этого:
- если состояние старше 1 дня и сети нет, **каждый** запуск падает, даже если DLL уже есть;
- `SaveState` не вызывается при ошибке, поэтому каждый из N параллельных процессов снова ходит в сеть (таймаут 30 с на каждый);
- PowerShell фиксирует каждый файл как FAIL.

Worker (`LibraryUpdateService.CheckAndUpdateCoreAsync`) в этом месте деградирует правильно (предупреждение, работа продолжается). Bootstrap — нет.

**Исправление:** если локальная DLL есть, то сбой проверки/скачивания = предупреждение, а не ошибка. Писать `LastCheckUtc` даже при неудаче, чтобы не повторять проверку на каждом файле.

### K4. Флаги обновления не работают
- `--no-update` не разбирается Bootstrap. Он всегда проверяет обновление и передаёт флаг Worker, который его игнорирует в режиме перепаковки.
- `--update` в Bootstrap: `args.Any(--update)` → `return 0` **без перепаковки**. Если кто-то передаст `--update` вместе с путями, файлы молча не обработаются.

**Исправление:** разобрать `--no-update` и `--update` в Bootstrap явно. Режим `--update` не должен срабатывать, если переданы рабочие аргументы.

### K5. Гонка обновления DLL между параллельными процессами
Mutex защищает только сам процесс обновления. Пока другой Worker держит `AssetsTools.NET.dll` загруженной, Bootstrap, который решил обновиться, делает `File.Move(dllPath, backupPath)`. Это приводит к ошибке и откату, и файл попадает в FAIL. Проверка выполняется в каждом из N параллельных запусков.

**Исправление (рекомендуется):** одно обновление в `ps1` **до** запуска пула (однопоточно). Все последующие запуски Bootstrap получают `--no-update`. Это исключает и гонку, и повторные сетевые запросы.

### K6. Пин версии в CI не гарантирует версию у пользователя
CI проверяет SHA-256 `AssetsTools.NET.dll` версии 3.0.5. Но Bootstrap при первом запуске (состояния нет, `LastCheckUtc == default`) и затем раз в сутки **заменяет DLL на последнюю стабильную версию NuGet**. Проверенный в CI файл у пользователя перестаёт быть актуальным. Комментарий в `csproj` («exact package version recorded by the updater state») не соответствует коду.

**Решение нужно принять:** см. раздел 7, вопрос 2.

### K7. Потеря исходного файла при сбое замены
`ps1`, job-скрипт, строки ~570–590:
1. `Move-Item $filePath → $backupFile` (оригинал уходит в backup);
2. `Move-Item $newFile → $filePath`. Если шаг 2 падает (нехватка места, блокировка антивирусом, Ctrl+C), то внешний `catch` возвращает FAIL и **не восстанавливает оригинал**. Файл остаётся только в `backup/`, а в исходной папке его нет.

**Исправление:** откат из backup в `catch` (try/finally) для любого исключения после шага 1; проверка, что `$filePath` существует, перед возвратом FAIL.

### K8. Устаревший backup подменяет оригинал
Если `backup\<relativePath>` уже существует (от прошлого запуска), блок `if (-not (Test-Path $backupFile))` **не** переносит текущий файл, но `Move-Item $newFile → $filePath` его всё равно перезаписывает. Оригинал текущего прогона нигде не сохраняется. Откат (строка ~585) восстановит **старый** backup, а не текущий оригинал.

**Исправление:** backup per run (`backup\<stamp>\...`) либо проверка «backup уже есть» → FAIL с понятным сообщением.

### K9. Рекурсивный режим: backup и temp попадают в выборку, и имена `.new` сталкиваются
- `Get-ProcessableFiles -Recurse` ищет `*.bundle` и `data.unity3d` во всей дереве, включая `backup\` и `temp\`. При повторном запуске в той же папке прошлые оригиналы будут обработаны снова.
- `$tempNewFile = TempDir\<fileName>.new` (строка ~492) не уникален. Два `data.unity3d` из разных подпапок в параллельных job'ах пишут в один файл, и job, который стартует позже, **удаляет `.new` другого job'а** (строки ~497–500).

**Исправление:** исключить `backup`, `temp` и `log` из поиска; использовать уникальные имена временных файлов (например, по относительному пути или GUID).

### K10. Очистка артефактов не находит реальные остатки
- CLI пишет временный файл `<output>.tmp.decomp` (`TempFileSuffix`). Для `…bundle.new` это `…bundle.new.tmp.decomp`. Шаблон `*.new` его **не** находит, поэтому при аварийном завершении остаются большие временные файлы.
- Наоборот, `Remove-Artifacts` удаляет **все** `*.new` во всей папке, включая файлы, которые к утилите не относятся.
- Во `finally` очистка выполняется, пока дочерние CLI-процессы могут ещё писать (Ctrl+C не останавливает их).

**Исправление:** узкий шаблон (только свои артефакты, например по списку, созданному в текущем запуске); останавливать дочерние процессы перед очисткой.

### K11. `.gitignore` блокирует размещение файлов в `bin/` репозитория
Правило `bin/` игнорирует **любой** каталог `bin` (подтверждено `git check-ignore`). Если `batch_lzma_cli_pwsh.ps1` и `7za.exe` положить в `bin/` репозитория, git их не добавит.

**Исправление:** хранить сборочную структуру не в `bin/` в корне (см. раздел 3), либо добавить исключение `!release/bin/`.

---

## 3. Целевая структура поставки

```
Unity bundle recompress launcher.cmd
bin\
  batch_lzma_cli_pwsh.ps1
  BundleRecompressCli.exe           (Bootstrap)
  BundleRecompressCli.Worker.exe    (НЕ указан в ТЗ, но обязателен — см. K1)
  AssetsTools.NET.dll
  System.Text.Json.dll + зависимости (см. K2)
  7za.exe                           (опционально)
```

Структура совместима с текущим `ps1`: `$ScriptDir = bin`, `$RootDir = корень`, `log\` создаётся рядом с `.cmd`, `$CliPath = bin\BundleRecompressCli.exe`.

**Предложение по репозиторию:** держать шаблон поставки в `release/` (там же `launcher.cmd` и `bin/`). `ps1` и `.cmd` переехать туда. Скрипт `scripts/package.ps1` собирает zip из сборки CI, а не из репозитория. Так `bin/`-правило `.gitignore` не мешает.

---

## 4. Важные доработки (качество и поддержка)

| # | Проблема | Где | Предложение |
|---|---|---|---|
| Q1 | Два независимых updater'а: в Bootstrap и в Worker (`LibraryUpdateService`, `AppStartup`, `UpdateCommand`, `HandleUpdateMode`). Worker-версия не вызывается, ведёт себя по-другому и расходится. | `Program.cs` (worker) | Удалить из Worker. Единственный updater — в Bootstrap. |
| Q2 | Источник библиотеки: сейчас NuGet API (`api.nuget.org`). В ТЗ сказано «из её репозитория». | Bootstrap, CI | Уточнить (вопрос 1). |
| Q3 | Нет проверки целостности скачанного DLL, кроме хеша в CI. | Bootstrap | Lock-файл `assetstools.lock.json`: версия + SHA-256. Установка только при совпадении. |
| Q4 | Прогресс CLI (`\rПрогресс: N%`) печатается в stdout и попадает в лог, так как `-p` не передаётся. | Worker `ConsoleCompressProgress`, `ps1` | Передавать `-p` в ps1 или отключать прогресс без tty. |
| Q5 | `ExitFileNotFound` (2) объявлен, но не используется. Все ошибки → 1 или 10. | Worker | Выставлять коды осмысленно, ps1 может различать ошибки. |
| Q6 | Оценка памяти: до N параллельных CLI, каждый до 1.5 ГБ распакованных данных в памяти. При 8 потоках — до ~12 ГБ RAM. | Worker `MemoryModeMaxDecompressedBytes`, ps1 `Select-ThreadCount` | Ограничивать потоки по RAM или переводить крупные файлы в temp при параллельной работе. |
| Q7 | Проверка места: сравнивает суммарный вход со свободным местом. Реальная потребность ~ вход + выход + распакованный temp. | ps1 ~строка 823 | Считать `сумма(DecompressedSize)` через поиск в Worker либо предупреждать с запасом 2×. |
| Q8 | Валидация результата — только `Length > 0`. | ps1 ~строка 589 | Проверять сигнатуру `UnityFS` и открываемость bundle до замены оригинала. |
| Q9 | Пункт меню «[3] упаковать backup» показывается без `7za.exe`, и при нажатии выходит ошибка. | ps1 `Get-ExitMenuParts` | Скрывать пункт без 7za либо делать fallback на `Compress-Archive`. |
| Q10 | Исключение в job → файл `"unknown"`, имя теряется в отчёте. | ps1 ~строка 696 | Передавать `relativePath` в job-результат при исключении. |
| Q11 | Ввод пути в кавычках (`"D:\папка"`) не распознаётся. | ps1 `Select-SourceDir` | Обрезать кавычки и пробелы у ввода. |
| Q12 | `.cmd`: двойная пауза (`pause` в cmd и `Pause-OnExit` в ps1), exit code pwsh не пробрасывается, нет проверки Worker и DLL. | `launcher.cmd` | Убрать лишний `pause`, пробрасывать `exit /b %errorlevel%`, проверять все нужные файлы в preflight. |
| Q13 | Preflight в ps1 проверяет только `BundleRecompressCli.exe`. | ps1 ~строка 742 | Проверять `Worker.exe`, `AssetsTools.NET.dll`, `System.Text.Json.dll`. |
| Q14 | Кодировка: `.cmd` ставит `chcp 65001`, но ps1 и Worker не задают `OutputEncoding` явно. Возможны «кракозябры» в логе. | ps1, Worker | Явно задать UTF-8 в обоих (проверить на Windows). |
| Q15 | Нет единой версии: `ScriptVersion = 2.1.3` только в ps1. | Все | Одна версия в сборке (`csproj` `Version`) и в логе. |
| Q16 | Ctrl+C: дочерние CLI не останавливаются, `backup/temp` могут остаться в неконсистентном состоянии. | ps1 `try/finally` | Ловить `Ctrl+C`, останавливать job'ы и процессы, затем откатывать незавершённые файлы. |
| Q17 | В `.gitignore` нет `log/`, `backup/`, `temp/`, `*.new`, `recent_source_dirs.txt`, `.assetstools_update_state.json`, `artifact/`, `*.zip`. | `.gitignore` | Добавить. |

---

## 5. CI (`.github/workflows/build.yml`)

Текущий workflow собирает и проверяет DLL, но поставку собирает неверно (см. K1, K2, K11).

Предлагаемые изменения:
1. **Сборка пакета по структуре из раздела 3** скриптом `scripts/package.ps1`. Упаковывать все нужные файлы, включая `Worker.exe` и зависимости `System.Text.Json`.
2. **Проверка содержимого пакета:** `Test-Path` для всех обязательных файлов, плюс хеш `AssetsTools.NET.dll`.
3. **Проверка ps1:** `[System.Management.Automation.Language.Parser]::ParseFile` и, желательно, PSScriptAnalyzer.
4. **Smoke-тест из пакета:** `bin\BundleRecompressCli.exe --help` с `--no-update` (после исправления K4), чтобы не ходить в сеть в CI.
5. **Артефакт** `BundleRecompressCli-win-x64.zip` с корнем пакета, без вложенной папки `artifact/`.
6. (По желанию) релиз по тегу `v*`.

---

## 6. Порядок работ

**Этап 1 — поставка (блокеры).** K1, K2, K11, раздел 3 и CI (раздел 5). Результат: корректный zip с нужной структурой.

**Этап 2 — процесс обновления.** K3, K4, K5, K6 (после решения вопроса 2), Q1, Q2, Q3. Результат: обновление один раз до запуска пула, офлайн-работа, работающие флаги, единственный updater.

**Этап 3 — безопасность файлов.** K7, K8, K9, K10, Q16, Q8. Результат: оригинал не теряется при сбое, backup уникален, временные файлы не конфликтуют и корректно очищаются.

**Этап 4 — UX и прочее.** Q4–Q7, Q9–Q15, Q17.

---

## 7. Вопросы, которые нужно решить до реализации

1. **Источник AssetsTools.NET:** NuGet (как сейчас) или репозиторий GitHub `nesrak1/AssetsTools.NET` (собирать из исходников/тега)?
2. **Версия библиотеки:** фиксированная (пин + lock-файл, воспроизводимая поставка) или авто-обновление у пользователя (как сейчас)? От ответа зависит K5/K6.
3. **Место шаблона поставки в репозитории:** `release/` (рекомендуется) или другое имя?
4. **7za.exe:** класть вручную в `release/bin/` или скачивать в CI? Сейчас CI его не трогает.
5. **Ручная проверка на Windows:** после реализации нужен прогон на машине с PowerShell 7 и .NET Framework 4.8 (в песочнице этого нет). Подтвердите, что это возможно.

---

## 8. Решения по итогам обсуждения

- Worker `BundleRecompressCli.Worker.exe` обязателен (K1).
- Стандарт: .NET Framework 4.8, зависимостей окружения нет.
- Источник библиотеки: NuGet. Пин 3.0.5 нужен для сборки, у пользователя Bootstrap обновляет до последней стабильной версии (K6 закрыт этим решением).
- 7za.exe кладётся вручную в `release/bin/`.
- Справка по двойному щелчку на `BundleRecompressCli.exe` возвращена: Bootstrap запускает Worker с `--help` и ждёт клавишу.

## 9. Статус реализации

**Сделано**

| Пункт | Что сделано |
|---|---|
| K1, K2, раздел 3 | Шаблон поставки в `release/` (`Unity bundle recompress launcher.cmd`, `bin/batch_lzma_cli_pwsh.ps1`). `scripts/package.ps1` собирает zip со структурой `bin\` и включает Worker, Bootstrap, `AssetsTools.NET.dll`, `System.Text.Json.dll` и зависимости, 7za при наличии. |
| K11, Q17 | `.gitignore`: `/bin/` и `/obj/` только в корне, а не `bin/` везде. Добавлены `log/`, `backup/`, `temp/`, `*.new`, `*.tmp.decomp` и др. |
| K3 | Bootstrap: сбой проверки или загрузки при уже установленной DLL — предупреждение, работа продолжается. Состояние сохраняется. |
| K4 | Bootstrap разбирает `--update` и `--no-update`. Флаги вырезаются, Worker их не получает. `--update` без аргументов завершается после обновления, с аргументами продолжает работу. |
| K5 | `ps1` вызывает `BundleRecompressCli.exe --update` один раз до запуска потоков. Каждый файл запускается с `--no-update`. |
| Дубликат updater | Из Worker удалены `LibraryUpdateService`, `AppStartup`, `UpdateCommand`, `HandleUpdateMode` и опции `--update`/`--no-update`. Единственный updater — Bootstrap. |
| Справка | Двойной клик по `BundleRecompressCli.exe` → `--help` у Worker → пауза. Двойной клик по Worker — тоже справка и пауза. |
| K7 | Если оригинал перенесён в backup, а замена не удалась, он возвращается на место. Иначе сообщается путь к backup. |
| K8 | Если в backup уже есть файл с тем же путём, файл не обрабатывается (FAIL с пояснением). Оригинал не перезаписывается. |
| K9 | `backup/`, `temp/` исключены из поиска файлов. Временные `.new` получают уникальные имена (GUID). |
| K10 | Очистка удаляет только `*.bundle.new`, `*.unity3d.new` и их `.tmp.decomp`. |
| Q4 | Progress-строки `\r` не пишутся, если stdout перенаправлен (лог PowerShell). |
| Q7 | Проверка места: запас ×2. |
| Q8 | Перед заменой проверяется сигнатура `UnityFS` выходного файла. |
| Q9 | Пункт меню «архив backup» показывается только при наличии `7za.exe`. |
| Q10 | При исключении в job имя файла сохраняется в отчёте. |
| Q11 | Ввод пути: кавычки и пробелы обрезаются. |
| Q12 | `.cmd` пробрасывает код возврата; пауза только при ошибке запуска. |
| Q13 | Preflight проверяет `BundleRecompressCli.exe` и `BundleRecompressCli.Worker.exe`, а также `AssetsTools.NET.dll` после подготовки. |
| CI | Проверка синтаксиса `ps1`, сборка пакета через `scripts/package.ps1`, smoke-тест `--no-update --help` из собранного пакета. |

**Осталось / отложено**

- Q5 (коды выхода Worker), Q14 (явная кодировка консоли), Q15 (единая версия), Q6 (ограничение потоков по RAM) — не делалось.
- Q16 (Ctrl+C): дочерние процессы CLI не останавливаются принудительно. Требует отдельной доработки.
- Q3 (lock-файл с SHA-256): не делалось, решено полагаться на NuGet и HTTPS.
- Не проверено на Windows: сборка C# (`dotnet build`), синтаксис PowerShell (`ps1` проверен только сбалансированность скобок, PowerShell 7 в песочнице недоступен — GitHub Releases заблокированы), поведение `Move-Item`/блокировок, двойной клик, кодировка вывода.

## 10. Как проверить на Windows

```powershell
dotnet restore BundleRecompressCli.csproj; dotnet restore Bootstrap/Bootstrap.csproj
dotnet build BundleRecompressCli.csproj -c Release --no-restore
dotnet build Bootstrap/Bootstrap.csproj -c Release --no-restore
pwsh scripts/package.ps1 -WorkerOutDir bin/Release/net48 -BootstrapOutDir Bootstrap/bin/Release/net48 `
  -TemplateDir release -StagingDir artifact/staging -ZipPath BundleRecompressCli-win-x64.zip
```

Затем в распакованном архиве: двойной клик по `BundleRecompressCli.exe` (ожидается справка и пауза), запуск `launcher.cmd` на тестовой папке с 2–3 bundle, проверка backup и лога, сценарий без сети (`--update` при установленной DLL должен дать предупреждение и код 0).
