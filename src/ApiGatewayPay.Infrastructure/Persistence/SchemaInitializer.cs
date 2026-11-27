using Microsoft.EntityFrameworkCore;

namespace ApiGatewayPay.Infrastructure.Persistence;

/// <summary>
/// Solo desarrollo/pruebas: crea el esquema y los triggers que hacen el libro append-only en SQLite.
/// En SQL Server use db/sqlserver/*.sql (incluye tabla LEDGER APPEND_ONLY y permisos mínimos).
/// </summary>
public static class SchemaInitializer
{
    public static async Task EnsureCreatedAsync(AppDbContext db, CancellationToken ct = default)
    {
        await db.Database.EnsureCreatedAsync(ct);
        if (db.Database.IsSqlite())
        {
            // WAL: lecturas concurrentes con una escritura (API + worker sobre el mismo archivo).
            await db.Database.ExecuteSqlRawAsync("PRAGMA journal_mode=WAL;", ct);
            await db.Database.ExecuteSqlRawAsync("""
                CREATE TRIGGER IF NOT EXISTS ledger_no_update BEFORE UPDATE ON ledger_entries
                BEGIN SELECT RAISE(ABORT, 'ledger_entries is append-only'); END;
                """, ct);
            await db.Database.ExecuteSqlRawAsync("""
                CREATE TRIGGER IF NOT EXISTS ledger_no_delete BEFORE DELETE ON ledger_entries
                BEGIN SELECT RAISE(ABORT, 'ledger_entries is append-only'); END;
                """, ct);
        }
        else if (db.Database.IsSqlServer())
        {
            await db.Database.ExecuteSqlRawAsync("""
                IF OBJECT_ID('dbo.trg_ledger_append_only', 'TR') IS NULL
                EXEC('CREATE TRIGGER dbo.trg_ledger_append_only ON dbo.ledger_entries INSTEAD OF UPDATE, DELETE AS THROW 51000, ''ledger_entries is append-only'', 1;');
                """, ct);
        }
    }

    /// <summary>Script DDL de SQL Server generado desde el modelo (base de db/sqlserver/).</summary>
    public static string GenerateSqlServerScript()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlServer("Server=.;Database=ApiGatewayPay;Integrated Security=true").Options;
        using var db = new AppDbContext(options);
        return db.Database.GenerateCreateScript();
    }
}
