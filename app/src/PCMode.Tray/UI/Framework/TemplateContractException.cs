using System.Windows.Controls;

namespace PCMode.Tray.UI.Framework;

/// <summary>
/// Thrown when a <see cref="ControlTemplate"/> applied to a
/// <see cref="FunctionalBlock"/> does not satisfy the block's declared
/// region contract.
/// </summary>
/// <remarks>
/// This is deliberately fatal rather than a silent no-op. A block whose
/// region is missing still renders -- it just renders without that part of
/// its content -- and that failure is invisible until someone notices the
/// hole. Failing at template application instead turns a whole class of
/// layout mistakes into an immediate, located error.
/// </remarks>
public sealed class TemplateContractException(string message)
    : InvalidOperationException(message);
