param([string]$ProjectRoot = (Join-Path (Split-Path $PSScriptRoot -Parent) 'BottleShips'))
$ErrorActionPreference = 'Stop'
$sources = foreach ($file in @('PlayerVehicleMap.cs', 'PlayerVehicleMap.Network.cs')) {
    $source = [IO.File]::ReadAllText((Join-Path $ProjectRoot $file))
    $source = [regex]::Replace($source, '(?m)^using [^\r\n]+;\r?\n', '')
    $source.Replace('namespace BottleShips;', 'namespace BottleShipsVehicleMapChecks {') + "`n}"
}
$fixture = [IO.File]::ReadAllText((Join-Path $PSScriptRoot 'VehicleMapChecks.cs'))
$pluginSource = [IO.File]::ReadAllText((Join-Path $ProjectRoot 'Plugin.cs'))
$category = [regex]::Matches($pluginSource, '(?ms)^        private static string GetConfigurationManagerCategory\(.*?^        \}')
if ($category.Count -ne 1) { throw 'Expected one Configuration Manager category mapping method' }
$fixture = $fixture.Replace('/* CONFIG_CATEGORY */', $category[0].Value)
Add-Type -TypeDefinition ($fixture.Replace('/* PRODUCTION_SOURCES */', ($sources -join "`n")))
[BottleShipsVehicleMapChecks.Checks]::Run()
