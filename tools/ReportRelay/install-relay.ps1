# Installs or updates the TrialHater report relay as a Windows service (docs/REPORTING.md).
# Run from an elevated PowerShell on the compute box, in the staging folder the files were copied to:
#   powershell -NoProfile -ExecutionPolicy Bypass -File C:\th-jobs\relay\install-relay.ps1
# The first install asks for the GitHub token (fine-grained: TrialHater-reports only, Contents and
# Issues read/write) and takes relay.pfx and allowlist.json from the staging folder. Updates
# replace the program and keep the data folder (token, certificate, allowlist pins, log).
param(
    [int]$Port = 8443,
    [string]$Repo = 'Tihlyn/TrialHater-reports'
)

$ErrorActionPreference = 'Stop'
$staging = $PSScriptRoot
$program = Join-Path $env:ProgramFiles 'TrialHaterRelay'
$data = Join-Path $env:ProgramData 'TrialHaterRelay'
$service = 'TrialHaterRelay'
$account = 'NT AUTHORITY\LocalService'
$exe = Join-Path $program 'ReportRelay.exe'

$identity = New-Object Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent())
if (-not $identity.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw 'Run this from an elevated (administrator) PowerShell.'
}

if (-not (Test-Path (Join-Path $staging 'app\ReportRelay.exe'))) {
    throw "No relay build in $staging\app."
}

# The program: replaced on every run.
$existing = Get-Service $service -ErrorAction SilentlyContinue
if ($existing -and $existing.Status -ne 'Stopped') {
    Stop-Service $service
    $existing.WaitForStatus('Stopped', '00:00:30')
}
New-Item -ItemType Directory -Force $program | Out-Null
Copy-Item (Join-Path $staging 'app\*') $program -Recurse -Force

# The data folder: administrators and the service account only.
New-Item -ItemType Directory -Force $data | Out-Null
icacls $data /inheritance:r /grant:r 'Administrators:(OI)(CI)F' 'SYSTEM:(OI)(CI)F' "${account}:(OI)(CI)M" | Out-Null
if ($LASTEXITCODE -ne 0) { throw "icacls failed on $data." }
foreach ($file in 'relay.pfx', 'allowlist.json') {
    $target = Join-Path $data $file
    if (-not (Test-Path $target)) {
        Copy-Item (Join-Path $staging $file) $target
        Write-Host "Installed $file"
    }
}

$token = Join-Path $data 'github.token'
if (-not (Test-Path $token)) {
    $secure = Read-Host 'GitHub token (fine-grained, TrialHater-reports only)' -AsSecureString
    $bstr = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($secure)
    try { $plain = [Runtime.InteropServices.Marshal]::PtrToStringBSTR($bstr).Trim() }
    finally { [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($bstr) }
    if (-not $plain) { throw 'No token given.' }
    [IO.File]::WriteAllText($token, $plain)
    Write-Host 'Saved the GitHub token'
}

# The certificate's private key no longer needs to sit in the staging folder.
Remove-Item (Join-Path $staging 'relay.pfx') -ErrorAction SilentlyContinue

# Inbound TCP on the port, for the relay's program only.
$rule = 'TrialHater report relay'
if (Get-NetFirewallRule -DisplayName $rule -ErrorAction SilentlyContinue) {
    Set-NetFirewallRule -DisplayName $rule -Protocol TCP -LocalPort $Port -Program $exe -Action Allow -Enabled True
}
else {
    New-NetFirewallRule -DisplayName $rule -Direction Inbound -Protocol TCP -LocalPort $Port -Program $exe -Action Allow | Out-Null
}

# The service, as LocalService, restarted when it fails.
$binary = "`"$exe`" --data `"$data`" --port $Port --repo $Repo"
if (-not $existing) {
    New-Service -Name $service -BinaryPathName $binary -DisplayName 'TrialHater report relay' -StartupType Automatic `
        -Description 'Files TrialHater bug reports from the plugin as issues in the private reports repo.' | Out-Null
}
$change = Get-CimInstance Win32_Service -Filter "Name='$service'" |
    Invoke-CimMethod -MethodName Change -Arguments @{ PathName = $binary; StartName = $account; StartPassword = '' }
if ($change.ReturnValue -ne 0) { throw "Could not configure the service (Win32_Service.Change returned $($change.ReturnValue))." }
sc.exe failure $service reset= 86400 actions= restart/5000/restart/5000/restart/60000 | Out-Null

Start-Service $service
Start-Sleep -Seconds 3
Write-Host "Health: $(curl.exe -sk "https://localhost:$Port/v1/health")"
Get-Content (Join-Path $data 'relay.log') -Tail 5
