Set-StrictMode -Version 2
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.IO.Compression.FileSystem

function Read-ZipCsv($Zip, [string]$Name) {
    $entries = @($Zip.Entries | Where-Object { [IO.Path]::GetFileName($_.FullName) -eq $Name })
    if ($entries.Count -eq 0) { return }
    if ($entries.Count -ne 1) { throw "Duplicate ZIP entry: $Name" }
    $reader = [IO.StreamReader]::new($entries[0].Open(), [Text.Encoding]::UTF8, $true)
    try { $reader.ReadToEnd() | ConvertFrom-Csv } finally { $reader.Dispose() }
}

function Get-GtfsArchivePeriod {
    param([string]$ZipPath, [string]$ServiceDate)
    $date = [datetime]::ParseExact($ServiceDate, 'yyyy-MM-dd', [Globalization.CultureInfo]::InvariantCulture)
    $day = $date.ToString('yyyyMMdd')
    $zip = [IO.Compression.ZipFile]::OpenRead($ZipPath)
    try {
        $info = @(Read-ZipCsv $zip 'feed_info.txt')
        if ($info.Count -ne 1) { throw 'Expected one feed_info row.' }
        $active = @{}
        $weekday = $date.DayOfWeek.ToString().ToLowerInvariant()
        foreach ($calendar in @(Read-ZipCsv $zip 'calendar.txt')) {
            if ($calendar.start_date -le $day -and $calendar.end_date -ge $day -and $calendar.$weekday -eq '1') {
                $active[$calendar.service_id] = $true
            }
        }
        foreach ($exception in @(Read-ZipCsv $zip 'calendar_dates.txt')) {
            if ($exception.date -ne $day) { continue }
            if ($exception.exception_type -eq '1') { $active[$exception.service_id] = $true }
            elseif ($exception.exception_type -eq '2') { $active.Remove($exception.service_id) }
        }
        [pscustomobject]@{
            ValidFrom = $info[0].feed_start_date; ValidUntil = $info[0].feed_end_date
            SupportsDate = ($info[0].feed_start_date -le $day -and $info[0].feed_end_date -ge $day -and $active.Count -gt 0)
            ActiveServices = $active.Count
        }
    } finally { $zip.Dispose() }
}

function Get-WarsawDate {
    [TimeZoneInfo]::ConvertTimeBySystemTimeZoneId([DateTimeOffset]::UtcNow, 'Central European Standard Time').ToString('yyyy-MM-dd')
}

