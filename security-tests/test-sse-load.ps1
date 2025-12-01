# Server-Sent Events Load Testing Script
# Tests SSE streaming under concurrent load

param(
    [string]$ServerUrl = "https://localhost:5001",
    [int]$Clients = 10,
    [int]$DurationSeconds = 30,
    [string]$Endpoint = "/api/stream/time"
)

Write-Host "========================================" -ForegroundColor Cyan
Write-Host "SSE Load Testing" -ForegroundColor Cyan
Write-Host "========================================`n" -ForegroundColor Cyan

Write-Host "Configuration:" -ForegroundColor Yellow
Write-Host "  URL:         $ServerUrl$Endpoint"
Write-Host "  Clients:     $Clients"
Write-Host "  Duration:    $DurationSeconds seconds`n"

Write-Host "Starting $Clients concurrent SSE clients..." -ForegroundColor Green

$jobs = @()
$url = "$ServerUrl$Endpoint"

for ($i = 1; $i -le $Clients; $i++) {
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
            $dataBytes = 0
            $stopwatch = [System.Diagnostics.Stopwatch]::StartNew()
            
            while ($stopwatch.Elapsed.TotalSeconds -lt $duration) {
                $line = $reader.ReadLine()
                if ($line -match "^data:") {
                    $eventCount++
                    $dataBytes += $line.Length
                }
            }
            
            return @{
                Success = $true
                ClientId = $clientId
                Events = $eventCount
                DataBytes = $dataBytes
                Duration = $stopwatch.Elapsed.TotalSeconds
            }
        }
        catch {
            return @{
                Success = $false
                ClientId = $clientId
                Error = $_.Exception.Message
            }
        }
        finally {
            if ($client) { $client.Dispose() }
        }
    } -ArgumentList $url, $DurationSeconds, $i
    
    $jobs += $job
    Write-Host "  Started client $i" -ForegroundColor Gray
}

Write-Host "`nWaiting for clients to complete..." -ForegroundColor Yellow
$results = $jobs | Wait-Job | Receive-Job
$jobs | Remove-Job

# Analyze results
Write-Host "`n========================================" -ForegroundColor Cyan
Write-Host "Results" -ForegroundColor Cyan
Write-Host "========================================`n" -ForegroundColor Cyan

$successful = $results | Where-Object { $_.Success }
$failed = $results | Where-Object { -not $_.Success }

Write-Host "Successful Clients: $($successful.Count)/$Clients" -ForegroundColor $(if ($successful.Count -eq $Clients) { "Green" } else { "Yellow" })

if ($failed.Count -gt 0) {
    Write-Host "Failed Clients:     $($failed.Count)" -ForegroundColor Red
    foreach ($fail in $failed) {
        Write-Host "  Client $($fail.ClientId): $($fail.Error)" -ForegroundColor Red
    }
}

if ($successful.Count -gt 0) {
    $totalEvents = ($successful | Measure-Object -Property Events -Sum).Sum
    $totalData = ($successful | Measure-Object -Property DataBytes -Sum).Sum
    $avgEvents = [math]::Round($totalEvents / $successful.Count, 2)
    $avgData = [math]::Round($totalData / $successful.Count / 1KB, 2)
    $eventsPerSec = [math]::Round($totalEvents / $DurationSeconds, 2)
    
    Write-Host "`nPerformance Metrics:" -ForegroundColor Yellow
    Write-Host "  Total Events:        $totalEvents"
    Write-Host "  Total Data:          $([math]::Round($totalData / 1KB, 2)) KB"
    Write-Host "  Avg Events/Client:   $avgEvents"
    Write-Host "  Avg Data/Client:     $avgData KB"
    Write-Host "  Events/Second:       $eventsPerSec"
    Write-Host "  Throughput:          $([math]::Round($totalData / $DurationSeconds / 1KB, 2)) KB/s"
    
    Write-Host "`nPer-Client Results:" -ForegroundColor Yellow
    $successful | Sort-Object ClientId | ForEach-Object {
        Write-Host ("  Client {0}: {1} events, {2} KB, {3:F2}s" -f $_.ClientId, $_.Events, [math]::Round($_.DataBytes / 1KB, 2), $_.Duration)
    }
}

Write-Host "`n✅ SSE load test complete!" -ForegroundColor Green

# Exit code
if ($successful.Count -eq $Clients) {
    exit 0
} else {
    exit 1
}
