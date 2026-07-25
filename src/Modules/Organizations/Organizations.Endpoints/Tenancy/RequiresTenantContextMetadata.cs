namespace Organizations.Endpoints.Tenancy;

public sealed class RequiresTenantContextMetadata
{
    private RequiresTenantContextMetadata(
        int invalidContextStatusCode,
        int inactiveIdentityStatusCode)
    {
        InvalidContextStatusCode = invalidContextStatusCode;
        InactiveIdentityStatusCode = inactiveIdentityStatusCode;
    }

    public static RequiresTenantContextMetadata Instance { get; } = new(
        StatusCodes.Status400BadRequest,
        StatusCodes.Status403Forbidden);

    public int InvalidContextStatusCode { get; }
    public int InactiveIdentityStatusCode { get; }

    public static RequiresTenantContextMetadata WithInvalidContextStatus(int statusCode) => new(
        statusCode,
        StatusCodes.Status403Forbidden);

    public static RequiresTenantContextMetadata WithStatusCodes(
        int invalidContextStatusCode,
        int inactiveIdentityStatusCode) => new(
            invalidContextStatusCode,
            inactiveIdentityStatusCode);
}

public static class TenantEndpointConventionExtensions
{
    public static TBuilder RequireTenantContext<TBuilder>(this TBuilder builder)
        where TBuilder : IEndpointConventionBuilder
    {
        builder.WithMetadata(RequiresTenantContextMetadata.Instance);
        return builder;
    }

    public static TBuilder RequireTenantContext<TBuilder>(this TBuilder builder, int invalidContextStatusCode)
        where TBuilder : IEndpointConventionBuilder
    {
        builder.WithMetadata(RequiresTenantContextMetadata.WithInvalidContextStatus(invalidContextStatusCode));
        return builder;
    }

    public static TBuilder RequireTenantContext<TBuilder>(
        this TBuilder builder,
        int invalidContextStatusCode,
        int inactiveIdentityStatusCode)
        where TBuilder : IEndpointConventionBuilder
    {
        builder.WithMetadata(RequiresTenantContextMetadata.WithStatusCodes(
            invalidContextStatusCode,
            inactiveIdentityStatusCode));
        return builder;
    }
}
