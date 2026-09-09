namespace PCMode.Audio.Protocol;

public static class HostProtocolVersion
{
    public const int Current = 1;

    public const int MinimumCompatible = 1;

    public const string DefaultPipeName = "PCMode.Audio.Host";

    public static bool IsCompatible(int version)
    {
        return version >= MinimumCompatible && version <= Current;
    }
}
