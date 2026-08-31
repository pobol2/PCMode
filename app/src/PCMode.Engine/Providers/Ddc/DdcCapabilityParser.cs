using System.Globalization;

namespace PCMode.Engine;

/// <summary>
/// Reads the <c>vcp(...)</c> section out of a monitor's raw MCCS capability
/// string.
/// </summary>
/// <remarks>
/// A capability string looks like
/// <c>(prot(monitor)type(lcd)model(X)cmds(01 02 03)vcp(02 04 10 60(0F 11 12))mccs_ver(2.1))</c>.
/// Only the parenthesised <c>vcp(...)</c> block matters here: it is a
/// whitespace-separated list of two-hex-digit feature codes, each optionally
/// followed by its own parenthesised list of the values that code accepts.
///
/// Nothing outside <c>vcp(...)</c> is read. <c>prot</c>, <c>type</c>,
/// <c>model</c> and the rest describe the monitor, not what it will let this
/// build change, and are of no use to a bounded feature set that only ever
/// asks about two codes.
///
/// UNKNOWN CODES ARE DROPPED, NOT KEPT
///
/// A code that is not one of <see cref="DdcVcpCode"/>'s members is skipped
/// entirely rather than recorded under some generic "other" bucket. Nothing in
/// this build has a control for it, so keeping it would be keeping a fact
/// nothing can act on.
/// </remarks>
internal static class DdcCapabilityParser
{
    public static DdcCapabilities Parse(string raw)
    {
        ArgumentNullException.ThrowIfNull(raw);

        string? block = ExtractParenthesised(raw, "vcp");

        if (block is null)
        {
            return DdcCapabilities.None;
        }

        var features = new Dictionary<DdcVcpCode, DdcVcpCapability>();
        int index = 0;

        while (index < block.Length)
        {
            if (char.IsWhiteSpace(block[index]))
            {
                index++;
                continue;
            }

            int codeStart = index;

            while (index < block.Length && char.IsAsciiHexDigit(block[index]))
            {
                index++;
            }

            if (index == codeStart)
            {
                // Something that is neither whitespace nor a hex digit -- not a
                // shape this parser expects. Skipped rather than thrown on: a
                // capability string this build cannot fully parse should yield
                // whatever it could still make sense of, not nothing at all.
                index++;
                continue;
            }

            bool recognised =
                byte.TryParse(
                    block[codeStart..index],
                    NumberStyles.HexNumber,
                    CultureInfo.InvariantCulture,
                    out byte code) &&
                (code == (byte)DdcVcpCode.Brightness || code == (byte)DdcVcpCode.InputSource);

            var values = new List<uint>();

            if (index < block.Length && block[index] == '(')
            {
                if (TryExtractParenthesised(block, index, out string valuesText, out int afterClose))
                {
                    foreach (string token in valuesText.Split(' ', StringSplitOptions.RemoveEmptyEntries))
                    {
                        if (uint.TryParse(
                                token, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint value))
                        {
                            values.Add(value);
                        }
                    }

                    index = afterClose;
                }
                else
                {
                    // Never closed. Nothing after this point in the vcp block can
                    // be trusted to be a code boundary rather than stray content
                    // from inside the unbalanced group, so parsing stops here.
                    break;
                }
            }

            if (recognised)
            {
                var vcpCode = (DdcVcpCode)code;
                features[vcpCode] = new DdcVcpCapability(vcpCode, values);
            }
        }

        return new DdcCapabilities(features);
    }

    /// <summary>
    /// The text strictly between the first balanced pair of parentheses that
    /// follows <paramref name="key"/>.
    /// </summary>
    private static string? ExtractParenthesised(string text, string key)
    {
        int open = text.IndexOf(key + "(", StringComparison.OrdinalIgnoreCase);

        return open >= 0 && TryExtractParenthesised(text, open + key.Length, out string content, out _)
            ? content
            : null;
    }

    /// <summary>
    /// The text strictly between the balanced pair of parentheses opening at
    /// <paramref name="openParenIndex"/>, and the index one past its closing
    /// parenthesis -- so a caller resuming a scan never has to re-derive it from
    /// the extracted text's length.
    /// </summary>
    private static bool TryExtractParenthesised(
        string text, int openParenIndex, out string content, out int afterClose)
    {
        content = string.Empty;
        afterClose = openParenIndex;

        if (openParenIndex >= text.Length || text[openParenIndex] != '(')
        {
            return false;
        }

        int depth = 1;
        int start = openParenIndex + 1;
        int index = start;

        while (index < text.Length && depth > 0)
        {
            if (text[index] == '(')
            {
                depth++;
            }
            else if (text[index] == ')')
            {
                depth--;

                if (depth == 0)
                {
                    content = text[start..index];
                    afterClose = index + 1;
                    return true;
                }
            }

            index++;
        }

        // Never closed. A capability string this build cannot even balance the
        // parentheses of is not one it can trust the rest of.
        return false;
    }
}
