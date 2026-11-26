namespace IPhoneMirror.App.Services;

internal static class AppleDeviceIdentity
{
    internal static string Key(string value)
    {
        // Modern Apple UDIDs can omit the separator after the first eight hex digits.
        if (value.Length == 25 && value[8] == '-')
        {
            var compact = value.Remove(8, 1);
            if (IsCompactModernUdid(compact)) return compact;
        }
        return value;
    }

    internal static bool Matches(string? left, string? right) =>
        !string.IsNullOrWhiteSpace(left) && !string.IsNullOrWhiteSpace(right) &&
        string.Equals(Key(left), Key(right), StringComparison.OrdinalIgnoreCase);

    internal static string Format(string udid) => IsCompactModernUdid(udid) ? udid.Insert(8, "-") : udid;

    private static bool IsCompactModernUdid(string udid) => udid.Length == 24 && udid.All(char.IsAsciiHexDigit);
}
