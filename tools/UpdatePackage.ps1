# Builds the small Windows update package (frissítőcsomag) of a release: only the program's own files.
#
#   UpdatePackage.ps1 -New out\TruckScale -Version 1.2.3 -Previous TruckScale-1.2.2-win-x64.zip -PreviousVersion 1.2.2
#
# The new build is compared with the previous release's full package. If only the program's own files changed
# (TruckScale*.dll/exe, forms, docs), the package gets ALL own files, so it can be unpacked over the previous
# version. If anything else changed (.NET runtime, a library), no package is made: the full download is needed.
# Writes "note" (one line for the release text) to $env:GITHUB_OUTPUT when run in GitHub Actions.

param(
    [Parameter(Mandatory)] [string] $New,
    [Parameter(Mandatory)] [string] $Version,
    [string] $Previous,
    [string] $PreviousVersion,
    [string] $OutDir = "."
)
$ErrorActionPreference = "Stop"

# The program's own files (paths relative to the program folder, with "/").
$own = @(
    '^TruckScale\.(exe|dll|pdb|deps\.json|runtimeconfig\.json)$'
    '^TruckScale\.Core\.(dll|pdb)$'
    '^(README|FELHASZNALOI_UTMUTATO)\.md$'
    '^forms/[^/]+\.form$'
    '^import/TruckScale\.(Import|Core)\.(exe|dll|pdb|deps\.json|runtimeconfig\.json)$'
)
function IsOwn([string] $rel) { foreach ($p in $own) { if ($rel -match $p) { return $true } }; return $false }

function Hashes([string] $dir) {
    $root = (Resolve-Path $dir).Path.TrimEnd('\', '/')
    $h = @{}
    Get-ChildItem $root -Recurse -File | ForEach-Object {
        $h[$_.FullName.Substring($root.Length + 1).Replace('\', '/')] = (Get-FileHash $_.FullName -Algorithm SHA256).Hash
    }
    return $h
}

function Result([string] $note, [string] $zip) {
    Write-Host $note
    if ($env:GITHUB_OUTPUT) { "note=$note" | Out-File $env:GITHUB_OUTPUT -Append -Encoding utf8 }
    return $zip
}

if (-not $Previous -or -not (Test-Path $Previous)) {
    return Result "Ehhez a verzióhoz nincs frissítőcsomag (nincs korábbi kiadás): a teljes csomagot kell letölteni." $null
}

$work = Join-Path ([IO.Path]::GetTempPath()) ("ts-update-" + [guid]::NewGuid().ToString("N"))
try {
    Expand-Archive $Previous (Join-Path $work "prev")
    $old = Hashes (Join-Path $work "prev")
    $cur = Hashes $New

    $foreign = @()
    foreach ($k in $cur.Keys) { if (-not (IsOwn $k) -and $old[$k] -ne $cur[$k]) { $foreign += $k } }
    foreach ($k in $old.Keys) { if (-not (IsOwn $k) -and -not $cur.ContainsKey($k)) { $foreign += $k } }
    if ($foreign.Count -gt 0) {
        Write-Host "Changed files that are not the program's own ($($foreign.Count)):"
        $foreign | Sort-Object | Select-Object -First 20 | ForEach-Object { Write-Host "  $_" }
        return Result "Ehhez a verzióhoz nincs frissítőcsomag (változott a .NET vagy egy külső könyvtár): a teljes csomagot kell letölteni." $null
    }

    $stage = Join-Path $work "stage"
    $root = (Resolve-Path $New).Path
    $files = $cur.Keys | Where-Object { IsOwn $_ } | Sort-Object
    foreach ($rel in $files) {
        $dst = Join-Path $stage $rel
        New-Item -ItemType Directory -Force (Split-Path $dst) | Out-Null
        Copy-Item (Join-Path $root $rel) $dst
    }
    @"
Truck Scale $Version - frissítőcsomag

A(z) $PreviousVersion verzióra telepíthető (csak a program saját fájljai vannak benne).

1. Zárd be a programot.
2. Csomagold ki ezt a zipet a program mappájába, és írd felül a meglévő fájlokat.
3. Indítsd el a programot.

Az adatokat (truckscale.db) és a beállításokat a csomag nem érinti.
Ha a program nem indul, töltsd le a teljes csomagot (TruckScale-$Version-win-x64.zip).
"@ | Out-File (Join-Path $stage "FRISSITES.txt") -Encoding utf8

    New-Item -ItemType Directory -Force $OutDir | Out-Null
    $zip = Join-Path (Resolve-Path $OutDir).Path "TruckScale-$Version-win-x64-frissites.zip"
    if (Test-Path $zip) { Remove-Item $zip }
    Compress-Archive -Path (Join-Path $stage "*") -DestinationPath $zip
    $mb = "{0:0.0}" -f ((Get-Item $zip).Length / 1MB)
    Write-Host "$($files.Count) files -> $zip ($mb MB)"
    return Result "**Frissítés a(z) $PreviousVersion verzióról:** elég a kis ``TruckScale-$Version-win-x64-frissites.zip`` ($mb MB). Zárd be a programot, csomagold ki a program mappájába (felülírva a fájlokat), majd indítsd el." $zip
}
finally {
    Remove-Item $work -Recurse -Force -ErrorAction SilentlyContinue
}
