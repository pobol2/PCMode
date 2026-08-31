using PCMode.Audio.Protocol;

namespace PCMode.Audio.Host;

public sealed record HostOptions(
    string PipeName,
    string? DevelopmentSourceEndpointId)
{
    public bool UsesDevelopmentInput =>
        !string.IsNullOrWhiteSpace(
            DevelopmentSourceEndpointId);

    public static HostOptions Parse(
        string[] args)
    {
        string pipeName =
            HostProtocolVersion
                .DefaultPipeName;

        string? developmentSource =
            null;

        for (int i = 0;
             i < args.Length;
             ++i)
        {
            switch (args[i])
            {
                case "--pipe-name":
                    pipeName =
                        ReadValue(
                            args,
                            ref i,
                            "--pipe-name");

                    break;

                case "--development-source-id":
                    developmentSource =
                        ReadValue(
                            args,
                            ref i,
                            "--development-source-id");

                    break;

                default:
                    throw new ArgumentException(
                        $"Unknown Host argument '{args[i]}'.");
            }
        }

        return new HostOptions(
            pipeName,
            developmentSource);
    }

    private static string ReadValue(
        string[] args,
        ref int index,
        string argument)
    {
        index++;

        if (index >=
                args.Length ||
            string.IsNullOrWhiteSpace(
                args[index]))
        {
            throw new ArgumentException(
                $"{argument} requires a value.");
        }

        return args[index];
    }
}