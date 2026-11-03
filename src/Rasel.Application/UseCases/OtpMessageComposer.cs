using Rasel.Application.Common;

namespace Rasel.Application.UseCases;

/// <summary>Builds the OTP text from a randomly chosen template, so consecutive messages are not identical.</summary>
public sealed class OtpMessageComposer(OtpOptions options, IRandomSource random)
{
    public static readonly string[] DefaultEn =
    [
        "{app}: your verification code is {code}. It expires in {minutes} minutes. Do not share it.",
        "Your {app} code is {code}. Valid for {minutes} minutes. Keep it private.",
        "{code} is your {app} verification code. It will expire in {minutes} minutes.",
        "Use {code} to verify your {app} account. The code is valid for {minutes} minutes; never share it.",
        "{app} security code: {code}. Expires in {minutes} minutes. If you did not ask for it, ignore this message.",
        "Hello, {code} is the code for {app}. It works for {minutes} minutes only. Do not give it to anyone.",
    ];

    public static readonly string[] DefaultAr =
    [
        "{app}: رمز التحقق الخاص بك هو {code}. صالح لمدة {minutes} دقائق. لا تشاركه مع أحد.",
        "رمز {app} الخاص بك: {code}. ينتهي خلال {minutes} دقائق، يرجى إبقاؤه سريًا.",
        "{code} هو رمز التحقق الخاص بك في {app}. تنتهي صلاحيته بعد {minutes} دقائق.",
        "استخدم الرمز {code} لتأكيد حسابك في {app}. الرمز صالح لمدة {minutes} دقائق ولا يجوز مشاركته.",
        "رمز الأمان لـ {app}: {code}. صالح {minutes} دقائق. إذا لم تطلبه فتجاهل هذه الرسالة.",
        "مرحبًا، رمزك في {app} هو {code}. يعمل لمدة {minutes} دقائق فقط. لا تعطه لأي شخص.",
    ];

    public static bool IsArabic(string? language) =>
        string.Equals(language?.Trim(), "ar", StringComparison.OrdinalIgnoreCase);

    public string Compose(string code, string? language)
    {
        var pool = (IsArabic(language) ? options.TemplatesAr : options.TemplatesEn) is { Length: > 0 } custom
            ? custom : (IsArabic(language) ? DefaultAr : DefaultEn);
        var template = pool[random.Next(0, pool.Length)];
        return template.Replace("{app}", options.AppName).Replace("{code}", code)
            .Replace("{minutes}", Math.Max(1, options.TtlSeconds / 60).ToString());
    }
}
