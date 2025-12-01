# EffinitiveFramework Security Testing Suite

Comprehensive security testing scripts using **100% FREE tools**.

## Quick Start

### 1. Install Tools (One-Time Setup)

```powershell
.\security-tests\setup-tools.ps1
```

This installs:
- ✅ Microsoft.CodeAnalysis.NetAnalyzers (static analysis)
- ✅ SecurityCodeScan.VS2019 (security scanning)
- ✅ SharpFuzz (fuzzing)
- ✅ nghttp2/h2load (HTTP/2 load testing)
- ✅ h2spec (HTTP/2 compliance)
- ✅ Python/slowloris (Slowloris attack testing)

### 2. Run All Tests

```powershell
.\security-tests\run-all-tests.ps1
```

Runs 7 comprehensive security tests:
1. **Unit Tests** - All 71 framework tests
2. **Dependency Scan** - Check for vulnerable packages
3. **Static Analysis** - Roslyn + SecurityCodeScan
4. **HTTP/2 Load Test** - h2load stress testing
5. **SSE Load Test** - 10 concurrent streaming clients
6. **Slowloris Protection** - Slow request attack
7. **HTTP/2 Compliance** - h2spec protocol validation

### 3. Run Individual Tests

```powershell
# HTTP/2 load testing
.\security-tests\test-load.ps1

# SSE streaming load test
.\security-tests\test-sse-load.ps1 -Clients 50 -DurationSeconds 60

# Slowloris attack protection
.\security-tests\test-slowloris.ps1
```

## Test Coverage

### 1. Unit Tests ✅
**What it tests:** All framework functionality including DoS protections

**Command:**
```powershell
dotnet test --configuration Release
```

**Expected:** 71/71 tests passing

---

### 2. Dependency Scanning ✅
**What it tests:** Vulnerable NuGet packages

**Command:**
```powershell
dotnet list package --vulnerable
```

**Expected:** No vulnerabilities found

---

### 3. Static Code Analysis ✅
**What it tests:** Security vulnerabilities, code quality issues

**Tools:**
- Microsoft.CodeAnalysis.NetAnalyzers
- SecurityCodeScan.VS2019

**Command:**
```powershell
dotnet build /p:RunAnalyzers=true /p:EnforceCodeStyleInBuild=true
```

**Expected:** Build succeeds with no critical warnings

---

### 4. HTTP/2 Load Testing ✅
**What it tests:** Performance, DoS protections, concurrent streams limit

**Tool:** h2load (nghttp2)

**Tests:**
- Basic load: 1000 requests, 10 clients
- High concurrency: 1000 requests, 100 clients
- Stream limit: 200 concurrent streams (should reject >100)
- Large headers: >8KB headers (should reject)

**Command:**
```powershell
.\security-tests\test-load.ps1
```

**Expected:** Server handles load correctly and enforces limits

---

### 5. SSE Load Testing ✅
**What it tests:** Server-Sent Events under concurrent load

**Tests:**
- 10+ concurrent SSE connections
- Event streaming for 30-60 seconds
- Connection cleanup and resource management

**Command:**
```powershell
.\security-tests\test-sse-load.ps1 -Clients 10 -DurationSeconds 30
```

**Expected:** All clients receive events, no memory leaks

---

### 6. Slowloris Protection ✅
**What it tests:** Slow request attack (DoS)

**Tool:** Python slowloris

**Tests:**
- Manual slow request (incomplete HTTP request)
- Python slowloris attack (50 connections, 1 byte/sec)
- Server timeout enforcement (30 seconds)

**Command:**
```powershell
.\security-tests\test-slowloris.ps1
```

**Expected:** Connections timeout after 30s, server remains responsive

---

### 7. HTTP/2 Compliance ✅
**What it tests:** RFC 7540 protocol compliance

**Tool:** h2spec

**Tests:**
- Frame validation
- Stream states
- Header compression
- Flow control
- Error handling

