namespace BloodLink.Application.DTOs;

public sealed record PageRequest(int Number = 1, int Size = 25)
{
    public int SafeNumber => Math.Max(1, Number);
    public int SafeSize => Math.Clamp(Size, 1, 100);
    public int SafeOffset => (int)Math.Min((long)(SafeNumber - 1) * SafeSize, int.MaxValue);
}

public sealed record PagedResult<T>(IReadOnlyList<T> Items, int PageNumber, int PageSize, bool HasNext)
{
    public bool HasPrevious => PageNumber > 1;
}
