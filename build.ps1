param(
    [string]$Version = '',
    [string]$Configuration = 'Release',
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
    $Version = (Get-Content (Join-Path $root 'plugin.json') -Raw | ConvertFrom-Json).version
}

$arguments = @((Join-Path $root 'ci\package.py'), '--version', $Version, '--configuration', $Configuration)
if ($SkipBuild) { $arguments += '--skip-build' }

& $python @arguments
if ($LASTEXITCODE -ne 0) { throw "Packaging failed (exit code $LASTEXITCODE)." }
