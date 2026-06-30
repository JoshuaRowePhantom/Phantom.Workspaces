using System;
using System.Net;

namespace Phantom.Workspaces.Data.Web.Client;

/// <summary>
/// Thrown when a web data-access request fails. Distinguishes genuine connectivity failures (transport
/// errors with no response, or gateway/server 5xx responses — typically a dropped or unreachable dev
/// tunnel relay) from application-level failures (4xx), so callers such as a reconnecting client can
/// decide whether re-establishing the connection could help.
/// </summary>
public sealed class WebDataAccessRequestException : Exception
{
    public WebDataAccessRequestException(string message, HttpStatusCode? statusCode, Exception? innerException = null)
        : base(message, innerException)
    {
        this.StatusCode = statusCode;
    }

    /// <summary>The HTTP status code of the failed response, or null when no response was received (transport failure).</summary>
    public HttpStatusCode? StatusCode { get; }

    /// <summary>
    /// Whether the failure indicates a connectivity problem worth reconnecting for: no response at all
    /// (transport failure), a server/gateway error (status &gt;= 500), or an authentication failure
    /// (status 401 — a stale token that can be resolved by refreshing and reconnecting).
    /// </summary>
    public bool IsConnectivityFailure => this.StatusCode is null || this.StatusCode == HttpStatusCode.Unauthorized || (int)this.StatusCode >= 500;
}
