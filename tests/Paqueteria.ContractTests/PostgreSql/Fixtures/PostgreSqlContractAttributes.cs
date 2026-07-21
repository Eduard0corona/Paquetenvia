namespace Paqueteria.ContractTests.PostgreSql.Fixtures;

[AttributeUsage(AttributeTargets.Method)]
internal sealed class PostgreSqlContractFactAttribute : FactAttribute
{
    public PostgreSqlContractFactAttribute()
    {
        if (string.Equals(
                Environment.GetEnvironmentVariable("PAQUETERIA_SKIP_POSTGRES_CONTRACT_TESTS"),
                "true",
                StringComparison.OrdinalIgnoreCase))
        {
            Skip = "PostgreSQL contract tests were explicitly disabled with PAQUETERIA_SKIP_POSTGRES_CONTRACT_TESTS=true.";
        }
    }
}

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class PostgreSqlContractCollection : ICollectionFixture<PostgreSqlContractFixture>
{
    public const string Name = "PostgreSQL runtime contracts";
}
