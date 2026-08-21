using System.Text.Json;
using System.Text.Json.Nodes;

namespace PCMode.Engine.Tests;

/// <summary>
/// A webOS television that is not in the room.
/// </summary>
/// <remarks>
/// It answers the way a real one does -- registration first, then requests keyed
/// by id -- so everything worth testing about the television providers can be
/// tested: what happens when it is asleep, when it has never been paired with,
/// when it stops answering half way, and above all which of those a dry run is
/// allowed to cause.
///
/// It also records what was done TO it, which is the other half. The strongest
/// claims here are negative -- no packet was sent, no prompt was opened, no key
/// was written -- and a negative claim needs something that would have noticed.
/// </remarks>
internal sealed class FakeTelevision : ILgTvNetwork
{
    private readonly List<string> _requests = [];
    private readonly List<string> _woken = [];

    /// <summary>Whether it answers its socket at all. False is switched off.</summary>
    public bool Reachable { get; set; } = true;

    /// <summary>What getPowerState reports.</summary>
    public string PowerState { get; set; } = "Active";

    /// <summary>Set while it is still changing state.</summary>
    public string? Processing { get; set; }

    /// <summary>What getForegroundAppInfo reports.</summary>
    public string ForegroundApp { get; set; } = "com.webos.app.hdmi4";

    /// <summary>The certificate it presents.</summary>
    public string Fingerprint { get; set; } = new('A', 64);

    /// <summary>The key it hands out when somebody accepts a prompt.</summary>
    public string IssuedKey { get; set; } = "issued-key";

    /// <summary>Set to refuse switchInput.</summary>
    public bool RefusesInput { get; set; }

    /// <summary>Set to refuse the alert a settings write is carried on.</summary>
    public bool RefusesSettings { get; set; }

    /// <summary>Set to stop answering anything, without closing the socket.</summary>
    public bool GoesQuiet { get; set; }

    /// <summary>
    /// Everything that has actually been written to it, by category.
    /// </summary>
    /// <remarks>
    /// Only settings whose alert was created AND closed appear here, because that
    /// is the only sequence that writes anything on a real television. A test
    /// asserting a value is here is asserting the whole round trip happened.
    /// </remarks>
    public Dictionary<string, Dictionary<string, string>> Written { get; } =
        new(StringComparer.Ordinal);

    /// <summary>Each completed settings write, as the keys it carried, in order.</summary>
    public List<string> Writes { get; } = [];

    /// <summary>What it reports its picture mode to be.</summary>
    public string PictureMode
    {
        get => Setting(LgTvProfile.PictureCategory, LgTvProfile.PictureModeKey) ?? "normal";
        set => Record(LgTvProfile.PictureCategory, LgTvProfile.PictureModeKey, value);
    }

    /// <summary>One setting as it now stands, or null if nothing set it.</summary>
    public string? Setting(string category, string key) =>
        Written.TryGetValue(category, out Dictionary<string, string>? values) &&
        values.TryGetValue(key, out string? value)
            ? value
            : null;

    private void Record(string category, string key, string value)
    {
        if (!Written.TryGetValue(category, out Dictionary<string, string>? values))
        {
            values = new Dictionary<string, string>(StringComparer.Ordinal);
            Written[category] = values;
        }

        values[key] = value;
    }

    /// <summary>Every SSAP uri asked of it, in order.</summary>
    public IReadOnlyList<string> Requests => _requests;

    /// <summary>Every magic packet sent to it.</summary>
    public IReadOnlyList<string> Woken => _woken;

    /// <summary>How many sockets were opened.</summary>
    public int Connections { get; private set; }

    /// <summary>How many times a prompt was put on its screen.</summary>
    /// <remarks>
    /// The number that has to stay zero across every read, plan and dry run in
    /// this file. A registration with no key IS the prompt.
    /// </remarks>
    public int Prompts { get; private set; }

    /// <summary>The last input it was told to show.</summary>
    public string? SwitchedTo { get; private set; }

    public Task<ILgTvSocket> ConnectAsync(
        string host,
        int port,
        string? pinnedFingerprint,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (!Reachable)
        {
            throw new IOException($"No route to {host}.");
        }

        if (pinnedFingerprint is not null &&
            !string.Equals(pinnedFingerprint, Fingerprint, StringComparison.OrdinalIgnoreCase))
        {
            // What a pinned certificate is for. Something else is answering on the
            // television's address, and it does not get handed a client key.
            throw new IOException($"The certificate at {host} is not the one that was pinned.");
        }

        Connections++;

        return Task.FromResult<ILgTvSocket>(new Socket(this));
    }

    public Task WakeAsync(string host, string macAddress, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        _woken.Add(macAddress);

        return Task.CompletedTask;
    }

    /// <summary>One conversation, answering in the order it was spoken to.</summary>
    private sealed class Socket(FakeTelevision television) : ILgTvSocket
    {
        private readonly Queue<string> _pending = new();

        /// <summary>
        /// The settings an alert is carrying, until the alert is closed.
        /// </summary>
        /// <remarks>
        /// The whole point of the fake for this path. A real television writes
        /// nothing when the alert is created -- it writes when the alert is
        /// closed, because closing it is what fires the action attached to it. A
        /// fake that applied the settings on creation would let a bug that never
        /// closes the alert pass every test.
        /// </remarks>
        private (string Category, JsonElement Settings)? _alert;

