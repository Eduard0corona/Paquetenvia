using Microsoft.EntityFrameworkCore;

namespace Paqueteria.Infrastructure.DataProtection;

public sealed class PlatformDataProtectionDbContext(
    DbContextOptions<PlatformDataProtectionDbContext> options) : DbContext(options);
