@echo off
setlocal DisableDelayedExpansion
set "ROUTING_LAUNCHER_PATH=%~f0"
powershell.exe -NoLogo -NoProfile -ExecutionPolicy Bypass -Command "$lines = [IO.File]::ReadAllLines($env:ROUTING_LAUNCHER_PATH); $start = [Array]::IndexOf($lines, ':powershell'); & ([ScriptBlock]::Create(($lines[($start + 1)..($lines.Length - 1)] -join [Environment]::NewLine)))"
exit /b %ERRORLEVEL%
:powershell
$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath((Join-Path ([IO.Path]::GetDirectoryName($env:ROUTING_LAUNCHER_PATH)) '..'))
$code = 1
$pushed = $false
try {
    if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) { throw 'Zainstaluj .NET 8 SDK i uruchom plik ponownie.' }
    $sdks = @(& dotnet --list-sdks)
    if ($LASTEXITCODE -ne 0 -or -not ($sdks | Where-Object { $_ -match '^8\.' })) { throw 'Do uruchomienia potrzebny jest .NET 8 SDK.' }
    Push-Location -LiteralPath $root
    $pushed = $true
    $project = Join-Path $root 'AlgorithmComparison.CLI/AlgorithmComparison.CLI.csproj'
    if (-not (Test-Path -LiteralPath $project)) { throw 'Zachowaj folder Run wewnatrz pobranego repozytorium.' }
    & dotnet build $project -c Release --nologo --verbosity minimal
    if ($LASTEXITCODE -ne 0) { throw 'Nie udalo sie zbudowac aplikacji.' }
    $dll = Join-Path $root 'AlgorithmComparison.CLI/bin/Release/net8.0/AlgorithmComparison.CLI.dll'
    & dotnet $dll raptor-realtime-menu
    $code = $LASTEXITCODE
} catch { Write-Host $_.Exception.Message -ForegroundColor Red }
finally { if ($pushed) { Pop-Location } }
Read-Host 'Enter zamyka okno' | Out-Null
exit $code
