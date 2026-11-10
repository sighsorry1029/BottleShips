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

    # Public API is used only for the active local-player effect. Its implementation
    # does not own or suppress PoV rendering; the isolated Scan branch below does.
    foreach ($api in @(
        (Method 'Valheiman.Api.Pov' 'AtLeast' 'System.Boolean' @('System.Int32','System.Int32')),
        (Method 'Valheiman.Api.Pov' 'Has' 'System.Boolean' @('System.String')),
        (Method 'Valheiman.Api.Stats' 'Has' 'System.Boolean' @('System.String')))) {
        Require ($api.IsPublic -and $api.DeclaringType.IsPublic) "Non-public PoV API: $api"
    }
    $scan = Method 'PassiveTree.MapSight' 'Scan' 'System.Void' @('Minimap','Player','PassiveTree.StatSet')
    $null = Method 'PassiveTree.MapSight' 'AskFar' 'System.Void' @('Player')
    $null = Method 'PassiveTree.MapSight' 'ShipIcon' 'UnityEngine.Sprite' @()
    $null = Method 'PassiveTree.MapSight' 'Show' 'System.Void' @('Minimap','PassiveTree.MapSight/Kind','UnityEngine.Vector3','UnityEngine.Sprite','System.Boolean','System.String','System.String')
    $harbor = @($stat.Fields | Where-Object Name -eq HarborMark)
    Require ($harbor.Count -eq 1) 'HarborMark enum member missing'
    $instructions = $scan.Body.Instructions
    function LoadsInt($instruction, $value) {
        if ($instruction.OpCode.Name -in @('ldc.i4','ldc.i4.s')) { return [int]$instruction.Operand -eq $value }
        if ($instruction.OpCode.Name -match '^ldc\.i4\.([0-8])$') { return [int]$Matches[1] -eq $value }
        return $instruction.OpCode.Name -eq 'ldc.i4.m1' -and $value -eq -1
    }
    $predicates = @(for ($i = 1; $i + 2 -lt $instructions.Count; $i++) {
        if ((LoadsInt $instructions[$i] ([int]$harbor[0].Constant)) -and
            [string]$instructions[$i + 1].Operand -eq [string]$has[0]) { $i }
    })
    Require ($predicates.Count -eq 1) 'HarborMark must have one isolated scan predicate'
    $predicate = $predicates[0]
    Require ($instructions[$predicate - 1].OpCode.Name -eq 'ldarg.2' -and
        $instructions[$predicate + 2].OpCode.Name -in @('brfalse','brfalse.s')) 'HarborMark condition flow changed'
    $start = $predicate + 3
    $end = $instructions.IndexOf($instructions[$predicate + 2].Operand)
    Require ($end -gt $start -and $instructions[$end].OpCode.Name -eq 'ldarg.2' -and
        [string]$instructions[$end + 2].Operand -eq [string]$has[0]) 'HarborMark branch is no longer separate from other map features'
    $branch = @($instructions | Select-Object -Skip $start -First ($end - $start))
    $branchCalls = @($branch | Where-Object { $_.OpCode.Name -in @('call','callvirt') })
    $oldCalls = 'AskFar,GetEnumerator,get_Current,ShipIcon,Concat,Show,MoveNext,Dispose'
    $liveCalls = 'AskFar,GetPlayerID,get_Instances,GetEnumerator,get_Current,op_Inequality,GetComponentInParent,op_Inequality,GetComponent,op_Equality,op_Equality,IsValid,GetCreator,GetZDO,ToString,Add,get_transform,get_position,ShipIcon,Concat,Show,MoveNext,Dispose,GetEnumerator,get_Current,Contains,ShipIcon,Concat,Show,MoveNext,Dispose'
    $callsText = $branchCalls.Operand.Name -join ','
    Require ($callsText -in @($oldCalls, $liveCalls)) 'HarborMark helper calls changed'
    $liveScan = $callsText -eq $liveCalls
    Require (@($branch | Where-Object { $_.OpCode.Name -in @('stfld','stsfld','stobj','cpobj','initobj','calli','ret','throw') -or $_.OpCode.Name -like 'stind*' -or $_.OpCode.Name -like 'stelem*' }).Count -eq 0) 'HarborMark branch now mutates another state or exits Scan'
    $constructors = @($branch | Where-Object { $_.OpCode.Name -eq 'newobj' })
    if ($liveScan) {
        Require ($constructors.Count -eq 1 -and [string]$constructors[0].Operand -eq 'System.Void System.Collections.Generic.HashSet`1<System.String>::.ctor()') 'HarborMark live scan requires only a local ID set'
    } else {
        Require ($constructors.Count -eq 0) 'HarborMark remote scan unexpectedly allocates state'
    }
    $kind = $povTypes | Where-Object FullName -eq 'PassiveTree.MapSight/Kind'
    $shipKind = $kind.Fields | Where-Object Name -eq Ship
    $show = @($branch | Where-Object { $_.Operand -is [Mono.Cecil.MethodReference] -and $_.Operand.DeclaringType.FullName -eq 'PassiveTree.MapSight' -and $_.Operand.Name -eq 'Show' })
    Require ($show.Count -eq $(if ($liveScan) { 2 } else { 1 })) 'HarborMark ship drawing paths changed'
    $showIndex = [Array]::IndexOf($branch, $show[-1])
    Require ($showIndex -ge 11 -and $branch[$showIndex - 11].OpCode.Name -eq 'ldarg.0' -and
        (LoadsInt $branch[$showIndex - 10] ([int]$shipKind.Constant)) -and
        [string]$branch[$showIndex - 8].Operand -eq 'UnityEngine.Vector3 PassiveTree.MapSight/Far::Pos' -and
        $branch[$showIndex - 7].Operand.Name -eq 'ShipIcon' -and (LoadsInt $branch[$showIndex - 6] 0) -and
        $branch[$showIndex - 5].Operand -eq 's:' -and
        [string]$branch[$showIndex - 3].Operand -eq 'System.String PassiveTree.MapSight/Far::Id' -and
        $branch[$showIndex - 1].OpCode.Name -eq 'ldnull') 'HarborMark remote path no longer draws only temporary ship marks'
    if ($liveScan) {
        # Check real metadata and data/control flow, not only a matching call count.
        $liveShow = [Array]::IndexOf($branch, $show[0])
        Require ($liveShow -ge 11 -and $branch[$liveShow - 11].OpCode.Name -eq 'ldarg.0' -and
            (LoadsInt $branch[$liveShow - 10] ([int]$shipKind.Constant)) -and
            [string]$branch[$liveShow - 8].Operand -eq 'UnityEngine.Transform UnityEngine.Component::get_transform()' -and
            [string]$branch[$liveShow - 7].Operand -eq 'UnityEngine.Vector3 UnityEngine.Transform::get_position()' -and
            $branch[$liveShow - 6].Operand.Name -eq 'ShipIcon' -and (LoadsInt $branch[$liveShow - 5] 0) -and
            $branch[$liveShow - 4].Operand -eq 's:' -and $branch[$liveShow - 1].OpCode.Name -eq 'ldnull') 'HarborMark live path no longer draws only temporary ship marks'
        function CallIndex([string]$signature) {
            $found = @(for ($j = 0; $j -lt $branch.Count; $j++) {
                if ($branch[$j].OpCode.Name -in @('call','callvirt') -and [string]$branch[$j].Operand -eq $signature) { $j }
            })
            Require ($found.Count -eq 1) "HarborMark call missing/ambiguous: $signature"
            return $found[0]
        }
        $playerId = CallIndex 'System.Int64 Player::GetPlayerID()'
        $creator = CallIndex 'System.Int64 Piece::GetCreator()'
        $valid = CallIndex 'System.Boolean ZNetView::IsValid()'
        $liveNext = CallIndex 'System.Boolean System.Collections.Generic.List`1/Enumerator<IMonoUpdater>::MoveNext()'
        $farNext = CallIndex 'System.Boolean System.Collections.Generic.List`1/Enumerator<PassiveTree.MapSight/Far>::MoveNext()'
        $contains = CallIndex 'System.Boolean System.Collections.Generic.HashSet`1<System.String>::Contains(!0)'
        $add = CallIndex 'System.Boolean System.Collections.Generic.HashSet`1<System.String>::Add(!0)'
        $null = CallIndex 'System.Collections.Generic.List`1<IMonoUpdater> Ship::get_Instances()'
        $null = CallIndex '!!0 UnityEngine.Component::GetComponentInParent<Piece>()'
        $null = CallIndex '!!0 UnityEngine.Component::GetComponent<ZNetView>()'
        $zdo = CallIndex 'ZDO ZNetView::GetZDO()'
        Require ($branch[$playerId - 1].OpCode.Name -eq 'ldarg.1' -and
            $branch[$playerId + 1].OpCode.Name -like 'stloc*' -and $branch[$creator + 1].OpCode.Name -like 'ldloc*' -and
            $branch[$playerId + 1].Operand -eq $branch[$creator + 1].Operand -and
            $branch[$creator + 2].OpCode.Name -in @('bne.un','bne.un.s') -and
            $branch[$creator + 2].Operand -eq $branch[$liveNext - 1] -and
            $branch[$valid + 1].OpCode.Name -in @('brfalse','brfalse.s') -and
            $branch[$valid + 1].Operand -eq $branch[$liveNext - 1]) 'HarborMark live owner/validity guards changed'
        $setStore = $branch[[Array]::IndexOf($branch, $constructors[0]) + 1]
        Require ($setStore.OpCode.Name -like 'stloc*' -and
            $branch[$add - 2].Operand -eq $setStore.Operand -and $branch[$contains - 3].Operand -eq $setStore.Operand -and
            $branch[$add - 1].Operand -eq $branch[$liveShow - 3].Operand -and
            [string]$branch[$zdo + 1].Operand -eq 'ZDOID ZDO::m_uid' -and $branch[$zdo + 1].OpCode.Name -eq 'ldflda' -and
            $branch[$contains + 1].OpCode.Name -in @('brtrue','brtrue.s') -and
            $branch[$contains + 1].Operand -eq $branch[$farNext - 1]) 'HarborMark live/remote ID deduplication changed'
        $farShip = @($branch | Where-Object { $_.OpCode.Name -eq 'ldfld' -and [string]$_.Operand -eq 'System.Boolean PassiveTree.MapSight/Far::Ship' })
        Require ($farShip.Count -eq 1) 'HarborMark far ship filter missing/ambiguous'
        $farFilter = [Array]::IndexOf($branch, $farShip[0])
        Require ($branch[$farFilter + 1].OpCode.Name -in @('brfalse','brfalse.s') -and
            $branch[$farFilter + 1].Operand -eq $branch[$farNext - 1]) 'HarborMark far filter no longer excludes other kinds'
    }
    # No unrelated branch may enter the skipped body, and both foreach finally
    # regions must be wholly inside it. A jump to the next feature is allowed.
    foreach ($instruction in $instructions) {
        $targets = @()
        if ($instruction.Operand -is [Mono.Cecil.Cil.Instruction]) { $targets = @($instruction.Operand) }
        elseif ($instruction.Operand -is [Mono.Cecil.Cil.Instruction[]]) { $targets = $instruction.Operand }
        $inside = $instructions.IndexOf($instruction) -ge $start -and $instructions.IndexOf($instruction) -lt $end
        foreach ($target in $targets) {
            $targetIndex = $instructions.IndexOf($target)
            if ($inside) { Require ($targetIndex -ge $start -and $targetIndex -le $end) 'HarborMark branch exits to unrelated code' }
            else { Require ($targetIndex -lt $start -or $targetIndex -ge $end) 'Another branch enters HarborMark body' }
        }
    }
    foreach ($handler in $scan.Body.ExceptionHandlers) {
        $first = $instructions.IndexOf($handler.TryStart)
        $last = $instructions.IndexOf($handler.HandlerEnd)
        if ($first -lt $end -and $last -gt $start) {
            Require ($first -ge $start -and $last -le $end -and $handler.HandlerType -eq 'Finally') 'HarborMark exception region crosses its boundary'
        }
    }
    $tail = @($instructions | Select-Object -Skip ($end + 1))
    Require (@($tail | Where-Object { [string]$_.Operand -like '*MapSight::seenNow' }).Count -ge 1) 'PoV stale mark detection missing'
    Require (@($tail | Where-Object { [string]$_.Operand -like '*Minimap::DestroyPinMarker*' }).Count -eq 1) 'PoV stale pin cleanup changed'
    Require (@($tail | Where-Object { [string]$_.Operand -like '*MapSight/Mark*::Remove*' }).Count -eq 1) 'PoV stale mark removal changed'

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
    Write-Output "PoV public API, isolated HarborMark branch/cleanup and packing IL verified; $accesses direct game references checked against original metadata."
    Write-Output "PoV SHA-256: $((Get-FileHash -LiteralPath $PathOfValheimanDll -Algorithm SHA256).Hash)"
} finally {
    $pov.Dispose()
    $mod.Dispose()
    $resolver.Dispose()
}
