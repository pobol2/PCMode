using System.Globalization;
using System.Text;

namespace PCMode.Engine;

/// <summary>
/// Makes the stable half of an identity from the name somebody typed.
/// </summary>
/// <remarks>
/// Derived from the name rather than generated, because an id appears in a key
/// -- in the recorded current target, in a picker favorite, in a log line --
/// and 'desk/gaming/steam' is something a person can match to what they see on
/// screen where a number would not be. It is derived once, at creation: a later
/// rename changes the name and deliberately leaves the id alone, which is the
/// whole point of having both.
/// </remarks>
internal static class LogicalId
{
    /// <summary>An id from <paramref name="name"/> that is not one of <paramref name="taken"/>.</summary>
    public static string From(string name, ISet<string> taken)
    {
        ArgumentNullException.ThrowIfNull(taken);

        var slug = new StringBuilder(name?.Length ?? 0);

        foreach (char character in (name ?? string.Empty).ToLowerInvariant())
        {
            if (char.IsAsciiLetterOrDigit(character))
            {
                slug.Append(character);
            }
            else if (slug.Length > 0 && slug[^1] != '-')
            {
                slug.Append('-');
            }
        }

        string candidate = slug.ToString().Trim('-');

        // A name written entirely in characters an id cannot hold is still a
        // perfectly good name. It just needs its id from somewhere else.
        if (candidate.Length == 0)
        {
            candidate = "item";
        }

        if (taken.Add(candidate))
        {
            return candidate;
        }

        for (int suffix = 2; ; suffix++)
        {
            string next = string.Create(CultureInfo.InvariantCulture, $"{candidate}-{suffix}");

            if (taken.Add(next))
            {
                return next;
            }
        }
    }
}
