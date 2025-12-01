# EffinitiveFramework Security Test Suite
# Runs all security tests using FREE tools only

param(
    [string]$ServerUrl = "https://localhost:5001",
    [int]$LoadTestDuration = 30,
    [switch]$SkipServerStart,
    [switch]$Verbose
)

$ErrorActionPreference = "Continue"
$testResults = @()

function Write-TestHeader($message) {
    Write-Host "`n========================================" -ForegroundColor Cyan
    Write-Host $message -ForegroundColor Cyan
    Write-Host "========================================`n" -ForegroundColor Cyan
}

function Write-TestResult($testName, $passed, $details = "") {
    $result = @{
        Test = $testName
        Passed = $passed
        Details = $details
        Timestamp = Get-Date
    }
    $script:testResults += $result
    
    if ($passed) {
        Write-Host "✅ PASS: $testName" -ForegroundColor Green
    } else {
        Write-Host "❌ FAIL: $testName" -ForegroundColor Red
    }
    if ($details) {
        Write-Host "   $details" -ForegroundColor Gray
    }
}

# Test 1: Unit Tests
Write-TestHeader "TEST 1: Running Unit Tests"
try {
    $output = dotnet test tests\EffinitiveFramework.Tests\EffinitiveFramework.Tests.csproj --configuration Release --no-build --verbosity minimal 2>&1
    $passed = $LASTEXITCODE -eq 0
    $testCount = if ($output -match "Passed!\s+-\s+Failed:\s+0,\s+Passed:\s+(\d+)") { $matches[1] } else { "?" }
    Write-TestResult "Unit Tests" $passed "Passed: $testCount tests"
    if ($Verbose) { Write-Host $output }
} catch {
    Write-TestResult "Unit Tests" $false $_.Exception.Message
}

# Test 2: Dependency Scan
Write-TestHeader "TEST 2: Scanning for Vulnerable Dependencies"
try {
    $output = dotnet list package --vulnerable 2>&1 | Out-String
    $hasVulnerabilities = $output -match "has the following vulnerable packages"
    Write-TestResult "Dependency Scan" (-not $hasVulnerabilities) $(if ($hasVulnerabilities) { "Vulnerabilities found!" } else { "No vulnerabilities detected" })
    if ($Verbose -or $hasVulnerabilities) { Write-Host $output }
} catch {
    Write-TestResult "Dependency Scan" $false $_.Exception.Message
}

# Test 3: Static Analysis
Write-TestHeader "TEST 3: Running Static Code Analysis"
try {
    Write-Host "Running Roslyn analyzers..." -ForegroundColor Yellow
    $output = dotnet build EffinitiveFramework.sln /p:RunAnalyzers=true /p:EnforceCodeStyleInBuild=true /p:TreatWarningsAsErrors=false --verbosity quiet 2>&1
    $passed = $LASTEXITCODE -eq 0
    $warningCount = ([regex]::Matches($output, "warning")).Count
    Write-TestResult "Static Analysis" $passed "Build succeeded with $warningCount warnings"
    if ($Verbose) { Write-Host $output }
} catch {
    Write-TestResult "Static Analysis" $false $_.Exception.Message
}

# Start server if needed
$serverProcess = $null
if (-not $SkipServerStart) {
    Write-TestHeader "Starting Test Server"
    Write-Host "Starting server at $ServerUrl..." -ForegroundColor Yellow
    $serverProcess = Start-Process -FilePath "dotnet" -ArgumentList "run --project samples/EffinitiveFramework.Sample --configuration Release --urls $ServerUrl" -PassThru -WindowStyle Hidden
    Write-Host "Waiting for server to start..." -ForegroundColor Yellow
    Start-Sleep -Seconds 8
    
    # Verify server is running
    try {
        $handler = New-Object System.Net.Http.HttpClientHandler
        $handler.ServerCertificateCustomValidationCallback = { $true }
        $client = New-Object System.Net.Http.HttpClient($handler)
        $response = $client.GetAsync("$ServerUrl/api/health").Result
        Write-Host "✅ Server is responding (Status: $($response.StatusCode))" -ForegroundColor Green
        $client.Dispose()
    } catch {
        Write-Host "⚠️  Warning: Could not verify server status: $_" -ForegroundColor Yellow
    }
}

