# Downloads the datasets and the native libzstd used by the experiments.
# Everything is verified by SHA-256 so results are comparable byte-for-byte.
#
# Requirements: PowerShell 7+, curl, and xz (ships with Git for Windows) or 7-Zip.

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot

function Assert-Hash([string]$Path, [string]$Expected) {
    $actual = (Get-FileHash $Path -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($actual -ne $Expected) {
        throw "SHA-256 mismatch for $Path`n  expected $Expected`n  actual   $actual"
    }
    Write-Host "  OK $([IO.Path]::GetFileName($Path)) ($Expected)"
}

# --- 1. libzstd 1.5.7 (official facebook/zstd release binary) -------------------------------
$dll = Join-Path $root 'libzstd.dll'
if (-not (Test-Path $dll)) {
    Write-Host 'Downloading zstd v1.5.7 win64 release...'
    $zip = Join-Path $env:TEMP 'zstd-v1.5.7-win64.zip'
    curl.exe -sL -o $zip 'https://github.com/facebook/zstd/releases/download/v1.5.7/zstd-v1.5.7-win64.zip'
    Expand-Archive $zip -DestinationPath (Join-Path $env:TEMP 'zstd-v1.5.7') -Force
    Copy-Item (Join-Path $env:TEMP 'zstd-v1.5.7/zstd-v1.5.7-win64/dll/libzstd.dll') $dll
}
Assert-Hash $dll '8f07e1112ed283e5cd2798833e9a3c32d8961381bc36da04af57a1b0ca9bd40b'

# --- 2. Linux kernel tars (the public dataset; ~290 MB download, ~3 GB on disk) -------------
$data = Join-Path $root 'data'
New-Item -ItemType Directory -Force $data | Out-Null

$tars = @(
    @{ Name = 'linux-6.12.92.tar'; Bytes = 1548513280; Sha = '6357d098952cad2d4fb5179da50f3cfe883085fec50fdcaf169f2415b7e01b63' }
    @{ Name = 'linux-6.12.93.tar'; Bytes = 1548564480; Sha = '5c9cd2d34b009dd237489906cd2beba122aa2a17680048fb3746817d06d5c345' }
)

foreach ($t in $tars) {
    $tar = Join-Path $data $t.Name
    if (-not (Test-Path $tar)) {
        $xzName = "$($t.Name).xz"
        $xzPath = Join-Path $data $xzName
        Write-Host "Downloading $xzName from cdn.kernel.org..."
        curl.exe -sL -o $xzPath "https://cdn.kernel.org/pub/linux/kernel/v6.x/$xzName"
        Write-Host "Decompressing $xzName..."
        $xz = Get-Command xz -ErrorAction SilentlyContinue
        if ($xz) {
            & $xz.Source -d -T0 $xzPath
        } else {
            $7z = Get-Command 7z -ErrorAction SilentlyContinue
            if (-not $7z) { throw 'Neither xz nor 7z found. Install Git for Windows (ships xz) or 7-Zip.' }
            & $7z.Source x $xzPath "-o$data" -y | Out-Null
            Remove-Item $xzPath
        }
    }
    if ((Get-Item $tar).Length -ne $t.Bytes) { throw "$($t.Name): unexpected size $((Get-Item $tar).Length), expected $($t.Bytes)" }
    Assert-Hash $tar $t.Sha
}

Write-Host ''
Write-Host 'Setup complete. Build and run, e.g.:'
Write-Host '  dotnet build Probe/Probe.csproj -c Release'
Write-Host '  ./Probe/bin/Release/net11.0/Probe.exe matrix 3,15,19 32,96,160'
