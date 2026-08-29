using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace WSGM.DeviceLab.Capture;

/// <summary>
/// What a redaction replaced, so a reader of a shared capture knows something was removed rather
/// than never present.
/// </summary>
/// <param name="Category">The kind of value removed.</param>
/// <param name="Occurrences">How many were replaced.</param>
internal sealed record RedactionSummary(RedactionCategory Category, int Occurrences);

/// <summary>Kinds of identifying value removed from shareable output.</summary>
internal enum RedactionCategory
{
    /// <summary>User account or computer name.</summary>
    AccountName,

    /// <summary>Windows security identifier.</summary>
    SecurityIdentifier,

    /// <summary>A path inside a user profile.</summary>
    ProfilePath,

    /// <summary>A device serial number.</summary>
    SerialNumber,

    /// <summary>A device instance identifier unique to one machine.</summary>
    DeviceInstance,

    /// <summary>A hardware or network address.</summary>
    NetworkAddress,

    /// <summary>A process, API endpoint, or other identifier meaningful only within this session.</summary>
    SessionIdentifier,
}

/// <summary>
/// Removes machine-identifying values from output intended to be shared.
/// </summary>
/// <remarks>
/// A capture is meant to be sent to a developer, so it has to lose what identifies the sender without
/// losing what identifies the hardware. Those pull in opposite directions exactly where a value is
/// most useful: a device instance path contains both the model, which is the point, and the
/// enumeration path, which is not.
/// <para>
/// Redaction is therefore substitution rather than removal. A redacted value becomes a stable token,
/// so two events about the same device stay correlatable in the shared capture — a developer reading
/// it can still follow one device through a sequence.
/// </para>
/// </remarks>
internal sealed partial class CaptureRedactor
{
    private readonly Dictionary<string, string> _tokens = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<RedactionCategory, int> _counts = [];

    /// <summary>
    /// Redacts one text value.
    /// </summary>
    /// <param name="value">The value as observed.</param>
    /// <returns>The value with identifying parts replaced by stable tokens.</returns>
    public string Redact(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        string result = value;

        // Order matters: SIDs are replaced before account names, because a SID's textual form does
        // not contain a name but a resolved "DOMAIN\user" string does, and replacing the name first
        // would leave a half-redacted composite.
        result = ReplaceAll(result, Sid(), RedactionCategory.SecurityIdentifier, "SID");
        result = ReplaceAll(result, ProfilePath(), RedactionCategory.ProfilePath, "PROFILE");
        result = ReplaceAll(result, MacAddress(), RedactionCategory.NetworkAddress, "MAC");
        result = RedactDeviceInstance(result);
        result = RedactGenericPnpInstance(result);
        result = ReplaceAccountNames(result);

        return result;
    }

    /// <summary>Replaces one opaque session-only identifier with a stable shareable token.</summary>
    /// <param name="value">Private identifier.</param>
    /// <returns>Stable token for this redactor instance, or an empty string.</returns>
    public string TokenizeSessionIdentifier(string? value) => string.IsNullOrEmpty(value)
        ? string.Empty
        : TokenFor(value, RedactionCategory.SessionIdentifier, "SESSION");

    /// <summary>What was redacted, for the bundle's redaction manifest.</summary>
    /// <returns>One summary per category that had at least one occurrence.</returns>
    public IReadOnlyList<RedactionSummary> Summarize()
    {
        List<RedactionSummary> summaries = [];
        foreach ((RedactionCategory category, int count) in _counts)
        {
            summaries.Add(new RedactionSummary(category, count));
        }

        summaries.Sort((a, b) => a.Category.CompareTo(b.Category));
        return summaries;
    }

    /// <summary>
    /// Redacts a device instance path, keeping the model and dropping the instance.
    /// </summary>
    /// <remarks>
    /// Vendor and product identifiers come from the descriptor and are byte-identical on every unit
    /// of a model, so they describe the hardware rather than its owner. Everything after them — the
    /// enumeration path, and the serial where the device exposes one — describes this machine.
    /// </remarks>
    private string RedactDeviceInstance(string value)
    {
        return DeviceInstancePath().Replace(value, match =>
        {
            string prefix = match.Groups["prefix"].Value;
            string token = TokenFor(match.Value, RedactionCategory.DeviceInstance, "DEV");
            return $"{prefix}\\{token}";
        });
    }

