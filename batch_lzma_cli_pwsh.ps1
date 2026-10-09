param()
$ErrorActionPreference = "Stop"

# ── Версия скрипта ──
$ScriptVersion = "2.1.3"

# ── Основные пути ──
$ScriptDir	= Split-Path -Parent $MyInvocation.MyCommand.Path
$RootDir	  = Split-Path -Parent $ScriptDir
$CliPath	  = Join-Path $ScriptDir "BundleRecompressCli.exe"
$SevenZipPath = Join-Path $ScriptDir "7za.exe"
$LogsDir	  = Join-Path $RootDir "log"
$RecentFile   = Join-Path $ScriptDir "recent_source_dirs.txt"

# ══════════════════════════════════════════════════════════════
# UI / Helper функции
# ══════════════════════════════════════════════════════════════

function Show-AppHeader {
	param([string]$Title = "UNITY BUNDLE RECOMPRESS TOOL")
	Write-Host $Title -ForegroundColor Cyan
	Write-Host ("=" * $Title.Length) -ForegroundColor DarkCyan
	Write-Host ""
}

function Show-Section {
	param([string]$Title)
	Write-Host ""
	Write-Host $Title -ForegroundColor Yellow
	Write-Host ("-" * $Title.Length) -ForegroundColor DarkYellow
}

function Show-InfoLine {
	param(
		[string]$Label,
		[string]$Value,
		[string]$Color = "White"
	)
	Write-Host ("{0,-14} {1}" -f ($Label + ":"), $Value) -ForegroundColor $Color
}

function Show-WarningMessage {
	param(
		[string]$Text,
		[int]$DelayMs = 1500
	)
	Clear-Host
	Show-AppHeader
	Write-Host $Text -ForegroundColor Yellow
	Start-Sleep -Milliseconds $DelayMs
}

function Show-ErrorMessage {
	param(
		[string]$Text,
		[int]$DelayMs = 1500
	)
	Clear-Host
	Show-AppHeader
	Write-Host $Text -ForegroundColor Red
	Start-Sleep -Milliseconds $DelayMs
}

function Show-MainSummary {
	param(
		[string]$SourceDir,
		[int]$FileCount,
		[string]$CompressionModeDisplay,
		[int]$MaxParallel
	)
	Clear-Host
	Show-AppHeader
	Show-Section "Параметры запуска"
	Show-InfoLine "Папка"   $SourceDir
	Show-InfoLine "Файлов"  $FileCount
	Show-InfoLine "Метод"   $CompressionModeDisplay
	Show-InfoLine "Потоков" $MaxParallel
	Write-Host ""
}

function Read-MenuChoiceKey {
	param([string[]]$AllowedKeys)
	while ($true) {
		$keyInfo = [Console]::ReadKey($true)
		$keyName = $keyInfo.Key.ToString()
		if ($AllowedKeys -contains $keyName) {
			return $keyName
		}
	}
}

function Test-KeyMatch {
	param(
		[string]$KeyName,
		[string]$Digit
	)
	return ($KeyName -eq ("D" + $Digit) -or $KeyName -eq ("NumPad" + $Digit))
}

function Get-ExitMenuParts {
	param(
		[string]$SourceDir,
		[string]$LogPath,
		[string]$BackupDir
	)
	$menuParts = @("Enter - выйти")
	if (-not [string]::IsNullOrWhiteSpace($SourceDir) -and (Test-Path $SourceDir)) {
		$menuParts += "[1] открыть папку"
	}
	if (-not [string]::IsNullOrWhiteSpace($LogPath) -and (Test-Path $LogPath)) {
		$menuParts += "[2] открыть лог"
	}
	if (-not [string]::IsNullOrWhiteSpace($BackupDir) -and (Test-Path $BackupDir)) {
		$menuParts += "[3] упаковать backup в архив"
	}
	return $menuParts
}

function Compress-BackupArchive {
	param(
		[string]$BackupDir,
		[DateTime]$EndTime,
		[string]$SevenZipPath,
		[int]$ArchiveThreads
	)
	if (-not (Test-Path $BackupDir)) {
		Write-Host "Папка backup не найдена: $BackupDir" -ForegroundColor Yellow
		return $false
	}
	if (-not (Test-Path $SevenZipPath)) {
		Write-Host "Не найден 7za.exe: $SevenZipPath" -ForegroundColor Red
		return $false
	}
	$parentDir   = Split-Path $BackupDir -Parent
	$stamp	   = $EndTime.ToString("yyyy-MM-dd_HH-mm-ss")
	$archivePath = Join-Path $parentDir ("Backup_{0}.7z" -f $stamp)
	try {
		if (Test-Path $archivePath) {
			Remove-Item -Force $archivePath -ErrorAction SilentlyContinue
		}
		Write-Host "Упаковка backup в архив..."
		$output   = & $SevenZipPath a -t7z -mx=9 "-mmt=$ArchiveThreads" $archivePath $BackupDir 2>&1
		$exitCode = $LASTEXITCODE
		if ($exitCode -ne 0) {
			Write-Host "Ошибка упаковки backup. Код: $exitCode" -ForegroundColor Red
			return $false
		}
		if (-not (Test-Path $archivePath)) {
			Write-Host "Архив не создан." -ForegroundColor Red
			return $false
		}
		Remove-Item -Recurse -Force $BackupDir -ErrorAction SilentlyContinue
		Write-Host "Архив создан: $archivePath" -ForegroundColor Green
		Write-Host "Папка backup удалена." -ForegroundColor Green
		return $true
	}
	catch {
		Write-Host "Ошибка при упаковке backup: $($_.Exception.Message)" -ForegroundColor Red
		return $false
	}
}

