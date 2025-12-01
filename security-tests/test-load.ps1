# HTTP/2 Load Testing Script
# Tests server performance and DoS protections

param(
    [string]$ServerUrl = "https://localhost:5001",
    [int]$Requests = 1000,
    [int]$Clients = 10,
    [string]$Endpoint = "/api/health"
)

Write-Host "========================================" -ForegroundColor Cyan
Write-Host "HTTP/2 Load Testing" -ForegroundColor Cyan
Write-Host "========================================`n" -ForegroundColor Cyan

if (-not (Get-Command h2load -ErrorAction SilentlyContinue)) {
    Write-Host "❌ h2load not found!" -ForegroundColor Red
    Write-Host "Install with: choco install nghttp2" -ForegroundColor Yellow
    Write-Host "Or download from: https://github.com/nghttp2/nghttp2/releases" -ForegroundColor Yellow
    exit 1
}

$url = "$ServerUrl$Endpoint"

Write-Host "Configuration:" -ForegroundColor Yellow
Write-Host "  URL:         $url"
Write-Host "  Requests:    $Requests"
Write-Host "  Clients:     $Clients"
Write-Host "  Protocol:    HTTP/2`n"

# Test 1: Basic load test
Write-Host "[1/4] Basic Load Test" -ForegroundColor Green
h2load -n $Requests -c $Clients -t 2 $url

# Test 2: High concurrency
Write-Host "`n[2/4] High Concurrency Test (100 clients)" -ForegroundColor Green
h2load -n 1000 -c 100 -t 4 $url

# Test 3: Stream limit test (should enforce max 100 concurrent streams)
Write-Host "`n[3/4] Stream Limit Test (200 concurrent streams - should reject some)" -ForegroundColor Green
h2load -n 1000 -c 1 -m 200 $url

# Test 4: Large header test (should enforce 8KB limit)
Write-Host "`n[4/4] Large Header Test (should reject >8KB headers)" -ForegroundColor Green
$largeValue = "A" * 9000
h2load -n 10 -c 1 -H "X-Large-Header: $largeValue" $url

Write-Host "`n✅ Load tests complete!" -ForegroundColor Green
Write-Host "Review the results above for any errors or rejections" -ForegroundColor Yellow
