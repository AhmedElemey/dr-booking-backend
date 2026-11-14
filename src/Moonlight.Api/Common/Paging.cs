using Microsoft.EntityFrameworkCore;

namespace Moonlight.Api.Common;

public sealed record PagedResult<T>(IReadOnlyList<T> Items, int Page, int PageSize, int TotalCount);

public sealed record PageRequest(int Page, int PageSize)
{
    public const int DefaultPageSize = 20;
    public const int MaxPageSize = 100;

    public int Skip => (Page - 1) * PageSize;

    public static PageRequest Create(int? page, int? pageSize)
    {
        var errors = new Dictionary<string, string[]>();
        if (page is < 1)
        {
            errors["page"] = ["page starts at 1."];
        }

        if (pageSize is < 1 or > MaxPageSize)
        {
            errors["pageSize"] = [$"pageSize must be between 1 and {MaxPageSize}."];
        }

        if (errors.Count > 0)
        {
            throw new ApiException(ErrorCodes.ValidationFailed, errors: errors);
        }

        return new PageRequest(page ?? 1, pageSize ?? DefaultPageSize);
    }
}

public static class PagingExtensions
{
    // The query must already have a stable, explicit ordering.
    public static async Task<PagedResult<TOut>> ToPagedAsync<TIn, TOut>(
        this IQueryable<TIn> query,
        PageRequest request,
        Func<IReadOnlyList<TIn>, Task<IReadOnlyList<TOut>>> map,
        CancellationToken ct)
    {
        var total = await query.CountAsync(ct);
        var rows = await query.Skip(request.Skip).Take(request.PageSize).ToListAsync(ct);
        return new PagedResult<TOut>(await map(rows), request.Page, request.PageSize, total);
    }
}
