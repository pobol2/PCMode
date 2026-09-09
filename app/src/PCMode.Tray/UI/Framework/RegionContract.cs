namespace PCMode.Tray.UI.Framework;

/// <summary>Whether a Region holds one child or a sequence of them.</summary>
public enum RegionArity
{
    Single,
    Repeated,
}

/// <summary>
/// One Region owned by a Functional Block: its name, its arity, and what may
/// appear in it.
/// </summary>
/// <remarks>
/// This attribute is the whole contract. A block declares the regions it owns
/// here and nothing else; a Layout is any <see cref="System.Windows.Controls.ControlTemplate"/>
/// that supplies exactly these regions, under these names, at these arities.
/// Because the declaration lives on the block itself there is no second
/// description of the tree to keep in step -- renaming a block or a region is
/// a compiler-checked change rather than a string that silently stops
/// matching.
///
/// <see cref="Blocks"/> names the child Functional Blocks the region may
/// recurse into. <see cref="Terminal"/> says whether it may instead bottom out
/// in an explicit <see cref="FinalUiComponent"/>. A region
/// that allows neither is a mistake, and is rejected when the block's template
/// is applied.
/// </remarks>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = true, Inherited = true)]
public sealed class RegionAttribute(string name, params Type[] blocks) : Attribute
{
    /// <summary>The template part name the Layout must use, e.g. <c>PART_Selector</c>.</summary>
    public string Name { get; } = name;

    /// <summary>The child Functional Blocks this region may recurse into.</summary>
    public IReadOnlyList<Type> Blocks { get; } = blocks;

    public RegionArity Arity { get; init; } = RegionArity.Single;

    /// <summary>Whether this region may hold terminal content rather than a child block.</summary>
    public bool Terminal { get; init; }

    /// <summary>Whether <paramref name="candidate"/> is a block this region admits.</summary>
    public bool Admits(Type candidate)
    {
        ArgumentNullException.ThrowIfNull(candidate);

        foreach (Type allowed in Blocks)
        {
            if (allowed.IsAssignableFrom(candidate))
            {
                return true;
            }
        }

        return false;
    }

    public string Describe() =>
        Blocks.Count == 0
            ? Terminal
                ? "terminal content only"
                : "nothing"
            : string.Join(", ", Blocks.Select(block => block.Name))
                + (Terminal ? ", or terminal content" : string.Empty);
}
