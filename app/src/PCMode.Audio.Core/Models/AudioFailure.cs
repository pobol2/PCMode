namespace PCMode.Audio.Core.Models;

public sealed record AudioFailure
{
    public string Type { get; }

    public string Message { get; }

    public int HResult { get; }

    public DateTimeOffset TimestampUtc { get; }

    public AudioFailure(
        string type,
        string message,
        int hResult,
        DateTimeOffset timestampUtc)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(
            type);

        ArgumentException.ThrowIfNullOrWhiteSpace(
            message);

        Type =
            type;

        Message =
            message;

        HResult =
            hResult;

        TimestampUtc =
            timestampUtc;
    }

    public static AudioFailure FromException(
        Exception exception)
    {
        ArgumentNullException.ThrowIfNull(
            exception);

        Exception effective =
            exception;

        while (effective.InnerException is not null)
        {
            effective =
                effective.InnerException;
        }

        return new AudioFailure(
            effective.GetType().FullName ??
                effective.GetType().Name,
            effective.Message,
            effective.HResult,
            DateTimeOffset.UtcNow);
    }
}