# Test Benchmark Endpoints
# Quick test to verify the benchmark endpoints return correct responses

Write-Host "Testing Benchmark Endpoints" -ForegroundColor Cyan
Write-Host "==============================`n" -ForegroundColor Cyan

# Start server in background
Write-Host "Starting server..." -ForegroundColor Yellow
$server = Start-Process -FilePath "dotnet" -ArgumentList "run --project samples/EffinitiveFramework.Sample --configuration Release" -PassThru -WindowStyle Hidden
Start-Sleep -Seconds 6

try {
    # Test 1: GET / (should return empty string)
    Write-Host "`n[Test 1] GET /" -ForegroundColor Green
    $response = Invoke-WebRequest -Uri "http://localhost:5000/" -Method GET
    $body = $response.Content
    Write-Host "  Status: $($response.StatusCode)"
    Write-Host "  Body: '$body'"
    Write-Host "  Length: $($body.Length)"
    if ($body.Length -eq 0) {
        Write-Host "  ✅ PASS: Empty response" -ForegroundColor Green
    } else {
        Write-Host "  ❌ FAIL: Expected empty, got: $body" -ForegroundColor Red
    }

    # Test 2: GET /user/0 (should return "0" as plain text)
    Write-Host "`n[Test 2] GET /user/0" -ForegroundColor Green
    $response = Invoke-WebRequest -Uri "http://localhost:5000/user/0" -Method GET
    $body = $response.Content
    Write-Host "  Status: $($response.StatusCode)"
    Write-Host "  Body: '$body'"
    Write-Host "  ContentType: $($response.Headers['Content-Type'])"
    if ($body -eq "0") {
        Write-Host "  ✅ PASS: Returns '0' as plain text" -ForegroundColor Green
    } else {
        Write-Host "  ❌ FAIL: Expected '0', got: $body" -ForegroundColor Red
    }

    # Test 3: GET /user/123 (should return "123" as plain text)
    Write-Host "`n[Test 3] GET /user/123" -ForegroundColor Green
    $response = Invoke-WebRequest -Uri "http://localhost:5000/user/123" -Method GET
    $body = $response.Content
    Write-Host "  Status: $($response.StatusCode)"
    Write-Host "  Body: '$body'"
    if ($body -eq "123") {
        Write-Host "  ✅ PASS: Returns '123' as plain text" -ForegroundColor Green
    } else {
        Write-Host "  ❌ FAIL: Expected '123', got: $body" -ForegroundColor Red
    }

    # Test 4: POST /user (should return empty string)
    Write-Host "`n[Test 4] POST /user" -ForegroundColor Green
    $response = Invoke-WebRequest -Uri "http://localhost:5000/user" -Method POST
    $body = $response.Content
    Write-Host "  Status: $($response.StatusCode)"
    Write-Host "  Body: '$body'"
    Write-Host "  Length: $($body.Length)"
    if ($body.Length -eq 0) {
        Write-Host "  ✅ PASS: Empty response" -ForegroundColor Green
    } else {
        Write-Host "  ❌ FAIL: Expected empty, got: $body" -ForegroundColor Red
    }

} catch {
    Write-Host "`n❌ Error during testing: $_" -ForegroundColor Red
} finally {
    Write-Host "`nStopping server..." -ForegroundColor Yellow
    Stop-Process -Id $server.Id -Force -ErrorAction SilentlyContinue
    Write-Host "Done!" -ForegroundColor Green
}
