namespace Rasel.Domain.Enums;

public enum SessionStatus { Starting, NeedsQr, Connected, Failed, Stopped }

public enum MessageStatus { Queued, Sent, Failed }

public enum TargetType { Phone, Group }

public enum OtpStatus { Active, Consumed, Locked, Superseded, Expired }

public enum OtpAttemptResult { Verified, Invalid, Locked, Expired, AlreadyUsed, Superseded }
