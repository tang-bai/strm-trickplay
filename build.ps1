param(
    [string]$Version = '',
    [string]$Configuration = 'Release',
    [string]$Changelog = '',
    [switch]$SkipBuild
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $MyInvocation.MyCommand.Path

# Python writes UTF-8; make the console show it correctly and avoid locale issues.
$env:PYTHONUTF8 = '1'
$env:PYTHONIOENCODING = 'utf-8'
try { [Console]::OutputEncoding = [System.Text.Encoding]::UTF8 } catch { }

# Locate a usable Python 3 (the packaging script needs it).
$python = $null
foreach ($candidate in @('python', 'py')) {
    $command = Get-Command $candidate -ErrorAction SilentlyContinue
    if ($command) { $python = $command.Source; break }
}
if (-not $python) { throw 'Python 3 not found; ci/package.py requires it.' }

if (-not $Version) {
    # Read as UTF-8 explicitly: Windows PowerShell 5.1 defaults to the ANSI code page,
    # which corrupts the non-ASCII text in plugin.json and breaks ConvertFrom-Json.
    $Version = (Get-Content (Join-Path $root 'plugin.json') -Raw -Encoding UTF8 | ConvertFrom-Json).version
}

# Release notes come from CHANGELOG.md (single source of truth, also used by CI).
$changelogScript = Join-Path $root 'ci\changelog.py'
if (-not $Changelog) {
    $Changelog = (& $python $changelogScript --version $Version --format summary 2>$null | Select-Object -First 1)
    if (-not $Changelog) {
        Write-Warning "CHANGELOG.md has no section for $Version - the package will carry no release notes."
    }
}

$full = (& $python $changelogScript --version $Version --format full 2>$null) -join [Environment]::NewLine
if ($full) {
    Write-Host ''
    Write-Host "===== Changelog for $Version ====="
    Write-Host $full
    Write-Host '=================================='
    Write-Host ''
}

$arguments = @((Join-Path $root 'ci\package.py'), '--version', $Version, '--configuration', $Configuration)
if ($Changelog) { $arguments += @('--changelog', $Changelog) }
if ($SkipBuild) { $arguments += '--skip-build' }

& $python @arguments
if ($LASTEXITCODE -ne 0) { throw "Packaging failed (exit code $LASTEXITCODE)." }
