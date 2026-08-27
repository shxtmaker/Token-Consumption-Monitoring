using System.Text.Json;
using TokenConsumptionMonitoring.Models.Usage;

namespace TokenConsumptionMonitoring.Services.QueryMethods;

/// <summary>统一把传输异常转换为候选和快照状态。</summary>
public static class QueryFailureClassifier
{
    public static CandidateStatus StatusOf(Exception exception)
    {
        if (exception is QueryTransportException transport) return transport.Status;
        if (exception is JsonException) return CandidateStatus.SchemaMismatch;
        var message = exception.Message;
        if (message.Contains("401", StringComparison.OrdinalIgnoreCase)) return CandidateStatus.AuthRequired;
        if (message.Contains("403", StringComparison.OrdinalIgnoreCase)) return CandidateStatus.Forbidden;
        if (message.Contains("429", StringComparison.OrdinalIgnoreCase)) return CandidateStatus.RateLimited;
        if (message.Contains("schema", StringComparison.OrdinalIgnoreCase)
            || message.Contains("field", StringComparison.OrdinalIgnoreCase)) return CandidateStatus.SchemaMismatch;
        if (exception is InvalidOperationException or FormatException or KeyNotFoundException)
            return CandidateStatus.SchemaMismatch;
        if (exception is TimeoutException or TaskCanceledException or HttpRequestException)
            return CandidateStatus.NetworkFailure;
        return CandidateStatus.NetworkFailure;
    }

    public static string ReasonOf(Exception exception)
        => exception is QueryTransportException transport
            ? transport.Message
            : exception switch
            {
                TimeoutException or TaskCanceledException => "请求超时",
                _ => exception.Message,
            };

    public static SnapshotStatus SnapshotStatusOf(CandidateStatus status) => status switch
    {
        CandidateStatus.AuthRequired => SnapshotStatus.AuthRequired,
        CandidateStatus.Forbidden => SnapshotStatus.Forbidden,
        CandidateStatus.RateLimited => SnapshotStatus.RateLimited,
        CandidateStatus.NetworkFailure => SnapshotStatus.TemporaryFailure,
        CandidateStatus.SchemaMismatch => SnapshotStatus.SchemaMismatch,
        CandidateStatus.Stale => SnapshotStatus.Stale,
        CandidateStatus.Unsupported or CandidateStatus.NoReliableUsage or CandidateStatus.RequiresSelection
            => SnapshotStatus.PermanentFailure,
        _ => SnapshotStatus.NoData,
    };
}

/// <summary>远程方法用于保留 HTTP 状态分类的异常。</summary>
public class QueryTransportException : Exception
{
    public CandidateStatus Status { get; }
    public int? HttpStatus { get; }

    public QueryTransportException(CandidateStatus status, string message, int? httpStatus = null, Exception? inner = null)
        : base(message, inner)
    {
        Status = status;
        HttpStatus = httpStatus;
    }
}
