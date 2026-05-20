using System.Collections.Generic;
using System.Threading.Tasks;
using PinBoard.Models;

namespace PinBoard.Services;

public interface IHistoryStore : IAsyncDisposable
{
    /// Creates the DB schema (idempotent — safe to call on every launch).
    Task InitializeAsync();

    /// Inserts a new item and returns its assigned id.
    Task<long> AddAsync(ClipItem item);

    Task UpdatePinnedAsync(long id, bool pinned);
    Task DeleteAsync(long id);

    /// Returns the most recent items ordered by (pinned DESC, created DESC).
    /// Optionally filter by kind(s) and/or pinned-only.
    Task<IReadOnlyList<ClipItem>> GetRecentAsync(
        int limit = 100,
        IReadOnlyCollection<ClipItemKind>? kinds = null,
        bool pinnedOnly = false);

    /// Full-text search via FTS5; returns matches ordered by rank.
    /// Optionally filter by kind(s) and/or pinned-only.
    Task<IReadOnlyList<ClipItem>> SearchAsync(
        string query,
        int limit = 50,
        IReadOnlyCollection<ClipItemKind>? kinds = null,
        bool pinnedOnly = false);

    Task ClearUnpinnedAsync();

    /// Deletes the oldest unpinned items so at most keepCount remain.
    Task EvictOldestAsync(int keepCount);

    /// Deletes unpinned items older than the cutoff. days == 0 is a no-op
    /// (interpreted as "unlimited"). Returns the number of rows removed.
    Task<int> SweepExpiredAsync(int days);
}