function Save-GzmGtfs {
    param([string]$PackageRoot, [string]$ServiceDate = (Get-WarsawDate))
    [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
    $root = [IO.Path]::GetFullPath($PackageRoot)
    $data = Join-Path $root 'DataRealTime/Slask'
    New-Item -ItemType Directory -Path $data -Force | Out-Null
    $api = 'https://otwartedane.metropoliagzm.pl/api/3/action/package_show?id=rozklady-jazdy-i-lokalizacja-przystankow-gtfs-wersja-rozszerzona'
    Write-Host "Sprawdzanie oficjalnego katalogu GZM dla dnia $ServiceDate..."
    $catalog = Invoke-RestMethod -Uri $api -TimeoutSec 30
    if (-not $catalog.success) { throw 'GZM catalog returned an error.' }
    $stamp = ([datetime]::ParseExact($ServiceDate, 'yyyy-MM-dd', [Globalization.CultureInfo]::InvariantCulture)).ToString('yyyy.MM.dd')
    $resources = @($catalog.result.resources | Where-Object { $_.format -eq 'ZIP' } | Sort-Object `
        @{ Expression = { if ($_.name -like "*$stamp*_0005.zip") { 0 } elseif ($_.name -like "*$stamp*") { 1 } else { 2 } } }, `
        @{ Expression = { $_.created }; Descending = $true } | Select-Object -First 12)
    $work = Join-Path $data ('downloads/' + [guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path $work -Force | Out-Null
    foreach ($resource in $resources) {
        $uri = [uri]$resource.url
        if ($uri.Scheme -ne 'https' -or $uri.Host -ne 'otwartedane.metropoliagzm.pl') { continue }
        $name = [IO.Path]::GetFileName($uri.AbsolutePath)
        $archive = Join-Path $work $name
        Write-Host "Pobieranie $name..."
        Invoke-WebRequest -UseBasicParsing -Uri $uri.AbsoluteUri -OutFile $archive -TimeoutSec 60
        if ((Get-Item -LiteralPath $archive).Length -gt 67108864) { throw 'GTFS archive exceeds 64 MiB.' }
        $period = Get-GtfsArchivePeriod -ZipPath $archive -ServiceDate $ServiceDate
        if (-not $period.SupportsDate) {
            Write-Host "Pominieto: zakres $($period.ValidFrom)-$($period.ValidUntil); brak zgodnego dnia."
            continue
        }
        $relative = 'DataRealTime/Slask/static/gtfs-' + $ServiceDate.Replace('-', '') + '-' + [guid]::NewGuid().ToString('N').Substring(0, 8)
        $target = Join-Path $root $relative
        New-Item -ItemType Directory -Path $target | Out-Null
        $zip = [IO.Compression.ZipFile]::OpenRead($archive)
        try {
            $wanted = @('agency.txt','stops.txt','routes.txt','trips.txt','stop_times.txt','calendar.txt','calendar_dates.txt','feed_info.txt')
            foreach ($file in $wanted) {
                $entries = @($zip.Entries | Where-Object { [IO.Path]::GetFileName($_.FullName) -eq $file })
                if ($entries.Count -gt 1) { throw "Duplicate ZIP entry: $file" }
                if ($entries.Count -eq 0) { continue }
                if ($entries[0].Length -gt 536870912) { throw "GTFS entry too large: $file" }
                [IO.Compression.ZipFileExtensions]::ExtractToFile($entries[0], (Join-Path $target $file), $false)
            }
        } finally { $zip.Dispose() }
        foreach ($file in @('stops.txt','routes.txt','trips.txt','stop_times.txt')) {
            if (-not (Test-Path -LiteralPath (Join-Path $target $file))) { throw "Missing GTFS input: $file" }
        }
        $manifest = [ordered]@{
            ServiceDate = $ServiceDate; GtfsDirectory = $relative; SourceUrl = $uri.AbsoluteUri
            DownloadedAt = [DateTimeOffset]::UtcNow.ToString('O'); Sha256 = (Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash
            Archive = $archive.Substring($root.Length).TrimStart([char[]]'\/')
            ValidFrom = $period.ValidFrom; ValidUntil = $period.ValidUntil; ActiveServices = $period.ActiveServices
        }
        $json = $manifest | ConvertTo-Json -Depth 5
        $encoding = [Text.UTF8Encoding]::new($false)
        [IO.File]::WriteAllText((Join-Path $target 'download-manifest.json'), $json, $encoding)
        $pointer = Join-Path $data 'current-gtfs.json'
        $temp = $pointer + '.' + [guid]::NewGuid().ToString('N') + '.tmp'
        [IO.File]::WriteAllText($temp, $json, $encoding)
        if (Test-Path -LiteralPath $pointer) { [IO.File]::Replace($temp, $pointer, [System.Management.Automation.Language.NullString]::Value) }
        else { [IO.File]::Move($temp, $pointer) }
        Write-Host "Gotowy GTFS: $target (zakres $($period.ValidFrom)-$($period.ValidUntil))."
        return [pscustomobject]$manifest
    }
    throw "Nie znaleziono GTFS obowiazujacego $ServiceDate. Nie uruchomiono pomiaru."
}

Export-ModuleMember -Function Save-GzmGtfs, Get-GtfsArchivePeriod, Get-WarsawDate