        public string CertificateFingerprint => television.Fingerprint;

        public Task SendAsync(string message, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            using JsonDocument document = JsonDocument.Parse(message);
            JsonElement root = document.RootElement;

            string id = root.GetProperty("id").GetString()!;

            if (root.GetProperty("type").GetString() == "register")
            {
                Register(id, root.GetProperty("payload"));
                return Task.CompletedTask;
            }

            string uri = root.GetProperty("uri").GetString()!;
            JsonElement payload = root.GetProperty("payload");

            television._requests.Add(uri);

            if (uri == "ssap://tv/switchInput")
            {
                television.SwitchedTo = payload.GetProperty("inputId").GetString();
            }

            if (uri == "ssap://system.notifications/createAlert" && !television.RefusesSettings)
            {
                // Read from 'onclose', which is the action that will actually run.
                JsonElement parameters = payload.GetProperty("onclose").GetProperty("params");

                _alert = (parameters.GetProperty("category").GetString()!,
                          parameters.GetProperty("settings").Clone());
            }

            if (uri == "ssap://system.notifications/closeAlert" && _alert is (string category, JsonElement settings))
            {
                var keys = new List<string>();

                foreach (JsonProperty setting in settings.EnumerateObject())
                {
                    television.Record(category, setting.Name, setting.Value.GetString()!);
                    keys.Add(setting.Name);
                }

                television.Writes.Add(string.Join(",", keys));
                _alert = null;
            }

            _pending.Enqueue(new JsonObject
            {
                ["id"] = id,
                ["type"] = "response",
                ["payload"] = PayloadFor(uri, payload),
            }.ToJsonString());

            return Task.CompletedTask;
        }

        public async Task<string> ReceiveAsync(CancellationToken cancellationToken)
        {
            if (!television.GoesQuiet && _pending.Count > 0)
            {
                return _pending.Dequeue();
            }

            // A television that is thinking about it. Ends when whoever is waiting
            // gives up -- which is exactly what the test of giving up needs.
            await Task.Delay(Timeout.Infinite, cancellationToken).ConfigureAwait(false);

            throw new InvalidOperationException("Unreachable.");
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        private void Register(string id, JsonElement payload)
        {
            bool hasKey = payload.TryGetProperty("client-key", out JsonElement key) &&
                          !string.IsNullOrWhiteSpace(key.GetString());

            if (!hasKey)
            {
                // The prompt. Nothing else in this fake increments it, so a test
                // asserting it stayed at zero is asserting that nobody's evening
                // was interrupted.
                television.Prompts++;
            }

            _pending.Enqueue(new JsonObject
            {
                ["id"] = id,
                ["type"] = "registered",
                ["payload"] = new JsonObject { ["client-key"] = hasKey ? key.GetString() : television.IssuedKey },
            }.ToJsonString());
        }

        private JsonObject PayloadFor(string uri, JsonElement request)
        {
            if (uri == "ssap://settings/getSystemSettings")
            {
                string category = request.GetProperty("category").GetString()!;
                var reported = new JsonObject();

                foreach (JsonElement key in request.GetProperty("keys").EnumerateArray())
                {
                    if (television.Setting(category, key.GetString()!) is string value)
                    {
                        reported[key.GetString()!] = value;
                    }
                }

                return new JsonObject { ["returnValue"] = true, ["settings"] = reported };
            }

            if (uri == "ssap://system.notifications/createAlert")
            {
                return television.RefusesSettings
                    ? new JsonObject { ["returnValue"] = false }
                    : new JsonObject { ["returnValue"] = true, ["alertId"] = "alert_1" };
            }

            if (uri.EndsWith("getPowerState", StringComparison.Ordinal))
            {
                var power = new JsonObject { ["state"] = television.PowerState };

                if (television.Processing is not null)
                {
                    power["processing"] = television.Processing;
                }

                return power;
            }

            if (uri.EndsWith("getForegroundAppInfo", StringComparison.Ordinal))
            {
                return new JsonObject { ["appId"] = television.ForegroundApp };
            }

            if (uri == "ssap://tv/switchInput")
            {
                return new JsonObject { ["returnValue"] = !television.RefusesInput };
            }

            return new JsonObject { ["returnValue"] = true };
        }
    }
}

/// <summary>A credential store that keeps nothing beyond the test.</summary>
internal sealed class FakeCredentials : ILgTvCredentials
{
    private readonly Dictionary<string, LgTvCredential> _stored = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>How many times a credential was written.</summary>
    public int Writes { get; private set; }

    /// <summary>Set to make saving fail.</summary>
    public bool Refuses { get; set; }

    public LgTvCredential? Read(string host) =>
        _stored.TryGetValue(host, out LgTvCredential? credential) ? credential : null;

    public bool TryWrite(string host, LgTvCredential credential, out string? problem)
    {
        Writes++;

        if (Refuses)
        {
            problem = "the credential could not be saved.";
            return false;
        }

        _stored[host] = credential;
        problem = null;
        return true;
    }

    /// <summary>Puts a credential in place, as a previous pairing would have.</summary>
    public FakeCredentials Paired(string host, string key, string fingerprint)
    {
        _stored[host] = new LgTvCredential(key, fingerprint);
        Writes = 0;
        return this;
    }
}