# Test 4: HTTP/2 Load Testing (if h2load available)
Write-TestHeader "TEST 4: HTTP/2 Load Testing"
if (Get-Command h2load -ErrorAction SilentlyContinue) {
    try {
        Write-Host "Running h2load with 1000 requests, 10 concurrent clients..." -ForegroundColor Yellow
        $output = h2load -n 1000 -c 10 -t 2 "$ServerUrl/api/health" 2>&1 | Out-String
        $passed = $output -match "finished in" -and $output -notmatch "failed"
        $rps = if ($output -match "(\d+\.?\d*) req/s") { $matches[1] } else { "?" }
        Write-TestResult "h2load Load Test" $passed "Throughput: $rps req/s"
        if ($Verbose) { Write-Host $output }
    } catch {
        Write-TestResult "h2load Load Test" $false $_.Exception.Message
    }
} else {
    Write-Host "⚠️  h2load not found - Install nghttp2 to run this test" -ForegroundColor Yellow
    Write-Host "   Download from: https://github.com/nghttp2/nghttp2/releases" -ForegroundColor Gray
    Write-TestResult "h2load Load Test" $false "Tool not installed"
}

# Test 5: SSE Load Testing
Write-TestHeader "TEST 5: Server-Sent Events Load Testing"
try {
    Write-Host "Starting 10 concurrent SSE clients for $LoadTestDuration seconds..." -ForegroundColor Yellow
    
    $jobs = @()
    for ($i = 1; $i -le 10; $i++) {
        $job = Start-Job -ScriptBlock {
            param($url, $duration, $clientId)
            
            try {
                $handler = New-Object System.Net.Http.HttpClientHandler
                $handler.ServerCertificateCustomValidationCallback = { $true }
                $client = New-Object System.Net.Http.HttpClient($handler)
                $client.Timeout = [TimeSpan]::FromSeconds($duration + 10)
                
                $response = $client.GetAsync($url, [System.Net.Http.HttpCompletionOption]::ResponseHeadersRead).Result
                $stream = $response.Content.ReadAsStreamAsync().Result
                $reader = New-Object System.IO.StreamReader($stream)
                
                $eventCount = 0
                $stopwatch = [System.Diagnostics.Stopwatch]::StartNew()
                
                while ($stopwatch.Elapsed.TotalSeconds -lt $duration) {
                    $line = $reader.ReadLine()
                    if ($line -match "^data:") {
                        $eventCount++
                    }
                }
                
                return @{ Success = $true; Events = $eventCount; Client = $clientId }
            }
            catch {
                return @{ Success = $false; Error = $_.Exception.Message; Client = $clientId }
            }
            finally {
                if ($client) { $client.Dispose() }
            }
        } -ArgumentList "$ServerUrl/api/stream/time", $LoadTestDuration, $i
        
        $jobs += $job
    }
    
    # Wait for all jobs
    $results = $jobs | Wait-Job | Receive-Job
    $jobs | Remove-Job
    
    $successCount = ($results | Where-Object { $_.Success }).Count
    $totalEvents = ($results | Where-Object { $_.Success } | Measure-Object -Property Events -Sum).Sum
    $avgEventsPerClient = if ($successCount -gt 0) { [math]::Round($totalEvents / $successCount, 2) } else { 0 }
    
    Write-TestResult "SSE Load Test" ($successCount -eq 10) "$successCount/10 clients succeeded, $totalEvents total events, $avgEventsPerClient avg/client"
    
} catch {
    Write-TestResult "SSE Load Test" $false $_.Exception.Message
}

