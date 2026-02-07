using System.Collections.Concurrent;
using System.Reflection;
using System.Linq;
using InlineXML.Configuration;
using InlineXML.Modules.Eventing;

namespace InlineXML.Modules.DI;

/// <summary>
/// This service container will provide services to the rest of the codebase
/// ensuring single instance of a service at all times, and a get or instance
/// methodology ensuring that services will always exist at time of need.
/// </summary>
public static class Services
{
	/// <summary>
	/// Thread-safe collection of cached service instances with atomic operations.
	/// </summary>
	private static readonly ConcurrentDictionary<Type, AbstractService> _serviceCache = [];

	/// <summary>
	/// Nice wrapper around the other "Get" signature.
	/// </summary>
	/// <typeparam name="T"></typeparam>
	/// <returns></returns>
	public static T Get<T>() where T : AbstractService
	{
		return (T)Get(typeof(T));
	}
	
	/// <summary>
	/// Returns
	/// </summary>
	/// <param name="t"></param>
	/// <returns></returns>
	private static AbstractService Get(Type t)
	{
		// if it's in cache, perfect, return it.
		if (_serviceCache.TryGetValue(t, out var service))
		{
			return service;
		}
		
		// no bother, we just need to instance it, we do so by collecting
		// the parameters the service needs, services can only reference
		// each other, which is rather convenient in this case. In the code
		// below, we create a list to hold out parameters as we're collecting
		// them.
		List<object> ctorParams = [];
		
		// We want the longest as we want to satisfy the biggest constructor.
		var constructor = GetConstructorWithMostParameters(t);

		// iterate params.
		foreach (var param in constructor.GetParameters())
		{
			if (!typeof(AbstractService).IsAssignableFrom(param.ParameterType))
			{
				throw new ArgumentException("Invalid parameter type: " + param.ParameterType + ", only services can be passed in a service constructor");
			}
			ctorParams.Add(Get(param.ParameterType));
		}
		
		var svc = constructor.Invoke(ctorParams.ToArray()) as AbstractService;
		_serviceCache[t] = svc;
		Events.ServiceReady.Dispatch(svc);
		return svc;
	}
	
	public static void InstanceAll(ExecutionMode mode)
	{
		Events.BeforeAllServicesReady.Dispatch();
		var allInAssembly = typeof(Services).Assembly.GetTypes().Where((type) =>
		{
			if (typeof(AbstractService).IsAssignableFrom(type))
			{
				return true;
			}

			return false;
		});
    
		foreach (var serviceType in allInAssembly)
		{
			// instances them all.
			Get(serviceType);
		}
		Events.AfterAllServicesReady.Dispatch(mode);
	}
	
	private static ConstructorInfo GetConstructorWithMostParameters(Type t)
	{
		var constructors = t.GetConstructors();
    
		if (constructors.Length == 0)
		{
			throw new InvalidOperationException($"No public constructors found for type '{t.Name}'.");
		}
    
		ConstructorInfo? bestConstructor = null;
		int maxParams = -1;
    
		foreach (var constructor in constructors)
		{
			int paramCount = constructor.GetParameters().Length;
        
			if (paramCount > maxParams)
			{
				maxParams = paramCount;
				bestConstructor = constructor;
			}
		}
    
		return bestConstructor!;
	}
}