using Microsoft.Extensions.Options;
using SQLPulseAPI.Net8.Configuration;
using SQLPulseAPI.Net8.Models.SqlErrors;

namespace SQLPulseAPI.Net8.Services.SqlErrors;

/// <summary>Resolves a previously returned error event by id.</summary>
public interface ISqlErrorEventCache
{
    /// <summary>Remembers events so they stay resolvable by id.</summary>
    void Store(IEnumerable<SqlErrorEvent> events);

    /// <summary>Returns a remembered event, or null when it has been evicted or never seen.</summary>
    SqlErrorEvent? Get(string id);
}

/// <summary>
/// A bounded, in-memory index of the error events most recently returned to a caller.
/// </summary>
/// <remarks>
/// <para>
/// SQLPulse deliberately does not persist error events: they belong to whichever system
/// produced them, and copying an application's error stream into SQLPulse's own database
/// would duplicate data SQLPulse is not the owner of. The detail endpoint therefore
/// answers from what a list query has just returned.
/// </para>
/// <para>
/// Eviction is reported honestly. A caller asking for an event that has aged out is told
/// to re-run the query rather than being shown a partially reconstructed one.
/// </para>
/// </remarks>
public sealed class SqlErrorEventCache(IOptions<SqlErrorOptions> options) : ISqlErrorEventCache
{
    private readonly int _capacity = options.Value.EventCacheSize;
    private readonly Dictionary<string, SqlErrorEvent> _byId = new(StringComparer.Ordinal);
    private readonly Queue<string> _insertionOrder = new();
    private readonly object _gate = new();

    /// <inheritdoc />
    public void Store(IEnumerable<SqlErrorEvent> events)
    {
        ArgumentNullException.ThrowIfNull(events);

        lock (_gate)
        {
            foreach (var errorEvent in events)
            {
                if (string.IsNullOrEmpty(errorEvent.Id) || !_byId.TryAdd(errorEvent.Id, errorEvent))
                {
                    // Ids are derived from content, so a repeat is the same observation.
                    continue;
                }

                _insertionOrder.Enqueue(errorEvent.Id);

                while (_insertionOrder.Count > _capacity)
                {
                    _byId.Remove(_insertionOrder.Dequeue());
                }
            }
        }
    }

    /// <inheritdoc />
    public SqlErrorEvent? Get(string id)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            return null;
        }

        lock (_gate)
        {
            return _byId.GetValueOrDefault(id);
        }
    }
}
