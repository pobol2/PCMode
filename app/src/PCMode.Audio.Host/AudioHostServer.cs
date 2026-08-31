using System.Collections.Concurrent;
using System.IO.Pipes;
using PCMode.Audio.Core.Models;
using PCMode.Audio.Protocol;
using PCMode.Audio.Protocol.Messages;

namespace PCMode.Audio.Host;

public sealed class AudioHostServer
{
    private readonly HostOptions _options;

    private readonly AudioHostRuntime _runtime;

    private readonly AudioHostLifetime _lifetime;

    public AudioHostServer(
        HostOptions options,
        AudioHostRuntime runtime,
        AudioHostLifetime lifetime)
    {
        ArgumentNullException.ThrowIfNull(
            options);

        ArgumentNullException.ThrowIfNull(
            runtime);

        ArgumentNullException.ThrowIfNull(
            lifetime);

        _options =
            options;

        _runtime =
            runtime;

        _lifetime =
            lifetime;
    }

    public async Task RunAsync(
        CancellationToken cancellationToken = default)
    {
        var activeClients =
            new ConcurrentDictionary<Task, byte>();

        while (!cancellationToken.IsCancellationRequested)
        {
            var pipe =
                new NamedPipeServerStream(
                    _options.PipeName,
                    PipeDirection.InOut,
                    NamedPipeServerStream.MaxAllowedServerInstances,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous);

            try
            {
                await pipe.WaitForConnectionAsync(
                    cancellationToken);
            }
            catch (OperationCanceledException)
                when (cancellationToken.IsCancellationRequested)
            {
                pipe.Dispose();

                break;
            }

            // Each connection is handled concurrently so one slow,
            // hung, or malformed client cannot block every other
            // client (including PCMode itself) from reaching the Host.
            Task clientTask =
                RunClientAsync(
                    pipe,
                    cancellationToken);

            activeClients[clientTask] =
                0;

            _ = clientTask.ContinueWith(
                completed =>
                    activeClients.TryRemove(
                        completed,
                        out _),
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }

        await Task.WhenAll(
            activeClients.Keys);
    }

    private async Task RunClientAsync(
        NamedPipeServerStream pipe,
        CancellationToken cancellationToken)
    {
        try
        {
            await HandleClientAsync(
                pipe,
                cancellationToken);
        }
        finally
        {
            pipe.Dispose();
        }
    }

    private async Task HandleClientAsync(
        Stream stream,
        CancellationToken cancellationToken)
    {
        while (true)
        {
            AudioHostCommand? command;

            try
            {
                command =
                    await AudioHostWireProtocol
                        .ReadCommandAsync(
                            stream,
                            cancellationToken);
            }
            catch (EndOfStreamException)
            {
                return;
            }

            if (command is null)
            {
                return;
            }

            AudioHostResponse response =
                await DispatchAsync(
                    command);

            await AudioHostWireProtocol
                .WriteResponseAsync(
                    stream,
                    response,
                    cancellationToken);

            if (command.Kind ==
                    AudioHostCommandKind.Shutdown &&
                response.Success)
            {
                _lifetime.RequestStop();

                return;
            }
        }
    }

    private async Task<AudioHostResponse>
        DispatchAsync(
            AudioHostCommand command)
    {
        if (!HostProtocolVersion
                .IsCompatible(
                    command.ProtocolVersion))
        {
            return AudioHostResponse.Error(
                command.RequestId,
                "IncompatibleProtocol",
                $"Client protocol version {command.ProtocolVersion} " +
                $"is incompatible with Host version " +
                $"{HostProtocolVersion.Current}. " +
                $"Supported versions: " +
                $"{HostProtocolVersion.MinimumCompatible}-" +
                $"{HostProtocolVersion.Current}.");
        }

        try
        {
            switch (command.Kind)
            {
                case AudioHostCommandKind.Hello:
                {
                    AudioHostStatus status =
                        await _runtime
                            .GetStatusAsync();

                    return AudioHostResponse.Ok(
                        command.RequestId,
                        status: status);
                }

                case AudioHostCommandKind.ApplyTarget:
                {
                    ApplyAudioTargetRequest request =
                        command.ApplyTarget
                        ?? throw new InvalidDataException(
                            "ApplyTarget command has no target payload.");

                    await _runtime.ApplyAsync(
                        request.Target.ToModel());

                    return AudioHostResponse.Ok(
                        command.RequestId);
                }

                case AudioHostCommandKind.Stop:
                    await _runtime.StopAsync();

                    return AudioHostResponse.Ok(
                        command.RequestId);

                case AudioHostCommandKind.GetStatus:
                {
                    AudioHostStatus status =
                        await _runtime
                            .GetStatusAsync();

                    return AudioHostResponse.Ok(
                        command.RequestId,
                        status: status);
                }

                case AudioHostCommandKind.GetHealth:
                {
                    var health =
                        await _runtime
                            .GetHealthAsync();

                    return AudioHostResponse.Ok(
                        command.RequestId,
                        health:
                            AudioEngineHealthDto
                                .FromModel(
                                    health));
                }

                case AudioHostCommandKind.Shutdown:
                    await _runtime.StopAsync();

                    return AudioHostResponse.Ok(
                        command.RequestId);

                case AudioHostCommandKind.RecalibrateRoute:
                {
                    RecalibrateRouteRequest request =
                        command.Recalibrate
                        ?? throw new InvalidDataException(
                            "RecalibrateRoute command has no payload.");

                    await _runtime.RecalibrateAsync(
                        new AudioEndpointId(
                            request.EndpointId),
                        request.Volume,
                        request.Muted,
                        request.Processing.GainDb,
                        TimeSpan.FromMilliseconds(
                            request.DelayMilliseconds),
                        request.Processing.ParametricEq
                            ?.ToModel());

                    return AudioHostResponse.Ok(
                        command.RequestId);
                }

                default:
                    return AudioHostResponse.Error(
                        command.RequestId,
                        "UnknownCommand",
                        $"Unsupported command '{command.Kind}'.");
            }
        }
        catch (Exception ex)
        {
            return AudioHostResponse.Error(
                command.RequestId,
                ex.GetType().Name,
                ex.InnerException is null
                    ? ex.Message
                    : $"{ex.Message} {ex.InnerException.Message}");
        }
    }
}