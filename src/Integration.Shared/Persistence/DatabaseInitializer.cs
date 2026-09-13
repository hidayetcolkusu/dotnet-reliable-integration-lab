using System.Data.Common;
using System.Text.RegularExpressions;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

namespace Integration.Shared.Persistence;

/// <summary>
/// Creates the IntegrationLab database when missing and applies EF Core migrations.
/// Only ever invoked from the explicit initialize mode (Development/Testing); normal
/// startup never migrates.
/// </summary>
public static partial class DatabaseInitializer
{
    [GeneratedRegex("^[A-Za-z0-9_]{1,100}$")]
    private static partial Regex SafeDatabaseNameRegex();

    public static async Task EnsureCreatedAndMigratedAsync(string connectionString, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);

        var builder = new SqlConnectionStringBuilder(connectionString);
        var databaseName = builder.InitialCatalog;
        if (string.IsNullOrWhiteSpace(databaseName) || !SafeDatabaseNameRegex().IsMatch(databaseName))
        {
            throw new ArgumentException(
                $"The connection string must target a simple database name (letters, digits, underscore): '{databaseName}'.",
                nameof(connectionString));
        }

        builder.InitialCatalog = "master";
        builder.ConnectTimeout = 15;
        await using (var master = new SqlConnection(builder.ConnectionString))
        {
            await master.OpenAsync(cancellationToken).ConfigureAwait(false);
            await using DbCommand command = master.CreateCommand();
            command.CommandText = $"IF DB_ID(N'{databaseName}') IS NULL CREATE DATABASE [{databaseName}]";
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        var options = new DbContextOptionsBuilder<LabDbContext>()
            .UseSqlServer(connectionString, sql =>
            {
                sql.MigrationsAssembly(typeof(LabDbContext).Assembly.FullName);
                sql.CommandTimeout(120);
            })
            .Options;

        await using (var context = new LabDbContext(options))
        {
            await context.Database.MigrateAsync(cancellationToken).ConfigureAwait(false);
        }
    }
}
