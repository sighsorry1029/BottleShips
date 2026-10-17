param([string]$ProjectRoot = (Join-Path (Split-Path $PSScriptRoot -Parent) 'BottleShips'))
$ErrorActionPreference = 'Stop'
# Execute the complete optional-compat source against small, controlled boundaries.
# No game or third-party plugin assembly is loaded or patched by this harness.
$source = [IO.File]::ReadAllText((Join-Path $ProjectRoot 'PathOfValheimanCompat.cs'))
$source = [regex]::Replace($source, '(?m)^using [^\r\n]+;\r?\n', '')
$source = $source.Replace('namespace BottleShips;', 'namespace BottleShipsCompatChecks {') + "`n}"
$fixture = [IO.File]::ReadAllText((Join-Path $PSScriptRoot 'PathOfValheimanCompatChecks.cs'))
Add-Type -TypeDefinition ($fixture.Replace('/* PRODUCTION_SOURCE */', $source))
[BottleShipsCompatChecks.Checks]::Run()
