using System;
using System.Collections.Generic;

namespace ValleyAgent.Infrastructure;

public interface IServiceContainer
{
    public void RegisterSingleton<TService>(TService instance) where TService : class;
    public void RegisterFactory<TService>(Func<IServiceContainer, TService> factory) where TService : class;
    public TService GetService<TService>() where TService : class;
    public bool TryGetService<TService>(out TService? service) where TService : class;
}

public class ServiceContainer : IServiceContainer
{
    private readonly HashSet<Type> _creating = new();
    private readonly Dictionary<Type, Func<IServiceContainer, object>> _factories = new();
    private readonly Dictionary<Type, object> _instances = new();

    public void RegisterSingleton<TService>(TService instance) where TService : class => _instances[typeof(TService)] =
        instance ?? throw new ArgumentNullException(nameof(instance));

    public void RegisterFactory<TService>(Func<IServiceContainer, TService> factory) where TService : class =>
        _factories[typeof(TService)] = factory ?? throw new ArgumentNullException(nameof(factory));

    public TService GetService<TService>() where TService : class
    {
        return TryGetService(out TService? service)
            ? service!
            : throw new InvalidOperationException($"Service {typeof(TService).Name} not registered");
    }

    public bool TryGetService<TService>(out TService? service) where TService : class
    {
        var serviceType = typeof(TService);

        if (_instances.TryGetValue(serviceType, out var existing))
        {
            service = (TService)existing;
            return true;
        }

        if (_factories.TryGetValue(serviceType, out var factory))
        {
            if (!_creating.Add(serviceType))
            {
                throw new InvalidOperationException($"Circular dependency detected for {serviceType.Name}");
            }

            try
            {
                var instance = factory(this);
                _instances[serviceType] = instance;
                _ = _creating.Remove(serviceType);
                service = (TService)instance;
                return true;
            }
            catch
            {
                _ = _creating.Remove(serviceType);
                throw;
            }
        }

        service = null;
        return false;
    }
}