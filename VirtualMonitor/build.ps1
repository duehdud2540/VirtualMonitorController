param([ValidateSet('Debug','Release')][string]$Configuration = 'Release', [switch]$Verify)
$ErrorActionPreference = 'Stop'
Push-Location $PSScriptRoot
try {
    # Deliberately offline: restore is a separate, explicitly requested operation.
    & dotnet build VirtualMonitor.csproj --no-restore -c $Configuration -p:NuGetAudit=false
    if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }
    $outputPath = Join-Path $PSScriptRoot "bin\$Configuration\net10.0-windows"
    $entries = foreach ($name in @('VirtualMonitor.dll','VirtualMonitor.Capture.dll','VirtualMonitor.Preview.dll')) {
        $artifactPath = Join-Path $outputPath $name
        if (-not (Test-Path -LiteralPath $artifactPath)) { throw "Missing artifact: $name" }
        [ordered]@{ name = $name; sha256 = (Get-FileHash -LiteralPath $artifactPath -Algorithm SHA256).Hash }
    }
    [ordered]@{ version = '0.2.0'; builtUtc = [DateTime]::UtcNow.ToString('o'); architecture = 'x64'; files = @($entries) } |
        ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $outputPath 'build-manifest.json') -Encoding UTF8
    if ($Verify) {
        & dotnet (Join-Path $outputPath 'VirtualMonitor.dll') --diagnose-architecture
        if ($LASTEXITCODE -ne 0) { throw 'Architecture diagnostics failed.' }
    }
} finally { Pop-Location }
