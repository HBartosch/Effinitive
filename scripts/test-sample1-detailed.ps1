# Detailed Test Script for EffinitiveFramework Sample 1
# Tests all endpoints and outputs actual response data

Write-Host "========================================" -ForegroundColor Cyan
Write-Host "  Sample 1 - Detailed Endpoint Testing" -ForegroundColor Cyan
Write-Host "========================================" -ForegroundColor Cyan
Write-Host ""

# Start the server
Write-Host "Starting Main Sample server..." -ForegroundColor Yellow
$serverProcess = Start-Process powershell -ArgumentList "-NoExit", "-Command", "cd samples\EffinitiveFramework.Sample; dotnet run -c Release" -PassThru -WindowStyle Minimized
Start-Sleep -Seconds 3

$baseUrl = "http://localhost:5000"
$testResults = @()
$testCount = 0
$passCount = 0

function Test-Endpoint {
    param(
        [string]$Name,
        [string]$Method,
        [string]$Url,
        [string]$Body = $null,
        [string]$ContentType = "application/json",
        [int]$ExpectedStatus = 200
    )
    
    $script:testCount++
    
    Write-Host "`n[$script:testCount] Testing: $Name" -ForegroundColor Cyan
    Write-Host "  Method: $Method" -ForegroundColor Gray
    Write-Host "  URL: $Url" -ForegroundColor Gray
    
    try {
        $params = @{
            Uri = $Url
            Method = $Method
            UseBasicParsing = $true
            ErrorAction = 'Stop'
        }
        
        if ($Body) {
            $params['Body'] = $Body
            $params['ContentType'] = $ContentType
            Write-Host "  Body: $Body" -ForegroundColor Gray
        }
        
        $response = Invoke-WebRequest @params
        $status = $response.StatusCode
        $contentType = $response.Headers['Content-Type']
        $content = $response.Content
        
        Write-Host "  Status: $status" -ForegroundColor Green
        Write-Host "  Content-Type: $contentType" -ForegroundColor Gray
        
        if ($content) {
            Write-Host "  Response:" -ForegroundColor Magenta
            if ($contentType -like "*json*") {
                try {
                    $json = $content | ConvertFrom-Json
                    $formatted = $json | ConvertTo-Json -Depth 10
                    Write-Host $formatted -ForegroundColor White
                } catch {
                    Write-Host $content -ForegroundColor White
                }
            } else {
                if ($content.Length -gt 200) {
                    Write-Host "  [Content too long, showing first 200 chars]" -ForegroundColor Yellow
                    Write-Host "  $($content.Substring(0, 200))..." -ForegroundColor White
                } else {
                    Write-Host "  $content" -ForegroundColor White
                }
            }
        } else {
            Write-Host "  Response: [Empty]" -ForegroundColor Gray
        }
        
        if ($status -eq $ExpectedStatus) {
            Write-Host "  ✅ PASS" -ForegroundColor Green
            $script:passCount++
            $result = "PASS"
        } else {
            Write-Host "  ❌ FAIL: Expected $ExpectedStatus, got $status" -ForegroundColor Red
            $result = "FAIL"
        }
        
        $script:testResults += [PSCustomObject]@{
            Test = $Name
            Status = $status
            Result = $result
        }
        
    } catch {
        Write-Host "  ❌ FAIL: $($_.Exception.Message)" -ForegroundColor Red
        $script:testResults += [PSCustomObject]@{
            Test = $Name
            Status = "ERROR"
            Result = "FAIL"
        }
    }
}

Write-Host "`n=== FastEndpoints-Style Endpoints ===" -ForegroundColor Yellow

Test-Endpoint `
    -Name "GET / (Home)" `
    -Method "GET" `
    -Url "$baseUrl/"

Test-Endpoint `
    -Name "GET /user/123 (Route Parameter)" `
    -Method "GET" `
    -Url "$baseUrl/user/123"

Test-Endpoint `
    -Name "GET /user/alice (Route Parameter)" `
    -Method "GET" `
    -Url "$baseUrl/user/alice"

Test-Endpoint `
    -Name "POST /user (Empty Response)" `
    -Method "POST" `
    -Url "$baseUrl/user"

Write-Host "`n=== ValueTask Endpoints (Synchronous) ===" -ForegroundColor Yellow

Test-Endpoint `
    -Name "GET /api/users (List Users)" `
    -Method "GET" `
    -Url "$baseUrl/api/users"

Test-Endpoint `
    -Name "GET /health (Health Check)" `
    -Method "GET" `
    -Url "$baseUrl/health"

Write-Host "`n=== Task/Async Endpoints (I/O Operations) ===" -ForegroundColor Yellow

Test-Endpoint `
    -Name "POST /api/users (Create User)" `
    -Method "POST" `
    -Url "$baseUrl/api/users" `
    -Body '{"name":"John Doe","email":"john@example.com"}' `
    -ContentType "application/json"

Write-Host "`n=== Additional Endpoints ===" -ForegroundColor Yellow

Test-Endpoint `
    -Name "GET /api/health (API Health)" `
    -Method "GET" `
    -Url "$baseUrl/api/health"

Test-Endpoint `
    -Name "GET /api/plain (Plain Text)" `
    -Method "GET" `
    -Url "$baseUrl/api/plain"

Test-Endpoint `
    -Name "GET /api/html (HTML Content)" `
    -Method "GET" `
    -Url "$baseUrl/api/html"

Test-Endpoint `
    -Name "GET /api/stats/database (Database Stats)" `
    -Method "GET" `
    -Url "$baseUrl/api/stats/database"

# Stop the server
Write-Host "`nStopping server..." -ForegroundColor Yellow
Stop-Process -Id $serverProcess.Id -Force

# Summary
Write-Host "`n========================================" -ForegroundColor Cyan
Write-Host "  Test Summary" -ForegroundColor Cyan
Write-Host "========================================" -ForegroundColor Cyan
Write-Host "Total Tests: $testCount" -ForegroundColor White
Write-Host "Passed: $passCount" -ForegroundColor Green
Write-Host "Failed: $($testCount - $passCount)" -ForegroundColor $(if ($testCount -eq $passCount) { "Green" } else { "Red" })
Write-Host "Success Rate: $([math]::Round(($passCount / $testCount) * 100, 2))%" -ForegroundColor White

Write-Host "`nDetailed Results:" -ForegroundColor Yellow
$testResults | Format-Table -AutoSize

if ($testCount -eq $passCount) {
    Write-Host "`n🎉 All tests passed!" -ForegroundColor Green
} else {
    Write-Host "`n⚠️ Some tests failed. Please review the output above." -ForegroundColor Yellow
}
