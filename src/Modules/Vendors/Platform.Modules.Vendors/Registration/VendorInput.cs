using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Platform.Modules.Vendors.Contracts;
using Platform.Shared.Results;
using Platform.Shared.Text;

namespace Platform.Modules.Vendors.Registration;

/// <summary>A registration after <see cref="VendorInput.Normalize"/>: trimmed, ASCII digits, lower-cased email.</summary>
internal sealed record NormalizedRegistration(
    string CrNumber,
    string NameAr,
    string NameEn,
    string VatNumber,
    string? Address,
    string ContactName,
    string? ContactPhone,
    string ContactEmail,
    string PrivacyNoticeVersion,
    string PrivacyNoticeCulture);

/// <summary>
/// The rules for a vendor company's registration (F-11, vendor plan task 2). A CR number is exactly 10 digits and a VAT
/// number 15 digits starting and ending with 3, as the database checks them too; Arabic-Indic and Extended Arabic-Indic
/// digits count as their ASCII digits, since an Arabic keyboard types those. Company names follow the staff display-name
/// rule (letters of any script, marks, spaces, apostrophes, hyphens, periods) widened by the digits and the
/// <c>&amp; , ( ) /</c> that registered company names carry, 1 to 200 characters. The contact person's name follows the
/// display-name rule itself (up to 200). Every free-text value refuses the invisible and bidi-control characters of
/// <see cref="TextSafety"/> and control characters; the contact email follows <see cref="EmailAddresses"/>. The privacy
/// notice must be the current version, shown in one of <see cref="VendorPrivacyNotice.Cultures"/> (exactly as named).
/// </summary>
internal static partial class VendorInput
{
    public const int MaxNameLength = 200;
    public const int MaxAddressLength = 500;

    public static IReadOnlyList<Error> Validate(VendorRegistration registration)
    {
        ArgumentNullException.ThrowIfNull(registration);
        var errors = new List<Error>();
        if (!CrNumber().IsMatch(Digits(registration.CrNumber)))
        {
            errors.Add(Error.Validation(VendorErrors.InvalidCrNumber, "Enter the 10-digit commercial registration (CR) number, using digits only."));
        }

        if (!IsCompanyName(Trim(registration.NameAr)))
        {
            errors.Add(Error.Validation(VendorErrors.InvalidNameAr, NameMessage("Arabic")));
        }

        if (!IsCompanyName(Trim(registration.NameEn)))
        {
            errors.Add(Error.Validation(VendorErrors.InvalidNameEn, NameMessage("English")));
        }

        if (!VatNumber().IsMatch(Digits(registration.VatNumber)))
        {
            errors.Add(Error.Validation(VendorErrors.InvalidVatNumber, "Enter the 15-digit VAT number; it starts and ends with 3."));
        }

        if (Trim(registration.Address) is { Length: > 0 } address && !IsAddress(address))
        {
            errors.Add(Error.Validation(VendorErrors.InvalidAddress, $"Enter the address in up to {MaxAddressLength} characters, on one line."));
        }

        if (!IsPersonName(Trim(registration.ContactName)))
        {
            errors.Add(Error.Validation(
                VendorErrors.InvalidContactName,
                $"Enter the contact person's full name, up to {MaxNameLength} characters, using letters, spaces, apostrophes, hyphens and periods."));
        }

        if (Trim(registration.ContactPhone) is { Length: > 0 } && !Phone().IsMatch(Digits(registration.ContactPhone)))
        {
            errors.Add(Error.Validation(VendorErrors.InvalidContactPhone, "Enter a phone number of 7 to 15 digits; + at the start, spaces and hyphens are allowed."));
        }

        if (EmailAddresses.Normalize(registration.ContactEmail) is null)
        {
            errors.Add(Error.Validation(VendorErrors.InvalidContactEmail, "Enter the contact's work email address, for example name@company.com."));
        }

        if (!string.Equals(registration.AcceptedPrivacyNotice, VendorPrivacyNotice.CurrentVersion, StringComparison.Ordinal)
            || (registration.PrivacyNoticeCulture is { } culture && !VendorPrivacyNotice.Cultures.Contains(culture, StringComparer.Ordinal)))
        {
            errors.Add(Error.Validation(VendorErrors.PrivacyNoticeRequired, "Read and accept the privacy notice to register."));
        }

        return errors;
    }

    /// <summary>The stored form of a registration that passed <see cref="Validate"/>.</summary>
    public static NormalizedRegistration Normalize(VendorRegistration registration)
    {
        ArgumentNullException.ThrowIfNull(registration);
        return new NormalizedRegistration(
            Digits(registration.CrNumber),
            Trim(registration.NameAr),
            Trim(registration.NameEn),
            Digits(registration.VatNumber),
            Trim(registration.Address) is { Length: > 0 } address ? address : null,
            Trim(registration.ContactName),
            Trim(registration.ContactPhone) is { Length: > 0 } ? Digits(registration.ContactPhone) : null,
            EmailAddresses.Normalize(registration.ContactEmail)!,
            registration.AcceptedPrivacyNotice!,
            registration.PrivacyNoticeCulture ?? CurrentCulture());
    }

