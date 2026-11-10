#Requires -Version 7.2
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Bundle,
    [Parameter(Mandatory)][ValidatePattern('^S-1-15-2(?:-\d+){7}$')][string]$PackageSid,
    [Parameter(Mandatory)][ValidatePattern('^LocalBrain\.SolTrial\.Deny\.[0-9a-f]{32}$')][string]$RulePrefix,
    [switch]$Wfp,
    [switch]$DurableWfp
)

$ErrorActionPreference = 'Stop'
$profile = 'localbrain.sol.egress.deny.' + $RulePrefix.Split('.')[-1]
$derived = [IntPtr]::Zero
[Runtime.InteropServices.Marshal]::ThrowExceptionForHR(
    [LocalBrainTrialNetworkProfile]::DeriveAppContainerSidFromAppContainerName($profile,[ref]$derived))
try {
    if ([Security.Principal.SecurityIdentifier]::new($derived).Value -cne $PackageSid) {
        throw 'Profile-derived SID mismatch'
    }
} finally { [void][LocalBrainTrialNetworkProfile]::FreeSid($derived) }
$bundleItem = Get-Item -LiteralPath $Bundle
if ($bundleItem.Name -ne 'bundle' -or $bundleItem.Parent.Name -notmatch '^sol-loopback-[0-9a-f]{8}$') {
    throw 'Expected a fresh disposable Sol loopback fixture bundle'
}
$exe = Join-Path $bundleItem.FullName 'LocalBrain.ClientHost.Tests.exe'
if (-not (Test-Path -LiteralPath $exe -PathType Leaf)) { throw 'Missing qualification executable' }

# These listeners accept only synthetic local connection attempts. They do not proxy traffic.
$allowed = [Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback,0)
$other4 = [Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback,0)
$other6 = [Net.Sockets.TcpListener]::new([Net.IPAddress]::IPv6Loopback,0)
$alias4 = $null
$udp = $null
$names = [Collections.Generic.List[string]]::new()
$netIsolation = Join-Path $env:SystemRoot 'System32/CheckNetIsolation.exe'
$exemptionAttempted = $false
$wfpSession = $null
$lease = $null
$leaseFile = Join-Path $bundleItem.Parent.FullName 'wfp-lease.json'
function Format-Prefix([string]$Value) {
    $parts = $Value.Split('/')
    $ip = [Net.IPAddress]::Parse($parts[0])
    $width = $ip.GetAddressBytes().Length * 8
    $length = $width
    if ($parts.Length -eq 2) {
        if ($parts[1].Contains('.')) {
            $bits = -join ([Net.IPAddress]::Parse($parts[1]).GetAddressBytes() |
                ForEach-Object { [Convert]::ToString($_,2).PadLeft(8,'0') })
            if ($bits -notmatch '^1*0*$') { throw 'Noncontiguous active IPv4 netmask' }
            $length = $bits.TrimEnd('0').Length
        } else { $length = [int]$parts[1] }
    }
    [Net.IPNetwork]::new($ip,$length).ToString()
}

