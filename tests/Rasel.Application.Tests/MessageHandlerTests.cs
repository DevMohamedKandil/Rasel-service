using Rasel.Application.Common;
using Rasel.Application.UseCases;
using Rasel.Domain.Enums;

namespace Rasel.Application.Tests;

public class MessageHandlerTests
{
    [Fact]
    public async Task Phone_message_is_queued_without_being_sent_inline()
    {
        var h = await Harness.CreateConnectedAsync();
        var result = await h.SendText().HandleAsync(h.Client, new SendTextCommand(null, "+201001234567", null, "hello"), default);

        Assert.Equal(MessageStatus.Queued, result.Status);
        Assert.Equal("hello", Assert.Single(h.Store.Queued).Text);
        Assert.Empty(h.WhatsApp.SentMessages);
    }

    [Fact]
    public async Task Sender_delivers_queued_message_and_marks_it_sent()
    {
        var h = await Harness.CreateConnectedAsync();
        await h.SendText().HandleAsync(h.Client, new SendTextCommand(null, "+201001234567", null, "hello"), default);
        await h.Sender().SendAsync(h.Store.Queued.Single(), default);

        Assert.Equal("hello", Assert.Single(h.WhatsApp.SentMessages).Text);
        Assert.Equal(MessageStatus.Sent, h.Store.Messages.Single().Status);
    }

    [Fact]
    public async Task Sender_records_failure()
    {
        var h = await Harness.CreateConnectedAsync();
        await h.SendText().HandleAsync(h.Client, new SendTextCommand(null, "+201001234567", null, "hello"), default);
        h.WhatsApp.FailSends = true;
        await h.Sender().SendAsync(h.Store.Queued.Single(), default);

        var message = h.Store.Messages.Single();
        Assert.Equal(MessageStatus.Failed, message.Status);
        Assert.NotNull(message.Error);
    }

    [Fact]
    public async Task Group_is_resolved_by_exact_name()
    {
        var h = await Harness.CreateConnectedAsync();
        h.WhatsApp.AddGroup("Ops");
        await h.SendText().HandleAsync(h.Client, new SendTextCommand(null, null, "Ops", "hi"), default);
        Assert.Equal(TargetType.Group, h.Store.Queued.Single().TargetType);
    }

    [Theory]
    [InlineData("Ops", 0, ErrorKind.NotFound)]
    [InlineData("ops", 1, ErrorKind.NotFound)]
    [InlineData("Ops", 2, ErrorKind.Conflict)]
    public async Task Group_with_no_or_multiple_exact_matches_is_rejected(string name, int copiesOfOps, ErrorKind expected)
    {
        var h = await Harness.CreateConnectedAsync();
        for (var i = 0; i < copiesOfOps; i++) h.WhatsApp.AddGroup("Ops");

        var ex = await Assert.ThrowsAsync<AppException>(() => h.SendText().HandleAsync(h.Client, new SendTextCommand(null, null, name, "hi"), default));
        Assert.Equal(expected, ex.Kind);
        Assert.Empty(h.Store.Queued);
    }

    [Theory]
    [InlineData("+201001234567", "Ops")]
    [InlineData(null, null)]
    public async Task Exactly_one_target_is_required(string? phone, string? group)
    {
        var h = await Harness.CreateConnectedAsync();
        var ex = await Assert.ThrowsAsync<AppException>(() => h.SendText().HandleAsync(h.Client, new SendTextCommand(null, phone, group, "hi"), default));
        Assert.Equal("invalid_target", ex.Code);
    }

    [Fact]
    public async Task Daily_limit_is_enforced()
    {
        var h = await Harness.CreateConnectedAsync(dailyLimit: 2);
        for (var i = 0; i < 2; i++) await h.SendText().HandleAsync(h.Client, new SendTextCommand(null, "+201001234567", null, "x"), default);

        var ex = await Assert.ThrowsAsync<AppException>(() => h.SendText().HandleAsync(h.Client, new SendTextCommand(null, "+201001234567", null, "over"), default));
        Assert.Equal(ErrorKind.TooManyRequests, ex.Kind);
    }

    [Fact]
    public async Task Sending_requires_a_connected_session()
    {
        var h = new Harness();
        var ex = await Assert.ThrowsAsync<AppException>(() => h.SendText().HandleAsync(h.Client, new SendTextCommand(null, "+201001234567", null, "hi"), default));
        Assert.Equal("session_not_found", ex.Code);
    }

    [Fact]
    public async Task Session_lifecycle_needs_qr_then_connected_then_stopped()
    {
        var h = new Harness();
        var dto = await h.CreateSession().HandleAsync(h.Client.Id, "main", default);
        Assert.Equal(SessionStatus.NeedsQr, dto.Status);

        var qr = await new GetSessionQrHandler(h.Store, h.WhatsApp, h.Clock).HandleAsync(h.Client.Id, dto.Id, default);
        Assert.StartsWith("fake-qr:", qr.Qr);
        Assert.Equal(SessionStatus.Connected, qr.Status);

        await new StopSessionHandler(h.Store, h.WhatsApp, h.Clock).HandleAsync(h.Client.Id, dto.Id, default);
        var status = await new GetSessionStatusHandler(h.Store, h.WhatsApp, h.Clock).HandleAsync(h.Client.Id, dto.Id, default);
        Assert.Equal(SessionStatus.Stopped, status.Status);
    }
}
