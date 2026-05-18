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
    Task<IReadOnlyList<ClipItem>> GetRecentAsync(int limit = 100);

    /// Full-text search via FTS5; returns matches ordered by rank.
    Task<IReadOnlyList<ClipItem>> SearchAsync(string query, int limit = 50);

    Task ClearUnpinnedAsync();

    /// Deletes the oldest unpinned items so at most keepCount remain.
    Task EvictOldestAsync(int keepCount);
}
