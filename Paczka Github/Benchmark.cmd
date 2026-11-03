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
function Read-Integer([string]$Prompt, [int]$Default, [int]$Minimum, [int]$Maximum) {
    while ($true) {
        $text = Read-Host "$Prompt [$Default]"
        if ([string]::IsNullOrWhiteSpace($text)) { return $Default }
        $number = 0
        if ([int]::TryParse($text, [ref]$number) -and $number -ge $Minimum -and $number -le $Maximum) { return $number }
        Write-Host "Podaj liczbe calkowita od $Minimum do $Maximum."
    }
}
try {
    if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) { throw 'Zainstaluj .NET 8 SDK i uruchom plik ponownie.' }
    $sdks = @(& dotnet --list-sdks)
    if ($LASTEXITCODE -ne 0 -or -not ($sdks | Where-Object { $_ -match '^8\.' })) { throw 'Do uruchomienia potrzebny jest .NET 8 SDK.' }
    Push-Location -LiteralPath $root
    $pushed = $true
    $project = Join-Path $root 'AlgorithmComparison.CLI/AlgorithmComparison.CLI.csproj'
    if (-not (Test-Path -LiteralPath $project)) { throw 'Zachowaj folder Paczka Github wewnatrz pobranego repozytorium.' }
    if (-not (Test-Path -LiteralPath (Join-Path $root 'Data'))) { throw 'Brak folderu Data. Pobierz dane repozytorium przez git lfs pull.' }
    & dotnet build $project -c Release --nologo --verbosity minimal
    if ($LASTEXITCODE -ne 0) { throw 'Nie udalo sie zbudowac aplikacji.' }
    $count = Read-Integer 'Liczba zapytan na zbior' 10 1 2147483647
    while ($true) {
        $text = Read-Host 'Promien pojedynczego przejscia pieszego w metrach [500]'
        $radius = 500.0
        if ([string]::IsNullOrWhiteSpace($text)) { break }
        if ([double]::TryParse($text.Replace(',', '.'), [Globalization.NumberStyles]::Float,
            [Globalization.CultureInfo]::InvariantCulture, [ref]$radius) -and $radius -ge 0 -and
            -not [double]::IsNaN($radius) -and -not [double]::IsInfinity($radius)) { break }
        Write-Host 'Podaj nieujemna liczbe.'
    }
    $transfers = Read-Integer 'Maksymalna liczba przesiadek' 7 0 2147483645
    $dll = Join-Path $root 'AlgorithmComparison.CLI/bin/Release/net8.0/AlgorithmComparison.CLI.dll'
    & dotnet $dll benchmark-menu $count --radius-metres $radius.ToString([Globalization.CultureInfo]::InvariantCulture) --max-transfers $transfers
    $code = $LASTEXITCODE
} catch { Write-Host $_.Exception.Message -ForegroundColor Red }
finally { if ($pushed) { Pop-Location } }
Read-Host 'Enter zamyka okno' | Out-Null
exit $code
