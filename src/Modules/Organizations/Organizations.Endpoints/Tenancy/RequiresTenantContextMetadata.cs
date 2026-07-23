namespace Organizations.Endpoints.Tenancy;

public sealed class RequiresTenantContextMetadata
{
    private RequiresTenantContextMetadata(int invalidContextStatusCode) =>
        InvalidContextStatusCode = invalidContextStatusCode;

    public static RequiresTenantContextMetadata Instance { get; } = new(StatusCodes.Status400BadRequest);

    public int InvalidContextStatusCode { get; }

    public static RequiresTenantContextMetadata WithInvalidContextStatus(int statusCode) => new(statusCode);
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
}
