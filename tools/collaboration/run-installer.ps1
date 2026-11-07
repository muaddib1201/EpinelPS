param([Parameter(Mandatory=$true)][string]$Installer,[string]$LockHandles = '')
$ErrorActionPreference = 'Stop'
if ($LockHandles) {
    Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public static class NikkeDeploymentHandles {
    [DllImport("kernel32.dll",SetLastError=true)]
    [return:MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetHandleInformation(IntPtr handle,uint mask,uint flags);
}
'@
    foreach ($deploymentHandle in $LockHandles.Split(',')) {
        # Retain our inherited exclusive handles until this installer exits; do not pass them to the game server.
        if (-not [NikkeDeploymentHandles]::SetHandleInformation([IntPtr]::new([long]$deploymentHandle),1,0)) {
            throw [ComponentModel.Win32Exception]::new([Runtime.InteropServices.Marshal]::GetLastWin32Error())
        }
    }
}
$global:LASTEXITCODE = 0
& $Installer
exit $LASTEXITCODE
