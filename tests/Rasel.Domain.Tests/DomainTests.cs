using Rasel.Domain.Entities;
using Rasel.Domain.Enums;
using Rasel.Domain.ValueObjects;

namespace Rasel.Domain.Tests;

public class PhoneNumberTests
{
    [Theory]
    [InlineData("+20 100 123-4567", "+201001234567")]
    [InlineData("0020 (100) 1234567", "+201001234567")]
    [InlineData(" +966501234567 ", "+966501234567")]
    public void Normalizes_to_e164(string input, string expected) =>
        Assert.Equal(expected, PhoneNumber.Parse(input).E164);

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("01001234567")]
    [InlineData("+0123456789")]
    [InlineData("+12345")]
    [InlineData("+1234567890123456")]
    [InlineData("+20abc1234567")]
    public void Rejects_invalid(string? input)
    {
        Assert.False(PhoneNumber.TryParse(input, out _));
        Assert.Throws<ArgumentException>(() => PhoneNumber.Parse(input));
    }

    [Fact]
    public void Masks_middle_digits_and_never_prints_full_number()
    {
        var phone = PhoneNumber.Parse("+201001234567");
        Assert.Equal("+20*******567", phone.Masked);
        Assert.DoesNotContain("1001234", phone.ToString());
    }
}

public class OtpChallengeTests
{
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    private static OtpChallenge NewChallenge(int maxAttempts = 5) =>
        OtpChallenge.Issue(Guid.NewGuid(), Guid.NewGuid(), "+201001234567", "login", "hash", Now,
            TimeSpan.FromMinutes(5), TimeSpan.FromSeconds(60), maxAttempts);

    [Fact]
    public void Correct_code_is_accepted_once()
    {
        var c = NewChallenge();
        Assert.Equal(OtpAttemptResult.Verified, c.Attempt(true, Now.AddSeconds(10)));
        Assert.Equal(OtpStatus.Consumed, c.Status);
        Assert.Equal(OtpAttemptResult.AlreadyUsed, c.Attempt(true, Now.AddSeconds(11)));
    }

    [Fact]
    public void Locks_after_max_failed_attempts_even_for_correct_code()
    {
        var c = NewChallenge(maxAttempts: 3);
        for (var i = 0; i < 3; i++) Assert.Equal(OtpAttemptResult.Invalid, c.Attempt(false, Now));
        Assert.Equal(OtpStatus.Locked, c.Status);
        Assert.Equal(OtpAttemptResult.Locked, c.Attempt(true, Now));
    }

    [Fact]
    public void Expires_after_ttl()
    {
        var c = NewChallenge();
        Assert.Equal(OtpAttemptResult.Expired, c.Attempt(true, Now.AddMinutes(5)));
        Assert.Equal(OtpStatus.Expired, c.Status);
    }

    [Fact]
    public void Superseded_challenge_cannot_be_verified()
    {
        var c = NewChallenge();
        c.Supersede();
        Assert.Equal(OtpAttemptResult.Superseded, c.Attempt(true, Now));
    }

    [Fact]
    public void Supersede_does_not_override_a_consumed_challenge()
    {
        var c = NewChallenge();
        c.Attempt(true, Now);
        c.Supersede();
        Assert.Equal(OtpStatus.Consumed, c.Status);
    }
    private static Message NewMessage() =>
        Message.Queue(Guid.NewGuid(), Guid.NewGuid(), TargetType.Phone, "+20*******567", "+201001234567", "secret text", null, null, Now);

    [Fact]
    public void Pending_message_keeps_body_and_destination_until_terminal()
    {
        var m = NewMessage();
        Assert.Equal("secret text", m.Body);
        Assert.Equal("+201001234567", m.Destination);

        m.MarkSent(Now);
        Assert.Equal(MessageStatus.Sent, m.Status);
        Assert.Null(m.Body);
        Assert.Null(m.Destination);
        Assert.Equal(Now, m.SentAt);
    }

    [Fact]
    public void Permanent_failure_ends_the_message_and_clears_content()
    {
        var m = NewMessage();
        m.RecordFailure("boom", transient: false, Now, maxAttempts: 5, TimeSpan.FromSeconds(5));
        Assert.Equal(MessageStatus.Failed, m.Status);
        Assert.Equal("boom", m.Error);
        Assert.Null(m.Body);
        Assert.Null(m.Destination);
    }

    [Fact]
    public void Transient_failure_reschedules_until_attempts_are_exhausted()
    {
        var m = NewMessage();
        m.Lease(Now, TimeSpan.FromMinutes(2));

        m.RecordFailure("timeout", transient: true, Now, maxAttempts: 3, TimeSpan.FromSeconds(10));
        Assert.Equal(MessageStatus.Queued, m.Status);
        Assert.Equal(Now.AddSeconds(10), m.NextAttemptAt);
        Assert.Null(m.LeasedUntil);
        Assert.Equal("secret text", m.Body);

        m.RecordFailure("timeout", transient: true, Now, maxAttempts: 3, TimeSpan.FromSeconds(20));
        Assert.Equal(MessageStatus.Queued, m.Status);

        m.RecordFailure("timeout", transient: true, Now, maxAttempts: 3, TimeSpan.FromSeconds(40));
        Assert.Equal(MessageStatus.Failed, m.Status);
        Assert.Equal(3, m.Attempts);
        Assert.Null(m.Body);
    }

    [Fact]
    public void Every_state_change_bumps_the_concurrency_version()
    {
        var m = NewMessage();
        var v = m.Version;
        m.Lease(Now, TimeSpan.FromSeconds(30));
        Assert.True(m.Version > v);

        var c = NewChallenge();
        var cv = c.Version;
        c.Attempt(false, Now);
        Assert.True(c.Version > cv);
    }

    [Fact]
    public void Terminal_challenge_attempts_do_not_change_version()
    {
        var c = NewChallenge();
        c.Attempt(true, Now);
        var v = c.Version;
        c.Attempt(true, Now);
        c.Supersede();
        Assert.Equal(v, c.Version);
    }
}
