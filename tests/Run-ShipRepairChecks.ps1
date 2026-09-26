param([string]$ProjectRoot = (Join-Path (Split-Path $PSScriptRoot -Parent) 'BottleShips'))
$ErrorActionPreference = 'Stop'
# Compile the complete production feature against controlled game/config/UI boundaries.
$source = [IO.File]::ReadAllText((Join-Path $ProjectRoot 'ShipRepairManager.cs'))
$source = [regex]::Replace($source, '(?m)^using [^\r\n]+;\r?\n', '')
$source = $source.Replace('namespace BottleShips;', 'namespace BottleShipsRepairChecks {')
$source = $source.Replace('UnityEngine.Object', 'UnityObject') + "`n}"
$fixture = [IO.File]::ReadAllText((Join-Path $PSScriptRoot 'ShipRepairChecks.cs'))
Add-Type -TypeDefinition ($fixture.Replace('/* PRODUCTION_SOURCE */', $source))
[BottleShipsRepairChecks.Checks]::Run()
