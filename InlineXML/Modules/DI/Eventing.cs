using InlineXML.Configuration;
using InlineXML.Modules.DI;

namespace InlineXML.Modules.Eventing;

/// <summary>
/// Contains service lifecycle events.
/// </summary>
/// <remarks>
/// <para>
/// This partial class provides event dispatchers for tracking service initialization
/// and readiness states. Use these events to hook into the service container's
/// lifecycle and perform initialization logic when services become available.
/// </para>
/// </remarks>
public static partial class Events
{
    /// <summary>
    /// Dispatched when an individual service is ready and has been initialized.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This event fires for each service as it becomes available in the dependency
    /// injection container. The event data is the service instance that just became ready.
    /// </para>
    /// <para>
    /// Use this event to perform service-specific initialization or to coordinate
    /// startup logic that depends on a particular service being available.
    /// </para>
    /// </remarks>
    public static readonly EventGroup<AbstractService> ServiceReady = new();

    /// <summary>
    /// Dispatched before all services in the container have been initialized.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This event fires once during the service container startup, before the full
    /// initialization of all registered services is complete. The event data is the
    /// current service being processed.
    /// </para>
    /// <para>
    /// Use this event to perform pre-initialization setup or to prepare the system
    /// before all services are fully ready.
    /// </para>
    /// </remarks>
    public static readonly EventGroup BeforeAllServicesReady = new();

    /// <summary>
    /// Dispatched after all services in the container have been initialized.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This event fires once after all registered services have been fully initialized
    /// and are ready for use. The event data is the last service that was initialized.
    /// </para>
    /// <para>
    /// Use this event to perform post-initialization setup or to start operations
    /// that require all services to be available.
    /// </para>
    /// </remarks>
    public static readonly EventGroup<ExecutionMode> AfterAllServicesReady = new();
}