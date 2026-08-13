namespace BoardSync.Api.Data;

/// <summary>
/// Whether the current unit of work is inside an explicit transaction.
/// </summary>
/// <remarks>
/// Exists so services that are otherwise ignorant of persistence can still tell when they are
/// running inside one, without taking a dependency on <see cref="BoardSyncDbContext"/> and
/// reopening the door this codebase closed by moving all data access behind repositories.
/// </remarks>
public interface ITransactionState
{
    /// <summary>True while an explicit transaction is open on this request's unit of work.</summary>
    bool InTransaction { get; }
}

/// <inheritdoc />
public class TransactionState : ITransactionState
{
    private readonly BoardSyncDbContext _context;

    public TransactionState(BoardSyncDbContext context)
    {
        _context = context;
    }

    public bool InTransaction => _context.Database.CurrentTransaction is not null;
}
