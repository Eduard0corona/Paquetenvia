using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Notifications.Infrastructure.Persistence;

public sealed class NotificationsDbContextFactory : IDesignTimeDbContextFactory<NotificationsDbContext>
{
    public NotificationsDbContext CreateDbContext(string[] args)
    {
        var connectionString = Environment.GetEnvironmentVariable("PAQUETERIA_DESIGN_CONNECTION")
            ?? "Host=localhost;Database=paqueteria;Username=paqueteria_migrator;Password=design-only";
        var options = new DbContextOptionsBuilder<NotificationsDbContext>()
            .UseNpgsql(connectionString, postgres =>
                postgres.MigrationsHistoryTable("__ef_migrations_history_notifications", "platform"))
            .Options;
        return new NotificationsDbContext(options);
    }
}
