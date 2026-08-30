using Microsoft.Extensions.Configuration;

namespace ScarAlpha.Infrastructure.Persistence;

public static class DatabaseProviderHelper
{
    public const string DefaultProvider = "MySql";

    public static string ResolveProvider(IConfiguration configuration) =>
        (configuration["DATABASE_PROVIDER"] ?? configuration["Database:Provider"] ?? DefaultProvider).Trim();

    public static bool IsInMemory(IConfiguration configuration) =>
        string.Equals(ResolveProvider(configuration), "InMemory", StringComparison.OrdinalIgnoreCase);

    public static bool IsMySql(IConfiguration configuration)
    {
        var provider = ResolveProvider(configuration);
        return provider.Equals("MySql", StringComparison.OrdinalIgnoreCase)
               || provider.Equals("MySQL", StringComparison.OrdinalIgnoreCase)
               || provider.Equals("MariaDb", StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsNpgsql(IConfiguration configuration)
    {
        var provider = ResolveProvider(configuration);
        return provider.Equals("Npgsql", StringComparison.OrdinalIgnoreCase)
               || provider.Equals("Postgres", StringComparison.OrdinalIgnoreCase)
               || provider.Equals("PostgreSQL", StringComparison.OrdinalIgnoreCase);
    }
}
