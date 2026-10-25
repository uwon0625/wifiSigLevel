$csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
& $csc /nologo /target:winexe /out:"$PSScriptRoot\WifiSigLevel.exe" `
    /r:System.Windows.Forms.dll /r:System.Drawing.dll "$PSScriptRoot\WifiSigLevel.cs"
exit $LASTEXITCODE