**Command:**
```powershell
h2spec -h localhost -p 5001 -t -k
```

**Expected:** All tests pass (or minor skips for non-implemented features)

---

## CI/CD Integration

Add to GitHub Actions:

```yaml
name: Security Tests

on: [push, pull_request]

jobs:
  security:
    runs-on: windows-latest
    steps:
      - uses: actions/checkout@v3
      
      - name: Setup .NET
        uses: actions/setup-dotnet@v3
        with:
          dotnet-version: '8.0.x'
      
      - name: Install Tools
        run: .\security-tests\setup-tools.ps1
        
      - name: Run Security Tests
        run: .\security-tests\run-all-tests.ps1
```

---

## Test Results

After running tests, view the report:

```powershell
Get-Content security-test-report-*.json | ConvertFrom-Json | Format-Table
```

Example output:
```
Test                  Passed  Details
----                  ------  -------
Unit Tests            True    Passed: 71 tests
Dependency Scan       True    No vulnerabilities detected
Static Analysis       True    Build succeeded with 0 warnings
h2load Load Test      True    Throughput: 25000 req/s
SSE Load Test         True    10/10 clients succeeded, 600 total events
Slowloris Protection  True    Server remained responsive after attack
h2spec Compliance     True    150 passed, 0 failed
```

---

## Tools Installation Guide

### Windows

```powershell
# Run automated setup
.\security-tests\setup-tools.ps1

# Or manually:
choco install nghttp2 python
pip install slowloris
dotnet tool install --global SharpFuzz.CommandLine
```

### Linux

```bash
# Install nghttp2
sudo apt-get install nghttp2-client

# Install h2spec
wget https://github.com/summerwind/h2spec/releases/download/v2.6.0/h2spec_linux_amd64.tar.gz
tar -xzf h2spec_linux_amd64.tar.gz
sudo mv h2spec /usr/local/bin/

# Install Python tools
pip install slowloris

# Install SharpFuzz
dotnet tool install --global SharpFuzz.CommandLine
```

### macOS

```bash
# Install via Homebrew
brew install nghttp2
brew install python

pip install slowloris
dotnet tool install --global SharpFuzz.CommandLine

# h2spec
wget https://github.com/summerwind/h2spec/releases/download/v2.6.0/h2spec_darwin_amd64.tar.gz
tar -xzf h2spec_darwin_amd64.tar.gz
sudo mv h2spec /usr/local/bin/
```

---

## Troubleshooting

### h2load not found
```powershell
choco install nghttp2
# Or download from: https://github.com/nghttp2/nghttp2/releases
```

### Python not found
```powershell
choco install python
# Or download from: https://www.python.org/downloads/
```

### h2spec not found
```powershell
# Download from: https://github.com/summerwind/h2spec/releases
# Extract to security-tests\tools\h2spec.exe
```

### SSL Certificate Errors
Tests use self-signed certificates by default. This is normal and expected.

---

## Security Test Checklist

Before releasing:

- [ ] All 71 unit tests passing
- [ ] No vulnerable dependencies
- [ ] Static analysis clean
- [ ] h2load load test passes
- [ ] SSE load test passes (10+ clients)
- [ ] Slowloris protection verified
- [ ] h2spec compliance verified
- [ ] Manual penetration testing (optional)
- [ ] Security assessment reviewed

---

## Contributing

To add new security tests:

1. Create script in `security-tests/`
2. Add test to `run-all-tests.ps1`
3. Document in this README
4. Update `SECURITY_ASSESSMENT.md`

---

## Resources

- [SECURITY_ASSESSMENT.md](../SECURITY_ASSESSMENT.md) - Full security analysis
- [RFC 7540 - HTTP/2](https://datatracker.ietf.org/doc/html/rfc7540)
- [OWASP Top 10](https://owasp.org/www-project-top-ten/)
- [h2spec](https://github.com/summerwind/h2spec)
- [nghttp2](https://nghttp2.org/)

---

## License

Same as EffinitiveFramework - MIT License
