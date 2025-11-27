# Simple POST test using curl
Write-Host "Starting EffinitiveFramework server on port 6001..." -ForegroundColor Cyan

# Start the benchmark server in background
$job = Start-Job -ScriptBlock {
    Set-Location "C:\Projects\EffinitiveFrameowrk\benchmarks\EffinitiveFramework.Benchmarks\bin\Release\net8.0"
    
    # Load assemblies
    Add-Type -Path ".\EffinitiveFramework.Core.dll"
    Add-Type -Path ".\EffinitiveFramework.Benchmarks.dll"
    
    # Create benchmark instance
    $bench = New-Object EffinitiveFramework.Benchmarks.Http2Benchmarks
    
    # Call Setup (async method)
    $setupMethod = $bench.GetType().GetMethod("Setup")
    $task = $setupMethod.Invoke($bench, $null)
    $task.Wait()
    
    Write-Host "Server started on https://localhost:6001"
    
    # Keep running
    while ($true) {
        Start-Sleep -Seconds 1
    }
}

# Wait for server to start
Write-Host "Waiting for server to start..." -ForegroundColor Yellow
Start-Sleep -Seconds 3

Write-Host "`nTesting GET request..." -ForegroundColor Cyan
curl.exe -k --http2 -X GET https://localhost:6001/api/http2-benchmark -v

Write-Host "`n`nTesting POST request..." -ForegroundColor Cyan
curl.exe -k --http2 -X POST https://localhost:6001/api/http2-benchmark -H "Content-Type: application/json" -d '{\"Name\":\"Test\",\"Email\":\"test@test.com\"}' -v

Write-Host "`n`nStopping server..." -ForegroundColor Yellow
Stop-Job $job
Remove-Job $job

Write-Host "Done!" -ForegroundColor Green
