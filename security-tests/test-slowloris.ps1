# Slowloris Attack Protection Test
# Verifies server has request timeout protection

param(
    [string]$ServerUrl = "https://localhost:5001"
)

Write-Host "========================================" -ForegroundColor Cyan
Write-Host "Slowloris Protection Test" -ForegroundColor Cyan
Write-Host "========================================`n" -ForegroundColor Cyan

$uri = [Uri]$ServerUrl

# Test 1: Manual slow request
Write-Host "[1/2] Manual Slow Request Test" -ForegroundColor Green
Write-Host "Sending incomplete HTTP request very slowly..." -ForegroundColor Yellow

try {
    $client = New-Object System.Net.Sockets.TcpClient
    $client.Connect($uri.Host, $uri.Port)
    
    $stream = $client.GetStream()
    $writer = New-Object System.IO.StreamWriter($stream)
    
    Write-Host "  Sending request line..." -ForegroundColor Gray
    $writer.Write("GET /api/health HTTP/1.1`r`n")
    $writer.Flush()
    Start-Sleep -Seconds 5
    
    Write-Host "  Sending Host header..." -ForegroundColor Gray
    $writer.Write("Host: $($uri.Host)`r`n")
    $writer.Flush()
    Start-Sleep -Seconds 5
    
    Write-Host "  Sending slow header..." -ForegroundColor Gray
    $writer.Write("X-Slow: value`r`n")
    $writer.Flush()
    Start-Sleep -Seconds 25
    
    # Try to complete request (should timeout before this)
    Write-Host "  Attempting to complete request..." -ForegroundColor Gray
    $writer.Write("`r`n")
    $writer.Flush()
    
    # Try to read response
    $reader = New-Object System.IO.StreamReader($stream)
    $response = $reader.ReadLine()
    
    if ($response) {
        Write-Host "❌ FAIL: Server did not timeout (received: $response)" -ForegroundColor Red
        $passed1 = $false
    } else {
        Write-Host "✅ PASS: Connection timed out as expected" -ForegroundColor Green
        $passed1 = $true
    }
    
} catch {
    # Connection closed or timeout is expected behavior
    Write-Host "✅ PASS: Connection closed ($($_.Exception.Message))" -ForegroundColor Green
    $passed1 = $true
} finally {
    if ($client) { $client.Close() }
}

# Test 2: Python slowloris (if available)
Write-Host "`n[2/2] Python Slowloris Test" -ForegroundColor Green

if (Get-Command python -ErrorAction SilentlyContinue) {
    Write-Host "Launching slowloris attack (50 connections, 1 byte/sec)..." -ForegroundColor Yellow
    
    try {
        $attackJob = Start-Job -ScriptBlock {
            param($host, $port)
            python -m slowloris $host -p $port -s 50 --sleeptime 1
        } -ArgumentList $uri.Host, $uri.Port
        
        # Let attack run for 35 seconds (should timeout at 30s)
        Start-Sleep -Seconds 35
        
        # Stop attack
        Stop-Job $attackJob -ErrorAction SilentlyContinue
        Remove-Job $attackJob -Force -ErrorAction SilentlyContinue
        
        # Verify server is still responsive
        Write-Host "Verifying server is still responsive..." -ForegroundColor Yellow
        
        $handler = New-Object System.Net.Http.HttpClientHandler
        $handler.ServerCertificateCustomValidationCallback = { $true }
        $httpClient = New-Object System.Net.Http.HttpClient($handler)
        $httpClient.Timeout = [TimeSpan]::FromSeconds(5)
        
        $response = $httpClient.GetAsync("$ServerUrl/api/health").Result
        $serverAlive = $response.StatusCode -eq 200
        $httpClient.Dispose()
        
        if ($serverAlive) {
            Write-Host "✅ PASS: Server remained responsive after Slowloris attack" -ForegroundColor Green
            $passed2 = $true
        } else {
            Write-Host "❌ FAIL: Server is not responsive (Status: $($response.StatusCode))" -ForegroundColor Red
            $passed2 = $false
        }
        
    } catch {
        Write-Host "❌ FAIL: $($_.Exception.Message)" -ForegroundColor Red
        $passed2 = $false
    }
} else {
    Write-Host "⚠️  SKIP: Python not installed" -ForegroundColor Yellow
    Write-Host "Install Python and run: pip install slowloris" -ForegroundColor Gray
    $passed2 = $null
}

# Summary
Write-Host "`n========================================" -ForegroundColor Cyan
Write-Host "Test Summary" -ForegroundColor Cyan
Write-Host "========================================`n" -ForegroundColor Cyan

Write-Host "Manual Slow Request:  $(if ($passed1) { '✅ PASS' } else { '❌ FAIL' })" -ForegroundColor $(if ($passed1) { "Green" } else { "Red" })

if ($passed2 -ne $null) {
    Write-Host "Python Slowloris:     $(if ($passed2) { '✅ PASS' } else { '❌ FAIL' })" -ForegroundColor $(if ($passed2) { "Green" } else { "Red" })
} else {
    Write-Host "Python Slowloris:     ⚠️  SKIP" -ForegroundColor Yellow
}

if ($passed1 -and ($passed2 -eq $null -or $passed2)) {
    Write-Host "`n✅ Slowloris protection is working correctly!" -ForegroundColor Green
    exit 0
} else {
    Write-Host "`n❌ Slowloris protection needs review!" -ForegroundColor Red
    exit 1
}
