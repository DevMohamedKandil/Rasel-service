using Rasel.Application.Common;
using Rasel.Application.UseCases;

namespace Rasel.Application.Tests;

public class LinkedNumberTests
{
    [Theory]
    [InlineData("201000000880", "+20100****880")]
    [InlineData("+20 100 000 0880", "+20100****880")]
    [InlineData("966501234567", "+96650****567")]
    [InlineData("12345678", "+******78")]
    public void Mask_hides_the_middle(string input, string expected)
    {
        var masked = PhoneMask.Mask(input);
        Assert.Equal(expected.Length, masked!.Length);
        Assert.Equal(expected, masked);
    }

    [Fact]
    public void Mask_handles_empty() { Assert.Null(PhoneMask.Mask(null)); Assert.Null(PhoneMask.Mask("  ")); Assert.Null(PhoneMask.Full("abc")); }

    [Fact]
    public async Task Status_and_qr_expose_only_the_masked_number_and_the_full_one_is_client_scoped()
    {
        var h = await Harness.CreateConnectedAsync();
        var status = await new GetSessionStatusHandler(h.Store, h.WhatsApp, h.Clock).HandleAsync(h.Client.Id, h.Session.Id, default);
        Assert.NotNull(status.LinkedPhoneMasked);
        Assert.Contains("*", status.LinkedPhoneMasked);
        Assert.DoesNotContain("201000000000", System.Text.Json.JsonSerializer.Serialize(status));

        var full = await new GetLinkedNumberHandler(h.Store, h.WhatsApp).HandleAsync(h.Client.Id, h.Session.Id, default);
        Assert.Equal("+201000000000", full.Phone);

        var other = await Assert.ThrowsAsync<AppException>(() => new GetLinkedNumberHandler(h.Store, h.WhatsApp).HandleAsync(Guid.NewGuid(), h.Session.Id, default));
        Assert.Equal(ErrorKind.NotFound, other.Kind);
    }
}
