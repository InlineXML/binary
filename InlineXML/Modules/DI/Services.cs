using System.Collections.Concurrent;
using System.Reflection;
using System.Linq;
using InlineXML.Configuration;
using InlineXML.Modules.Eventing;

namespace InlineXML.Modules.DI;

/// <summary>
/// the service container is the central registry for the application.
/// it ensures that every service exists as a single instance and 
/// handles automatic dependency resolution through reflection. 
/// </summary>
public static class Services
{
    /// <summary>
    /// a thread-safe cache of all active service instances.
    /// we use a concurrent dictionary to prevent race conditions during
    /// the initial application boot or when services are lazily loaded.
    /// </summary>
    private static readonly ConcurrentDictionary<Type, AbstractService> ServiceCache = [];

    /// <summary>
    /// a type-safe wrapper to retrieve a service from the container.
    /// if the service doesn't exist yet, it will be automatically 
    /// instanced along with all of its dependencies.
    /// </summary>
    /// <typeparam name="T"></typeparam>
    /// <returns></returns>
    public static T? Get<T>() where T : AbstractService
    {
       return (T?)Get(typeof(T));
    }
    
    /// <summary>
    /// the internal resolution logic. this method performs a recursive
    /// search to satisfy the constructor requirements of the requested service.
    /// </summary>
    /// <param name="t"></param>
    /// <returns></returns>
    public static AbstractService? Get(Type t)
    {
       // if the service is already in our cache, we return it immediately.
       if (ServiceCache.TryGetValue(t, out var service))
       {
          return service;
       }
       
       // to instance a service, we need to satisfy its constructor.
       // we collect the necessary parameters by recursively calling 'Get'
       // on each required type.
       List<object> ctorParams = [];
       
       // we always aim for the longest constructor to ensure that we
       // satisfy the most complex version of the service.
       var constructor = GetConstructorWithMostParameters(t);

       if (constructor == null)
       {
          if (typeof(AbstractService) == t)
          {
             return null;
          }

          // handle parameterless constructors
          var ctorlessSvc = Activator.CreateInstance(t) as AbstractService;
          ServiceCache[t] = ctorlessSvc;
          Events.ServiceReady.Dispatch(ctorlessSvc);
          return ctorlessSvc;
       }

       // we iterate through the parameters. a strict rule of our architecture
       // is that services can only depend on other services.
       foreach (var param in constructor.GetParameters())
       {
          if (!typeof(AbstractService).IsAssignableFrom(param.ParameterType))
          {
             throw new ArgumentException($"Invalid parameter type: {param.ParameterType}. only services can be passed in a service constructor.");
          }

          // recursive resolution: get or instance the dependency.
          ctorParams.Add(Get(param.ParameterType));
       }
       
       // instance the service using the collected dependencies
       var svc = constructor.Invoke(ctorParams.ToArray()) as AbstractService;
       ServiceCache[t] = svc;

       // notify the system that a new service is online and ready for use.
       Events.ServiceReady.Dispatch(svc);
       return svc;
    }
    
    /// <summary>
    /// crawls the assembly and pre-instances every service. 
    /// this ensures that the entire system is "hot" and ready
    /// before we begin processing any files.
    /// </summary>
    /// <param name="mode"></param>
    public static void InstanceAll(ExecutionMode mode)
    {
       Events.BeforeAllServicesReady.Dispatch();

       // find every class that inherits from AbstractService
       var allInAssembly = typeof(Services).Assembly.GetTypes().Where((type) =>
       {
          return typeof(AbstractService).IsAssignableFrom(type) && !type.IsAbstract;
       });
    
       foreach (var serviceType in allInAssembly)
       {
          Get(serviceType);
       }

       Events.AfterAllServicesReady.Dispatch(mode);
    }
    
    /// <summary>
    /// identifies the constructor with the highest number of parameters.
    /// this allows us to support dependency injection for complex services.
    /// </summary>
    /// <param name="t"></param>
    /// <returns></returns>
    private static ConstructorInfo? GetConstructorWithMostParameters(Type t)
    {
       var constructors = t.GetConstructors();
    
       if (constructors.Length == 0)
       {
          return null;
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