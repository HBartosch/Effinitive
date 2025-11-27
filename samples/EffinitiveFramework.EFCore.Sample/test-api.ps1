# Test script for EffinitiveFramework + Entity Framework Core Sample

$baseUrl = "http://localhost:5000"

Write-Host "═══════════════════════════════════════════════" -ForegroundColor Cyan
Write-Host "   EffinitiveFramework + EF Core API Test" -ForegroundColor Cyan
Write-Host "═══════════════════════════════════════════════" -ForegroundColor Cyan
Write-Host ""

# Test 1: Get all products
Write-Host "1️⃣  GET /api/products (Get all products)" -ForegroundColor Yellow
$response = Invoke-RestMethod -Uri "$baseUrl/api/products" -Method Get
Write-Host "   Found $($response.Count) products" -ForegroundColor Green
$response | Select-Object -First 2 | Format-Table Id, Name, Category, Price, Stock -AutoSize
Write-Host ""

# Test 2: Get product by ID
Write-Host "2️⃣  GET /api/products/1 (Get specific product)" -ForegroundColor Yellow
$product = Invoke-RestMethod -Uri "$baseUrl/api/products/1" -Method Get
Write-Host "   Product: $($product.name) - `$$($product.price)" -ForegroundColor Green
Write-Host ""

# Test 3: Get products by category
Write-Host "3️⃣  GET /api/products/category/Electronics" -ForegroundColor Yellow
$electronics = Invoke-RestMethod -Uri "$baseUrl/api/products/category/Electronics" -Method Get
Write-Host "   Found $($electronics.Count) electronics" -ForegroundColor Green
$electronics | Format-Table Id, Name, Price, Stock -AutoSize
Write-Host ""

# Test 4: Create a new product
Write-Host "4️⃣  POST /api/products (Create new product)" -ForegroundColor Yellow
$newProduct = @{
    name = "Gaming Headset"
    description = "7.1 surround sound gaming headset with RGB"
    price = 79.99
    stock = 100
    category = "Accessories"
} | ConvertTo-Json

$created = Invoke-RestMethod -Uri "$baseUrl/api/products" -Method Post -Body $newProduct -ContentType "application/json"
Write-Host "   Created product ID: $($created.id) - $($created.name)" -ForegroundColor Green
Write-Host ""

# Test 5: Update product
Write-Host "5️⃣  PUT /api/products/$($created.id) (Update product)" -ForegroundColor Yellow
$updatedProduct = @{
    id = $created.id
    product = @{
        name = "Gaming Headset Pro"
        description = "Premium 7.1 surround sound gaming headset with RGB"
        price = 89.99
        stock = 95
        category = "Accessories"
    }
} | ConvertTo-Json -Depth 3

$updated = Invoke-RestMethod -Uri "$baseUrl/api/products/$($created.id)" -Method Put -Body $updatedProduct -ContentType "application/json"
Write-Host "   Updated: $($updated.name) - `$$($updated.price)" -ForegroundColor Green
Write-Host ""

# Test 6: Create an order
Write-Host "6️⃣  POST /api/orders (Create new order)" -ForegroundColor Yellow
$newOrder = @{
    customerName = "John Doe"
    customerEmail = "john.doe@example.com"
    items = @(
        @{ productId = 1; quantity = 1 },
        @{ productId = 2; quantity = 2 }
    )
} | ConvertTo-Json -Depth 3

$order = Invoke-RestMethod -Uri "$baseUrl/api/orders" -Method Post -Body $newOrder -ContentType "application/json"
Write-Host "   Order created! ID: $($order.id)" -ForegroundColor Green
Write-Host "   Customer: $($order.customerName)" -ForegroundColor Green
Write-Host "   Total: `$$($order.totalAmount)" -ForegroundColor Green
Write-Host "   Items: $($order.items.Count)" -ForegroundColor Green
$order.items | Format-Table ProductName, Quantity, UnitPrice, Subtotal -AutoSize
Write-Host ""

# Test 7: Get order by ID
Write-Host "7️⃣  GET /api/orders/$($order.id) (Get order details)" -ForegroundColor Yellow
$orderDetails = Invoke-RestMethod -Uri "$baseUrl/api/orders/$($order.id)" -Method Get
Write-Host "   Status: $($orderDetails.status)" -ForegroundColor Green
Write-Host ""

# Test 8: Get orders by customer email
Write-Host "8️⃣  GET /api/orders/customer/john.doe@example.com" -ForegroundColor Yellow
$customerOrders = Invoke-RestMethod -Uri "$baseUrl/api/orders/customer/john.doe@example.com" -Method Get
Write-Host "   Found $($customerOrders.Count) order(s)" -ForegroundColor Green
Write-Host ""

# Test 9: Update order status
Write-Host "9️⃣  PATCH /api/orders/$($order.id)/status (Update to Shipped)" -ForegroundColor Yellow
$statusUpdate = @{
    id = $order.id
    status = "Shipped"
} | ConvertTo-Json

$updatedOrder = Invoke-RestMethod -Uri "$baseUrl/api/orders/$($order.id)/status" -Method Patch -Body $statusUpdate -ContentType "application/json"
Write-Host "   New status: $($updatedOrder.status)" -ForegroundColor Green
Write-Host "   Shipped date: $($updatedOrder.shippedDate)" -ForegroundColor Green
Write-Host ""

# Test 10: Delete product
Write-Host "🔟 DELETE /api/products/$($created.id) (Delete product)" -ForegroundColor Yellow
$deleteRequest = @{
    id = $created.id
} | ConvertTo-Json

$deleteResult = Invoke-RestMethod -Uri "$baseUrl/api/products/$($created.id)" -Method Delete -Body $deleteRequest -ContentType "application/json"
if ($deleteResult.success) {
    Write-Host "   Product deleted successfully ✅" -ForegroundColor Green
}
Write-Host ""

# Test 11: Performance test - Get all products 100 times
Write-Host "⚡ Performance Test: 100x GET /api/products" -ForegroundColor Yellow
$stopwatch = [System.Diagnostics.Stopwatch]::StartNew()
for ($i = 1; $i -le 100; $i++) {
    $null = Invoke-RestMethod -Uri "$baseUrl/api/products" -Method Get
}
$stopwatch.Stop()
$avgMs = $stopwatch.ElapsedMilliseconds / 100
Write-Host "   Average response time: $([math]::Round($avgMs, 2))ms" -ForegroundColor Green
Write-Host "   Total time: $($stopwatch.ElapsedMilliseconds)ms" -ForegroundColor Green
Write-Host ""

Write-Host "═══════════════════════════════════════════════" -ForegroundColor Cyan
Write-Host "   All tests completed! ✨" -ForegroundColor Cyan
Write-Host "═══════════════════════════════════════════════" -ForegroundColor Cyan