    private string RedactGenericPnpInstance(string value)
    {
        return GenericPnpInstancePath().Replace(value, match =>
        {
            string prefix = match.Groups["prefix"].Value;
            string token = TokenFor(match.Value, RedactionCategory.DeviceInstance, "DEV");
            return $"{prefix}\\{token}";
        });
    }

    private string ReplaceAccountNames(string value)
    {
        string userName = Environment.UserName;
        string machineName = Environment.MachineName;

        if (userName.Length > 2 && value.Contains(userName, StringComparison.OrdinalIgnoreCase))
        {
            Count(RedactionCategory.AccountName);
            value = value.Replace(userName, "[USER]", StringComparison.OrdinalIgnoreCase);
        }

        if (machineName.Length > 2 && value.Contains(machineName, StringComparison.OrdinalIgnoreCase))
        {
            Count(RedactionCategory.AccountName);
            value = value.Replace(machineName, "[MACHINE]", StringComparison.OrdinalIgnoreCase);
        }

        return value;
    }

    private string ReplaceAll(string value, Regex pattern, RedactionCategory category, string prefix) =>
        pattern.Replace(value, match => TokenFor(match.Value, category, prefix));

    private string TokenFor(string original, RedactionCategory category, string prefix)
    {
        if (_tokens.TryGetValue(original, out string? existing))
        {
            return existing;
        }

        Count(category);
        string token = $"[{prefix}-{_tokens.Count:D3}]";
        _tokens[original] = token;
        return token;
    }

    private void Count(RedactionCategory category) =>
        _counts[category] = _counts.TryGetValue(category, out int existing) ? existing + 1 : 1;

    [GeneratedRegex(@"S-1-(?:\d+-){2,}\d+")]
    private static partial Regex Sid();

    [GeneratedRegex(@"[A-Za-z]:\\Users\\[^\\""<>|]+", RegexOptions.IgnoreCase)]
    private static partial Regex ProfilePath();

    [GeneratedRegex(@"\b(?:[0-9A-Fa-f]{2}[:-]){5}[0-9A-Fa-f]{2}\b")]
    private static partial Regex MacAddress();

    /// <summary>
    /// Matches the instance portion of a device path while keeping the VID/PID prefix.
    /// </summary>
    /// <remarks>
    /// Anchored on the vendor and product identifiers so the model survives. The trailing segment
    /// covers both forms this hardware produces: an <c>iSerialNumber</c> in one controller mode and a
    /// hub-and-port enumeration path in the other.
    /// <para>
    /// The descriptor suffix accepts <em>any</em> <c>&amp;TOKEN</c> run rather than an enumerated
    /// list. An earlier version allowed only <c>&amp;MI_xx</c> and silently failed to redact
    /// <c>VID_0DB0&amp;PID_1901&amp;IG_00\8&amp;1717EFAA&amp;0&amp;0000</c> — a real path on the
    /// reference unit — because the XInput designator did not fit the pattern. Every unrecognised
    /// suffix is a leak, so the shape is matched structurally instead of by enumeration.
    /// </para>
    /// </remarks>
    [GeneratedRegex(
        @"(?<prefix>VID_[0-9A-Fa-f]{4}&PID_[0-9A-Fa-f]{4}(?:&[A-Za-z0-9_]+)*)(?:\\|#)[^\\""#\s]+")]
    private static partial Regex DeviceInstancePath();

    [GeneratedRegex(
        @"(?<prefix>\b(?:PCI|ACPI|ROOT|BTHENUM|SWD|USBSTOR|USB|HID)\\[^\\""#\s]+)\\(?!\[DEV-)[^\\""#\s]+",
        RegexOptions.IgnoreCase)]
    private static partial Regex GenericPnpInstancePath();
}
