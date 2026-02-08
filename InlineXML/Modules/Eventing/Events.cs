using System.Diagnostics;
using System.Reflection;

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
/// <typeparam name="T">The type of object being passed through the event pipeline.</typeparam>
public class EventGroup<T>
{
    private readonly List<Func<T, T>> _events = [];

    public void AddEventListener(Func<T, T> listener)
    {
       ArgumentNullException.ThrowIfNull(listener);
       _events.Add(listener);
    }

    /// <summary>
    /// Dispatches an event and forces visibility into the console.
    /// It attempts to resolve the name of the event from the caller context.
    /// </summary>
    public T Dispatch(T obj)
    {
       var eventName = GetEventName();
       
       System.Console.ForegroundColor = System.ConsoleColor.Cyan;
       System.Console.WriteLine($"[EVENT] >>> {eventName} | Type: {typeof(T).Name} | Listeners: {_events.Count}");
       System.Console.ResetColor();

       if (_events.Count == 0)
       {
          System.Console.ForegroundColor = System.ConsoleColor.Yellow;
          System.Console.WriteLine($"[WARNING] {eventName} has NO listeners. This event is a no-op!");
          System.Console.ResetColor();
       }

       for (int i = 0; i < _events.Count; i++)
       {
          try
          {
             obj = _events[i](obj);
          }
          catch (Exception ex)
          {
             System.Console.ForegroundColor = System.ConsoleColor.Red;
             System.Console.Error.WriteLine($"[ERROR] {eventName} failed at listener index {i}");
             System.Console.Error.WriteLine($"Exception: {ex.Message}");
             System.Console.Error.WriteLine(ex.StackTrace);
             System.Console.ResetColor();
             throw;
          }
       }

       return obj;
    }

    private string GetEventName()
    {
        // Walk the stack to find who owns this instance in the Events class
        var stack = new StackTrace();
        foreach (var frame in stack.GetFrames())
        {
            var method = frame.GetMethod();
            if (method == null) continue;
            
            // Check fields in the Events partial classes
            var fields = typeof(Events).GetFields(BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy);
            foreach (var field in fields)
            {
                if (ReferenceEquals(field.GetValue(null), this)) return field.Name;
                
                // Check nested properties (like Events.Workspace.FileChanged)
                var value = field.GetValue(null);
                if (value == null) continue;
                var subFields = value.GetType().GetFields(BindingFlags.Public | BindingFlags.Instance);
                foreach (var subField in subFields)
                {
                    if (ReferenceEquals(subField.GetValue(value), this)) return $"{field.Name}.{subField.Name}";
                }
            }
        }
        return "UnknownEvent";
    }
}

/// <summary>
/// A simple event dispatcher for events that do not pass data to listeners.
/// </summary>
public class EventGroup
{
    private readonly List<Action> _events = [];

    public void AddEventListener(Action listener)
    {
       ArgumentNullException.ThrowIfNull(listener);
       _events.Add(listener);
    }

    public void Dispatch()
    {
       System.Console.ForegroundColor = System.ConsoleColor.Magenta;
       System.Console.WriteLine($"[SIGNAL] >>> Triggered Signal | Listeners: {_events.Count}");
       System.Console.ResetColor();

       foreach (var listener in _events)
       {
          try
          {
             listener();
          }
          catch (Exception ex)
          {
             System.Console.ForegroundColor = System.ConsoleColor.Red;
             System.Console.Error.WriteLine($"[ERROR] Signal failed!");
             System.Console.Error.WriteLine($"Exception: {ex.Message}");
             System.Console.ResetColor();
             throw;
          }
       }
    }
}