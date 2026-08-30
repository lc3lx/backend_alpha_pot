using Microsoft.AspNetCore.Identity;
using MySqlConnector;
using Npgsql;

/**
 * Upsert website admin user into MySQL or PostgreSQL.
 *
 * Usage (from backend/):
 *   export DATABASE_PROVIDER=MySql
 *   export DATABASE_CONNECTION_STRING='Server=127.0.0.1;Port=3306;Database=scaralpha;User=scaralpha;Password=...'
 *   dotnet run --project tools/seed-admin -- scaralphaai@gmail.com 'YourPassword'
 */
if (args.Length < 2)
{
    Console.Error.WriteLine("Usage: seed-admin <email> <password>");
    return 1;
}

var email = args[0].Trim().ToLowerInvariant();
var password = args[1];
if (password.Length < 8)
{
    Console.Error.WriteLine("Password must be at least 8 characters.");
    return 1;
}

var provider = (Environment.GetEnvironmentVariable("DATABASE_PROVIDER") ?? "MySql").Trim();
var cs = Environment.GetEnvironmentVariable("DATABASE_CONNECTION_STRING")
         ?? Environment.GetEnvironmentVariable("ConnectionStrings__Default")
         ?? "";
if (string.IsNullOrWhiteSpace(cs))
{
    Console.Error.WriteLine("DATABASE_CONNECTION_STRING is required.");
    return 1;
}

var hasher = new PasswordHasher<object>();
var hash = hasher.HashPassword(new object(), password);
var now = DateTimeOffset.UtcNow;
var id = Guid.NewGuid();

var useMySql = provider.Equals("MySql", StringComparison.OrdinalIgnoreCase)
               || provider.Equals("MySQL", StringComparison.OrdinalIgnoreCase)
               || provider.Equals("MariaDb", StringComparison.OrdinalIgnoreCase)
               || cs.Contains("Port=3306", StringComparison.OrdinalIgnoreCase);

if (useMySql)
{
    await using var conn = new MySqlConnection(cs);
    await conn.OpenAsync();

    await using (var find = new MySqlCommand(
        "SELECT CAST(Id AS CHAR(36)) FROM users WHERE LOWER(Email) = LOWER(@email) LIMIT 1;", conn))
    {
        find.Parameters.AddWithValue("@email", email);
        var existing = await find.ExecuteScalarAsync();
        if (existing is string existingId)
        {
            await using var upd = new MySqlCommand(
                """
                UPDATE users
                SET PasswordHash = @hash,
                    Role = 1,
                    UpdatedAt = @now,
                    FullName = COALESCE(NULLIF(FullName, ''), 'ScarAlpha Admin')
                WHERE Id = @id;
                """, conn);
            upd.Parameters.AddWithValue("@hash", hash);
            upd.Parameters.AddWithValue("@now", now);
            upd.Parameters.AddWithValue("@id", existingId);
            var n = await upd.ExecuteNonQueryAsync();
            Console.WriteLine($"Updated admin user {email} (Role=Admin). rows={n}");
            return 0;
        }
    }

    await using (var ins = new MySqlCommand(
        """
        INSERT INTO users (Id, Email, PasswordHash, FullName, Role, IsMarketingDemo, CreatedAt, UpdatedAt)
        VALUES (@id, @email, @hash, @name, 1, 0, @now, @now);
        """, conn))
    {
        ins.Parameters.AddWithValue("@id", id);
        ins.Parameters.AddWithValue("@email", email);
        ins.Parameters.AddWithValue("@hash", hash);
        ins.Parameters.AddWithValue("@name", "ScarAlpha Admin");
        ins.Parameters.AddWithValue("@now", now);
        await ins.ExecuteNonQueryAsync();
    }
}
else
{
    await using var conn = new NpgsqlConnection(cs);
    await conn.OpenAsync();

    await using (var find = new NpgsqlCommand(
        """SELECT "Id"::text FROM users WHERE lower("Email") = lower(@email) LIMIT 1;""", conn))
    {
        find.Parameters.AddWithValue("email", email);
        var existing = await find.ExecuteScalarAsync();
        if (existing is string existingId)
        {
            await using var upd = new NpgsqlCommand(
                """
                UPDATE users
                SET "PasswordHash" = @hash,
                    "Role" = 1,
                    "UpdatedAt" = @now,
                    "FullName" = COALESCE(NULLIF("FullName", ''), 'ScarAlpha Admin')
                WHERE "Id" = @id::uuid;
                """, conn);
            upd.Parameters.AddWithValue("hash", hash);
            upd.Parameters.AddWithValue("now", now);
            upd.Parameters.AddWithValue("id", existingId);
            var n = await upd.ExecuteNonQueryAsync();
            Console.WriteLine($"Updated admin user {email} (Role=Admin). rows={n}");
            return 0;
        }
    }

    await using (var ins = new NpgsqlCommand(
        """
        INSERT INTO users ("Id", "Email", "PasswordHash", "FullName", "Role", "IsMarketingDemo", "CreatedAt", "UpdatedAt")
        VALUES (@id, @email, @hash, @name, 1, FALSE, @now, @now);
        """, conn))
    {
        ins.Parameters.AddWithValue("id", id);
        ins.Parameters.AddWithValue("email", email);
        ins.Parameters.AddWithValue("hash", hash);
        ins.Parameters.AddWithValue("name", "ScarAlpha Admin");
        ins.Parameters.AddWithValue("now", now);
        await ins.ExecuteNonQueryAsync();
    }
}

Console.WriteLine($"Created admin user {email} (Role=Admin).");
Console.WriteLine("Remember: set ADMIN_EMAILS in scaralpha.env and restart scaralpha-api.");
return 0;
