# TabletDash.exe'yi Windows ile gelen .NET Framework derleyicisiyle derler.
$csc = "$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
& $csc /nologo /codepage:65001 /optimize+ /target:exe /platform:x64 /out:"$PSScriptRoot\TabletDash.exe" /r:System.Core.dll "$PSScriptRoot\src\Server.cs"
if ($LASTEXITCODE -eq 0) { "Derlendi: $PSScriptRoot\TabletDash.exe" }
