using SwarmUI.Text2Image;

namespace Base2Edit;

/// <summary>
/// Generic snapshot/restore for arbitrary <see cref="T2IParamType"/> entries in a
/// <see cref="T2IParamInput"/>. Captures the presence and value of each param at
/// construction time so they can be restored, removed, or reset later.
/// Replaces hand-rolled per-field snapshot classes.
/// </summary>
internal sealed class ParamSnapshot
{
    private readonly T2IParamInput _input;
    private readonly Entry[] _entries;

    private readonly record struct Entry(string Id, bool Had, object Value);

    private ParamSnapshot(T2IParamInput input, Entry[] entries)
    {
        _input = input;
        _entries = entries;
    }

    /// <summary>
    /// Captures the current state of the given params from <paramref name="input"/>.
    /// </summary>
    public static ParamSnapshot Of(T2IParamInput input, params T2IParamType[] paramTypes)
    {
        Entry[] entries = new Entry[paramTypes.Length];
        for (int i = 0; i < paramTypes.Length; i++)
        {
            bool had = input.TryGetRaw(paramTypes[i], out object val);
            entries[i] = new Entry(paramTypes[i].ID, had, val);
        }

        return new ParamSnapshot(input, entries);
    }

    /// <summary>
    /// Restores all params to their snapshotted state: sets back values that existed,
    /// removes any that were absent at snapshot time. This is the full bidirectional restore.
    /// </summary>
    public void Restore()
    {
        foreach (Entry entry in _entries)
        {
            if (entry.Had)
            {
                _input.InternalSet.ValuesInput[entry.Id] = entry.Value;
            }
            else
            {
                _input.InternalSet.ValuesInput.Remove(entry.Id);
            }
        }
    }

    /// <summary>
    /// Strips all snapshotted params from input (only those that were present at snapshot time).
    /// Params that were absent at snapshot time are left untouched.
    /// </summary>
    public void Remove()
    {
        foreach (Entry entry in _entries)
        {
            if (entry.Had)
            {
                _input.InternalSet.ValuesInput.Remove(entry.Id);
            }
        }
    }

    /// <summary>
    /// Restores only the params that existed at snapshot time (sets, never removes).
    /// Params that were absent at snapshot time are left in whatever state they're currently in.
    /// </summary>
    public void Reset()
    {
        foreach (Entry entry in _entries)
        {
            if (entry.Had)
            {
                _input.InternalSet.ValuesInput[entry.Id] = entry.Value;
            }
        }
    }
}