try {
    $allowed.Start()
    $other4.Start()
    $other6.Start()
    $port = $allowed.LocalEndpoint.Port
    $alias4 = [Net.Sockets.TcpListener]::new([Net.IPAddress]::Parse('127.0.0.2'),$port)
    $alias4.Start()
    $udp = [Net.Sockets.UdpClient]::new([Net.IPEndPoint]::new([Net.IPAddress]::Loopback,$port))
    $common = @{
        PolicyStore='PersistentStore'; Package=$PackageSid; Direction='Outbound'
        Action='Block'; Profile='Any'; Enabled='True'
    }
    function Add-Block([string]$Suffix,[hashtable]$Conditions) {
        $name = "$RulePrefix.$Suffix"
        $names.Add($name)
        try { New-NetFirewallRule @common -Name $name -DisplayName $name @Conditions | Out-Null }
        catch { throw "Creating $Suffix failed: $($_.Exception.Message)" }
        $rule = Get-NetFirewallRule -PolicyStore ActiveStore -Name $name
        $address = $rule | Get-NetFirewallAddressFilter
        $transport = $rule | Get-NetFirewallPortFilter
        $plannedAddress = @($Conditions.RemoteAddress | ForEach-Object { Format-Prefix $_ })
        $activeAddress = @($address.RemoteAddress | ForEach-Object { Format-Prefix $_ })
        if (($rule | Get-NetFirewallApplicationFilter).Package -cne $PackageSid -or
            $rule.Action -ne 'Block' -or $rule.Direction -ne 'Outbound' -or $rule.Enabled -ne 'True' -or
            $rule.Profile -ne 'Any' -or $transport.Protocol -ne $Conditions.Protocol -or
            (Compare-Object $plannedAddress $activeAddress) -or
            ($Conditions.ContainsKey('RemotePort') -and (Compare-Object @($Conditions.RemotePort) @($transport.RemotePort)))) {
            throw "Active rule mismatch for $name. Planned: $plannedAddress, $($Conditions.Protocol), $($Conditions.RemotePort). Active: $activeAddress, $($transport.Protocol), $($transport.RemotePort)."
        }
    }
    if ($Wfp) {
        Add-Type -Path (Join-Path $PSScriptRoot 'SolTrialWfp.cs')
        if ($DurableWfp) {
            $key = [guid]::NewGuid()
            $record = [ordered]@{ sid=$PackageSid; profile=$profile; broker_port=$port; lease_key=$key }
            $bytes = [Text.Encoding]::UTF8.GetBytes(($record | ConvertTo-Json -Compress))
            $file = [IO.FileStream]::new($leaseFile,'CreateNew','Write','None',4096,'WriteThrough')
            try { $file.Write($bytes); $file.Flush($true) } finally { $file.Dispose() }
            $lease = $key
            $wfpSession = [SolTrialWfp]::new($PackageSid,$port,$lease)
            $installed = $wfpSession.FilterIds
            $wfpSession.Dispose()
            $wfpSession = $null
            $wrongPort = if ($port -eq 65535) { $port - 1 } else { $port + 1 }
            try {
                [SolTrialWfp]::ReleasePersistent($PackageSid,$wrongPort,$lease)
                throw 'Wrong-port removal unexpectedly succeeded'
            } catch {
                if ($_.Exception.ToString() -notmatch 'Unexpected WFP condition: non-broker IPv4 port') { throw }
            }
            $ids = [SolTrialWfp]::VerifyPersistent($PackageSid,$port,$lease)
            if (Compare-Object $installed $ids) { throw 'Persistent filter IDs changed after installer session closed' }
        } else {
            $wfpSession = [SolTrialWfp]::new($PackageSid,$port)
            $ids = $wfpSession.FilterIds
        }
        [pscustomobject]@{ status='WFP_ACTIVE'; sid=$PackageSid; filter_ids=$ids; broker_port=$port; installer_session_closed=[bool]$lease; mismatched_removal_rejected=[bool]$lease } |
            ConvertTo-Json -Compress
    } else {
        # Disjoint complements: no catch-all block intersects the one candidate broker tuple.
        $exceptLoopback = @(foreach ($bit in 0..31) {
            $network = (([uint64]0x7f000001 -shr $bit) -bxor 1) -shl $bit
            $bytes = [BitConverter]::GetBytes([uint32]$network)
            [Array]::Reverse($bytes)
            [Net.IPAddress]::new($bytes).ToString() + '/' + (32 - $bit)
        })
        Add-Block 'NonLoopbackV4' @{ RemoteAddress=$exceptLoopback; Protocol='Any' }
        Add-Block 'AllV6' @{ RemoteAddress=@('::/1','8000::/1'); Protocol='Any' }
        Add-Block 'OtherLoopbackTcpPort' @{
            RemoteAddress='127.0.0.1'; Protocol='TCP'; RemotePort=@("1-$($port-1)","$($port+1)-65535")
        }
        Add-Block 'LoopbackUdp' @{ RemoteAddress='127.0.0.1'; Protocol='UDP' }
        [pscustomobject]@{ status='RULES_ACTIVE'; sid=$PackageSid; rules=$names; broker_port=$port } | ConvertTo-Json -Compress
    }

    $existing = (& $netIsolation LoopbackExempt -s | Out-String)
    if ($LASTEXITCODE -ne 0 -or $existing.Contains($PackageSid)) { throw 'Fixture SID already exempt or lookup failed' }
    $exemptionAttempted = $true
    & $netIsolation LoopbackExempt -a "-p=$PackageSid" | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'Adding the scoped loopback exemption failed' }
    $state = (& $netIsolation LoopbackExempt -s | Out-String)
    if ($LASTEXITCODE -ne 0 -or !$state.Contains($PackageSid)) { throw 'Scoped loopback exemption not verified' }

    $result = & $exe --review-loopback-policy-host $bundleItem.FullName $PackageSid $port $other4.LocalEndpoint.Port $other6.LocalEndpoint.Port
    if ($LASTEXITCODE -ne 0) { throw "Qualification executable failed with exit $LASTEXITCODE" }
    $probe = $result | ConvertFrom-Json
    $observed = [ordered]@{
        status = 'UNQUALIFIED'
        probe = $probe
        approved_tcp_observed = $allowed.Pending()
        other_ipv4_tcp_observed = $other4.Pending()
        other_ipv4_address_observed = $alias4.Pending()
        other_ipv6_tcp_observed = $other6.Pending()
        udp_observed = $udp.Available -gt 0
    }
    # Run the unrestricted local controls after capturing trial traffic, while the same filters are still active.
    $observed.non_trial_tcp_control = $true
    foreach ($listener in @($other4,$alias4,$other6)) {
        $endpoint = $listener.LocalEndpoint
        $client = [Net.Sockets.TcpClient]::new($endpoint.AddressFamily)
        try { $client.Connect($endpoint.Address,$endpoint.Port) } finally { $client.Dispose() }
        $observed.non_trial_tcp_control = $observed.non_trial_tcp_control -and $listener.Pending()
    }
    $udpControl = [Net.Sockets.UdpClient]::new()
    try {
        [void]$udpControl.Send([byte[]]@(1),1,[Net.IPEndPoint]::new([Net.IPAddress]::Loopback,$port))
        $observed.non_trial_udp_control = $udp.Client.Poll(1000000,[Net.Sockets.SelectMode]::SelectRead)
    } finally { $udpControl.Dispose() }
    if ($probe.approved_tcp -eq 0 -and $observed.approved_tcp_observed -and
        $probe.unapproved_ipv4_tcp -eq 10013 -and $probe.unapproved_ipv4_address -eq 10013 -and $probe.unapproved_ipv6_tcp -eq 10013 -and
        $probe.environment_proxy_result -eq 10013 -and $observed.non_trial_tcp_control -and $observed.non_trial_udp_control -and
        !$observed.other_ipv4_tcp_observed -and !$observed.other_ipv4_address_observed -and
        !$observed.other_ipv6_tcp_observed -and !$observed.udp_observed) {
        $observed.status = 'LOCAL_TUPLE_FIXTURE_PASS'
    }
    $observed | ConvertTo-Json -Depth 4 -Compress
    if ($observed.status -ne 'LOCAL_TUPLE_FIXTURE_PASS') { throw 'Trial network boundary unqualified' }
} finally {
    $allowed.Stop()
    $other4.Stop()
    $other6.Stop()
    if ($alias4) { $alias4.Stop() }
    if ($udp) { $udp.Dispose() }
    if ($exemptionAttempted) {
        & $netIsolation LoopbackExempt -d "-p=$PackageSid" | Out-Null
        $state = (& $netIsolation LoopbackExempt -s | Out-String)
        if ($LASTEXITCODE -ne 0 -or $state.Contains($PackageSid)) {
            throw 'Rollback could not verify exemption removal. Retaining restrictive rules.'
        }
    }
    if ($wfpSession) {
        $wfpSession.Dispose()
        [pscustomobject]@{ status='WFP_FILTERS_REMOVED'; filter_ids=$wfpSession.FilterIds } | ConvertTo-Json -Compress
    }
    if ($lease) {
        [SolTrialWfp]::ReleasePersistent($PackageSid,$port,$lease)
        [SolTrialWfp]::ReleasePersistent($PackageSid,$port,$lease)
        [pscustomobject]@{ status='WFP_DURABLE_LEASE_REMOVED'; lease_key=$lease; recovery_record=$leaseFile; verified_absent=$true } |
            ConvertTo-Json -Compress
    }
    $rules = @(Get-NetFirewallRule -PolicyStore PersistentStore | Where-Object Name -CIn $names)
    foreach ($rule in $rules) {
        if (($rule | Get-NetFirewallApplicationFilter).Package -cne $PackageSid) {
            throw 'Rollback stopped at a changed rule identity'
        }
        $rule | Remove-NetFirewallRule
    }
    if (Get-NetFirewallRule -PolicyStore ActiveStore | Where-Object Name -CIn $names) {
        throw 'A trial rule remains in ActiveStore'
    }
    [pscustomobject]@{ status='LOOPBACK_ROLLED_BACK'; sid=$PackageSid; exemption_removed=$true } | ConvertTo-Json -Compress
}
