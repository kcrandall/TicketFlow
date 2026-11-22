using System.Collections.Concurrent;
using System.Reflection;

namespace TicketFlow.Automation.Tests;

/// <summary>Counts calls per method name and lets tests replace results (a non-null override result is returned instead of calling through).</summary>
internal sealed class CallLog
{
    public ConcurrentDictionary<string, int> Counts { get; } = new();

    public Func<string, object?[]?, object?>? Override { get; set; }

    public int this[string method] => Counts.GetValueOrDefault(method);

    public void Reset() => Counts.Clear();
}

internal class Interceptor<T> : DispatchProxy where T : class
{
    private T inner = null!;
    private CallLog log = null!;
    private string prefix = string.Empty;

    public static T Wrap(T inner, CallLog log, string prefix = "")
    {
        var proxy = Create<T, Interceptor<T>>();
        var interceptor = (Interceptor<T>)(object)proxy;
        interceptor.inner = inner;
        interceptor.log = log;
        interceptor.prefix = prefix;
        return proxy;
    }

    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
    {
        var name = prefix + targetMethod!.Name;
        log.Counts.AddOrUpdate(name, 1, (_, count) => count + 1);
        if (log.Override?.Invoke(name, args) is { } replaced)
            return replaced;
        try
        {
            return targetMethod.Invoke(inner, args);
        }
        catch (TargetInvocationException error) when (error.InnerException is not null)
        {
            throw error.InnerException;
        }
    }
}
