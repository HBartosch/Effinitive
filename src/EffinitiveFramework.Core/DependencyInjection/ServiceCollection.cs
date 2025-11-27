namespace EffinitiveFramework.Core.DependencyInjection;

/// <summary>
/// Service lifetime for dependency injection
/// </summary>
public enum ServiceLifetime
{
    /// <summary>Created once per application lifetime</summary>
    Singleton,
    /// <summary>Created once per request/scope</summary>
    Scoped,
    /// <summary>Created every time it's requested</summary>
    Transient
}

/// <summary>
/// Service descriptor for DI registration
/// </summary>
public sealed class ServiceDescriptor
{
    public Type ServiceType { get; }
    public Type? ImplementationType { get; }
    public object? Instance { get; }
    public Func<IServiceProvider, object>? Factory { get; }
    public ServiceLifetime Lifetime { get; }

    public ServiceDescriptor(Type serviceType, Type implementationType, ServiceLifetime lifetime)
    {
        ServiceType = serviceType;
        ImplementationType = implementationType;
        Lifetime = lifetime;
    }

    public ServiceDescriptor(Type serviceType, object instance)
    {
        ServiceType = serviceType;
        Instance = instance;
        Lifetime = ServiceLifetime.Singleton;
    }

    public ServiceDescriptor(Type serviceType, Func<IServiceProvider, object> factory, ServiceLifetime lifetime)
    {
        ServiceType = serviceType;
        Factory = factory;
        Lifetime = lifetime;
    }
}

/// <summary>
/// Minimal, high-performance service collection for dependency injection
/// </summary>
public sealed class ServiceCollection
{
    private readonly List<ServiceDescriptor> _descriptors = new();

    /// <summary>
    /// Add a transient service (created each time it's requested)
    /// </summary>
    public ServiceCollection AddTransient<TService, TImplementation>()
        where TService : class
        where TImplementation : class, TService
    {
        _descriptors.Add(new ServiceDescriptor(
            typeof(TService),
            typeof(TImplementation),
            ServiceLifetime.Transient));
        return this;
    }

    /// <summary>
    /// Add a transient service (non-generic, for runtime type registration)
    /// </summary>
    public ServiceCollection AddTransient(Type serviceType, Type implementationType)
    {
        _descriptors.Add(new ServiceDescriptor(
            serviceType,
            implementationType,
            ServiceLifetime.Transient));
        return this;
    }

    /// <summary>
    /// Add a transient service with factory
    /// </summary>
    public ServiceCollection AddTransient<TService>(Func<IServiceProvider, TService> factory)
        where TService : class
    {
        _descriptors.Add(new ServiceDescriptor(
            typeof(TService),
            sp => factory(sp)!,
            ServiceLifetime.Transient));
        return this;
    }

    /// <summary>
    /// Add a scoped service (created once per request)
    /// </summary>
    public ServiceCollection AddScoped<TService, TImplementation>()
        where TService : class
        where TImplementation : class, TService
    {
        _descriptors.Add(new ServiceDescriptor(
            typeof(TService),
            typeof(TImplementation),
            ServiceLifetime.Scoped));
        return this;
    }

    /// <summary>
    /// Add a scoped service with factory
    /// </summary>
    public ServiceCollection AddScoped<TService>(Func<IServiceProvider, TService> factory)
        where TService : class
    {
        _descriptors.Add(new ServiceDescriptor(
            typeof(TService),
            sp => factory(sp)!,
            ServiceLifetime.Scoped));
        return this;
    }

    /// <summary>
    /// Add a singleton service (created once for the application lifetime)
    /// </summary>
    public ServiceCollection AddSingleton<TService, TImplementation>()
        where TService : class
        where TImplementation : class, TService
    {
        _descriptors.Add(new ServiceDescriptor(
            typeof(TService),
            typeof(TImplementation),
            ServiceLifetime.Singleton));
        return this;
    }

    /// <summary>
    /// Add a singleton instance
    /// </summary>
    public ServiceCollection AddSingleton<TService>(TService instance)
        where TService : class
    {
        _descriptors.Add(new ServiceDescriptor(typeof(TService), instance));
        return this;
    }

    /// <summary>
    /// Add a singleton with factory
    /// </summary>
    public ServiceCollection AddSingleton<TService>(Func<IServiceProvider, TService> factory)
        where TService : class
    {
        _descriptors.Add(new ServiceDescriptor(
            typeof(TService),
            sp => factory(sp)!,
            ServiceLifetime.Singleton));
        return this;
    }

    /// <summary>
    /// Build the service provider
    /// </summary>
    public IServiceProvider BuildServiceProvider()
    {
        return new ServiceProvider(_descriptors);
    }

    internal IReadOnlyList<ServiceDescriptor> GetDescriptors() => _descriptors;
}
