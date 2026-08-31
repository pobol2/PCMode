namespace PCMode.Tray.Platform.Startup;

/// <summary>The one per-user Windows startup value PCMode owns.</summary>
public interface IAutoStartRegistration
{
    string? ReadCommand();

    void WriteCommand(string? command);
}
