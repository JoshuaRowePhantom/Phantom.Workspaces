using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security;
using System.Text;
using System.Text.RegularExpressions;

namespace Phantom.Workspaces.Services.Logging;

/// <summary>
/// Opt-in marker for application values that carry a protected payload. A sensitive value renders a
/// stable, non-materializing marker (plus any safe metadata it chooses to expose) instead of its
/// protected contents. Implementers must never return plaintext from <see cref="ToDiagnosticMarker"/>.
/// </summary>
public interface ISensitiveDiagnosticValue
{
    string ToDiagnosticMarker();
}

/// <summary>
/// Bounds applied while rendering an operational snapshot or an exception, so verbose diagnostics
/// cannot become an unbounded recursive dump. Every limit has an explicit truncation marker.
/// </summary>
public sealed record DiagnosticLimits
{
    public int MaxDepth { get; init; } = 6;
    public int MaxStringLength { get; init; } = 4096;
    public int MaxCollectionItems { get; init; } = 64;
    public int MaxMembers { get; init; } = 64;
    public int MaxExceptionDepth { get; init; } = 16;

    public static DiagnosticLimits Default { get; } = new();
}

/// <summary>
/// Removes credential-bearing fragments from otherwise-ordinary free text (upstream SDK messages,
/// URLs, headers, response bodies) while preserving the surrounding operational content. This is a
/// narrow boundary scrub, not blanket message suppression: only recognised secret-bearing locations
/// are redacted.
/// </summary>
public static class SensitiveTextRedactor
{
    public const string RedactionMarker = "[redacted]";

    // Query/fragment/body parameters whose values are credentials. Matched case-insensitively.
    private const string SecretParameterNames =
        "access[_-]?token|refresh[_-]?token|id[_-]?token|session[_-]?token|client[_-]?secret"
        + "|token|secret|password|passwd|pwd|pass|api[_-]?key|apikey|auth|authorization"
        + "|sig|signature|sas|code|credential|private[_-]?key|x-api-key";

