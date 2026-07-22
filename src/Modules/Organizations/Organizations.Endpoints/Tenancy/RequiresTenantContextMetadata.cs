namespace Organizations.Endpoints.Tenancy;

public sealed class RequiresTenantContextMetadata
{
    private RequiresTenantContextMetadata()
    {
    }

    public static RequiresTenantContextMetadata Instance { get; } = new();
}

public static class TenantEndpointConventionExtensions
{
    public static TBuilder RequireTenantContext<TBuilder>(this TBuilder builder)
        where TBuilder : IEndpointConventionBuilder
    {
        builder.WithMetadata(RequiresTenantContextMetadata.Instance);
        return builder;
    }
}
