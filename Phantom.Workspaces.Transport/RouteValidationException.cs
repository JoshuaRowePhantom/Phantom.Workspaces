namespace Phantom.Workspaces.Transport;

/// <summary>A value-free rejection of a reachability rule before persistence.</summary>
public sealed class RouteValidationException : ArgumentException
{
    public RouteValidationException(string reasonCode, string field)
        : base(SafeReason(reasonCode), SafeField(field))
    {
        this.ReasonCode = SafeReason(reasonCode);
        this.Field = SafeField(field);
    }

    public string ReasonCode { get; }

    public string Field { get; }

    public bool IsIdentityFailure => this.ReasonCode is "descriptor.entity-id.missing-or-invalid"
        or "descriptor.entity-id.target-mismatch";

    private static string SafeReason(string reason) => reason switch
    {
        "route-id.missing" or "route-id.invalid" or "route.priority.out-of-range"
        or "route.expiry.not-after-confirmation" or "descriptor.shape.invalid"
        or "descriptor.type.missing" or "descriptor.type.unsupported"
        or "descriptor.type.route-mismatch" or "descriptor.entity-id.missing-or-invalid"
        or "descriptor.entity-id.target-mismatch" or "descriptor.hub-urls.invalid-count"
        or "endpoint.missing" or "endpoint.nonabsolute" or "endpoint.invalid-scheme"
        or "endpoint.missing-host" or "endpoint.userinfo"
        or "endpoint.credential-query-or-fragment" => reason,
        _ => "route.validation-unknown",
    };

    private static string SafeField(string field) => field switch
    {
        "route-id" or "route.priority" or "route.expires-at" or "descriptor"
        or "descriptor.type" or "descriptor.url" or "descriptor.entity-id"
        or "descriptor.hub-urls" or "descriptor.hub-urls[]" or "endpoint" => field,
        _ => "unknown",
    };
}
