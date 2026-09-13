using System.Net;
using System.Net.Sockets;
using Microsoft.Data.SqlClient;

namespace Integration.Shared.Diagnostics;

/// <summary>
/// Every value that leaves the process - HTTP body, DLQ payload, SQL column, log field -
/// goes through here. Connection strings, credentials and raw exception text never do.
/// </summary>
public static class SafeErrors
{
    public const string Unknown = "unknown_error";
    public const string SqlUnavailable = "sql_unavailable";
    public const string SqlError = "sql_error";
    public const string BrokerUnavailable = "broker_unavailable";
    public const string BrokerNack = "broker_nack";
    public const string BrokerReturned = "broker_unroutable";
    public const string BrokerConfirmTimeout = "broker_confirm_timeout";
    public const string HttpConnection = "http_connection";
    public const string HttpTimeout = "http_timeout";
    public const string HttpInvalidResponse = "http_invalid_response";
    public const string Cancelled = "cancelled";
    public const string AttemptBudgetExhausted = "attempt_budget_exhausted";

    public static string Classify(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        return exception switch
        {
            SqlException sql when IsConnectivity(sql) => SqlUnavailable,
            SqlException => SqlError,
            OperationCanceledException => Cancelled,
            TimeoutException => HttpTimeout,
            SocketException => HttpConnection,
            HttpRequestException => HttpConnection,
            _ => Unknown,
        };
    }

    /// <summary>Short, non-sensitive description stored in JobAttempts.SafeErrorCode and logs.</summary>
    public static string Describe(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        return $"{exception.GetType().Name}:{Classify(exception)}";
    }

    public static string FromStatusCode(HttpStatusCode statusCode) => $"http_{(int)statusCode}";

    public static bool IsSqlUnavailable(Exception exception) =>
        exception is SqlException sql && IsConnectivity(sql);

    private static bool IsConnectivity(SqlException exception)
    {
        foreach (SqlError error in exception.Errors)
        {
            // Host unreachable, database unavailable, command timeout, reset connection, login failure.
            if (error.Number is 53 or 40613 or -2 or 10054 or 18456 or 4060 or 233 or 64 or 121)
            {
                return true;
            }
        }

        return false;
    }
}
