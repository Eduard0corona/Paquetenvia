using Microsoft.AspNetCore.Mvc;

namespace Paqueteria.ArchitectureFixtures;

public sealed class InvalidDomainController : ControllerBase
{
    public Type ForeignInfrastructure => typeof(Pricing.Infrastructure.AssemblyReference);
}
