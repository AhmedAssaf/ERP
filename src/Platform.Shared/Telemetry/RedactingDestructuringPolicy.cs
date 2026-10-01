using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Security.Claims;
using Serilog.Core;
using Serilog.Events;

namespace Platform.Shared.Telemetry;

/// <summary>
/// The destructuring policy of the host's Serilog logger (W-10, plan task 3; spec O-10, O-11): an object logged with
/// <c>{@...}</c> that carries a request, its headers, form or query, a claims principal or a stream is never taken apart; it
/// is logged as its type name only. Every other object is destructured by Serilog as usual, within the logger's caps
/// (depth 4, 32 elements), and its string members are masked, then cut to 4096 characters, by <see cref="RedactingEnricher"/>,
/// which runs over every property value of every event.
/// </summary>
/// <remarks>
/// The ASP.NET Core types are recognised by their full names, walking the value's base types and interfaces, so this
/// project gains no ASP.NET Core framework reference (the worker is built from it). <see cref="ClaimsPrincipal"/>,
/// <see cref="ClaimsIdentity"/>, <see cref="Claim"/> and <see cref="Stream"/> are base class library types.
/// </remarks>
public sealed class RedactingDestructuringPolicy : IDestructuringPolicy
{
    private static readonly HashSet<string> RefusedTypeNames = new(StringComparer.Ordinal)
    {
        "Microsoft.AspNetCore.Http.HttpContext",
        "Microsoft.AspNetCore.Http.HttpRequest",
        "Microsoft.AspNetCore.Http.HttpResponse",
        "Microsoft.AspNetCore.Http.IFormCollection",
        "Microsoft.AspNetCore.Http.IHeaderDictionary",
        "Microsoft.AspNetCore.Http.IQueryCollection",
        "Microsoft.AspNetCore.Http.IRequestCookieCollection",
    };

    private static readonly ConcurrentDictionary<Type, bool> Refusals = new();

    public bool TryDestructure(object value, ILogEventPropertyValueFactory propertyValueFactory, [NotNullWhen(true)] out LogEventPropertyValue? result)
    {
        ArgumentNullException.ThrowIfNull(value);
        var type = value.GetType();
        if (Refusals.GetOrAdd(type, IsRefused))
        {
            result = new ScalarValue(type.FullName ?? type.Name);
            return true;
        }

        result = null;
        return false;
    }

    private static bool IsRefused(Type type)
    {
        if (typeof(ClaimsPrincipal).IsAssignableFrom(type) || typeof(ClaimsIdentity).IsAssignableFrom(type)
            || typeof(Claim).IsAssignableFrom(type) || typeof(Stream).IsAssignableFrom(type))
        {
            return true;
        }

        for (var current = type; current is not null; current = current.BaseType)
        {
            if (current.FullName is { } name && RefusedTypeNames.Contains(name))
            {
                return true;
            }
        }

        return type.GetInterfaces().Any(i => i.FullName is { } name && RefusedTypeNames.Contains(name));
    }
}
