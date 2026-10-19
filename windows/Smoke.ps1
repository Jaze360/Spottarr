param([Parameter(Mandatory)][string]$ServerDirectory)
$ErrorActionPreference = 'Stop'
$scratch = Join-Path ([IO.Path]::GetTempPath()) ('Spottarr.Smoke.' + [guid]::NewGuid())
New-Item -ItemType Directory -Path $scratch | Out-Null
$listener = [Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback, 0)
$listener.Start(); $port = $listener.LocalEndpoint.Port; $listener.Stop()
$start = [Diagnostics.ProcessStartInfo]::new((Join-Path $ServerDirectory 'Spottarr.Web.exe'))
$start.WorkingDirectory = $ServerDirectory
$start.UseShellExecute = $false; $start.CreateNoWindow = $true
$start.RedirectStandardOutput = $true; $start.RedirectStandardError = $true
$start.Environment['ASPNETCORE_ENVIRONMENT'] = 'Production'
$start.Environment['DOTNET_ENVIRONMENT'] = 'Production'
$start.Environment['Urls'] = "http://127.0.0.1:$port"
$start.Environment['Usenet__Hostname'] = '127.0.0.1'
$start.Environment['Usenet__Port'] = '9'
$start.Environment['Usenet__Username'] = 'smoke-user'
$start.Environment['Usenet__Password'] = 'SMOKE-SECRET-NEVER-LOG'
$start.Environment['Usenet__UseTls'] = 'false'
$start.Environment['Usenet__MaxConnections'] = '1'
$start.Environment['Database__Provider'] = 'Sqlite'
$start.Environment['Database__ConnectionString'] = "Data Source=$scratch\test.db"
$start.Environment['Newznab__ApiKey'] = 'SMOKE-API-NEVER-LOG'
$start.Environment['Logging__LogLevel__Usenet'] = 'None'
$process = $null
try {
    $process = [Diagnostics.Process]::Start($start)
    $stdout = $process.StandardOutput.ReadToEndAsync(); $stderr = $process.StandardError.ReadToEndAsync()
    $ready = $false
    for ($attempt = 0; $attempt -lt 40; $attempt++) {
        if ($process.HasExited) { throw 'Server exited before readiness' }
        try { $response = Invoke-WebRequest "http://127.0.0.1:$port/healthz" -TimeoutSec 1; if ($response.StatusCode -eq 200) { $ready = $true; break } } catch { Start-Sleep -Milliseconds 250 }
    }
    if (!$ready) { throw 'Health check timed out' }
    [xml]$caps = (Invoke-WebRequest "http://127.0.0.1:$port/newznab/api?t=caps&apikey=SMOKE-API-NEVER-LOG").Content
    if (!$caps.caps) { throw 'Newznab caps missing' }
    [xml]$search = (Invoke-WebRequest "http://127.0.0.1:$port/newznab/api?t=search&apikey=SMOKE-API-NEVER-LOG").Content
    if (!$search.rss) { throw 'Authenticated search failed' }
    [xml]$denied = (Invoke-WebRequest "http://127.0.0.1:$port/newznab/api?t=search").Content
    if (!$denied.error) { throw 'Unauthenticated request was not rejected' }
    if (!(Test-Path (Join-Path $scratch 'test.db'))) { throw 'SQLite database not created' }
    $process.Kill($true); $process.WaitForExit()
    $logs = $stdout.GetAwaiter().GetResult() + $stderr.GetAwaiter().GetResult()
    if ($logs.Contains('SMOKE-SECRET-NEVER-LOG') -or $logs.Contains('SMOKE-API-NEVER-LOG')) { throw 'Secret found in server output' }
    Write-Output 'PASS native server: SQLite migrations, health, caps, authenticated search, rejected anonymous search, no secret in captured logs.'
} finally {
    if ($process) { if (!$process.HasExited) { $process.Kill($true); $process.WaitForExit() }; $process.Dispose() }
    # This freshly-created, random directory is the only deletion target.
    $resolvedScratch = [IO.Path]::GetFullPath($scratch)
    if (!$resolvedScratch.StartsWith([IO.Path]::GetFullPath([IO.Path]::GetTempPath()), [StringComparison]::OrdinalIgnoreCase) -or !(Split-Path $resolvedScratch -Leaf).StartsWith('Spottarr.Smoke.')) { throw 'Unsafe cleanup target' }
    Remove-Item -LiteralPath $resolvedScratch -Recurse -Force
}
