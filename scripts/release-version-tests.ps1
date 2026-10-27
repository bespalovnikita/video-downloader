#requires -Version 7.0
$ErrorActionPreference = "Stop"
Import-Module (Join-Path $PSScriptRoot "../.github/scripts/ReleaseVersion.psm1") -Force

function Assert-Release {
    param([string]$Expected,[string]$Base,[string[]]$Tags=@(),[string[]]$OnCommit=@())
    $actual = Get-VdNextReleaseTag -BaseVersion $Base -ExistingTags $Tags -HeadTags $OnCommit
    if ($actual -cne $Expected) {
        throw "Wrong release version. Wanted '$Expected', got '$actual' (base=$Base)."
    }
}

# First stable native release is 2.0.0; an old draft v1.0.0 is not touched.
Assert-Release "v2.0.0" "2.0.0"
Assert-Release "v2.0.0" "2.0.0" @("v1.0.0","untagged-123")
Assert-Release "v2.0.1" "2.0.0" @("v1.0.0","v2.0.0")
Assert-Release "v2.0.10" "2.0.0" @("v2.0.2","v2.0.9","beta-999")
Assert-Release "v2.1.0" "2.1.0" @("v2.0.99")
Assert-Release "v2.1.5" "2.1.0" @("v2.1.4")
Assert-Release "v2.0.0" "2.0.0" @("v1.0.0","v2.0.0-rc.1","v2.0.0.1")
Assert-Release "v2.0.3" "2.0.0" @("v2.0.3") @("v2.0.3")
Assert-Release "v2.0.2" "2.0.0" @("v2.0.1","v2.0.2") @("v2.0.2")
Assert-Release "v2.1.0" "2.1.0" @("v2.0.44") @("v2.0.44")

$refused = $false
try { [void](Get-VdNextReleaseTag -BaseVersion "2.0") }
catch { $refused = $true }
if (-not $refused) { throw "Invalid project version must fail closed." }

Write-Host "Automatic main-release version assignment tests passed."
