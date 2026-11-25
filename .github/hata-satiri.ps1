# When the self-test fails in CI: builds a throwaway copy that returns the line of the first failing check.
# Run from the project folder.
$sp = Join-Path $env:TEMP 'allinone-hata-satiri'
$probe = "$sp\probe"
New-Item -ItemType Directory -Force $probe | Out-Null
Get-ChildItem -Filter *.cs | Copy-Item -Destination $probe -Force
Copy-Item .\Editor.xaml, .\app.ico $probe -Force
$lines = [IO.File]::ReadAllText((Resolve-Path .\AllInOne.cs).Path).Replace("`r`n", "`n") -split "`n"
$out = New-Object System.Collections.Generic.List[string]
$seen = $false; $last = ''
for ($i = 0; $i -lt $lines.Count; $i++) {
    if ($lines[$i] -match 'bool ok = true;') { $seen = $true; $out.Add($lines[$i]); $last = $lines[$i]; continue }
    if ($lines[$i] -match '^ {8}finally') { $seen = $false }
    if ($seen -and $lines[$i] -match '^ {12}[A-Za-z]' -and $lines[$i] -notmatch '^ {12}(else|catch|finally|while)\b' -and $last -match '(;|\})\s*(//.*)?$') {
        $out.Add("            if (!ok) return $($i + 1000);")
    }
    $out.Add($lines[$i])
    if ($lines[$i].Trim() -ne '' -and -not $lines[$i].Trim().StartsWith('//')) { $last = $lines[$i] }
}
[IO.File]::WriteAllText("$probe\AllInOne.cs", ($out -join "`n"), (New-Object Text.UTF8Encoding $true))
$fw = "$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319"; $meta = "$env:WINDIR\System32\WinMetadata"
Push-Location $probe
Remove-Item probe.exe -ErrorAction SilentlyContinue
& "$fw\csc.exe" /nologo /codepage:65001 /target:winexe "/out:$probe\probe.exe" "/lib:$fw\WPF" /r:WindowsBase.dll /r:Microsoft.CSharp.dll /r:System.IO.Compression.dll /r:PresentationCore.dll /r:PresentationFramework.dll /r:System.Xaml.dll /r:WindowsFormsIntegration.dll "/resource:app.ico,app.ico" "/resource:Editor.xaml,Editor.xaml" "/r:$meta\Windows.UI.winmd" "/r:$meta\Windows.Data.winmd" "/r:$meta\Windows.Foundation.winmd" "/r:$meta\Windows.Media.winmd" "/r:$meta\Windows.Graphics.winmd" "/r:$meta\Windows.Globalization.winmd" "/r:$meta\Windows.Storage.winmd" "/r:$meta\Windows.Security.winmd" "/r:$fw\System.Runtime.dll" "/r:$fw\System.Runtime.WindowsRuntime.dll" "/r:$fw\System.Runtime.InteropServices.WindowsRuntime.dll" (Get-ChildItem -Filter *.cs).FullName
Pop-Location
if (Test-Path "$probe\probe.exe") {
    $p = Start-Process "$probe\probe.exe" '--test' -Wait -PassThru; "cikis kodu: $($p.ExitCode)"
    if ($p.ExitCode -ge 1000) { $at = $p.ExitCode - 1000; "satir $($at) oncesi:"; $lines[($at-7)..($at-1)] }
}
