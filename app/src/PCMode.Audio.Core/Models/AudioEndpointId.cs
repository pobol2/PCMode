namespace PCMode.Audio.Core.Models;

public readonly record struct AudioEndpointId
{
    public string Value { get; }

    public AudioEndpointId(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);

        Value = value;
    }

    public override string ToString()
    {
        return Value ?? string.Empty;
    }
}