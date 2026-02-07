namespace InlineXML.Modules.Eventing;

/// <summary>
/// Contains event-related utilities and functionality for the application.
/// </summary>
public static partial class Events
{
    
}

/// <summary>
/// A generic event dispatcher that chains multiple event listeners together.
/// </summary>
/// <remarks>
/// <para>
/// <c>EventGroup&lt;T&gt;</c> implements a pipeline pattern where event listeners are executed
/// sequentially, with each listener's output becoming the next listener's input.
/// </para>
/// <para>
/// This is useful for scenarios where multiple handlers need to process or transform
/// the same object in a specific order, such as middleware chains or event pipelines.
/// </para>
/// </remarks>
/// <typeparam name="T">The type of object being passed through the event pipeline.</typeparam>
public class EventGroup<T>
{
    private readonly List<Func<T, T>> _events = [];

    /// <summary>
    /// Registers an event listener to be invoked when an event is dispatched.
    /// </summary>
    /// <remarks>
    /// Listeners are invoked in the order they were added. Each listener receives the
    /// output of the previous listener as input.
    /// </remarks>
    /// <param name="listener">A function that accepts an object of type <typeparamref name="T"/>,
    /// processes it, and returns the (possibly modified) object.</param>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="listener"/> is null.</exception>
    public void AddEventListener(Func<T, T> listener)
    {
       ArgumentNullException.ThrowIfNull(listener);
       _events.Add(listener);
    }

    /// <summary>
    /// Dispatches an event by passing the given object through all registered listeners.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Listeners are executed sequentially in the order they were registered. Each listener
    /// receives the output of the previous listener, allowing for object transformation
    /// at each stage of the pipeline.
    /// </para>
    /// <para>
    /// If no listeners are registered, the original object is returned unchanged.
    /// </para>
    /// </remarks>
    /// <param name="obj">The object to dispatch through the event pipeline.</param>
    /// <returns>The object after being processed by all registered listeners.</returns>
    public T Dispatch(T obj)
    {
       foreach (var consumer in _events)
       {
          obj = consumer(obj);
       }

       return obj;
    }
}

/// <summary>
/// A simple event dispatcher for events that do not pass data to listeners.
/// </summary>
/// <remarks>
/// <para>
/// <c>EventGroup</c> is a non-generic variant of <see cref="EventGroup{T}"/> used for
/// events that only signal that something has occurred, without requiring data to be
/// passed to or between listeners.
/// </para>
/// <para>
/// This is useful for notification-style events such as application startup, shutdown,
/// or other state changes where listeners only need to know that an event occurred.
/// </para>
/// </remarks>
public class EventGroup
{
    private readonly List<Action> _events = [];

    /// <summary>
    /// Registers an event listener to be invoked when an event is dispatched.
    /// </summary>
    /// <remarks>
    /// Listeners are invoked in the order they were added.
    /// </remarks>
    /// <param name="listener">An action to be executed when the event is dispatched.</param>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="listener"/> is null.</exception>
    public void AddEventListener(Action listener)
    {
       ArgumentNullException.ThrowIfNull(listener);
       _events.Add(listener);
    }

    /// <summary>
    /// Dispatches an event by invoking all registered listeners.
    /// </summary>
    /// <remarks>
    /// Listeners are executed sequentially in the order they were registered.
    /// If no listeners are registered, this method completes without performing any actions.
    /// </remarks>
    public void Dispatch()
    {
       foreach (var listener in _events)
       {
          listener();
       }
    }
}