# ══════════════════════════════════════════════════════════════
# Меню завершения
# ══════════════════════════════════════════════════════════════

function Pause-OnExit {
	param(
		[string]$SourceDir,
		[string]$LogPath,
		[string]$BackupDir,
		[DateTime]$EndTime,
		[string]$SevenZipPath,
		[int]$ArchiveThreads
	)
	$menuTop	  = [Console]::CursorTop
	$menuLeft	 = 0
	$consoleWidth = [Console]::BufferWidth

	function Rewrite-MenuLine {
		param([string]$Text = "")
		[Console]::SetCursorPosition($menuLeft, $menuTop)
		Write-Host (" " * ($consoleWidth - 1)) -NoNewline
		[Console]::SetCursorPosition($menuLeft, $menuTop)
		if (-not [string]::IsNullOrEmpty($Text)) {
			Write-Host $Text -NoNewline
		}
	}

	while ($true) {
		$menuParts = Get-ExitMenuParts -SourceDir $SourceDir -LogPath $LogPath -BackupDir $BackupDir
		$prompt	= "Нажмите " + ($menuParts -join ", ")
		Rewrite-MenuLine -Text $prompt
		$keyInfo = [Console]::ReadKey($true)
		$keyName = $keyInfo.Key.ToString()

		if ($keyName -eq "Enter") {
			Rewrite-MenuLine
			[Console]::SetCursorPosition(0, $menuTop)
			return
		}
		if ((Test-KeyMatch -KeyName $keyName -Digit "1") -and (Test-Path $SourceDir)) {
			Start-Process explorer.exe $SourceDir
			continue
		}
		if ((Test-KeyMatch -KeyName $keyName -Digit "2") -and (Test-Path $LogPath)) {
			Start-Process $LogPath
			continue
		}
		if ((Test-KeyMatch -KeyName $keyName -Digit "3") -and (Test-Path $BackupDir)) {
			Rewrite-MenuLine
			[Console]::SetCursorPosition(0, $menuTop)
			[void](Compress-BackupArchive -BackupDir $BackupDir -EndTime $EndTime `
				-SevenZipPath $SevenZipPath -ArchiveThreads $ArchiveThreads)
			$menuTop = [Console]::CursorTop
			continue
		}
	}
}

# ══════════════════════════════════════════════════════════════
# Работа с историей папок
# ══════════════════════════════════════════════════════════════

function Load-RecentSourceDirs {
	param([string]$RecentFile)
	if (-not (Test-Path $RecentFile)) { return @() }
	try {
		$rawLines = [System.IO.File]::ReadAllLines($RecentFile)
		$result   = [System.Collections.Generic.List[string]]::new()
		$seen	 = [System.Collections.Generic.HashSet[string]]::new()
		foreach ($line in $rawLines) {
			$s = [string]$line.Trim()
			if ($s -and $seen.Add($s)) { $result.Add($s) }
		}
		return @($result | Select-Object -First 3)
	}
	catch { return @() }
}

function Save-RecentSourceDirs {
	param(
		[string]$RecentFile,
		[string[]]$Dirs
	)
	$result = [System.Collections.Generic.List[string]]::new()
	$seen   = [System.Collections.Generic.HashSet[string]]::new()
	foreach ($d in $Dirs) {
		$s = [string]$d.Trim()
		if ($s -and $seen.Add($s)) { $result.Add($s) }
	}
	$final = @($result | Select-Object -First 3)
	[System.IO.File]::WriteAllLines($RecentFile, $final)
}

function Update-RecentSourceDirs {
	param(
		[string]$RecentFile,
		[string]$SelectedDir
	)
	$s = [string]$SelectedDir.Trim()
	if (-not $s) { return }
	$recent  = @(Load-RecentSourceDirs -RecentFile $RecentFile)
	$newList = [System.Collections.Generic.List[string]]::new()
	$newList.Add($s)
	foreach ($item in $recent) {
		if ($item -ne $s) { $newList.Add($item) }
	}
	Save-RecentSourceDirs -RecentFile $RecentFile -Dirs (@($newList | Select-Object -First 3))
}

function Remove-RecentSourceDir {
	param(
		[string]$RecentFile,
		[string]$PathToRemove
	)
	$s = [string]$PathToRemove.Trim()
	if (-not $s) { return }
	$recent  = @(Load-RecentSourceDirs -RecentFile $RecentFile)
	$updated = [System.Collections.Generic.List[string]]::new()
	foreach ($item in $recent) {
		if ($item -ne $s) { $updated.Add($item) }
	}
	Save-RecentSourceDirs -RecentFile $RecentFile -Dirs (@($updated | Select-Object -First 3))
}

# ══════════════════════════════════════════════════════════════
# Выбор папки, метода сжатия, рекурсии и потоков
# ══════════════════════════════════════════════════════════════

function Select-SourceDir {
	param(
		[string]$RootDir,
		[string]$RecentFile
	)
	while ($true) {
		Clear-Host
		Show-AppHeader
		$recent = @(Load-RecentSourceDirs -RecentFile $RecentFile)
		Show-Section "Выбор папки"
		Write-Host "Укажите папку с bundle / data.unity3d"
		Write-Host "Можно:"
		Write-Host "  - ввести 1..3 для выбора из истории"
		Write-Host "  - вставить новый путь вручную"
		Write-Host "  - просто нажать Enter, чтобы использовать папку: $RootDir"
		Write-Host ""
		if ($recent.Count -gt 0) {
			for ($i = 0; $i -lt $recent.Count; $i++) {
				Write-Host ("  {0}) {1}" -f ($i + 1), $recent[$i])
			}
			Write-Host ""
		}
		$inputValue = Read-Host "Путь или номер"
		if ([string]::IsNullOrWhiteSpace($inputValue)) {
			return (Resolve-Path $RootDir).Path
		}
		$num = 0
		if ([int]::TryParse($inputValue, [ref]$num)) {
			$idx = $num - 1
			if ($idx -ge 0 -and $idx -lt $recent.Count) {
				$candidate = [string]$recent[$idx]
				if (-not (Test-Path $candidate)) {
					Remove-RecentSourceDir -RecentFile $RecentFile -PathToRemove $candidate
					Show-WarningMessage -Text "Путь из истории не найден и будет удалён: $candidate"
					continue
				}
				return (Resolve-Path $candidate).Path
			}
		}
		$candidate = $inputValue
		if (-not (Test-Path $candidate)) {
			Show-ErrorMessage -Text "Папка не найдена: $candidate"
			continue
		}
		return (Resolve-Path $candidate).Path
	}
}

# [УЛУЧШЕНИЕ 1.1] Рекурсивный поиск файлов
function Get-ProcessableFiles {
	param(
		[string]$SourceDir,
		[switch]$Recurse
	)
	$params = @{ Path = $SourceDir; File = $true }
	if ($Recurse) { $params.Recurse = $true }

	$files  = @()
	$files += Get-ChildItem @params -Filter *.bundle
	$files += Get-ChildItem @params -Filter data.unity3d
	return @($files | Sort-Object FullName -Unique)
}

# [УЛУЧШЕНИЕ 1.1] Выбор режима рекурсии
function Select-RecurseMode {
	Clear-Host
	Show-AppHeader
	Show-Section "Режим поиска файлов"
	Write-Host "Искать файлы рекурсивно (включая подпапки)?"
	Write-Host ""
	Write-Host "  1) Только в указанной папке"
	Write-Host "  2) Рекурсивно (включая подпапки)"
	Write-Host ""
	Write-Host "По умолчанию: только в указанной папке"
	Write-Host "Нажмите [1], [2] или Enter"

	$keyName = Read-MenuChoiceKey -AllowedKeys @("Enter", "D1", "NumPad1", "D2", "NumPad2")
	if (Test-KeyMatch $keyName "2") {
		return $true
	}
	return $false
}

function Select-CompressionMode {
	Clear-Host
	Show-AppHeader
	Show-Section "Выбор метода сжатия"
	Write-Host "Выберите метод сжатия для всех файлов."
	Write-Host ""
	Write-Host ("{0,-4} {1,-8} {2}" -f "1)", "LZMA",	"меньше размер, медленнее")
	Write-Host ("{0,-4} {1,-8} {2}" -f "2)", "LZ4",	 "быстрее, больше размер")
	Write-Host ("{0,-4} {1,-8} {2}" -f "3)", "LZ4Fast", "ещё быстрее упаковка, обычно хуже сжатие")
	Write-Host ""
	Write-Host "По умолчанию: LZMA"
	Write-Host "Нажмите клавишу [1], [2], [3] или Enter"

	$keyName = Read-MenuChoiceKey -AllowedKeys @(
		"Enter", "D1", "NumPad1", "D2", "NumPad2", "D3", "NumPad3"
	)
	switch ($keyName) {
		{ Test-KeyMatch $_ "2" } {
			return [PSCustomObject]@{ Mode = "lz4";	 Display = "LZ4" }
		}
		{ Test-KeyMatch $_ "3" } {
			return [PSCustomObject]@{ Mode = "lz4fast"; Display = "LZ4Fast" }
		}
		default {
			return [PSCustomObject]@{ Mode = "lzma";	Display = "LZMA" }
		}
	}
}

function Select-ThreadCount {
	$cpuThreads	 = [Environment]::ProcessorCount
	$defaultThreads = [Math]::Min($cpuThreads, 6)
	$hardLimit	  = [Math]::Min($cpuThreads, 8)

	Clear-Host
	Show-AppHeader
	Show-Section "Выбор количества потоков"
	Write-Host "Введите количество потоков."
	Write-Host "Если просто нажать Enter, будет использовано: $defaultThreads"
	Write-Host "Рекомендации: HDD = 2-3 потока, SSD = 4-6 потоков"
	Write-Host "Логических ядер обнаружено: $cpuThreads"
	Write-Host "Максимум для этого скрипта: $hardLimit"
	Write-Host ""

	$threadsInput = Read-Host "Количество потоков"
	if ([string]::IsNullOrWhiteSpace($threadsInput)) {
		return $defaultThreads
	}
	[int]$MaxParallel = 0
	if (-not [int]::TryParse($threadsInput, [ref]$MaxParallel) -or $MaxParallel -lt 1) {
		Show-WarningMessage -Text "Некорректное количество потоков. Будет использовано значение по умолчанию: $defaultThreads"
		return $defaultThreads
	}
	if ($MaxParallel -gt $hardLimit) {
		Show-WarningMessage -Text "Указано слишком большое значение. Будет использован лимит: $hardLimit"
		return $hardLimit
	}
	return $MaxParallel
}

# ══════════════════════════════════════════════════════════════
# Прогресс
# ══════════════════════════════════════════════════════════════

function Update-ProgressDisplay {
	param(
		[System.Collections.Generic.List[PSCustomObject]]$Results,
		[System.Collections.Generic.List[object]]$Jobs,
		[int]$Total,
		[DateTime]$StartTime
	)
	$completed = $Results.Count
	$remaining = $Total - $completed
	$running   = @($Jobs | Where-Object { $_.State -eq 'Running' }).Count
	$failedNow = @($Results | Where-Object Status -eq "FAIL").Count
	$noopNow   = @($Results | Where-Object Status -eq "NOT_APPLIED").Count
	$okNow	 = @($Results | Where-Object Status -eq "OK").Count

	# FIX: ограничиваем 0..100
	$percent = if ($Total -gt 0) {
		[Math]::Min(100, [Math]::Max(0, [int](($completed / $Total) * 100)))
	} else { 100 }

	$elapsed	 = (Get-Date) - $StartTime
	$elapsedText = "{0:mm\:ss}" -f $elapsed

	Write-Progress -Id 1 `
		-Activity "Сжатие bundle" `
		-Status ("$completed/$Total ($percent%) | Осталось: $remaining | OK: $okNow | FAIL: $failedNow | SKIP: $noopNow | RUN: $running | $elapsedText") `
		-PercentComplete $percent
}

# ══════════════════════════════════════════════════════════════
# Job-скрипт для одного файла
# ══════════════════════════════════════════════════════════════
# [УЛУЧШЕНИЕ 1.2] Валидация + откат после замены
# [УЛУЧШЕНИЕ 1.3] Бэкап сохраняет структуру подпапок (RelativePath)
# [УЛУЧШЕНИЕ 3.2] Move вместо Copy для бэкапа (мгновенный rename)
# [УЛУЧШЕНИЕ 5.1] Вывод CLI сохраняется в лог для всех статусов
# [УЛУЧШЕНИЕ 6.2] Предпроверка доступности файла (locked/readonly)

$jobScript = {
	param(
		$filePath,
		$fileName,
		$relativePath,
		$CliPath,
		$BackupDir,
		$TempDir,
		$MemoryThresholdBytes,
		$CompressionMode,
		$CompressionModeDisplay
	)

	$fileSize   = (Get-Item $filePath).Length
	$useTempDir = ($fileSize -ge $MemoryThresholdBytes)

	$localNewFile = Join-Path (Split-Path $filePath -Parent) ($fileName + ".new")
	$tempNewFile  = Join-Path $TempDir ($fileName + ".new")
	$newFile	  = if ($useTempDir) { $tempNewFile } else { $localNewFile }

	# [6.2] Предпроверка: файл не заблокирован
	try {
		$stream = [System.IO.File]::Open($filePath, 'Open', 'ReadWrite', 'None')
		$stream.Close()
		$stream.Dispose()
	}
	catch {
		return [PSCustomObject]@{
			File	   = $relativePath
			Status	 = "FAIL"
			LogLines   = @("FAIL: файл заблокирован — $($_.Exception.Message)")
			InputSize  = $fileSize
			OutputSize = $null
			BackupMade = $false
		}
	}

	try {
		# Очистка артефактов предыдущих запусков
		foreach ($f in @($localNewFile, $tempNewFile)) {
			if (Test-Path $f) { Remove-Item -Force $f -ErrorAction SilentlyContinue }
		}

		$output   = & $CliPath $filePath $newFile -c $CompressionMode 2>&1
		$exitCode = $LASTEXITCODE
		$outputLines = @($output | ForEach-Object { [string]$_ })

		# ── Проверка: файл уже сжат нужным методом ──
		$skipMarker = switch ($CompressionMode) {
			"lzma"	{ "SKIPPED_ALREADY_LZMA" }
			"lz4"	 { "SKIPPED_ALREADY_LZ4" }
			"lz4fast" { "SKIPPED_ALREADY_LZ4" }
			default   { "SKIPPED_ALREADY_COMPRESSED" }
		}

		if ($outputLines -contains $skipMarker) {
			return [PSCustomObject]@{
				File	   = $relativePath
				Status	 = "NOT_APPLIED"
				LogLines   = @("NOT_APPLIED файл уже сжат в $CompressionModeDisplay") + $outputLines
				InputSize  = $fileSize
				OutputSize = $null
				BackupMade = $false
			}
		}

		# ── CLI вернул ошибку ──
		if ($exitCode -ne 0) {
			return [PSCustomObject]@{
				File	   = $relativePath
				Status	 = "FAIL"
				LogLines   = @("FAIL: CLI завершился с кодом $exitCode") + $outputLines
				InputSize  = $fileSize
				OutputSize = $null
				BackupMade = $false
			}
		}

		# ── Выходной файл не создан или пуст ──
		if ((-not (Test-Path $newFile)) -or ((Get-Item $newFile).Length -le 0)) {
			return [PSCustomObject]@{
				File	   = $relativePath
				Status	 = "NOT_APPLIED"
				LogLines   = @("NOT_APPLIED выходной файл не создан или пуст") + $outputLines
				InputSize  = $fileSize
				OutputSize = $null
				BackupMade = $false
			}
		}

		# ── Бэкап: Move оригинала (мгновенный rename на одном диске) ──
		# [1.3] Сохраняем относительный путь в структуре backup
		$backupFile	= Join-Path $BackupDir $relativePath
		$backupSubDir  = Split-Path $backupFile -Parent

		if (-not (Test-Path $backupFile)) {
			if (-not (Test-Path $backupSubDir)) {
				New-Item -ItemType Directory -Force -Path $backupSubDir | Out-Null
			}
			# [3.2] Move вместо Copy — мгновенно на одном томе
			Move-Item -Force $filePath $backupFile
		}

		# ── Замена: перемещаем новый файл на место оригинала ──
		Move-Item -Force $newFile $filePath

		# [1.2] Валидация после замены
		if ((-not (Test-Path $filePath)) -or ((Get-Item $filePath).Length -le 0)) {
			# Откат из бэкапа
			if (Test-Path $backupFile) {
				Move-Item -Force $backupFile $filePath
			}
			return [PSCustomObject]@{
				File	   = $relativePath
				Status	 = "FAIL"
				LogLines   = @("FAIL: файл после замены отсутствует или пуст, выполнен откат") + $outputLines
				InputSize  = $fileSize
				OutputSize = $null
				BackupMade = $false
			}
		}

		$outputSize = (Get-Item $filePath).Length

		return [PSCustomObject]@{
			File	   = $relativePath
			Status	 = "OK"
			LogLines   = $outputLines
			InputSize  = $fileSize
			OutputSize = $outputSize
			BackupMade = $true
		}
	}
	catch {
		# Очистка артефактов
		foreach ($f in @($localNewFile, $tempNewFile)) {
			if (Test-Path $f) { Remove-Item -Force $f -ErrorAction SilentlyContinue }
		}
		return [PSCustomObject]@{
			File	   = $relativePath
			Status	 = "FAIL"
			LogLines   = @("FAIL: $($_.Exception.Message)")
			InputSize  = $fileSize
			OutputSize = $null
			BackupMade = $false
		}
	}
}

# ══════════════════════════════════════════════════════════════
# [УЛУЧШЕНИЕ 3.1] Оптимизированный планировщик
# ══════════════════════════════════════════════════════════════

function Start-ProcessingJobs {
	param(
		[array]$Files,
		[int]$MaxParallel,
		[string]$SourceDir,
		[string]$CliPath,
		[string]$BackupDir,
		[string]$TempDir,
		[long]$MemoryThresholdBytes,
		[string]$CompressionMode,
		[string]$CompressionModeDisplay,
		[DateTime]$StartTime
	)

	$jobs	= [System.Collections.Generic.List[object]]::new()
	$results = [System.Collections.Generic.List[PSCustomObject]]::new()
	$total   = $Files.Count
	$queued  = 0

	# ── Главный цикл ──
	while ($queued -lt $total -or $jobs.Count -gt 0) {

		# 1. Заполняем свободные слоты
		while ($jobs.Count -lt $MaxParallel -and $queued -lt $total) {
			$file		 = $Files[$queued]
			$relativePath = $file.FullName.Substring($SourceDir.Length).TrimStart('\', '/')

			$job = Start-ThreadJob -ScriptBlock $jobScript -ArgumentList `
				$file.FullName,
				$file.Name,
				$relativePath,
				$CliPath,
				$BackupDir,
				$TempDir,
				$MemoryThresholdBytes,
				$CompressionMode,
				$CompressionModeDisplay

			$jobs.Add($job)
			$queued++
		}

		# 2. Обновляем прогресс
		Update-ProgressDisplay -Results $results -Jobs $jobs `
			-Total $total -StartTime $StartTime

		# 3. Собираем завершённые jobs
		$collected = 0
		$finished  = @($jobs | Where-Object {
			$_.State -eq 'Completed' -or
			$_.State -eq 'Failed' -or
			$_.State -eq 'Stopped'
		})

		foreach ($dj in $finished) {
			try {
				$res = Receive-Job $dj -ErrorAction Stop
				# Receive-Job может вернуть массив — берём только PSCustomObject
				foreach ($r in @($res)) {
					if ($r -is [PSCustomObject]) {
						$results.Add($r)
					}
				}
			}
			catch {
				$results.Add([PSCustomObject]@{
					File	   = "unknown"
					Status	 = "FAIL"
					LogLines   = @("FAIL: job exception — $($_.Exception.Message)")
					InputSize  = $null
					OutputSize = $null
					BackupMade = $false
				})
			}
			finally {
				Remove-Job $dj -Force -ErrorAction SilentlyContinue
				$jobs.Remove($dj) | Out-Null
				$collected++
			}
		}

		# 4. Если ничего не собрали и есть активные jobs — пауза
		if ($collected -eq 0 -and $jobs.Count -gt 0) {
			Start-Sleep -Milliseconds 100
		}
	}

	Write-Progress -Id 1 -Activity "Сжатие bundle" -Completed
	return @($results)
}

# ══════════════════════════════════════════════════════════════
# [УЛУЧШЕНИЕ 2.3] Очистка артефактов (.new файлы, temp)
# ══════════════════════════════════════════════════════════════

function Remove-Artifacts {
	param(
		[string]$SourceDir,
		[string]$TempDir
	)
	# Удалить .new файлы-артефакты
	Get-ChildItem -Path $SourceDir -Filter "*.new" -Recurse -ErrorAction SilentlyContinue |
		Remove-Item -Force -ErrorAction SilentlyContinue

	# Удалить temp если есть
	if (Test-Path $TempDir) {
		Remove-Item -Recurse -Force $TempDir -ErrorAction SilentlyContinue
	}
}

# ══════════════════════════════════════════════════════════════
# Проверка зависимостей
# ══════════════════════════════════════════════════════════════

if (-not (Test-Path $CliPath)) {
	Show-ErrorMessage -Text "Не найден BundleRecompressCli.exe рядом со скриптом:`n$CliPath" -DelayMs 3000
	exit 1
}

New-Item -ItemType Directory -Force -Path $LogsDir | Out-Null

# ══════════════════════════════════════════════════════════════
# Выбор папки, рекурсии и проверка файлов
# ══════════════════════════════════════════════════════════════

$SourceDir  = $null
$files	  = @()
$useRecurse = $false

while ($true) {
	$SourceDir = Select-SourceDir -RootDir $RootDir -RecentFile $RecentFile
	Update-RecentSourceDirs -RecentFile $RecentFile -SelectedDir $SourceDir

	# [1.1] Спрашиваем рекурсию
	$useRecurse = Select-RecurseMode

	$getFilesParams = @{ SourceDir = $SourceDir }
	if ($useRecurse) { $getFilesParams.Recurse = $true }
	$files = Get-ProcessableFiles @getFilesParams

	if ($files.Count -eq 0) {
		$modeText = if ($useRecurse) { " (рекурсивно)" } else { "" }
		Show-WarningMessage -Text "В папке нет подходящих файлов (.bundle, data.unity3d)${modeText}:`n$SourceDir" -DelayMs 1500
		continue
	}
	break
}

# ══════════════════════════════════════════════════════════════
# Выбор режима и потоков
# ══════════════════════════════════════════════════════════════

$compressionSelection   = Select-CompressionMode
$CompressionMode		= $compressionSelection.Mode
$CompressionModeDisplay = $compressionSelection.Display
$MaxParallel			= Select-ThreadCount

Show-MainSummary -SourceDir $SourceDir -FileCount $files.Count `
	-CompressionModeDisplay $CompressionModeDisplay -MaxParallel $MaxParallel

# ══════════════════════════════════════════════════════════════
# Подготовка папок и переменных
# ══════════════════════════════════════════════════════════════

$BackupDir			= Join-Path $SourceDir "backup"
$TempDir			  = Join-Path $SourceDir "temp"
$stamp				= Get-Date -Format "yyyy-MM-dd_HH-mm-ss"
$LogFile			  = Join-Path $LogsDir ("{0}_batch_{1}.log" -f $stamp, $CompressionMode)
$FailFile			 = Join-Path $SourceDir ("failed_files_{0}.txt" -f $stamp)
$NoopFile			 = Join-Path $SourceDir ("not_applied_files_{0}.txt" -f $stamp)
$ScriptStartTime	  = Get-Date
$MemoryThresholdBytes = 500MB

# Удаление пустых backup/temp от предыдущих запусков
if (Test-Path $BackupDir) {
	$backupItems = Get-ChildItem -Path $BackupDir -Force -ErrorAction SilentlyContinue
	if ($null -eq $backupItems -or @($backupItems).Count -eq 0) {
		Remove-Item -Recurse -Force $BackupDir -ErrorAction SilentlyContinue
	}
}
if (Test-Path $TempDir) {
	Remove-Item -Recurse -Force $TempDir -ErrorAction SilentlyContinue
}

# [2.1] Создание рабочих папок ДО запуска потоков — исключаем race condition
New-Item -ItemType Directory -Force -Path $BackupDir | Out-Null
New-Item -ItemType Directory -Force -Path $TempDir   | Out-Null

# [6.3] Грубая проверка свободного места
$totalFilesSize = ($files | Measure-Object -Property Length -Sum).Sum
try {
	$driveRoot = [System.IO.Path]::GetPathRoot((Resolve-Path $SourceDir).Path)
	$driveInfo = [System.IO.DriveInfo]::new($driveRoot)
	$freeSpace = $driveInfo.AvailableFreeSpace

	if ($freeSpace -lt $totalFilesSize) {
		$neededGb = [math]::Round($totalFilesSize / 1GB, 2)
		$freeGb   = [math]::Round($freeSpace / 1GB, 2)
		Show-WarningMessage -Text ("Может не хватить места на диске!`n" +
			"Нужно ~$neededGb GB для бэкапа, свободно $freeGb GB`n" +
			"Продолжаем, но возможны ошибки.") -DelayMs 3000
	}
}
catch {
	# Не удалось проверить — не критично, продолжаем
}

# ══════════════════════════════════════════════════════════════
# [2.3] Выполнение обработки с защитой от аварийного завершения
# ══════════════════════════════════════════════════════════════

$results = $null

try {
	$results = Start-ProcessingJobs `
		-Files				  $files `
		-MaxParallel			$MaxParallel `
		-SourceDir			  $SourceDir `
		-CliPath				$CliPath `
		-BackupDir			  $BackupDir `
		-TempDir				$TempDir `
		-MemoryThresholdBytes   $MemoryThresholdBytes `
		-CompressionMode		$CompressionMode `
		-CompressionModeDisplay $CompressionModeDisplay `
		-StartTime			  $ScriptStartTime
}
finally {
	# Очистка артефактов при любом завершении (включая Ctrl+C)
	Remove-Artifacts -SourceDir $SourceDir -TempDir $TempDir
}

# Если results пуст (аварийное завершение до получения результатов)
if ($null -eq $results) {
	$results = @()
}

# ══════════════════════════════════════════════════════════════
# Финализация
# ══════════════════════════════════════════════════════════════

$failedItems = @($results | Where-Object { $_.Status -eq "FAIL" })
$noopItems   = @($results | Where-Object { $_.Status -eq "NOT_APPLIED" })
$okItems	 = @($results | Where-Object { $_.Status -eq "OK" })

$failed = @($failedItems | Select-Object -ExpandProperty File)
$noop   = @($noopItems   | Select-Object -ExpandProperty File)
$ok	 = $okItems.Count

$sizeResults = @($results | Where-Object { $null -ne $_.InputSize -and $null -ne $_.OutputSize })

$totalInputSize  = 0L
$totalOutputSize = 0L
foreach ($item in $sizeResults) {
	$totalInputSize  += [long]$item.InputSize
	$totalOutputSize += [long]$item.OutputSize
}

$totalInputKb  = [math]::Round(([double]$totalInputSize  / 1KB), 2)
$totalOutputKb = [math]::Round(([double]$totalOutputSize / 1KB), 2)
$totalDiffKb   = [math]::Round((([double]$totalOutputSize - [double]$totalInputSize) / 1KB), 2)

$totalDiffText = if ($totalDiffKb -gt 0)  { "+$totalDiffKb KB" }
	elseif ($totalDiffKb -lt 0)		   { "$totalDiffKb KB" }
	else								  { "0 KB" }

$ScriptEndTime = Get-Date
$Duration	  = $ScriptEndTime - $ScriptStartTime
$DurationText  = "{0:hh\:mm\:ss}" -f $Duration

# ── Лог ──
$finalLog = [System.Collections.Generic.List[string]]::new()
$finalLog.Add(("[{0}] START" -f (Get-Date -Format "yyyy-MM-dd HH:mm:ss")))
$finalLog.Add("")

# [5.2] Версия скрипта и окружения
$finalLog.Add("Script=$ScriptVersion")
$finalLog.Add("PowerShell=$($PSVersionTable.PSVersion)")
$finalLog.Add("OS=$([Environment]::OSVersion.VersionString)")
$finalLog.Add("CPU_THREADS=$([Environment]::ProcessorCount)")
$finalLog.Add("")

$finalLog.Add("Папка=$SourceDir")
$finalLog.Add("CLI=$CliPath")
$finalLog.Add("Метод=$CompressionModeDisplay")
$finalLog.Add("Потоков=$MaxParallel")
$finalLog.Add("Рекурсия=$(if ($useRecurse) { 'Да' } else { 'Нет' })")
$finalLog.Add("Файлов=$($files.Count)")
$finalLog.Add("")
$finalLog.Add("SUMMARY")
$finalLog.Add("DURATION=$DurationText")
$finalLog.Add("OK=$ok")
$finalLog.Add("FAIL=$($failed.Count)")
$finalLog.Add("NOT_APPLIED=$($noop.Count)")
$finalLog.Add("TOTAL_INPUT_SIZE_KB=$totalInputKb")
$finalLog.Add("TOTAL_OUTPUT_SIZE_KB=$totalOutputKb")
$finalLog.Add("TOTAL_DIFF_KB=$totalDiffText")

# [5.3] Размер бэкапа
if (Test-Path $BackupDir) {
	$backupSizeBytes = (Get-ChildItem $BackupDir -Recurse -File -ErrorAction SilentlyContinue |
		Measure-Object -Property Length -Sum).Sum
	if ($backupSizeBytes) {
		$backupSizeMb = [math]::Round($backupSizeBytes / 1MB, 2)
		$finalLog.Add("BACKUP_SIZE_MB=$backupSizeMb")
	}
}

foreach ($res in ($results | Sort-Object File)) {
	$finalLog.Add("")
	$finalLog.Add(("===== {0} [{1}] =====" -f $res.File, $res.Status))
	if ($null -ne $res.InputSize -and $null -ne $res.OutputSize) {
		$inputKb  = [math]::Round(([double]$res.InputSize  / 1KB), 2)
		$outputKb = [math]::Round(([double]$res.OutputSize / 1KB), 2)
		$diffKb   = [math]::Round((([double]$res.OutputSize - [double]$res.InputSize) / 1KB), 2)
		$diffText = if ($diffKb -gt 0)  { "+$diffKb" }
			elseif ($diffKb -lt 0)	  { "$diffKb" }
			else						{ "0" }
		$finalLog.Add("SIZE: $inputKb KB -> $outputKb KB ($diffText KB)")
	}
	# [5.1] CLI output сохраняется для всех статусов
	if ($res.LogLines -and @($res.LogLines).Count -gt 0) {
		foreach ($line in $res.LogLines) { $finalLog.Add($line) }
	}
}

$finalLog.Add("")
$finalLog.Add(("[{0}] END" -f (Get-Date -Format "yyyy-MM-dd HH:mm:ss")))
$finalLog | Set-Content -Path $LogFile -Encoding UTF8

# ── Файлы со списком ошибок / пропусков ──
if ($failedItems.Count -gt 0) {
	$failedLines = foreach ($item in $failedItems) {
		$reason = if ($item.LogLines -and @($item.LogLines).Count -gt 0) {
			$item.LogLines[0]
		} else { "FAIL" }
		"{0} | {1}" -f $item.File, $reason
	}
	$failedLines | Set-Content -Path $FailFile -Encoding UTF8
}

if ($noopItems.Count -gt 0) {
	$noopLines = foreach ($item in $noopItems) {
		$reason = if ($item.LogLines -and @($item.LogLines).Count -gt 0) {
			$item.LogLines[0]
		} else { "NOT_APPLIED" }
		"{0} | {1}" -f $item.File, $reason
	}
	$noopLines | Set-Content -Path $NoopFile -Encoding UTF8
}

# ── Очистка пустых папок ──
if (Test-Path $TempDir) {
	$tempItems = Get-ChildItem -Path $TempDir -Force -ErrorAction SilentlyContinue
	if ($null -eq $tempItems -or @($tempItems).Count -eq 0) {
		Remove-Item -Recurse -Force $TempDir -ErrorAction SilentlyContinue
	}
}

if (Test-Path $BackupDir) {
	$backupItems = Get-ChildItem -Path $BackupDir -Force -ErrorAction SilentlyContinue
	if ($null -eq $backupItems -or @($backupItems).Count -eq 0) {
		Remove-Item -Recurse -Force $BackupDir -ErrorAction SilentlyContinue
	}
}

# ══════════════════════════════════════════════════════════════
# [4.3] Цветной вывод результата
# ══════════════════════════════════════════════════════════════

Clear-Host
Show-AppHeader

Show-Section "Результат"
Show-InfoLine "Время" $DurationText
Show-InfoLine "Метод" $CompressionModeDisplay

$okColor   = if ($ok -gt 0)			{ "Green" }	  else { "White" }
$failColor = if ($failed.Count -gt 0)  { "Red" }		else { "White" }
$noopColor = if ($noop.Count -gt 0)	{ "DarkYellow" } else { "White" }

Show-InfoLine "OK"		  $ok			-Color $okColor
Show-InfoLine "FAIL"		$failed.Count  -Color $failColor
Show-InfoLine "NOT APPLIED" $noop.Count	-Color $noopColor

Show-Section "Эффективность:"
Show-InfoLine "Input KB"  $totalInputKb
Show-InfoLine "Output KB" $totalOutputKb
Show-InfoLine "Diff KB"   $totalDiffText

Write-Host ""
Show-InfoLine "Log" $LogFile
if (Test-Path $BackupDir) { Show-InfoLine "Backup"	  $BackupDir }
if (Test-Path $TempDir)   { Show-InfoLine "Temp"		$TempDir }
if ($failed.Count -gt 0)  { Show-InfoLine "Failed list" $FailFile }
if ($noop.Count -gt 0)	{ Show-InfoLine "No-op list"  $NoopFile }

Pause-OnExit -SourceDir $SourceDir -LogPath $LogFile -BackupDir $BackupDir `
	-EndTime $ScriptEndTime -SevenZipPath $SevenZipPath -ArchiveThreads $MaxParallel
