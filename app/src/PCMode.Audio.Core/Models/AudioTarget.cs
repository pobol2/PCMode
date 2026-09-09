namespace PCMode.Audio.Core.Models;

public sealed class AudioTarget
{
    public string Id { get; }

    public string Name { get; }

    public IReadOnlyList<AudioRoute> Routes { get; }

    public AudioTarget(string id, string name, params AudioRoute[] routes)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(routes);

        if (routes.Length == 0)
        {
            throw new ArgumentException("An audio target must contain at least one route.", nameof(routes));
        }

        var routeCopy = new AudioRoute[routes.Length];

        for (int i = 0; i < routes.Length; i++)
        {
            routeCopy[i] = routes[i] ?? throw new ArgumentException($"Route {i} cannot be null.", nameof(routes));
        }

        Id = id;
        Name = name;
        Routes = Array.AsReadOnly(routeCopy);
    }
}
