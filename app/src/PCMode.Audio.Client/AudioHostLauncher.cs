using System.Diagnostics;
using PCMode.Audio.Core.Models;
using PCMode.Audio.Protocol;

namespace PCMode.Audio.Client;

public sealed class AudioHostLauncher
{
    private readonly string _hostPath;

    private readonly string _pipeName;

    public AudioHostLauncher(
        string? hostPath = null,
        string pipeName =
            HostProtocolVersion.DefaultPipeName)
    {
        _hostPath =
            hostPath ??
            Path.Combine(
                AppContext.BaseDirectory,
                "PCMode.Audio.Host.exe");

        _pipeName =
            pipeName;
    }

    public Process Launch()
    {
        ProcessStartInfo startInfo =
            CreateStartInfo();

        AddPipeArguments(
            startInfo);

        return Start(
            startInfo);
    }

    public Process LaunchDevelopment(
        AudioEndpointId sourceId)
    {
        if (sourceId == default)
        {
            throw new ArgumentException(
                "Development source endpoint is required.",
                nameof(sourceId));
        }

        ProcessStartInfo startInfo =
            CreateStartInfo();

        startInfo.ArgumentList.Add(
            "--development-source-id");

        startInfo.ArgumentList.Add(
            sourceId.Value);

        AddPipeArguments(
            startInfo);

        return Start(
            startInfo);
    }

    public async Task<Process>
        LaunchAndWaitUntilReadyAsync(
            IAudioHostClient client,
            TimeSpan timeout,
            CancellationToken cancellationToken =
                default)
    {
        Process process =
            Launch();

        return await WaitUntilReadyOrKillAsync(
            process,
            client,
            timeout,
            cancellationToken);
    }

    public async Task<Process>
        LaunchDevelopmentAndWaitUntilReadyAsync(
            AudioEndpointId sourceId,
            IAudioHostClient client,
            TimeSpan timeout,
            CancellationToken cancellationToken =
                default)
    {
        Process process =
            LaunchDevelopment(
                sourceId);

        return await WaitUntilReadyOrKillAsync(
            process,
            client,
            timeout,
            cancellationToken);
    }

    /// <summary>
    /// A Host that never becomes ready (crashed, or the driver refused its
    /// connect) must not be left running: it still holds the driver's
    /// single-consumer claim, so it would silently block every later launch
    /// -- including the next real one, not just the next test -- until
    /// somebody notices and kills it by hand.
    /// </summary>
    private async Task<Process> WaitUntilReadyOrKillAsync(
        Process process,
        IAudioHostClient client,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        try
        {
            return await WaitUntilReadyAsync(
                process,
                client,
                timeout,
                cancellationToken);
        }
        catch
        {
            if (!process.HasExited)
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                    process.WaitForExit(5_000);
                }
                catch
                {
                }
            }

            throw;
        }
    }

    private async Task<Process>
        WaitUntilReadyAsync(
            Process process,
            IAudioHostClient client,
            TimeSpan timeout,
            CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(
            client);

        Stopwatch stopwatch =
            Stopwatch.StartNew();

        while (stopwatch.Elapsed <
               timeout)
        {
            cancellationToken
                .ThrowIfCancellationRequested();

            if (process.HasExited)
            {
                throw new InvalidOperationException(
                    $"PCMode.Audio.Host exited with code {process.ExitCode}.");
            }

            try
            {
                await client.GetStatusAsync(
                    cancellationToken);

                return process;
            }
            catch (IOException)
            {
            }
            catch (TimeoutException)
            {
            }

            await Task.Delay(
                100,
                cancellationToken);
        }

        throw new TimeoutException(
            "PCMode.Audio.Host did not become ready.");
    }

    private ProcessStartInfo
        CreateStartInfo()
    {
        if (!File.Exists(
                _hostPath))
        {
            throw new FileNotFoundException(
                "PCMode.Audio.Host executable was not found.",
                _hostPath);
        }

        ProcessStartInfo startInfo;

        if (string.Equals(
                Path.GetExtension(
                    _hostPath),
                ".dll",
                StringComparison.OrdinalIgnoreCase))
        {
            startInfo =
                new ProcessStartInfo(
                    "dotnet");

            startInfo.ArgumentList.Add(
                _hostPath);
        }
        else
        {
            startInfo =
                new ProcessStartInfo(
                    _hostPath);
        }

        startInfo.UseShellExecute =
            false;

        startInfo.CreateNoWindow =
            true;

        return startInfo;
    }

    private void AddPipeArguments(
        ProcessStartInfo startInfo)
    {
        startInfo.ArgumentList.Add(
            "--pipe-name");

        startInfo.ArgumentList.Add(
            _pipeName);
    }

    private static Process Start(
        ProcessStartInfo startInfo)
    {
        return Process.Start(
                   startInfo)
               ?? throw new InvalidOperationException(
                   "Failed to start PCMode.Audio.Host.");
    }
}