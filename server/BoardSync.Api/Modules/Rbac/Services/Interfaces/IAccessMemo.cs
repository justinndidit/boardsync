namespace BoardSync.Api.Modules.Rbac.Services.Interfaces;

/// <summary>
/// The per-request memo of resolved access, exposed so writes can drop it.
/// </summary>
/// <remarks>
/// Separate from <see cref="IAccessResolver"/> because it is the write side's concern, not the read
/// side's. A caller that only asks questions has no business being able to discard the answers.
/// </remarks>
public interface IAccessMemo
{
    /// <summary>Forgets everything resolved so far in this request.</summary>
    void Clear();
}
