using System.Security.Cryptography;
using Microsoft.Extensions.Logging;
using Rasel.Application.Common;
using Rasel.Application.Ports;
using Rasel.Domain.Entities;
using Rasel.Domain.Enums;

namespace Rasel.Application.UseCases;

/// <summary>Key metadata. Never contains the key or its hash.</summary>
public sealed record ApiKeyDto(Guid Id, string Name, ApiKeyScope Scope, bool Active, DateTimeOffset CreatedAt, DateTimeOffset? LastUsedAt, DateTimeOffset? ExpiresAt);

/// <summary>Returned exactly once, when a key is created or rotated. <see cref="ApiKey"/> cannot be retrieved again.</summary>
public sealed record ApiKeyCreatedDto(ApiKeyDto Key, string ApiKey);

public sealed record RotatedApiKeyDto(ApiKeyDto Key, string ApiKey, ApiKeyDto Previous);

public sealed record CreateKeyCommand(string? Name, ApiKeyScope? Scope, int? DailyMessageLimit);

/// <summary>API-key lifecycle (admin only; the API layer enforces the policy). Every operation is audit-logged without secrets.</summary>
public sealed class KeyManagementHandler(IApiClientRepository clients, TimeProvider time, ILogger<KeyManagementHandler> logger)
{
    public const int DefaultDailyLimit = 1000;
    public const int MaxGraceSeconds = 7 * 24 * 3600;

    // SQLite has a single writer, and so does this process: serialising key changes makes the last-admin guard race-free.
    private static readonly SemaphoreSlim Gate = new(1, 1);

    /// <summary>192 bits from the CSPRNG, hex encoded, with a recognisable prefix.</summary>
    public static string GenerateKey() => "rsl_" + Convert.ToHexString(RandomNumberGenerator.GetBytes(24)).ToLowerInvariant();

    private ApiKeyDto ToDto(ApiClient c) => new(c.Id, c.Name, c.Scope, c.IsUsable(time.GetUtcNow()), c.CreatedAt, c.LastUsedAt, c.ExpiresAt);

    public async Task<IReadOnlyList<ApiKeyDto>> ListAsync(CancellationToken ct) => (await clients.ListAsync(ct)).Select(ToDto).ToList();

    public async Task<ApiKeyCreatedDto> CreateAsync(Guid actorId, CreateKeyCommand cmd, CancellationToken ct)
    {
        var name = cmd.Name?.Trim();
        if (string.IsNullOrEmpty(name) || name.Length > 100)
            throw new AppException(ErrorKind.BadRequest, "invalid_name", "name is required (1-100 characters).");
        if (cmd.Scope is not { } scope || !Enum.IsDefined(scope))
            throw new AppException(ErrorKind.BadRequest, "invalid_scope", "scope is required: Admin or Standard.");
        var limit = cmd.DailyMessageLimit ?? DefaultDailyLimit;
        if (limit is < 1 or > 1_000_000)
            throw new AppException(ErrorKind.BadRequest, "invalid_limit", "dailyMessageLimit must be between 1 and 1000000.");

        var key = GenerateKey();
        var client = ApiClient.Create(name, ApiKeyHasher.Hash(key), limit, time.GetUtcNow(), scope);
        await Gate.WaitAsync(ct);
        try { await clients.AddAsync(client, ct); }
        finally { Gate.Release(); }
        logger.LogWarning("AUDIT key.create actor={ActorId} target={KeyId} scope={Scope}", actorId, client.Id, scope);
        return new ApiKeyCreatedDto(ToDto(client), key);
    }

    public async Task<ApiKeyDto> RevokeAsync(Guid actorId, Guid id, CancellationToken ct)
    {
        await Gate.WaitAsync(ct);
        try
        {
            var client = await clients.GetAsync(id, ct) ?? throw NotFound();
            var now = time.GetUtcNow();
            if (client.Scope == ApiKeyScope.Admin && client.IsUsable(now) && await clients.CountUsableAdminsAsync(now, ct) <= 1)
                throw LastAdmin();
            client.Revoke();
            await clients.SaveAsync(client, ct);
            logger.LogWarning("AUDIT key.revoke actor={ActorId} target={KeyId}", actorId, id);
            return ToDto(client);
        }
        finally { Gate.Release(); }
    }

    /// <summary>Issues a replacement with the same name, scope and quota. The old key is revoked now, or after <paramref name="graceSeconds"/>.</summary>
    public async Task<RotatedApiKeyDto> RotateAsync(Guid actorId, Guid id, int graceSeconds, CancellationToken ct)
    {
        if (graceSeconds is < 0 or > MaxGraceSeconds)
            throw new AppException(ErrorKind.BadRequest, "invalid_grace", $"graceSeconds must be between 0 and {MaxGraceSeconds}.");
        await Gate.WaitAsync(ct);
        try
        {
            var old = await clients.GetAsync(id, ct) ?? throw NotFound();
            var now = time.GetUtcNow();
            if (!old.IsUsable(now) || old.ExpiresAt is not null)
                throw new AppException(ErrorKind.Conflict, "key_not_active", "Only an active key that is not already being rotated can be rotated.");

            var key = GenerateKey();
            var replacement = ApiClient.Create(old.Name, ApiKeyHasher.Hash(key), old.DailyMessageLimit, now, old.Scope);
            await clients.AddAsync(replacement, ct);
            if (graceSeconds == 0) old.Revoke(); else old.ExpireAt(now.AddSeconds(graceSeconds));
            await clients.SaveAsync(old, ct);
            logger.LogWarning("AUDIT key.rotate actor={ActorId} target={KeyId} replacement={NewKeyId} graceSeconds={Grace}", actorId, id, replacement.Id, graceSeconds);
            return new RotatedApiKeyDto(ToDto(replacement), key, ToDto(old));
        }
        finally { Gate.Release(); }
    }

    private static AppException NotFound() => new(ErrorKind.NotFound, "key_not_found", "Key not found.");

    private static AppException LastAdmin() =>
        new(ErrorKind.Conflict, "last_admin_key", "This is the last active Admin key; create or keep another Admin key first.");
}
