using EffinitiveFramework.Core;
using EffinitiveFramework.Validation.Sample.Requests;

namespace EffinitiveFramework.Validation.Sample.Endpoints;

public class CreateUserEndpoint : AsyncEndpointBase<CreateUserRequest, object>
{
    protected override string Method => "POST";
    protected override string Route => "/users";

    public override async Task<object> HandleAsync(CreateUserRequest request, CancellationToken cancellationToken = default)
    {
        // If we reach here, validation has already passed!
        // The ValidationMiddleware intercepted the request and validated it

        // Simulate user creation
        await Task.Delay(10, cancellationToken); // Simulate async work

        return new
        {
            success = true,
            message = "User created successfully",
            user = new
            {
                name = request.Name,
                email = request.Email,
                age = request.Age,
                role = request.Role
            }
        };
    }
}
