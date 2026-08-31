namespace PCMode.Engine.Tests;

/// <summary>Reading the <c>vcp(...)</c> block out of a raw MCCS capability string.</summary>
public sealed class DdcCapabilityParserTests
{
    [Fact]
    public void Reads_a_bare_code_as_supported_with_no_enumerated_values()
    {
        DdcCapabilities capabilities = DdcCapabilityParser.Parse(
            "(prot(monitor)type(lcd)model(X)cmds(01 02 03)vcp(02 04 10 14)mccs_ver(2.1))");

        Assert.True(capabilities.Supports(DdcVcpCode.Brightness));
        Assert.Empty(capabilities.Features[DdcVcpCode.Brightness].SupportedValues);
        Assert.False(capabilities.Supports(DdcVcpCode.InputSource));
    }

    [Fact]
    public void Reads_a_code_with_an_enumerated_value_list()
    {
        DdcCapabilities capabilities = DdcCapabilityParser.Parse(
            "(prot(monitor)type(lcd)vcp(10 60(0F 11 12) AC AE)mccs_ver(2.2))");

        Assert.True(capabilities.Supports(DdcVcpCode.Brightness));
        Assert.True(capabilities.Supports(DdcVcpCode.InputSource));

        Assert.Equal(
            new uint[] { 0x0F, 0x11, 0x12 },
            capabilities.Features[DdcVcpCode.InputSource].SupportedValues);
    }

    [Fact]
    public void Drops_codes_this_build_has_no_control_for()
    {
        // 0x02, 0x04, 0x14, 0xAC, 0xAE and 0xB6 are ordinary MCCS features --
        // just not ones this build offers, so they must not appear at all
        // rather than under some generic "supported but unnamed" bucket.
        DdcCapabilities capabilities = DdcCapabilityParser.Parse(
            "(vcp(02 04 10 14 60(0F 11) AC AE B6)mccs_ver(2.1))");

        Assert.Equal(2, capabilities.Features.Count);
        Assert.True(capabilities.Supports(DdcVcpCode.Brightness));
        Assert.True(capabilities.Supports(DdcVcpCode.InputSource));
    }

    [Fact]
    public void A_string_with_no_vcp_block_supports_nothing()
    {
        DdcCapabilities capabilities = DdcCapabilityParser.Parse("(prot(monitor)type(lcd)mccs_ver(2.1))");

        Assert.Empty(capabilities.Features);
    }

    [Fact]
    public void An_empty_string_supports_nothing()
    {
        Assert.Empty(DdcCapabilityParser.Parse(string.Empty).Features);
    }

    [Fact]
    public void An_unbalanced_vcp_block_is_read_as_far_as_it_can_be_trusted()
    {
        // The value list for 60 is never closed. Nothing after that point can be
        // told apart from stray content inside the unbalanced group, so parsing
        // stops there -- 10, read before the damage, still counts.
        DdcCapabilities capabilities = DdcCapabilityParser.Parse("(vcp(10 60(0F 11)mccs_ver(2.1))");

        Assert.True(capabilities.Supports(DdcVcpCode.Brightness));
    }

    [Fact]
    public void Hex_digits_are_read_case_insensitively()
    {
        DdcCapabilities capabilities = DdcCapabilityParser.Parse("(vcp(10 60(0f 11 AC))mccs_ver(2.1))");

        Assert.Equal(
            new uint[] { 0x0F, 0x11, 0xAC },
            capabilities.Features[DdcVcpCode.InputSource].SupportedValues);
    }
}
