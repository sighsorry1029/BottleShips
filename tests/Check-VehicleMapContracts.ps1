param(
    [Parameter(Mandatory = $true)][string]$GameManaged,
    [Parameter(Mandatory = $true)][string]$BepInExCore,
    [Parameter(Mandatory = $true)][string]$CecilDll,
    [string]$ModDll = (Join-Path (Split-Path $PSScriptRoot -Parent) 'BottleShips\bin\Debug\BottleShips.dll'),
    [string]$PortalRulesDll
)
$ErrorActionPreference = 'Stop'
Add-Type -Path $CecilDll
$resolver = [Mono.Cecil.DefaultAssemblyResolver]::new()
foreach ($directory in @($GameManaged, $BepInExCore, (Split-Path $ModDll),
    "$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319")) { $resolver.AddSearchDirectory($directory) }
$parameters = [Mono.Cecil.ReaderParameters]::new()
$parameters.AssemblyResolver = $resolver
$module = [Mono.Cecil.ModuleDefinition]::ReadModule($ModDll, $parameters)
function Get-Types($types) { foreach ($type in $types) { $type; Get-Types $type.NestedTypes } }
try {
    $types = @(Get-Types $module.Types | Where-Object FullName -Like 'BottleShips.PlayerVehicleMap*')
    if (!$types.Count) { throw 'Vehicle map feature is missing from the merged DLL' }
    if (@($module.AssemblyReferences | Where-Object Name -in @('PortalRules', 'ServerSync', 'YamlDotNet')).Count) {
        throw 'Unexpected PortalRules dependency or unmerged runtime library'
    }
    $references = 0
    $patches = 0
    foreach ($type in $types) {
        foreach ($method in $type.Methods) {
            if (!$method.HasBody) { continue }
            foreach ($instruction in $method.Body.Instructions) {
                $operand = $instruction.Operand
                if ($operand -isnot [Mono.Cecil.MemberReference]) { continue }
                if ($operand.FullName -match 'PortalRules|ShipTweaksManager') {
                    throw "Unexpected dependency in vehicle map: $operand"
                }
                if ($operand -isnot [Mono.Cecil.MethodReference] -and $operand -isnot [Mono.Cecil.FieldReference]) { continue }
                if ($operand.DeclaringType.Scope.Name -notin @('assembly_valheim', 'assembly_utils', 'assembly_guiutils', 'Splatform')) { continue }
                $resolved = $operand.Resolve()
                if (!$resolved -or !$resolved.IsPublic) { throw "Missing/non-public direct game access: $operand" }
                $references++
                if ($operand.DeclaringType.Name -eq 'ZDO' -and $operand.Name -in @('Set', 'SetOwner')) {
                    throw "Map feature mutates ownership/world data: $operand"
                }
            }
        }
        foreach ($attribute in @($type.CustomAttributes | Where-Object { $_.AttributeType.FullName -eq 'HarmonyLib.HarmonyPatch' })) {
            $targetType = $attribute.ConstructorArguments[0].Value.Resolve()
            $targetName = [string]$attribute.ConstructorArguments[1].Value
            $targets = @($targetType.Methods | Where-Object Name -eq $targetName)
            if ($targets.Count -ne 1) { throw "Missing or ambiguous Harmony target: $($targetType.Name).$targetName" }
            foreach ($patch in @($type.Methods | Where-Object Name -in @('Prefix', 'Postfix'))) {
                foreach ($parameter in $patch.Parameters) {
                    if ($parameter.Name -eq '__instance') {
                        if ($parameter.ParameterType.FullName -ne $targetType.FullName) { throw "Incorrect __instance for $targetName" }
                    } else {
                        $match = @($targets[0].Parameters | Where-Object {
                            $_.Name -eq $parameter.Name -and $_.ParameterType.FullName -eq $parameter.ParameterType.FullName
                        })
                        if ($match.Count -ne 1) { throw "Incorrect patch argument: $targetName / $($parameter.Name)" }
                    }
                }
            }
            $patches++
        }
    }
    if ($patches -ne 6) { throw "Expected six vehicle lifecycle patches, found $patches" }
    $gameReference = $module.AssemblyReferences | Where-Object Name -eq 'assembly_valheim'
    $game = $resolver.Resolve($gameReference).MainModule
    $controller = $game.Types | Where-Object Name -eq 'PlayerController'
    $takeInput = @($controller.Methods | Where-Object {
        $_.Name -eq 'TakeInput' -and $_.ReturnType.FullName -eq 'System.Boolean' -and
        $_.Parameters.Count -eq 1 -and $_.Parameters[0].ParameterType.FullName -eq 'System.Boolean'
    })
    if ($takeInput.Count -ne 1 -or !$takeInput[0].IsPrivate) { throw 'Original private TakeInput(bool) contract changed' }
    foreach ($language in @('English', 'Korean')) {
        $resource = @($module.Resources | Where-Object Name -Like "*.translations.$language.yml")
        if ($resource.Count -ne 1) { throw "Missing embedded translation: $language" }
        $text = [Text.Encoding]::UTF8.GetString($resource[0].GetResourceData())
        foreach ($key in @('hint_show_my_vehicles', 'hint_hide_my_vehicles', 'vehicle_map_unavailable',
            'vehicle_map_truncated', 'key_shift', 'key_control', 'key_alt')) {
            if ($text -notmatch "(?m)^sighsorry_bottleships_${key}:") { throw "Missing $language translation: $key" }
        }
    }
    if ($PortalRulesDll) {
        $portal = [Mono.Cecil.ModuleDefinition]::ReadModule($PortalRulesDll)
        try {
            if (@(Get-Types $portal.Types | Where-Object FullName -Like '*PlayerVehicleMap*').Count) {
                throw 'PortalRules still contains the vehicle map, causing duplicate execution'
            }
            if (@($portal.AssemblyReferences | Where-Object Name -eq 'BottleShips').Count) {
                throw 'PortalRules must not depend on BottleShips'
            }
        } finally { $portal.Dispose() }
    }
    [pscustomobject]@{
        PublicGameMemberInstructionsChecked = $references
        HarmonyLifecycleTargetsChecked = $patches
        PrivateInputAccessorChecked = $true
        EmbeddedLanguagesChecked = 2
        PortalRulesDuplicationChecked = [bool]$PortalRulesDll
        Note = 'Static metadata checks against original game DLLs; does not execute Harmony, Unity or multiplayer.'
    } | ConvertTo-Json
} finally { $module.Dispose(); $resolver.Dispose() }
