using System.Linq.Expressions;
using System.Reflection;

namespace Guance.Rum.Windows;

internal sealed class ReflectionEventSubscription : IDisposable
{
    private readonly object target;
    private readonly EventInfo eventInfo;
    private readonly Delegate handler;
    private int disposed;

    private ReflectionEventSubscription(object target, EventInfo eventInfo, Delegate handler)
    {
        this.target = target;
        this.eventInfo = eventInfo;
        this.handler = handler;
    }

    public static ReflectionEventSubscription? TryCreate(object target, string eventName, Action<object?, object?> callback)
    {
        var eventInfo = target.GetType().GetRuntimeEvent(eventName);
        var handlerType = eventInfo?.EventHandlerType;
        var invoke = handlerType?.GetRuntimeMethods().FirstOrDefault(method => method.Name == "Invoke");
        if (eventInfo is null || handlerType is null || invoke is null || invoke.ReturnType != typeof(void))
        {
            return null;
        }

        var parameters = invoke.GetParameters()
            .Select(parameter => Expression.Parameter(parameter.ParameterType, parameter.Name))
            .ToArray();
        Expression sender = parameters.Length > 0
            ? Expression.Convert(parameters[0], typeof(object))
            : Expression.Constant(null, typeof(object));
        Expression args = parameters.Length > 1
            ? Expression.Convert(parameters[1], typeof(object))
            : Expression.Constant(null, typeof(object));
        var body = Expression.Call(
            Expression.Constant(callback),
            typeof(Action<object?, object?>).GetRuntimeMethod(nameof(Action.Invoke), new[] { typeof(object), typeof(object) })!,
            sender,
            args);
        var handler = Expression.Lambda(handlerType, body, parameters).Compile();
        eventInfo.AddEventHandler(target, handler);
        return new ReflectionEventSubscription(target, eventInfo, handler);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0)
        {
            return;
        }

        try
        {
            eventInfo.RemoveEventHandler(target, handler);
        }
        catch
        {
            // A disposed or thread-affine UI control may reject handler removal.
        }
    }
}
