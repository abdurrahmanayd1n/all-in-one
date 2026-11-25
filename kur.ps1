# Build, install, enable autostart and run. Remove with: .\kur.ps1 -Kaldir
param([switch]$Kaldir)
$ErrorActionPreference = 'Stop'

$dir = Join-Path $env:LOCALAPPDATA 'AllInOne'
$exe = Join-Path $dir 'AllInOne.exe'
$run = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
# Name and icon under which Windows shows the notifications.
$appId = 'HKCU:\Software\Classes\AppUserModelId\AllInOne.App'

$old = Get-Process AllInOne -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq $exe }
if ($Kaldir) {
    if ($old) { $old | Stop-Process -Force; $old | Wait-Process }
    Remove-ItemProperty $run -Name AllInOne -ErrorAction SilentlyContinue
    if (Test-Path $exe) { Remove-Item $exe }
    Remove-Item 'HKCU:\Software\AllInOne', $appId -ErrorAction SilentlyContinue
    if (Test-Path "$dir\app.ico") { Remove-Item "$dir\app.ico" }
    'Kaldirildi.'
    return
}

# Compiler that ships with Windows (C# 5), so nothing needs to be installed.
$fw = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319'
$built = Join-Path $PSScriptRoot 'bin\AllInOne.exe'
$meta = Join-Path $env:WINDIR 'System32\WinMetadata'
New-Item -ItemType Directory -Force (Split-Path $built), $dir | Out-Null
& "$fw\csc.exe" /nologo /codepage:65001 /target:winexe /optimize+ /warnaserror+ "/out:$built" "/lib:$fw\WPF" `
    /r:WindowsBase.dll /r:Microsoft.CSharp.dll /r:System.IO.Compression.dll `
    /r:PresentationCore.dll /r:PresentationFramework.dll /r:System.Xaml.dll /r:WindowsFormsIntegration.dll `
    "/win32icon:$PSScriptRoot\app.ico" "/resource:$PSScriptRoot\app.ico,app.ico" `
    "/resource:$PSScriptRoot\Editor.xaml,Editor.xaml" `
    "/r:$meta\Windows.UI.winmd" "/r:$meta\Windows.Data.winmd" "/r:$meta\Windows.Foundation.winmd" `
    "/r:$meta\Windows.Media.winmd" "/r:$meta\Windows.Graphics.winmd" "/r:$meta\Windows.Globalization.winmd" "/r:$meta\Windows.Storage.winmd" "/r:$meta\Windows.Security.winmd" `
    "/r:$fw\System.Runtime.dll" "/r:$fw\System.Runtime.WindowsRuntime.dll" "/r:$fw\System.Runtime.InteropServices.WindowsRuntime.dll" `
    (Get-ChildItem $PSScriptRoot -Filter *.cs).FullName
if ($LASTEXITCODE -ne 0) { throw 'Derleme basarisiz.' }

if ((Start-Process $built '--test' -Wait -PassThru).ExitCode -ne 0) { throw 'Self-test basarisiz.' }

if ($old) { $old | Stop-Process -Force; $old | Wait-Process }
# Windows can hold the old exe for a moment after the process has ended.
for ($try = 1; ; $try++) {
    try { Copy-Item $built $exe -Force; break }
    catch { if ($try -ge 20) { throw }; Start-Sleep -Milliseconds 500 }
}
# Autostart switched off in the app's settings stays off.
if ((Get-ItemProperty 'HKCU:\Software\AllInOne' -Name Autostart -ErrorAction SilentlyContinue).Autostart -ne 0) {
    Set-ItemProperty $run -Name AllInOne -Value "`"$exe`""
}
Copy-Item (Join-Path $PSScriptRoot 'app.ico') "$dir\app.ico" -Force
if (-not (Test-Path $appId)) { New-Item $appId -Force | Out-Null }
Set-ItemProperty $appId -Name DisplayName -Value 'All In One'
Set-ItemProperty $appId -Name IconUri -Value "$dir\app.ico"
Start-Process $exe
'Kuruldu. Sol Alt tusunu basili tut.'
