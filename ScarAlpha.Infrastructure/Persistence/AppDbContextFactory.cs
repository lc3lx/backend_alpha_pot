using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.Extensions.Configuration;

namespace ScarAlpha.Infrastructure.Persistence;

/// <summary>Design-time factory for <c>dotnet ef migrations</c> (MySQL by default).</summary>
public sealed class AppDbContextFactory : IDesignTimeDbContextFactory<AppDbContext>
{
    public AppDbContext CreateDbContext(string[] args)
    {
        var configuration = new ConfigurationBuilder()
            .AddEnvironmentVariables()
            .Build();

        var provider = DatabaseProviderHelper.ResolveProvider(configuration);
        var connectionString = configuration["DATABASE_CONNECTION_STRING"]
                               ?? configuration.GetConnectionString("Default")
                               ?? "Server=127.0.0.1;Port=3306;Database=scaralpha;User=root;Password=;";

        var optionsBuilder = new DbContextOptionsBuilder<AppDbContext>();

        if (DatabaseProviderHelper.IsMySql(configuration))
        {
            optionsBuilder.UseMySql(
                connectionString,
                ServerVersion.Parse("8.0.36-mysql"),
                mySql => mySql.MigrationsAssembly(typeof(AppDbContext).Assembly.FullName));
        }
        else if (string.Equals(provider, "InMemory", StringComparison.OrdinalIgnoreCase))
        {
            optionsBuilder.UseInMemoryDatabase("ScarAlphaDesignTime");
        }
        else
        {
            optionsBuilder.UseNpgsql(
                connectionString,
                npgsql => npgsql.MigrationsAssembly(typeof(AppDbContext).Assembly.FullName));
        }

        return new AppDbContext(optionsBuilder.Options, configuration);
    }
}
