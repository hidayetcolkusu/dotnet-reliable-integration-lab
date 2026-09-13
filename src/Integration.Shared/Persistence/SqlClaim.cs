using System.Data;
using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace Integration.Shared.Persistence;

/// <summary>
/// Helpers for the short, parameterised claim transactions the stores run.
///
/// Isolation assumption for this lab: READ_COMMITTED_SNAPSHOT is OFF on IntegrationLab, so
/// UPDLOCK/READPAST behaves as a work queue and two dispatchers never claim the same row.
/// All lease deadlines come from SQL's own UTC clock (SYSUTCDATETIME()), never from a client clock.
/// </summary>
internal static class SqlClaim
{
    public static DbCommand CreateCommand(DbContext context, string sql)
    {
        var connection = context.Database.GetDbConnection();
        var command = connection.CreateCommand();
        command.CommandText = sql;
        var transaction = context.Database.CurrentTransaction;
        if (transaction is not null)
        {
            command.Transaction = transaction.GetDbTransaction();
        }

        return command;
    }

    public static DbParameter Add(this DbCommand command, string name, DbType type, object? value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.DbType = type;
        parameter.Value = value ?? DBNull.Value;
        command.Parameters.Add(parameter);
        return parameter;
    }

    public static async Task EnsureOpenAsync(DbContext context, CancellationToken cancellationToken)
    {
        var connection = context.Database.GetDbConnection();
        if (connection.State != ConnectionState.Open)
        {
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    public static string? GetNullableString(this DbDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);

    public static Guid? GetNullableGuid(this DbDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetGuid(ordinal);

    public static DateTimeOffset? GetNullableDateTimeOffset(this DbDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetFieldValue<DateTimeOffset>(ordinal);
}