    // user:password@host inside a URL authority.
    private static readonly Regex UrlUserInfo = new(
        "(?<scheme>[a-zA-Z][a-zA-Z0-9+.\\-]*://)(?<userinfo>[^/\\s@:]+(?::[^/\\s@]*)?)@",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    // ?token=... &client_secret=... #access_token=...
    private static readonly Regex SecretParameter = new(
        "(?<prefix>[?&#;](?:" + SecretParameterNames + ")=)(?<value>[^&#\\s\"'<>]+)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    // "token": "..." / token = ... in structured text and headers.
    private static readonly Regex SecretAssignment = new(
        "(?<prefix>\"?(?:" + SecretParameterNames + ")\"?\\s*[:=]\\s*\"?)(?<value>[^\\s\"',;}&]+)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    // Authorization: Bearer <token>, Basic <token>, etc.
    private static readonly Regex AuthorizationScheme = new(
        "(?<prefix>\\b(?:Bearer|Basic|Negotiate|Digest|NTLM)\\s+)(?<value>[A-Za-z0-9._\\-+/=]{8,})",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public static string Redact(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return text ?? string.Empty;
        }

        var scrubbed = UrlUserInfo.Replace(text, match => $"{match.Groups["scheme"].Value}{RedactionMarker}@");
        scrubbed = SecretParameter.Replace(scrubbed, match => $"{match.Groups["prefix"].Value}{RedactionMarker}");
        scrubbed = SecretAssignment.Replace(scrubbed, match => $"{match.Groups["prefix"].Value}{RedactionMarker}");
        scrubbed = AuthorizationScheme.Replace(scrubbed, match => $"{match.Groups["prefix"].Value}{RedactionMarker}");
        return scrubbed;
    }
}

/// <summary>
/// Recognises protected values by type and renders a non-materializing marker for them. Detection
/// covers <see cref="SecureString"/>, any <see cref="ISensitiveDiagnosticValue"/>, and the
/// repository's guarded credential/lease contracts (the <c>Phantom.Workspaces.Llm.Interfaces.Access</c>
/// family) by namespace so this low-level logging assembly does not have to reference them. A
/// protected value is never recursed into, so no protected getter, marshal, or use-callback runs
/// merely to produce a log entry.
/// </summary>
public static class ProtectedValueInspector
{
    private const string GuardedAccessNamespace = "Phantom.Workspaces.Llm.Interfaces.Access";

    public static bool TryDescribe(object? value, out string marker)
    {
        switch (value)
        {
            case null:
                marker = string.Empty;
                return false;
            case ISensitiveDiagnosticValue sensitive:
                marker = SensitiveTextRedactor.Redact(SafeMarker(sensitive));
                return true;
            case SecureString:
                marker = "[protected:SecureString]";
                return true;
        }

        var type = value.GetType();
        var ns = type.Namespace;
        if (ns is not null
            && (string.Equals(ns, GuardedAccessNamespace, StringComparison.Ordinal)
                || ns.StartsWith(GuardedAccessNamespace + ".", StringComparison.Ordinal))
            && !type.IsEnum)
        {
            marker = $"[protected:{type.Name}]";
            return true;
        }

        marker = string.Empty;
        return false;
    }

    private static string SafeMarker(ISensitiveDiagnosticValue sensitive)
    {
        try
        {
            return sensitive.ToDiagnosticMarker();
        }
        catch (Exception exception)
        {
            return $"[protected:{sensitive.GetType().Name}; marker-failed:{exception.GetType().Name}]";
        }
    }
}

/// <summary>
/// Produces a bounded, cycle-safe, protected-aware textual snapshot of an arbitrary object graph.
/// Ordinary values (strings, numbers, enums, operational URLs/paths, collections, records) are
/// preserved; protected values render a marker without being materialized; free strings are scrubbed
/// of credential fragments. Depth, string length, collection size and member counts are capped with
/// explicit truncation markers rather than reduced to a field allowlist.
/// </summary>
public static class DiagnosticSnapshot
{
    public static string Capture(object? value, DiagnosticLimits? limits = null)
    {
        var effective = limits ?? DiagnosticLimits.Default;
        var builder = new StringBuilder();
        Write(builder, value, effective, 0, new HashSet<object>(ReferenceEqualityComparer.Instance));
        return builder.ToString();
    }

    private static void Write(
        StringBuilder builder, object? value, DiagnosticLimits limits, int depth, HashSet<object> seen)
    {
        if (value is null)
        {
            builder.Append("null");
            return;
        }

        if (ProtectedValueInspector.TryDescribe(value, out var marker))
        {
            builder.Append(marker);
            return;
        }

        switch (value)
        {
            case string text:
                WriteString(builder, text, limits);
                return;
            case bool boolean:
                builder.Append(boolean ? "true" : "false");
                return;
            case char character:
                WriteString(builder, character.ToString(), limits);
                return;
            case Enum enumeration:
                builder.Append(enumeration.ToString());
                return;
            case System.Text.Json.JsonElement json:
                WriteString(
                    builder,
                    json.ValueKind == System.Text.Json.JsonValueKind.String
                        ? json.GetString() ?? string.Empty
                        : json.GetRawText(),
                    limits);
                return;
            case IFormattable formattable
                when value is sbyte or byte or short or ushort or int or uint or long or ulong
                    or float or double or decimal or Guid or DateTime or DateTimeOffset or TimeSpan:
                builder.Append(formattable.ToString(null, CultureInfo.InvariantCulture));
                return;
        }

        var type = value.GetType();
        if (depth >= limits.MaxDepth)
        {
            builder.Append('<').Append(type.Name).Append(" …[depth-limited]>");
            return;
        }

        var isReferenceType = !type.IsValueType;
        if (isReferenceType && !seen.Add(value))
        {
            builder.Append("<cycle:").Append(type.Name).Append('>');
            return;
        }

        try
        {
            switch (value)
            {
                case IDictionary dictionary:
                    WriteDictionary(builder, dictionary, limits, depth, seen);
                    return;
                case IEnumerable enumerable:
                    WriteEnumerable(builder, enumerable, limits, depth, seen);
                    return;
                default:
                    WriteObject(builder, value, type, limits, depth, seen);
                    return;
            }
        }
        finally
        {
            if (isReferenceType)
            {
                seen.Remove(value);
            }
        }
    }

    private static void WriteString(StringBuilder builder, string text, DiagnosticLimits limits)
    {
        var redacted = SensitiveTextRedactor.Redact(text);
        if (redacted.Length > limits.MaxStringLength)
        {
            builder.Append('"')
                .Append(redacted, 0, limits.MaxStringLength)
                .Append("…[truncated ")
                .Append((redacted.Length - limits.MaxStringLength).ToString(CultureInfo.InvariantCulture))
                .Append(" chars]\"");
            return;
        }

        builder.Append('"').Append(redacted).Append('"');
    }

    private static void WriteDictionary(
        StringBuilder builder, IDictionary dictionary, DiagnosticLimits limits, int depth, HashSet<object> seen)
    {
        builder.Append('{');
        var count = 0;
        foreach (DictionaryEntry entry in dictionary)
        {
            if (count >= limits.MaxCollectionItems)
            {
                builder.Append(" …[").Append((dictionary.Count - count).ToString(CultureInfo.InvariantCulture)).Append(" more]");
                break;
            }

            if (count > 0)
            {
                builder.Append(", ");
            }

            Write(builder, entry.Key, limits, depth + 1, seen);
            builder.Append(": ");
            Write(builder, entry.Value, limits, depth + 1, seen);
            count++;
        }

        builder.Append('}');
    }

    private static void WriteEnumerable(
        StringBuilder builder, IEnumerable enumerable, DiagnosticLimits limits, int depth, HashSet<object> seen)
    {
        builder.Append('[');
        var count = 0;
        foreach (var item in enumerable)
        {
            if (count >= limits.MaxCollectionItems)
            {
                builder.Append(" …[more items]");
                break;
            }

            if (count > 0)
            {
                builder.Append(", ");
            }

            Write(builder, item, limits, depth + 1, seen);
            count++;
        }

        builder.Append(']');
    }

    private static void WriteObject(
        StringBuilder builder, object value, Type type, DiagnosticLimits limits, int depth, HashSet<object> seen)
    {
        builder.Append(type.Name).Append(" {");
        var properties = type.GetProperties(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance)
            .Where(property => property.CanRead && property.GetIndexParameters().Length == 0)
            .ToArray();
        var written = 0;
        foreach (var property in properties)
        {
            if (written >= limits.MaxMembers)
            {
                builder.Append(" …[").Append((properties.Length - written).ToString(CultureInfo.InvariantCulture)).Append(" more]");
                break;
            }

            if (written > 0)
            {
                builder.Append(',');
            }

            builder.Append(' ').Append(property.Name).Append('=');
            object? propertyValue;
            try
            {
                propertyValue = property.GetValue(value);
            }
            catch (Exception exception)
            {
                builder.Append("<getter-failed:").Append(exception.GetType().Name).Append('>');
                written++;
                continue;
            }

            Write(builder, propertyValue, limits, depth + 1, seen);
            written++;
        }

        builder.Append(" }");
    }
}

/// <summary>
/// Renders an exception at full fidelity — type, message, stack, exception <c>Data</c>, and the
/// complete inner/aggregate cause chain — while scrubbing credential fragments and never
/// materializing protected values. This is what durable and UI sinks emit instead of a raw
/// <see cref="Exception.ToString"/>, so an upstream SDK exception carrying a token/credential-bearing
/// URL cannot leak plaintext.
/// </summary>
public static class ExceptionDiagnostics
{
    public static string Render(Exception? exception, DiagnosticLimits? limits = null)
    {
        if (exception is null)
        {
            return string.Empty;
        }

        var effective = limits ?? DiagnosticLimits.Default;
        var builder = new StringBuilder();
        Write(builder, exception, effective, 0, 0, new HashSet<Exception>(ReferenceEqualityComparer.Instance));
        return builder.ToString();
    }

    private static void Write(
        StringBuilder builder, Exception exception, DiagnosticLimits limits, int depth, int indent, HashSet<Exception> seen)
    {
        var pad = new string(' ', indent * 2);
        if (depth >= limits.MaxExceptionDepth || !seen.Add(exception))
        {
            builder.Append(pad).Append("…[exception chain truncated]");
            return;
        }

        builder.Append(pad)
            .Append(exception.GetType().FullName)
            .Append(": ")
            .Append(SensitiveTextRedactor.Redact(exception.Message));

        foreach (DictionaryEntry entry in exception.Data)
        {
            builder.Append('\n').Append(pad).Append("  Data[")
                .Append(SensitiveTextRedactor.Redact(entry.Key?.ToString() ?? "null"))
                .Append("]=")
                .Append(DiagnosticSnapshot.Capture(entry.Value, limits));
        }

        if (exception.StackTrace is { } stack)
        {
            builder.Append('\n').Append(pad).Append("  ")
                .Append(SensitiveTextRedactor.Redact(stack).Replace("\n", "\n" + pad + "  "));
        }

        if (exception is AggregateException aggregate && aggregate.InnerExceptions.Count > 0)
        {
            foreach (var inner in aggregate.InnerExceptions)
            {
                builder.Append('\n').Append(pad).Append(" ---> aggregated:\n");
                Write(builder, inner, limits, depth + 1, indent + 1, seen);
            }

            return;
        }

        if (exception.InnerException is { } innerException)
        {
            builder.Append('\n').Append(pad).Append(" ---> inner:\n");
            Write(builder, innerException, limits, depth + 1, indent + 1, seen);
        }
    }
}
