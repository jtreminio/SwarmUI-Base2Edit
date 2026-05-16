namespace Base2Edit;

public sealed record StageLoras(
    IReadOnlyList<string> Names,
    IReadOnlyList<string> Weights,
    IReadOnlyList<string> TencWeights)
{
    public static readonly StageLoras Empty = new([], [], []);
    public bool IsEmpty => Names.Count == 0;
}
