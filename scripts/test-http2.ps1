# Test HTTP/2 Implementation

Write-Host "`n========================================" -ForegroundColor Cyan
Write-Host "  HTTP/2 Integration Test Suite" -ForegroundColor Cyan
Write-Host "========================================`n" -ForegroundColor Cyan

# Test 1: HTTP/1.1 endpoint
Write-Host "Test 1: HTTP/1.1 GET Request" -ForegroundColor Yellow
try {
    $response1 = Invoke-RestMethod -Uri "http://localhost:5000/api/users" -Method GET
    Write-Host "✅ HTTP/1.1 GET: " -NoNewline -ForegroundColor Green
    Write-Host "$($response1.Count) users returned"
} catch {
    Write-Host "❌ HTTP/1.1 GET failed: $_" -ForegroundColor Red
}

Write-Host ""

# Test 2: HTTP/1.1 POST
Write-Host "Test 2: HTTP/1.1 POST Request" -ForegroundColor Yellow
try {
    $user = @{
        name = "HTTP/1.1 User"
        email = "http11@test.com"
    } | ConvertTo-Json
    
    $response2 = Invoke-RestMethod -Uri "http://localhost:5000/api/users" -Method POST -Body $user -ContentType "application/json"
    Write-Host "✅ HTTP/1.1 POST: " -NoNewline -ForegroundColor Green
    Write-Host "User created - $($response2.name)"
} catch {
    Write-Host "❌ HTTP/1.1 POST failed: $_" -ForegroundColor Red
}

Write-Host ""

# Test 3: Check if HTTPS is available (HTTP/2)
Write-Host "Test 3: HTTPS/HTTP2 Availability Check" -ForegroundColor Yellow
try {
    # Test with HttpClient to see HTTP/2 negotiation
    $handler = [System.Net.Http.HttpClientHandler]::new()
    $handler.ServerCertificateCustomValidationCallback = { $true }
    
    $client = [System.Net.Http.HttpClient]::new($handler)
    $client.DefaultRequestVersion = [System.Net.Http.HttpVersion]::Version20
    
    $uri = [System.Uri]::new("https://localhost:5001/api/users")
    $response = $client.GetAsync($uri).GetAwaiter().GetResult()
    
    $version = $response.Version.ToString()
    $statusCode = $response.StatusCode
    
    if ($version -eq "2.0") {
        Write-Host "✅ HTTP/2 Negotiated: " -NoNewline -ForegroundColor Green
        Write-Host "Version $version, Status $statusCode"
    } else {
        Write-Host "⚠️  HTTP/2 Not Negotiated: " -NoNewline -ForegroundColor Yellow
        Write-Host "Version $version (expected 2.0)"
    }
    
    $client.Dispose()
    $handler.Dispose()
} catch {
    Write-Host "❌ HTTPS/HTTP2 test failed: $_" -ForegroundColor Red
}

Write-Host ""
Write-Host "========================================" -ForegroundColor Cyan
Write-Host "  Test Suite Complete" -ForegroundColor Cyan
Write-Host "========================================`n" -ForegroundColor Cyan

Write-Host "Note: For full HTTP/2 testing, use:" -ForegroundColor Gray
Write-Host "  curl -k --http2 https://localhost:5001/api/users -v" -ForegroundColor Gray
Write-Host ""
