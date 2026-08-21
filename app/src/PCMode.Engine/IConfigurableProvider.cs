using System.Text.Json;

namespace PCMode.Engine;

/// <summary>
/// A provider that has settings of its own in the configuration document.
/// </summary>
/// <remarks>
/// WHY THIS EXISTS AT ALL
///
/// The theme provider needs no configuration: "Dark" means the same thing on
/// every machine. The cursor provider does, because "Corner" is not a fact about
/// Windows -- it is a name the user chose for a place on their own screen. There
/// is nowhere else that could live.
///
/// DELIBERATELY THE SMALLEST THING THAT WORKS
///
/// One optional interface, and a "providers" object in the document keyed by the
/// same provider key the modes already use. A provider is handed its own slice
/// and parses it itself, which is the same division the rest of the boundary
/// already keeps: the engine knows that a resource has a value, and never what
/// the value means.
///
/// Nothing here is shaped for the providers that come later. Audio, display and
/// the television will each want settings, and what those look like is not
/// knowable from this one; guessing now would produce a schema that has to be
/// migrated before anything has ever used it.
///
/// CONFIGURED BEFORE ANY MODE IS VALIDATED
///
/// <see cref="ModeConfiguration.Parse"/> calls this before it checks a single
/// mode, because for a provider like this one the two questions are the same
/// question: whether "Corner" is a usable value depends entirely on what the
/// settings defined. A document validated in the other order would accept a mode
/// naming a position that does not exist.
/// </remarks>
public interface IConfigurableProvider : IModeResourceProvider
{
    /// <summary>
    /// Which object under "providers" this reads, when it is not the one named
    /// after the resource.
    /// </summary>
    /// <remarks>
    /// Almost always the resource key, which is why it defaults to it: the theme's
    /// settings belong under "theme" and nowhere else.
    ///
    /// It is separate because one piece of equipment can be several resources. A
    /// television's address, its MAC address and the names of its inputs are one
    /// set of facts about one device, and the resources that switch it on and
    /// choose its input are two resources only because a mode needs to be able to
    /// set them independently. Making each read its own section would mean writing
    /// the address twice and getting to keep the two copies in agreement, and the
    /// day they disagreed the mode would half work.
    ///
    /// Providers sharing a section is therefore expected rather than tolerated.
    /// Each is handed the same object and each reads it, so no provider depends on
    /// another having been configured first.
    /// </remarks>
    string SettingsKey => Key;

    /// <summary>
    /// Takes this provider's settings, or reports what is wrong with them.
    /// </summary>
    /// <param name="settings">
    /// The provider's own object from the document, or null when the document
    /// has no settings for it. Null is not an error in itself: a provider that
    /// can work unconfigured should say so by returning true.
    /// </param>
    /// <remarks>
    /// Returning false refuses the whole document. That is the same rule the
    /// rest of loading follows -- a configuration that is partly understood
    /// produces a mode list where some entries work and some quietly do nothing.
    /// </remarks>
    bool TryConfigure(JsonElement? settings, out string? problem);
}
