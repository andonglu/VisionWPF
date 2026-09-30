param(
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Debug",
    [switch]$NoBuild
)

$ErrorActionPreference = "Stop"
$repoRoot = Split-Path -Parent $PSScriptRoot
if (-not $NoBuild) {
    & dotnet build (Join-Path $PSScriptRoot "VisionFlow.Tests.csproj") --configuration $Configuration --no-restore --verbosity quiet
    if ($LASTEXITCODE -ne 0) {
        throw "Tests build failed: $LASTEXITCODE"
    }
}

# Run in a fresh pwsh process. Only this process loses native HALCON resolution.
$output = Join-Path $repoRoot "VisionFlow.Tests\bin\$Configuration\net9.0"
$halcon = [System.Reflection.Assembly]::LoadFrom((Join-Path $output "halcondotnet.dll"))
Add-Type -TypeDefinition @'
using System;
using System.Reflection;
using System.Runtime.InteropServices;

public static class MissingHalconRuntimeProbe
{
    public static void Install(Assembly assembly)
    {
        NativeLibrary.SetDllImportResolver(assembly, (name, owner, path) =>
            throw new DllNotFoundException("VF11 simulated missing native HALCON runtime"));
    }
}
'@
[MissingHalconRuntimeProbe]::Install($halcon)
$tests = [System.Reflection.Assembly]::LoadFrom((Join-Path $output "VisionFlow.Tests.dll"))
$cases = @(
    @("HalconRuntimeAvailabilityTests", "OwnedWrapper_Dispose_ReleasesNativeObject"),
    @("RealImageBaselineTests", "区域处理示例_结果符合基线"),
    @("RealImageBaselineTests", "XLD直线示例_结果符合基线"),
    @("HalconResourceLifecycleTests", "上下文释放_拥有对象真正回收_借用输入保留")
)
foreach ($case in $cases) {
    $type = $tests.GetType("VisionFlow.Tests." + $case[0], $true)
    $instance = [System.Activator]::CreateInstance($type)
    $failure = $null
    try {
        $type.GetMethod($case[1]).Invoke($instance, @()) | Out-Null
    }
    catch [System.Management.Automation.MethodInvocationException] {
        $failure = $_.Exception.GetBaseException()
    }
    if ($null -eq $failure -or
        $failure.GetType().FullName -ne "Xunit.Sdk.TrueException" -or
        -not $failure.Message.Contains("VF11 simulated missing native HALCON runtime")) {
        throw "Expected a runtime-gate assertion, not a pass or another error: $($case -join '.') -- $failure"
    }
    Write-Output "$($case -join '.'): missing runtime correctly fails"
}