    /// <summary>The published culture of the request's UI culture: Arabic ones show the Arabic text, all others the English.</summary>
    private static string CurrentCulture() =>
        CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "ar" ? VendorPrivacyNotice.Arabic : VendorPrivacyNotice.English;

    private static string NameMessage(string language) =>
        $"Enter the company's {language} name as registered, up to {MaxNameLength} characters, using letters, digits, spaces and . , ' - & ( ) /.";

    private static string Trim(string? value) => value?.Trim() ?? string.Empty;

    /// <summary>Trimmed, with Arabic-Indic (U+0660-0669) and Extended Arabic-Indic (U+06F0-06F9) digits as ASCII digits.</summary>
    internal static string Digits(string? value)
    {
        var trimmed = Trim(value);
        var builder = new StringBuilder(trimmed.Length);
        foreach (var c in trimmed)
        {
            builder.Append(c switch
            {
                >= '٠' and <= '٩' => (char)('0' + (c - '٠')),
                >= '۰' and <= '۹' => (char)('0' + (c - '۰')),
                _ => c,
            });
        }

        return builder.ToString();
    }

    private static bool IsCompanyName(string name) =>
        IsName(name, rune => rune.Value is '&' or ',' or '(' or ')' or '/' || Rune.GetUnicodeCategory(rune) is UnicodeCategory.DecimalDigitNumber);

    private static bool IsPersonName(string name) => IsName(name, _ => false);

    // The display-name rule of F-06 (DisplayNames in the Identity module), plus what `extra` allows.
    private static bool IsName(string name, Func<Rune, bool> extra)
    {
        if (name.Length == 0 || name.Length > MaxNameLength || TextSafety.HasInvisibleOrBidiControl(name))
        {
            return false;
        }

        foreach (var rune in name.EnumerateRunes())
        {
            var allowed = rune.Value is ' ' or '\'' or '-' or '.'
                || Rune.GetUnicodeCategory(rune) is UnicodeCategory.UppercaseLetter or UnicodeCategory.LowercaseLetter
                    or UnicodeCategory.TitlecaseLetter or UnicodeCategory.ModifierLetter or UnicodeCategory.OtherLetter
                    or UnicodeCategory.NonSpacingMark or UnicodeCategory.SpacingCombiningMark or UnicodeCategory.EnclosingMark
                || extra(rune);
            if (!allowed)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>A CR number as the registration accepts it, after <see cref="Digits"/>: ten ASCII digits.</summary>
    internal static bool IsCrNumber(string value) => CrNumber().IsMatch(value);

    /// <summary>
    /// A multi-line free text (ownership notes and dispute statements, W-33) as it is stored: trimmed, with every CRLF and
    /// lone CR turned into LF, so its length is the stored length.
    /// </summary>
    internal static string NormalizeFreeText(string? value) =>
        Trim(value).Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');

    /// <summary>
    /// A free text of 1 to <paramref name="maxLength"/> characters after <see cref="NormalizeFreeText"/>, under the address
    /// rules except that line breaks (LF) are allowed: no invisible or bidi controls, no other control, separator or
    /// unassigned characters, no angle brackets.
    /// </summary>
    internal static bool IsFreeText(string? value, int maxLength) =>
        NormalizeFreeText(value) is { Length: > 0 } text && text.Length <= maxLength && IsSafeText(text.Replace('\n', ' '));

    private static bool IsAddress(string address) =>
        address.Length <= MaxAddressLength && IsSafeText(address);

    private static bool IsSafeText(string address) =>
        !TextSafety.HasInvisibleOrBidiControl(address)
        && !address.EnumerateRunes().Any(r => Rune.GetUnicodeCategory(r) is UnicodeCategory.Control or UnicodeCategory.LineSeparator
            or UnicodeCategory.ParagraphSeparator or UnicodeCategory.PrivateUse or UnicodeCategory.Surrogate or UnicodeCategory.OtherNotAssigned)
        && !address.Contains('<', StringComparison.Ordinal) && !address.Contains('>', StringComparison.Ordinal);

    [GeneratedRegex("^[0-9]{10}$", RegexOptions.CultureInvariant)]
    private static partial Regex CrNumber();

    [GeneratedRegex("^3[0-9]{13}3$", RegexOptions.CultureInvariant)]
    private static partial Regex VatNumber();

    // 7 to 15 digits (E.164 allows at most 15), with an optional leading + and spaces or hyphens between digits.
    [GeneratedRegex(@"^\+?[0-9](?:[ -]?[0-9]){6,14}$", RegexOptions.CultureInvariant)]
    private static partial Regex Phone();
}
