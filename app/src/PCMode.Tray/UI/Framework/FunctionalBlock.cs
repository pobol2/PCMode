using System.Collections.Concurrent;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace PCMode.Tray.UI.Framework;

/// <summary>
/// Base type for a Functional Block: a lookless control that owns a set of
/// named regions and nothing else.
/// </summary>
/// <remarks>
/// A block declares the regions it owns with <see cref="RegionAttribute"/> and
/// its inputs as dependency properties. It never builds a visual tree of its
/// own -- a Layout does that, as a <see cref="ControlTemplate"/> that arranges
/// the block's regions. Because the block depends on the region contract and
/// never on the arrangement, layouts are swappable: any template supplying the
/// declared regions is a valid layout for the block.
///
/// Each region then either ends in terminal content or recurses into another
/// block, and the region does not choose which -- content holding a view model
/// resolves to its block through an implicit <see cref="DataTemplate"/>, so no
/// level needs to know how deep the tree goes.
///
/// The contract is enforced when the template is applied, in both directions:
/// every declared region must be supplied, and no region may be supplied that
/// was not declared. That second half is what stops a Layout from quietly
/// growing structure the block does not own.
/// </remarks>
public abstract class FunctionalBlock : System.Windows.Controls.Control
{
    /// <remarks>
    /// Setting the instance <see cref="FrameworkElement.DefaultStyleKey"/> to
    /// the concrete type, rather than each block needing a static constructor
    /// calling <c>OverrideMetadata</c>, is what lets this one base class serve
    /// every block: a derived block resolves its own default Layout from the
    /// available resources and needs no construction boilerplate of its own.
    /// </remarks>
    protected FunctionalBlock()
    {
        DefaultStyleKey = GetType();
    }

    /// <summary>The regions this block owns, as declared on its type.</summary>
    public IReadOnlyList<RegionAttribute> Regions => RegionsOf(GetType());

    /// <summary>
    /// Applies the Layout, then enforces the block's region contract.
    /// </summary>
    /// <exception cref="TemplateContractException">
    /// A declared region is missing, is present at the wrong arity, or the
    /// layout supplies a region the block never declared.
    /// </exception>
    public override void OnApplyTemplate()
    {
        base.OnApplyTemplate();

        IReadOnlyList<RegionAttribute> declared = Regions;
        var bound = new HashSet<DependencyObject>(ReferenceEqualityComparer.Instance);

        foreach (RegionAttribute region in declared)
        {
            if (region.Blocks.Count == 0 && !region.Terminal)
            {
                throw new TemplateContractException(
                    $"Region '{region.Name}' on {GetType().Name} admits no child block and no terminal " +
                    "content, so nothing could ever appear in it.");
            }

            object? part = GetTemplateChild(region.Name);

            if (part is null)
            {
                throw new TemplateContractException(
                    $"The layout applied to {GetType().Name} is missing the required region " +
                    $"'{region.Name}'. Every layout for this block must supply it as a " +
                    $"{(region.Arity == RegionArity.Single ? nameof(Region) : nameof(RepeatedRegion))}.");
            }

            switch (region.Arity, part)
            {
                case (RegionArity.Single, Region single):
                    single.Bind(region);
                    bound.Add(single);
                    break;

                case (RegionArity.Repeated, RepeatedRegion repeated):
                    repeated.Bind(region);
                    bound.Add(repeated);
                    break;

                default:
                    throw new TemplateContractException(
                        $"The layout applied to {GetType().Name} supplies region '{region.Name}' as a " +
                        $"{part.GetType().Name}, but this block declares it as a " +
                        $"{(region.Arity == RegionArity.Single ? nameof(Region) : nameof(RepeatedRegion))}.");
            }
        }

        VerifyNothingUndeclared(bound);
    }

    /// <summary>
    /// Rejects structure the Layout invented: a Region the block never
    /// declared, or a child block created directly instead of through one.
    /// </summary>
    private void VerifyNothingUndeclared(HashSet<DependencyObject> bound)
    {
        int roots = VisualTreeHelper.GetChildrenCount(this);

        for (int index = 0; index < roots; index++)
        {
            Walk(VisualTreeHelper.GetChild(this, index));
        }

        void Walk(DependencyObject current)
        {
            if (current is Region or RepeatedRegion)
            {
                if (!bound.Contains(current))
                {
                    throw new TemplateContractException(
                        $"The layout applied to {GetType().Name} contains a Region that the block does " +
                        "not declare. Every Region must correspond to a [Region] on its block.");
                }

                // Whatever a bound region holds is that region's business.
                return;
            }

            if (current is FunctionalBlock block)
            {
                throw new TemplateContractException(
                    $"A Layout creates Functional Block '{block.GetType().Name}' directly. " +
                    "A Layout may create only its declared Regions; child blocks must be " +
                    "reached through a Region.");
            }

            int count = VisualTreeHelper.GetChildrenCount(current);
            for (int index = 0; index < count; index++)
            {
                Walk(VisualTreeHelper.GetChild(current, index));
            }
        }
    }

    // Cached because a block inside a repeated region applies its template
    // once per item, and attribute reflection is not free at list sizes.
    private static readonly ConcurrentDictionary<Type, RegionAttribute[]> Declared = new();

    internal static RegionAttribute[] RegionsOf(Type blockType) =>
        Declared.GetOrAdd(blockType, static type =>
            [.. type.GetCustomAttributes(typeof(RegionAttribute), inherit: true).Cast<RegionAttribute>()]);
}
