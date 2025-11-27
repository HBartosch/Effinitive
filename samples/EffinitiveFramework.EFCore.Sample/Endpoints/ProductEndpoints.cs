using EffinitiveFramework.Core;
using EffinitiveFramework.EFCore.Sample.Models;
using EffinitiveFramework.EFCore.Sample.Services;

namespace EffinitiveFramework.EFCore.Sample.Endpoints;

// GET /api/products
public class GetAllProductsEndpoint : AsyncEndpointBase<EmptyRequest, List<Product>>
{
    private readonly IProductService _productService;

    public GetAllProductsEndpoint(IProductService productService)
    {
        _productService = productService;
    }

    protected override string Route => "/api/products";
    protected override string Method => "GET";

    public override async Task<List<Product>> HandleAsync(EmptyRequest request, CancellationToken cancellationToken)
    {
        return await _productService.GetAllProductsAsync(cancellationToken);
    }
}

// GET /api/products/{id}
public class GetProductByIdEndpoint : AsyncEndpointBase<GetProductRequest, Product?>
{
    private readonly IProductService _productService;

    public GetProductByIdEndpoint(IProductService productService)
    {
        _productService = productService;
    }

    protected override string Route => "/api/products/{id}";
    protected override string Method => "GET";

    public override async Task<Product?> HandleAsync(GetProductRequest request, CancellationToken cancellationToken)
    {
        return await _productService.GetProductByIdAsync(request.Id, cancellationToken);
    }
}

// GET /api/products/category/{category}
public class GetProductsByCategoryEndpoint : AsyncEndpointBase<GetProductsByCategoryRequest, List<Product>>
{
    private readonly IProductService _productService;

    public GetProductsByCategoryEndpoint(IProductService productService)
    {
        _productService = productService;
    }

    protected override string Route => "/api/products/category/{category}";
    protected override string Method => "GET";

    public override async Task<List<Product>> HandleAsync(GetProductsByCategoryRequest request, CancellationToken cancellationToken)
    {
        return await _productService.GetProductsByCategoryAsync(request.Category, cancellationToken);
    }
}

// POST /api/products
public class CreateProductEndpoint : AsyncEndpointBase<Product, Product>
{
    private readonly IProductService _productService;

    public CreateProductEndpoint(IProductService productService)
    {
        _productService = productService;
    }

    protected override string Route => "/api/products";
    protected override string Method => "POST";

    public override async Task<Product> HandleAsync(Product request, CancellationToken cancellationToken)
    {
        return await _productService.CreateProductAsync(request, cancellationToken);
    }
}

// PUT /api/products/{id}
public class UpdateProductEndpoint : AsyncEndpointBase<UpdateProductRequest, Product?>
{
    private readonly IProductService _productService;

    public UpdateProductEndpoint(IProductService productService)
    {
        _productService = productService;
    }

    protected override string Route => "/api/products/{id}";
    protected override string Method => "PUT";

    public override async Task<Product?> HandleAsync(UpdateProductRequest request, CancellationToken cancellationToken)
    {
        return await _productService.UpdateProductAsync(request.Id, request.Product, cancellationToken);
    }
}

// DELETE /api/products/{id}
public class DeleteProductEndpoint : AsyncEndpointBase<DeleteProductRequest, DeleteProductResponse>
{
    private readonly IProductService _productService;

    public DeleteProductEndpoint(IProductService productService)
    {
        _productService = productService;
    }

    protected override string Route => "/api/products/{id}";
    protected override string Method => "DELETE";

    public override async Task<DeleteProductResponse> HandleAsync(DeleteProductRequest request, CancellationToken cancellationToken)
    {
        var success = await _productService.DeleteProductAsync(request.Id, cancellationToken);
        return new DeleteProductResponse { Success = success };
    }
}

// Request/Response models
public class GetProductRequest
{
    public int Id { get; set; }
}

public class GetProductsByCategoryRequest
{
    public string Category { get; set; } = string.Empty;
}

public class UpdateProductRequest
{
    public int Id { get; set; }
    public Product Product { get; set; } = new();
}

public class DeleteProductRequest
{
    public int Id { get; set; }
}

public class DeleteProductResponse
{
    public bool Success { get; set; }
}
