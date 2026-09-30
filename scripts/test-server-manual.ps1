# Manual server test
Write-Host "Building..." -ForegroundColor Cyan
cd C:\Projects\EffinitiveFrameowrk
dotnet build -c Release

Write-Host "`nStarting server manually..." -ForegroundColor Cyan
cd C:\Projects\EffinitiveFrameowrk\benchmarks\EffinitiveFramework.Benchmarks
dotnet run -c Release
