using System.Collections.Concurrent;
using System.Linq.Expressions;
using System.Runtime.CompilerServices;

namespace EffinitiveFramework.Core.DependencyInjection;

/// <summary>
/// High-performance service provider with minimal overhead
/// </summary>
public sealed class ServiceProvider : IServiceProvider, IDisposable
{
    private readonly IReadOnlyList<ServiceDescriptor> _descriptors;
    private readonly ConcurrentDictionary<Type, object> _singletons = new();
    private readonly ConcurrentDictionary<Type, ServiceDescriptor> _descriptorCache = new();
    private static readonly ConcurrentDictionary<Type, Func<IServiceProvider, object>> ActivatorCache = new();
    private readonly bool _hasScopedRegistrations;
    private bool _disposed;

    internal ServiceProvider(IReadOnlyList<ServiceDescriptor> descriptors)
    {
        _descriptors = descriptors;
        _hasScopedRegistrations = descriptors.Any(d => d.Lifetime == ServiceLifetime.Scoped);
        
        // Pre-cache descriptors for fast lookup
        foreach (var descriptor in descriptors)
        {
            _descriptorCache[descriptor.ServiceType] = descriptor;
            
            // Pre-create singleton instances
            if (descriptor.Instance != null)
            {
                _singletons[descriptor.ServiceType] = descriptor.Instance;
            }
        }
    }

    /// <summary>
    /// True when the container has at least one scoped registration.
    /// Request pipelines can use this to skip creating a scope on every request
    /// when only transient/singleton services are registered.
    /// </summary>
    public bool HasScopedRegistrations => _hasScopedRegistrations;

