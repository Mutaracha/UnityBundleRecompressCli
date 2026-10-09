#requires -Version 7.0
<#
.SYNOPSIS
  Собирает поставочный архив BundleRecompressCli в структуре:

    Unity bundle recompress launcher.cmd
    bin\
      batch_lzma_cli_pwsh.ps1
      BundleRecompressCli.exe           (Bootstrap)
      BundleRecompressCli.Worker.exe    (рабочий процесс)
      AssetsTools.NET.dll
      System.Text.Json.dll и зависимости
      7za.exe                           (опционально, если лежит в шаблоне)

.PARAMETER WorkerOutDir
  Каталог сборки Worker (bin\Release\net48 в корне репозитория).

.PARAMETER BootstrapOutDir
  Каталог сборки Bootstrap (Bootstrap\bin\Release\net48).

.PARAMETER TemplateDir
  Шаблон поставки (release\ в репозитории): launcher.cmd, bin\batch_lzma_cli_pwsh.ps1, опционально bin\7za.exe.

.PARAMETER StagingDir
  Временный каталог, в котором собирается структура архива.

.PARAMETER ZipPath
  Путь к итоговому zip-архиву.
#>
param(
    [Parameter(Mandatory)] [string]$WorkerOutDir,
    [Parameter(Mandatory)] [string]$BootstrapOutDir,
    [Parameter(Mandatory)] [string]$TemplateDir,
    [Parameter(Mandatory)] [string]$StagingDir,
    [Parameter(Mandatory)] [string]$ZipPath
)

$ErrorActionPreference = 'Stop'

function Copy-Required {
    param([string]$Source, [string]$Destination)
    if (-not (Test-Path -LiteralPath $Source -PathType Leaf)) {
        throw "Не найден файл для пакета: $Source"
    }
    $dir = Split-Path -Parent $Destination
    if (-not (Test-Path -LiteralPath $dir)) { New-Item -ItemType Directory -Force -Path $dir | Out-Null }
    Copy-Item -LiteralPath $Source -Destination $Destination -Force
}

# 1. Чистый staging
if (Test-Path -LiteralPath $StagingDir) { Remove-Item -LiteralPath $StagingDir -Recurse -Force }
New-Item -ItemType Directory -Force -Path $StagingDir | Out-Null

# 2. Шаблон (launcher.cmd в корне, bin\ со скриптом и, при наличии, 7za.exe)
Copy-Item -Path (Join-Path $TemplateDir '*') -Destination $StagingDir -Recurse -Force

$bin = Join-Path $StagingDir 'bin'
New-Item -ItemType Directory -Force -Path $bin | Out-Null

# 3. Исполняемые файлы
Copy-Required (Join-Path $BootstrapOutDir 'BundleRecompressCli.exe')        (Join-Path $bin 'BundleRecompressCli.exe')
Copy-Required (Join-Path $WorkerOutDir    'BundleRecompressCli.Worker.exe') (Join-Path $bin 'BundleRecompressCli.Worker.exe')

# 4. Зависимости: AssetsTools.NET и все runtime-DLL из сборок (System.Text.Json и его зависимости).
#    Worker — приоритетнее (там же AssetsTools.NET); Bootstrap добирает недостающие.
foreach ($dir in @($WorkerOutDir, $BootstrapOutDir)) {
    Get-ChildItem -LiteralPath $dir -Filter *.dll -File | ForEach-Object {
        $target = Join-Path $bin $_.Name
        if (-not (Test-Path -LiteralPath $target)) { Copy-Item -LiteralPath $_.FullName -Destination $target }
    }
}

# 5. Проверка состава пакета
$required = @(
    'Unity bundle recompress launcher.cmd',
    'bin\batch_lzma_cli_pwsh.ps1',
    'bin\BundleRecompressCli.exe',
    'bin\BundleRecompressCli.Worker.exe',
    'bin\AssetsTools.NET.dll',
    'bin\System.Text.Json.dll'
)
$missing = @($required | Where-Object { -not (Test-Path -LiteralPath (Join-Path $StagingDir $_)) })
if ($missing.Count -gt 0) {
    throw "В пакете отсутствуют обязательные файлы:`n  " + ($missing -join "`n  ")
}

$optional = Join-Path $bin '7za.exe'
Write-Host ("7za.exe: " + $(if (Test-Path $optional) { 'включён' } else { 'не включён (опционально)' }))

# 6. Архив: содержимое staging в корне zip
if (Test-Path -LiteralPath $ZipPath) { Remove-Item -LiteralPath $ZipPath -Force }
Compress-Archive -Path (Join-Path $StagingDir '*') -DestinationPath $ZipPath -Force

Write-Host "Пакет собран: $ZipPath"
Get-ChildItem -LiteralPath $StagingDir -Recurse -File |
    ForEach-Object { '  ' + $_.FullName.Substring($StagingDir.Length).TrimStart('\', '/') } |
    Write-Host
