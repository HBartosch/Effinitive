# Manual HTTP/2 POST test script
$handler = New-Object System.Net.Http.HttpClientHandler
$handler.ServerCertificateCustomValidationCallback = { $true }
$handler.SslProtocols = [System.Security.Authentication.SslProtocols]::Tls12 -bor [System.Security.Authentication.SslProtocols]::Tls13

$client = New-Object System.Net.Http.HttpClient($handler)
$client.DefaultRequestVersion = [System.Net.HttpVersion]::Version20
$client.DefaultVersionPolicy = [System.Net.Http.HttpVersionPolicy]::RequestVersionExact

Write-Host "Testing EffinitiveFramework HTTP/2 POST..." -ForegroundColor Cyan

try {
    $requestData = @{
        Name = "HTTP/2 Test"
        Email = "http2@test.com"
    } | ConvertTo-Json

    $content = [System.Net.Http.StringContent]::new($requestData, [System.Text.Encoding]::UTF8, "application/json")
    
    $response = $client.PostAsync("https://localhost:6001/api/http2-benchmark", $content).Result
    
    Write-Host "Status: $($response.StatusCode)" -ForegroundColor Green
    Write-Host "Version: $($response.Version)" -ForegroundColor Green
    
    $responseBody = $response.Content.ReadAsStringAsync().Result
    Write-Host "Response: $responseBody" -ForegroundColor Green
} catch {
    Write-Host "Error: $_" -ForegroundColor Red
    Write-Host "Inner Exception: $($_.Exception.InnerException)" -ForegroundColor Red
}

$client.Dispose()
$handler.Dispose()
