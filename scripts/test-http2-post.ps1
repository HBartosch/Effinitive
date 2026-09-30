# Test HTTP/2 POST to EffinitiveFramework

Write-Host "Testing EffinitiveFramework HTTP/2 POST..." -ForegroundColor Cyan

# Create HTTP/2 client
Add-Type -AssemblyName System.Net.Http

$handler = New-Object System.Net.Http.HttpClientHandler
$handler.ServerCertificateCustomValidationCallback = { $true }

$client = New-Object System.Net.Http.HttpClient($handler)
$client.DefaultRequestVersion = [System.Net.HttpVersion]::Version20
$client.DefaultVersionPolicy = [System.Net.Http.HttpVersionPolicy]::RequestVersionExact

try {
    # Test POST
    $json = '{"Name":"Test User","Email":"test@example.com"}'
    $content = New-Object System.Net.Http.StringContent($json, [System.Text.Encoding]::UTF8, "application/json")
    
    $response = $client.PostAsync("https://localhost:6001/api/http2-benchmark", $content).Result
    
    Write-Host "Status: $($response.StatusCode)" -ForegroundColor Green
    Write-Host "Version: $($response.Version)" -ForegroundColor Green
    Write-Host "Body: $($response.Content.ReadAsStringAsync().Result)" -ForegroundColor Yellow
}
catch {
    Write-Host "Error: $_" -ForegroundColor Red
}
finally {
    $client.Dispose()
}
