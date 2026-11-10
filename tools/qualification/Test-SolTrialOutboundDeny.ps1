#Requires -Version 7.2
[CmdletBinding()]
param([switch]$Apply, [string]$LoopbackBundle, [switch]$Wfp)

$ErrorActionPreference = 'Stop'
if (-not $IsWindows) { throw 'Windows is required' }

Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public static class LocalBrainTrialNetworkProfile {
    [DllImport("userenv.dll", CharSet=CharSet.Unicode)]
    public static extern int DeriveAppContainerSidFromAppContainerName(string name, out IntPtr sid);
    [DllImport("userenv.dll", CharSet=CharSet.Unicode)]
    public static extern int CreateAppContainerProfile(string name, string display, string description,
        IntPtr caps, uint capCount, out IntPtr sid);
    [DllImport("userenv.dll", CharSet=CharSet.Unicode)]
    public static extern int DeleteAppContainerProfile(string name);
    [DllImport("advapi32.dll")]
    public static extern IntPtr FreeSid(IntPtr sid);
}
'@

$id = [guid]::NewGuid().ToString('N')
$profileName = "localbrain.sol.egress.deny.$id"
$ruleName = "LocalBrain.SolTrial.Deny.$id"
$derived = [IntPtr]::Zero
[Runtime.InteropServices.Marshal]::ThrowExceptionForHR(
    [LocalBrainTrialNetworkProfile]::DeriveAppContainerSidFromAppContainerName($profileName, [ref]$derived))
try { $packageSid = [Security.Principal.SecurityIdentifier]::new($derived).Value }
finally { [void][LocalBrainTrialNetworkProfile]::FreeSid($derived) }
if ($packageSid -notmatch '^S-1-15-2(?:-\d+){7}$') { throw 'Unexpected package SID shape' }

$plan = [ordered]@{
    profile = $profileName
    package_sid = $packageSid
    rule = $ruleName
    direction = 'Outbound'
    action = 'Block'
    protocol = 'Any'
    remote_address = 'Any'
    mode = if ($LoopbackBundle) { 'loopback_tuple_fixture' } else { 'deny_readback' }
    launches_process = [bool]$LoopbackBundle
    changes_profile_defaults = $false
}
if (-not $Apply) {
    $plan.status = 'PREVIEW'
    $plan | ConvertTo-Json -Compress
    return
}

$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
if (-not [Security.Principal.WindowsPrincipal]::new($identity).IsInRole(
    [Security.Principal.WindowsBuiltInRole]::Administrator)) {
    $plan.status = 'BLOCKED_ADMINISTRATOR_REQUIRED'
    $plan | ConvertTo-Json -Compress
    exit 3
}

function Get-ProfileDefaults {
    @(Get-NetFirewallProfile -PolicyStore ActiveStore | Sort-Object Name |
        Select-Object Name, Enabled, DefaultInboundAction, DefaultOutboundAction) | ConvertTo-Json -Compress
}

function Find-TrialRule([string]$Store) {
    Get-NetFirewallRule -PolicyStore $Store |
        Where-Object Name -CEQ $ruleName
}

$before = Get-ProfileDefaults
$profileCreated = $false
$attemptedRule = $false
$plan.status = 'APPLYING'
$plan | ConvertTo-Json -Compress
try {
    $created = [IntPtr]::Zero
    try {
        $hr = [LocalBrainTrialNetworkProfile]::CreateAppContainerProfile(
            $profileName, $profileName, 'LocalBrain outbound deny proof', [IntPtr]::Zero, 0, [ref]$created)
        [Runtime.InteropServices.Marshal]::ThrowExceptionForHR($hr)
        $profileCreated = $true
        if ([Security.Principal.SecurityIdentifier]::new($created).Value -cne $packageSid) {
            throw 'Created package SID differs from plan'
        }
    } finally {
        if ($created -ne [IntPtr]::Zero) { [void][LocalBrainTrialNetworkProfile]::FreeSid($created) }
    }
    if ($LoopbackBundle) {
        & (Join-Path $PSScriptRoot 'Test-SolTrialLoopback.ps1') -Bundle $LoopbackBundle -PackageSid $packageSid -RulePrefix $ruleName -Wfp:$Wfp
        return
    }
    $attemptedRule = $true
    $rule = @{
        PolicyStore = 'PersistentStore'; Name = $ruleName; DisplayName = $ruleName
        Package = $packageSid; Direction = 'Outbound'; Action = 'Block'
        Profile = 'Any'; Protocol = 'Any'; RemoteAddress = 'Any'; Enabled = 'True'
    }
    New-NetFirewallRule @rule | Out-Null
    $active = Get-NetFirewallRule -PolicyStore ActiveStore -Name $ruleName
    $app = $active | Get-NetFirewallApplicationFilter
    $address = $active | Get-NetFirewallAddressFilter
    $port = $active | Get-NetFirewallPortFilter
    if (@($active).Count -ne 1 -or $app.Package -cne $packageSid -or $active.Enabled -ne 'True' -or
        $active.Direction -ne 'Outbound' -or $active.Action -ne 'Block' -or $active.Profile -ne 'Any' -or
        $port.Protocol -ne 'Any' -or @($address.RemoteAddress).Count -ne 1 -or @($address.RemoteAddress)[0] -ne 'Any') {
        throw 'Active rule does not match the exact deny plan'
    }
    $plan.status = 'ACTIVE_RULE_READBACK_MATCHED'
    $plan | ConvertTo-Json -Compress
} finally {
    if ($attemptedRule) {
        $stored = Find-TrialRule 'PersistentStore'
        if ($stored) {
            $storedPackage = ($stored | Get-NetFirewallApplicationFilter).Package
            if (@($stored).Count -ne 1 -or $storedPackage -cne $packageSid) {
                throw 'Rollback stopped because the stored rule identity changed'
            }
            $stored | Remove-NetFirewallRule
        }
        if ((Find-TrialRule 'PersistentStore') -or (Find-TrialRule 'ActiveStore')) {
            throw 'Trial rule still present in active policy after rollback'
        }
    }
    if ($profileCreated) {
        [Runtime.InteropServices.Marshal]::ThrowExceptionForHR(
            [LocalBrainTrialNetworkProfile]::DeleteAppContainerProfile($profileName))
    }
    if ((Get-ProfileDefaults) -cne $before) { throw 'Firewall profile defaults changed during the trial' }
    [pscustomobject]@{ status = 'ROLLED_BACK'; package_sid = $packageSid; rule = $ruleName } |
        ConvertTo-Json -Compress
}
