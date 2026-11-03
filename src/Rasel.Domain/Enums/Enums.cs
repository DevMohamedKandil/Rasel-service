namespace Rasel.Domain.Enums;

public enum SessionStatus { Starting, NeedsQr, Connected, Failed, Stopped }

public enum MessageStatus { Queued, Sent, Failed }

public enum TargetType { Phone, Group }

public enum OtpStatus { Active, Consumed, Locked, Superseded, Expired }

public enum OtpAttemptResult { Verified, Invalid, Locked, Expired, AlreadyUsed, Superseded }

/// <summary>Standard keys send messages and OTPs; Admin keys additionally control the engine, delete sessions and manage keys. Admin implies Standard.</summary>
public enum ApiKeyScope { Standard = 0, Admin = 1 }