# Test 6: Slowloris Protection Test
Write-TestHeader "TEST 6: Slowloris Attack Protection"
if (Get-Command python -ErrorAction SilentlyContinue) {
    try {
        Write-Host "Installing slowloris module..." -ForegroundColor Yellow
        pip install slowloris 2>&1 | Out-Null
        
        Write-Host "Launching slowloris attack (should timeout after 30s)..." -ForegroundColor Yellow
        $timeoutJob = Start-Job -ScriptBlock {
            param($url)
            $uri = [Uri]$url
            python -m slowloris $uri.Host -p $uri.Port -s 50 --sleeptime 1
        } -ArgumentList $ServerUrl
        
        Start-Sleep -Seconds 35
        Stop-Job $timeoutJob -ErrorAction SilentlyContinue
        Remove-Job $timeoutJob -Force -ErrorAction SilentlyContinue
        
        # Verify server is still responsive
        $handler = New-Object System.Net.Http.HttpClientHandler
        $handler.ServerCertificateCustomValidationCallback = { $true }
        $client = New-Object System.Net.Http.HttpClient($handler)
        $response = $client.GetAsync("$ServerUrl/api/health").Result
        $serverAlive = $response.StatusCode -eq 200
        $client.Dispose()
        
        Write-TestResult "Slowloris Protection" $serverAlive "Server remained responsive after attack"
        
    } catch {
        Write-TestResult "Slowloris Protection" $false $_.Exception.Message
    }
} else {
    Write-Host "⚠️  Python not found - Install Python to run this test" -ForegroundColor Yellow
    Write-TestResult "Slowloris Protection" $false "Python not installed"
}

# Test 7: HTTP/2 Compliance (if h2spec available)
Write-TestHeader "TEST 7: HTTP/2 Protocol Compliance"
if (Get-Command h2spec -ErrorAction SilentlyContinue) {
    try {
        $uri = [Uri]$ServerUrl
        Write-Host "Running h2spec compliance tests..." -ForegroundColor Yellow
        $output = h2spec -h $uri.Host -p $uri.Port -t -k 2>&1 | Out-String
        $passed = $output -match "(\d+) tests, (\d+) passed, (\d+) skipped, (\d+) failed"
        $passedCount = if ($matches) { $matches[2] } else { "?" }
        $failedCount = if ($matches) { $matches[4] } else { "?" }
        
        Write-TestResult "h2spec Compliance" ($failedCount -eq "0" -or $failedCount -eq 0) "$passedCount passed, $failedCount failed"
        if ($Verbose) { Write-Host $output }
    } catch {
        Write-TestResult "h2spec Compliance" $false $_.Exception.Message
    }
} else {
    Write-Host "⚠️  h2spec not found - Download from https://github.com/summerwind/h2spec/releases" -ForegroundColor Yellow
    Write-TestResult "h2spec Compliance" $false "Tool not installed"
}

# Cleanup
if ($serverProcess) {
    Write-TestHeader "Cleanup"
    Write-Host "Stopping test server..." -ForegroundColor Yellow
    Stop-Process -Id $serverProcess.Id -Force -ErrorAction SilentlyContinue
    Write-Host "Server stopped" -ForegroundColor Green
}

# Summary Report
Write-TestHeader "TEST SUMMARY"
$passedTests = ($testResults | Where-Object { $_.Passed }).Count
$totalTests = $testResults.Count
$passRate = [math]::Round(($passedTests / $totalTests) * 100, 1)

Write-Host "Total Tests:  $totalTests" -ForegroundColor White
Write-Host "Passed:       $passedTests" -ForegroundColor Green
Write-Host "Failed:       $($totalTests - $passedTests)" -ForegroundColor $(if ($passedTests -eq $totalTests) { "Green" } else { "Red" })
Write-Host "Pass Rate:    $passRate%" -ForegroundColor $(if ($passRate -eq 100) { "Green" } elseif ($passRate -ge 70) { "Yellow" } else { "Red" })

Write-Host "`nDetailed Results:" -ForegroundColor White
$testResults | Format-Table -Property Test, Passed, Details -AutoSize

# Export results
$reportPath = "security-test-report-$(Get-Date -Format 'yyyyMMdd-HHmmss').json"
$testResults | ConvertTo-Json | Out-File $reportPath
Write-Host "`n📄 Full report saved to: $reportPath" -ForegroundColor Cyan

# Exit code
if ($passedTests -eq $totalTests) {
    Write-Host "`n✅ ALL SECURITY TESTS PASSED!" -ForegroundColor Green
    exit 0
} else {
    Write-Host "`n⚠️  SOME TESTS FAILED - Review results above" -ForegroundColor Yellow
    exit 1
}
