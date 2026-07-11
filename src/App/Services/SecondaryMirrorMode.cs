using System.Text;
using IPhoneMirror.App.Models;

namespace IPhoneMirror.App.Services;

internal sealed record SecondaryMirrorRequest(string Udid, string Name, string ProductType);

internal static class SecondaryMirrorMode
{
    private const string Switch = "--secondary-mirror";

    internal static bool TryParse(string[] args, out SecondaryMirrorRequest request)
    {
        request = new(string.Empty, string.Empty, string.Empty);
        var index = Array.FindIndex(args, value => value.Equals(Switch, StringComparison.OrdinalIgnoreCase));
        if (index < 0 || index + 1 >= args.Length) return false;
        try
        {
            var fields = Encoding.UTF8.GetString(Convert.FromBase64String(args[index + 1])).Split('\0');
            if (fields.Length != 3 || string.IsNullOrWhiteSpace(fields[0])) return false;
            request = new(fields[0], fields[1], fields[2]);
            return true;
        }
        catch (FormatException) { return false; }
    }

    internal static string Encode(DeviceViewModel device) => Convert.ToBase64String(
        Encoding.UTF8.GetBytes(string.Join('\0', device.Udid, device.DisplayName, device.ProductType)));
}
