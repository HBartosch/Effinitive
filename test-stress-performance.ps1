# Stress Test Script - Pure PowerShell Implementation
# No external dependencies required

Write-Host "🚀 EffinitiveFramework Stress Test" -ForegroundColor Cyan
Write-Host "===================================" -ForegroundColor Cyan
Write-Host ""

# Simple concurrent HTTP test using runspaces
function Invoke-StressTest {
    param(
        [string]$Url,
        [int]$Connections,
        [int]$DurationSeconds
    )
    
    Write-Host "Running test: $Connections concurrent connections for ${DurationSeconds}s..." -ForegroundColor Yellow
    
    $runspacePool = [runspacefactory]::CreateRunspacePool(1, $Connections)
    $runspacePool.Open()
    
    $scriptBlock = {
        param($url, $durationSeconds)
        
        $client = New-Object System.Net.Http.HttpClient
        $client.Timeout = [TimeSpan]::FromSeconds(5)
        
        $count = 0
        $errors = 0
        $stopwatch = [System.Diagnostics.Stopwatch]::StartNew()
        
        while ($stopwatch.Elapsed.TotalSeconds -lt $durationSeconds) {
            try {
                $response = $client.GetAsync($url).Result
                if ($response.IsSuccessStatusCode) {
                    $count++
                }
                $response.Dispose()
            } catch {
                $errors++
            }
        }
        
        $client.Dispose()
        
        return @{
            Requests = $count
            Errors = $errors
            Duration = $stopwatch.Elapsed.TotalSeconds
        }
    }
    
    # Start all workers
    $jobs = @()
    $stopwatch = [System.Diagnostics.Stopwatch]::StartNew()
    
    for ($i = 0; $i -lt $Connections; $i++) {
        $ps = [PowerShell]::Create()
        $ps.RunspacePool = $runspacePool
        [void]$ps.AddScript($scriptBlock).AddArgument($Url).AddArgument($DurationSeconds)
        
        $jobs += [PSCustomObject]@{
            Pipe = $ps
            Status = $ps.BeginInvoke()
        }
    }
    
    # Wait for completion
    $totalRequests = 0
    $totalErrors = 0
    
    foreach ($job in $jobs) {
        $result = $job.Pipe.EndInvoke($job.Status)
        $totalRequests += $result.Requests
        $totalErrors += $result.Errors
        $job.Pipe.Dispose()
    }
    
    $stopwatch.Stop()
    $runspacePool.Close()
    $runspacePool.Dispose()
    
    $elapsed = $stopwatch.Elapsed.TotalSeconds
    $reqPerSec = [math]::Round($totalRequests / $elapsed, 0)
    
    Write-Host "  ✅ Completed: $totalRequests requests in $([math]::Round($elapsed, 1))s" -ForegroundColor Green
    Write-Host "  📊 Rate: $reqPerSec req/s" -ForegroundColor Cyan
    if ($totalErrors -gt 0) {
        Write-Host "  ⚠️  Errors: $totalErrors" -ForegroundColor Yellow
    }
    Write-Host ""
    
    return [PSCustomObject]@{
        Connections = $Connections
        TotalRequests = $totalRequests
        Duration = $elapsed
        ReqPerSec = $reqPerSec
        Errors = $totalErrors
    }
}

# Start server in background
Write-Host "Starting server in Release mode..." -ForegroundColor Yellow
$serverProcess = Start-Process powershell -ArgumentList "-NoExit", "-Command", "cd $PSScriptRoot; dotnet run --configuration Release --project samples/EffinitiveFramework.Sample" -PassThru -WindowStyle Minimized

# Wait for server to start
Write-Host "Waiting for server to initialize..." -ForegroundColor Yellow
Start-Sleep -Seconds 10

# Test connection
try {
    $null = Invoke-WebRequest "http://localhost:5000/" -ErrorAction Stop
    Write-Host "✅ Server is ready!" -ForegroundColor Green
    Write-Host ""
} catch {
    Write-Host "❌ Server failed to start" -ForegroundColor Red
    Stop-Process -Id $serverProcess.Id -Force
    exit 1
}

# Run stress tests with increasing concurrency
$tests = @(
    @{ Connections = 64; Duration = 10 },
    @{ Connections = 256; Duration = 10 },
    @{ Connections = 512; Duration = 10 }
)

$results = @()

foreach ($test in $tests) {
    Write-Host "━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━" -ForegroundColor Cyan
    Write-Host "Test: $($test.Connections) concurrent connections" -ForegroundColor Cyan
    Write-Host "━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━" -ForegroundColor Cyan
    Write-Host ""
    
    $result = Invoke-StressTest -Url "http://localhost:5000/" -Connections $test.Connections -DurationSeconds $test.Duration
    $results += $result
}

# Summary
Write-Host "━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━" -ForegroundColor Green
Write-Host "SUMMARY - Performance Results" -ForegroundColor Green
Write-Host "━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━" -ForegroundColor Green
Write-Host ""
Write-Host "Concurrency | Requests/sec | Target    | Status" -ForegroundColor White
Write-Host "------------|--------------|-----------|--------" -ForegroundColor White

$targets = @{ 64 = 35000; 256 = 42000; 512 = 40000 }

foreach ($result in $results) {
    $target = $targets[$result.Connections]
    $actual = $result.ReqPerSec
    $status = if ($actual -ge $target) { "✅ PASS" } else { "⚠️  BELOW" }
    $color = if ($actual -ge $target) { "Green" } else { "Yellow" }
    
    Write-Host ("{0,-11} | {1,12:N0} | {2,9:N0} | {3}" -f $result.Connections, $result.ReqPerSec, $target, $status) -ForegroundColor $color
}

Write-Host ""
Write-Host "Baseline Comparison:" -ForegroundColor Cyan
Write-Host "  GenHTTP:                 39,923 req/s" -ForegroundColor White
Write-Host "  Before Optimization:     ~15,000 req/s" -ForegroundColor Yellow
Write-Host ""

# Performance analysis
$avg = ($results | Measure-Object -Property ReqPerSec -Average).Average
if ($avg -ge 35000) {
    Write-Host "🎉 EXCELLENT! Performance meets production targets" -ForegroundColor Green
} elseif ($avg -ge 25000) {
    Write-Host "✅ GOOD! Significant improvement, continue optimizing" -ForegroundColor Cyan
} else {
    Write-Host "⚠️  NEEDS WORK! Review optimization checklist" -ForegroundColor Yellow
}

# Cleanup
Write-Host "Stopping server..." -ForegroundColor Yellow
Stop-Process -Id $serverProcess.Id -Force
Write-Host "✅ Done!" -ForegroundColor Green
