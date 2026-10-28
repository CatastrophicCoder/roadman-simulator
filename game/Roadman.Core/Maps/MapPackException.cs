namespace Roadman.Core.Maps;

/// <summary>
/// A map pack file is missing, unreadable or doesn't match what this game version expects.
/// The message names the file and the problem.
/// </summary>
public sealed class MapPackException : Exception
{
    public MapPackException(string message)
        : base(message)
    {
    }

    public MapPackException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
