<#
.SYNOPSIS
    Opens Windows Firewall on the PC that runs the PROCTOR server, so lab PCs and the teacher
    app can find it and connect.

.DESCRIPTION
    Run once, from an elevated (Administrator) PowerShell, on the classroom server / teacher PC.
    Without these rules Windows blocks every incoming connection to the server, so no student PC
    is ever detected. Adds two inbound rules (group "PROCTOR"):

      * TCP <Port> (default 5080)  - the server's HTTPS/SignalR hub. Every agent and teacher app
                                     connects here. Any remote address, so labs that reach the
                                     server across subnets (PROCTOR:ServerAddress) still work.
      * UDP 47800                  - LAN discovery. Local subnet only: broadcast never leaves it.

    The rules are port-based, so they cover both a published PROCTOR.Server.exe and a server
    started from Visual Studio (F5). Re-running replaces them. -Remove deletes them.

.PARAMETER Port
    The server's port (PROCTOR:Port in the server's appsettings.json). Default 5080.

.PARAMETER AllowOnPublicNetworks
    Also open the ports when Windows classes the network as Public (e.g. a Wi-Fi network never
    marked Private). Off by default, so a laptop running the server isn't reachable on cafe or
    hotel Wi-Fi. Prefer marking the lab network Private instead.

.PARAMETER Remove
    Delete the rules instead of adding them.

.EXAMPLE
    .\Open-ServerFirewall.ps1
.EXAMPLE
    .\Open-ServerFirewall.ps1 -AllowOnPublicNetworks
.EXAMPLE
    .\Open-ServerFirewall.ps1 -Remove
#>
[CmdletBinding()]
param(
    [ValidateRange(1, 65535)]
    [int]$Port = 5080,
    [switch]$AllowOnPublicNetworks,
    [switch]$Remove
)

$ErrorActionPreference = 'Stop'

$principal = New-Object Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent())
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw 'This script must be run as Administrator (needed to change Windows Firewall rules).'
}

$DiscoveryPort = 47800   # Protocol.DiscoveryPort
$tcpRule = 'PROCTOR-Server-Tcp'
$udpRule = 'PROCTOR-Server-Discovery-Udp'

foreach ($name in $tcpRule, $udpRule) {
    Remove-NetFirewallRule -Name $name -ErrorAction SilentlyContinue
}
if ($Remove) {
    Write-Host 'Removed the PROCTOR server firewall rules.' -ForegroundColor Green
    return
}

$profiles = if ($AllowOnPublicNetworks) { 'Domain', 'Private', 'Public' } else { 'Domain', 'Private' }

New-NetFirewallRule -Name $tcpRule `
                    -DisplayName "PROCTOR Server (TCP $Port)" `
                    -Description 'PROCTOR classroom server: agents and teacher apps connect here (HTTPS/SignalR).' `
                    -Group 'PROCTOR' `
                    -Direction Inbound -Action Allow -Protocol TCP -LocalPort $Port `
                    -Profile $profiles | Out-Null

New-NetFirewallRule -Name $udpRule `
                    -DisplayName "PROCTOR Server discovery (UDP $DiscoveryPort)" `
                    -Description 'PROCTOR classroom server: answers LAN discovery so PCs find it automatically.' `
                    -Group 'PROCTOR' `
                    -Direction Inbound -Action Allow -Protocol UDP -LocalPort $DiscoveryPort `
                    -RemoteAddress LocalSubnet -Profile $profiles | Out-Null

Write-Host "Opened TCP $Port and UDP $DiscoveryPort (local subnet) on network profiles: $($profiles -join ', ')." -ForegroundColor Green

# Warn when the active network wouldn't be covered: the most common reason PCs still can't connect.
if (-not $AllowOnPublicNetworks) {
    $public = @(Get-NetConnectionProfile -ErrorAction SilentlyContinue | Where-Object NetworkCategory -eq 'Public')
    foreach ($p in $public) {
        Write-Warning ("'$($p.InterfaceAlias)' ($($p.Name)) is a Public network, so these rules don't apply to it. " +
            "Mark it Private (Settings > Network > Properties), or re-run with -AllowOnPublicNetworks.")
    }
}
