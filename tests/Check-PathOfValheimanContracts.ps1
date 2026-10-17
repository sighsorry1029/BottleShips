param(
    [Parameter(Mandatory = $true)][string]$PathOfValheimanDll,
    [string]$GameManaged = 'C:\Program Files (x86)\Steam\steamapps\common\Valheim\valheim_Data\Managed',
    [string]$BepInExCore = 'C:\Program Files (x86)\Steam\steamapps\common\Valheim\BepInEx\core',
    [string]$ModDll = (Join-Path (Split-Path $PSScriptRoot -Parent) 'BottleShips\bin\Debug\BottleShips.dll')
)
$ErrorActionPreference = 'Stop'
Add-Type -Path (Join-Path $BepInExCore 'Mono.Cecil.dll')
$resolver = [Mono.Cecil.DefaultAssemblyResolver]::new()
foreach ($path in @($GameManaged, $BepInExCore, (Split-Path $ModDll),
    "$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319")) { $resolver.AddSearchDirectory($path) }
$options = [Mono.Cecil.ReaderParameters]::new()
$options.AssemblyResolver = $resolver
$pov = [Mono.Cecil.ModuleDefinition]::ReadModule($PathOfValheimanDll, $options)
$mod = [Mono.Cecil.ModuleDefinition]::ReadModule($ModDll, $options)
function AllTypes($types) { foreach ($type in $types) { $type; AllTypes $type.NestedTypes } }
function Require($condition, $message) { if (!$condition) { throw $message } }
function Method($typeName, $methodName, $result, [string[]]$parameters) {
    $type = $script:povTypes | Where-Object FullName -eq $typeName
    $matches = @($type.Methods | Where-Object {
        $_.Name -eq $methodName -and $_.IsStatic -and $_.HasBody -and
        $_.ReturnType.FullName -eq $result -and
        (($_.Parameters | ForEach-Object { $_.ParameterType.FullName }) -join ',') -eq ($parameters -join ',')
    })
    Require ($matches.Count -eq 1) "Missing/ambiguous PoV method: $typeName.$methodName"
    return $matches[0]
}
try {
    $script:povTypes = @(AllTypes $pov.Types)
    $null = Method 'PassiveTree.Conveniences/Player_Repair_Mass' 'Postfix' 'System.Void' @('Player','ItemDrop/ItemData','Piece')
    $pack = Method 'PassiveTree.ShipCrate' 'Pack' 'System.Boolean' @('Player','Piece','Ship')
    $null = Method 'PassiveTree.ShipCrate/Created' 'Postfix' 'System.Void' @('Piece')
    $crate = $povTypes | Where-Object FullName -eq 'PassiveTree.ShipCrate'
    $placing = @($crate.Fields | Where-Object { $_.Name -eq 'placing' -and $_.IsStatic -and $_.FieldType.FullName -eq 'ItemDrop/ItemData' })
    Require ($placing.Count -eq 1) 'ShipCrate.placing contract changed'
    $progress = $povTypes | Where-Object FullName -eq 'PassiveTree.Progress'
    $current = $progress.Properties | Where-Object Name -eq Current
    Require ($current.GetMethod.IsPublic -and $current.GetMethod.IsStatic -and $current.PropertyType.FullName -eq 'PassiveTree.Progress') 'Progress.Current contract changed'
    $stats = $progress.Fields | Where-Object Name -eq Stats
    Require ($stats.IsPublic -and !$stats.IsStatic -and $stats.FieldType.FullName -eq 'PassiveTree.StatSet') 'Progress.Stats contract changed'
    $set = $povTypes | Where-Object FullName -eq 'PassiveTree.StatSet'
    $has = @($set.Methods | Where-Object {
        $_.Name -eq 'Has' -and $_.IsPublic -and !$_.IsStatic -and $_.ReturnType.FullName -eq 'System.Boolean' -and
        $_.Parameters.Count -eq 1 -and $_.Parameters[0].ParameterType.FullName -eq 'PassiveTree.Stat'
    })
    Require ($has.Count -eq 1) 'StatSet.Has contract changed'
    $stat = $povTypes | Where-Object FullName -eq 'PassiveTree.Stat'
    Require ($stat.IsEnum -and @($stat.Fields | Where-Object Name -eq ShipRepairAnywhere).Count -eq 1) 'ShipRepairAnywhere enum member missing'

    # Match the exact source IL operations used by the production packing transpiler.
    $calls = @{}
    $patterns = @{
        Clone = 'ItemDrop/ItemData ItemDrop/ItemData::Clone()'
        GetZdo = 'ZDO ZNetView::GetZDO()'
        RemoveAll = 'System.Void Inventory::RemoveAll()'
        AddItem = 'System.Boolean Inventory::AddItem(ItemDrop/ItemData)'
    }
    foreach ($key in $patterns.Keys) {
        $indices = @(for ($i = 0; $i -lt $pack.Body.Instructions.Count; $i++) {
            $instruction = $pack.Body.Instructions[$i]
            if ($instruction.OpCode.Name -in @('call','callvirt') -and [string]$instruction.Operand -eq $patterns[$key]) { $i }
        })
        Require ($indices.Count -eq 1) "Pack must contain exactly one $key call"
        $calls[$key] = $indices[0]
    }
    Require ($calls.GetZdo -lt $calls.Clone -and $calls.Clone -lt $calls.RemoveAll -and $calls.RemoveAll -lt $calls.AddItem) 'Pack mutation order changed'
    Require ($pack.Body.Instructions[$calls.Clone + 1].OpCode.Name -like 'stloc*') 'Clone result is no longer stored directly'
    Require (!$pack.Body.HasExceptionHandlers) 'Pack exception regions require another transpiler review'

    Require (@($mod.AssemblyReferences | Where-Object Name -in @('PathOfValheiman','ServerSync','YamlDotNet')).Count -eq 0) 'Hard PoV dependency or unmerged library in final DLL'
    $plugin = $mod.Types | Where-Object FullName -eq 'BottleShips.BottleShipsPlugin'
    $dependency = @($plugin.CustomAttributes | Where-Object {
        $_.AttributeType.Name -eq 'BepInDependency' -and $_.ConstructorArguments[0].Value -eq 'treextr.pathofvalheiman'
    })
    Require ($dependency.Count -eq 1 -and [int]$dependency[0].ConstructorArguments[1].Value -eq 2) 'PoV must be a soft BepInEx dependency'
    $accesses = 0
    foreach ($type in @(AllTypes $mod.Types | Where-Object { $_.FullName -like 'BottleShips.PathOfValheimanCompat*' })) {
        foreach ($method in $type.Methods) {
            if (!$method.HasBody) { continue }
            foreach ($instruction in $method.Body.Instructions) {
                $member = $instruction.Operand
                if ($member -isnot [Mono.Cecil.MethodReference] -and $member -isnot [Mono.Cecil.FieldReference]) { continue }
                if ($member.DeclaringType.Scope.Name -ne 'assembly_valheim') { continue }
                $resolved = $member.Resolve()
                Require ($resolved -and $resolved.IsPublic) "Non-public direct game access: $member"
                $accesses++
            }
        }
    }
    Write-Output "PoV contracts and packing IL verified; $accesses direct game references checked against original metadata."
    Write-Output "PoV SHA-256: $((Get-FileHash -LiteralPath $PathOfValheimanDll -Algorithm SHA256).Hash)"
} finally {
    $pov.Dispose()
    $mod.Dispose()
    $resolver.Dispose()
}