    /// <summary>
    /// Get a service instance (fast path with aggressive inlining)
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public object? GetService(Type serviceType)
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(ServiceProvider));

        // Fast path: check singleton cache
        if (_singletons.TryGetValue(serviceType, out var singleton))
            return singleton;

        // Fast path: check descriptor cache
        if (_descriptorCache.TryGetValue(serviceType, out var descriptor))
            return CreateInstance(descriptor);

        // Slow path: search through descriptors
        foreach (var desc in _descriptors)
        {
            if (desc.ServiceType == serviceType)
            {
                _descriptorCache[serviceType] = desc;
                return CreateInstance(desc);
            }
        }

        return null;
    }

    /// <summary>
    /// Create a scoped service provider (for per-request services)
    /// </summary>
    public IServiceScope CreateScope()
    {
        return new ServiceScope(this, _descriptors);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private object CreateInstance(ServiceDescriptor descriptor)
    {
        // Handle singleton
        if (descriptor.Lifetime == ServiceLifetime.Singleton)
        {
            return _singletons.GetOrAdd(descriptor.ServiceType, _ =>
            {
                if (descriptor.Instance != null)
                    return descriptor.Instance;

                if (descriptor.Factory != null)
                    return descriptor.Factory(this);

                return CreateInstanceFromType(descriptor.ImplementationType!);
            });
        }

        // Handle transient and scoped
        if (descriptor.Factory != null)
            return descriptor.Factory(this);

        return CreateInstanceFromType(descriptor.ImplementationType!);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private object CreateInstanceFromType(Type implementationType)
    {
        return CreateInstance(this, implementationType);
    }

    internal static object CreateInstance(IServiceProvider provider, Type implementationType)
    {
        var activator = ActivatorCache.GetOrAdd(implementationType, BuildActivator);
        return activator(provider);
    }

    private static Func<IServiceProvider, object> BuildActivator(Type implementationType)
    {
        var constructors = implementationType.GetConstructors();
        if (constructors.Length == 0)
            return _ => Activator.CreateInstance(implementationType)!;

        var constructor = constructors[0];
        var parameters = constructor.GetParameters();
        var providerParam = Expression.Parameter(typeof(IServiceProvider), "provider");
        var getServiceMethod = typeof(IServiceProvider).GetMethod(nameof(IServiceProvider.GetService))!;

        var args = new Expression[parameters.Length];
        for (int i = 0; i < parameters.Length; i++)
        {
            var serviceTypeExpr = Expression.Constant(parameters[i].ParameterType, typeof(Type));
            var resolveCall = Expression.Call(providerParam, getServiceMethod, serviceTypeExpr);
            args[i] = Expression.Convert(resolveCall, parameters[i].ParameterType);
        }

        var newExpr = Expression.New(constructor, args);
        var body = Expression.Convert(newExpr, typeof(object));
        return Expression.Lambda<Func<IServiceProvider, object>>(body, providerParam).Compile();
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;

        // Dispose singleton instances
        foreach (var singleton in _singletons.Values)
        {
            if (singleton is IDisposable disposable)
            {
                disposable.Dispose();
            }
        }

        _singletons.Clear();
        _descriptorCache.Clear();
    }
}

/// <summary>
/// Service scope for scoped services (one per request)
/// </summary>
public interface IServiceScope : IDisposable
{
    IServiceProvider ServiceProvider { get; }
}

/// <summary>
/// Implementation of service scope
/// </summary>
internal sealed class ServiceScope : IServiceScope
{
    private readonly ServiceProvider _rootProvider;
    private readonly ConcurrentDictionary<Type, object> _scopedInstances = new();
    private readonly IReadOnlyList<ServiceDescriptor> _descriptors;
    private readonly ScopedServiceProvider _scopedProvider;
    private bool _disposed;

    public IServiceProvider ServiceProvider => _scopedProvider;

    internal ServiceScope(ServiceProvider rootProvider, IReadOnlyList<ServiceDescriptor> descriptors)
    {
        _rootProvider = rootProvider;
        _descriptors = descriptors;
        _scopedProvider = new ScopedServiceProvider(this);
    }

    internal object? GetService(Type serviceType)
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(ServiceScope));

        // Find descriptor
        ServiceDescriptor? descriptor = null;
        foreach (var desc in _descriptors)
        {
            if (desc.ServiceType == serviceType)
            {
                descriptor = desc;
                break;
            }
        }

        if (descriptor == null)
            return _rootProvider.GetService(serviceType);

        // Singletons come from root provider
        if (descriptor.Lifetime == ServiceLifetime.Singleton)
            return _rootProvider.GetService(serviceType);

        // Scoped instances are cached in this scope
        if (descriptor.Lifetime == ServiceLifetime.Scoped)
        {
            return _scopedInstances.GetOrAdd(serviceType, _ =>
            {
                if (descriptor.Factory != null)
                    return descriptor.Factory(this.ServiceProvider);

                return CreateInstanceFromType(descriptor.ImplementationType!);
            });
        }

        // Transient always creates new instance
        if (descriptor.Factory != null)
            return descriptor.Factory(this.ServiceProvider);

        return CreateInstanceFromType(descriptor.ImplementationType!);
    }

    private object CreateInstanceFromType(Type implementationType)
    {
        return global::EffinitiveFramework.Core.DependencyInjection.ServiceProvider.CreateInstance(this.ServiceProvider, implementationType);
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;

        // Dispose scoped instances
        foreach (var instance in _scopedInstances.Values)
        {
            if (instance is IDisposable disposable)
            {
                disposable.Dispose();
            }
        }

        _scopedInstances.Clear();
    }
}

/// <summary>
/// Scoped service provider wrapper
/// </summary>
internal sealed class ScopedServiceProvider : IServiceProvider
{
    private readonly ServiceScope _scope;

    public ScopedServiceProvider(ServiceScope scope)
    {
        _scope = scope;
    }

    public object? GetService(Type serviceType)
    {
        return _scope.GetService(serviceType);
    }
}

/// <summary>
/// Extension methods for IServiceProvider
/// </summary>
public static class ServiceProviderExtensions
{
    /// <summary>
    /// Get service of type T
    /// </summary>
    public static T? GetService<T>(this IServiceProvider provider) where T : class
    {
        return provider.GetService(typeof(T)) as T;
    }

    /// <summary>
    /// Get required service of type T (throws if not found)
    /// </summary>
    public static T GetRequiredService<T>(this IServiceProvider provider) where T : class
    {
        return provider.GetService(typeof(T)) as T 
            ?? throw new InvalidOperationException($"Service of type {typeof(T).FullName} is not registered");
    }
}
