namespace BoardSync.Api.Shared.Kernel;

/// <summary>
/// Generic paginated result wrapper used across all list endpoints.
/// </summary>
public class PagedResult<T>
{
    public IReadOnlyList<T> Items { get; init; }
    public int TotalCount { get; init; }
    public int Page { get; init; }
    public int PageSize { get; init; }
    public int TotalPages => PageSize > 0 ? (int)Math.Ceiling((double)TotalCount / PageSize) : 0;
    public bool HasNextPage => Page < TotalPages;
    public bool HasPreviousPage => Page > 1;

    /// <summary>
    /// Opaque pointer at the last item on this page. Pass it back as <c>?cursor=</c> to get the next
    /// page without an offset scan; null when the endpoint does not support cursor paging or there
    /// is nothing after this page.
    /// </summary>
    /// <remarks>
    /// Purely additive — <c>?page</c> keeps working exactly as before, and clients that ignore this
    /// field see no change. It exists because offsets degrade with depth and, on a feed that is
    /// still being written to, shift rows across page boundaries while a client is reading.
    /// </remarks>
    public string? NextCursor { get; init; }

    public PagedResult(IReadOnlyList<T> items, int totalCount, int page, int pageSize, string? nextCursor = null)
    {
        Items = items;
        TotalCount = totalCount;
        Page = page;
        PageSize = pageSize;
        NextCursor = nextCursor;
    }

    public static PagedResult<T> Empty(int page = 1, int pageSize = 20)
        => new([], 0, page, pageSize);
}

/// <summary>
/// Query parameters for paginated requests.
/// </summary>
public class PaginationQuery
{
    private int _page = 1;
    private int _pageSize = 20;

    public int Page
    {
        get => _page;
        set => _page = value < 1 ? 1 : value;
    }

    public int PageSize
    {
        get => _pageSize;
        set => _pageSize = value < 1 ? 1 : value > 100 ? 100 : value;
    }

    /// <summary>
    /// Opaque cursor from a previous response's <see cref="PagedResult{T}.NextCursor"/>. When set,
    /// endpoints that support it page forward from that point and ignore <see cref="Page"/>.
    /// </summary>
    public string? Cursor { get; set; }

    public int Skip => (Page - 1) * PageSize;
}
