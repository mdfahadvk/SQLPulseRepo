using SQLPulseAPI.Net8.Models.Performance;
using SQLPulseAPI.Net8.Models.SqlErrors;
using SQLPulseAPI.Net8.Services.Performance;
using SQLPulseAPI.Net8.Services.SqlErrors;

namespace SQLPulseAPI.Net8.Tests.Fakes;

/// <summary>
/// An <see cref="ISqlErrorSource"/> whose outcome the test chooses.
/// </summary>
/// <remarks>
/// Lets a test stand in for an application log feed, an observability backend or an
/// approved Extended Events session without any of them existing, and — just as
/// importantly — lets it stand in for one that is missing or broken.
/// </remarks>
public sealed class StubSqlErrorSource(
    string name = "stub_source",
    SqlErrorSourceKind kind = SqlErrorSourceKind.ApplicationTelemetry) : ISqlErrorSource
{
    /// <summary>Result returned when <see cref="Throws"/> is not set.</summary>
    public SqlErrorSourceResult Result { get; set; } = SqlErrorSourceResult.Available([]);

    /// <summary>When set, <see cref="QueryAsync"/> throws this instead of returning.</summary>
    public Exception? Throws { get; set; }

    /// <summary>The query of the most recent call.</summary>
    public SqlErrorQuery? LastQuery { get; private set; }

    /// <inheritdoc />
    public string Name { get; } = name;

    /// <inheritdoc />
    public SqlErrorSourceKind Kind { get; } = kind;

    /// <summary>Builds a source that answers with the supplied events.</summary>
    public static StubSqlErrorSource Returning(params RawSqlErrorEvent[] events) =>
        new() { Result = SqlErrorSourceResult.Available(events) };

    /// <inheritdoc />
    public Task<SqlErrorSourceResult> QueryAsync(
        SqlErrorQuery query,
        CancellationToken cancellationToken = default)
    {
        LastQuery = query;

        if (Throws is not null)
        {
            throw Throws;
        }

        return Task.FromResult(Result);
    }
}

/// <summary>Returns pre-built SQL error responses without consulting any source.</summary>
public sealed class StubSqlErrorService : ISqlErrorService
{
    /// <summary>Response returned by <see cref="ListAsync"/>.</summary>
    public SqlErrorListResponse ListResponse { get; set; } = new();

    /// <summary>Response returned by <see cref="SummarizeAsync"/>.</summary>
    public SqlErrorSummaryResponse SummaryResponse { get; set; } = new();

    /// <summary>Events resolvable by <see cref="Find"/>, keyed by id.</summary>
    public Dictionary<string, SqlErrorEvent> Known { get; } = new(StringComparer.Ordinal);

    /// <summary>Queries received, in order.</summary>
    public List<SqlErrorQuery> Queries { get; } = [];

    /// <summary>Registers an event so it can be resolved by id.</summary>
    public StubSqlErrorService With(SqlErrorEvent errorEvent)
    {
        Known[errorEvent.Id] = errorEvent;
        return this;
    }

    /// <inheritdoc />
    public Task<SqlErrorListResponse> ListAsync(
        SqlErrorQuery query,
        CancellationToken cancellationToken = default)
    {
        Queries.Add(query);
        return Task.FromResult(ListResponse);
    }

    /// <inheritdoc />
    public Task<SqlErrorSummaryResponse> SummarizeAsync(
        SqlErrorQuery query,
        CancellationToken cancellationToken = default)
    {
        Queries.Add(query);
        return Task.FromResult(SummaryResponse);
    }

    /// <inheritdoc />
    public SqlErrorEvent? Find(string id) => Known.GetValueOrDefault(id);
}

/// <summary>Returns a fixed correlation and records what it was asked about.</summary>
public sealed class StubSqlErrorCorrelationService : ISqlErrorCorrelationService
{
    /// <summary>Correlation returned by every call.</summary>
    public SqlErrorCorrelation Correlation { get; set; } = new()
    {
        Caveats = [SqlErrorCorrelation.CausationCaveat],
    };

    /// <summary>Events passed to <see cref="CorrelateAsync"/>, in order.</summary>
    public List<SqlErrorEvent> Correlated { get; } = [];

    /// <inheritdoc />
    public Task<SqlErrorCorrelation> CorrelateAsync(
        SqlErrorEvent errorEvent,
        CancellationToken cancellationToken = default)
    {
        Correlated.Add(errorEvent);
        return Task.FromResult(Correlation);
    }
}

/// <summary>Returns a pre-built snapshot, so live correlation can be tested without a server.</summary>
public sealed class StubPerformanceSnapshotService : IPerformanceSnapshotService
{
    /// <summary>Snapshot returned by every call.</summary>
    public PerformanceSnapshot Snapshot { get; set; } = new();

    /// <summary>When set, <see cref="CollectAsync"/> throws this instead of returning.</summary>
    public Exception? Throws { get; set; }

    /// <summary>Requests received, in order.</summary>
    public List<PerformanceSnapshotRequest> Requests { get; } = [];

    /// <inheritdoc />
    public Task<PerformanceSnapshot> CollectAsync(
        PerformanceSnapshotRequest request,
        CancellationToken cancellationToken = default)
    {
        Requests.Add(request);

        if (Throws is not null)
        {
            throw Throws;
        }

        return Task.FromResult(Snapshot);
    }
}
