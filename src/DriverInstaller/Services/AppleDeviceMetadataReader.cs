using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace IPhoneMirror.DriverInstaller.Services;

internal sealed record AppleDeviceMetadata(
    string DeviceName,
    string ProductType,
    string OsVersion);

internal static class AppleDeviceMetadataReader
{
    private const int MaximumPacketSize = 8 * 1024 * 1024;

    internal static IReadOnlyDictionary<string, AppleDeviceMetadata> TryReadAll()
    {
        var result = new Dictionary<string, AppleDeviceMetadata>(
            StringComparer.OrdinalIgnoreCase);
        foreach (var port in new[] { 27015, 27016 })
        {
            try
            {
                using var mux = Connect(port);
                var list = SendMuxRequest(mux, "<key>MessageType</key><string>ListDevices</string>");
                foreach (var entry in GetArrayDictionaries(list, "DeviceList"))
                {
                    var properties = GetDictionary(entry, "Properties");
                    var serial = GetString(properties, "SerialNumber");
                    var deviceId = GetInteger(entry, "DeviceID");
                    if (string.IsNullOrWhiteSpace(serial) || deviceId <= 0) continue;
                    try
                    {
                        using var connection = Connect(port);
                        var connectExtra =
                            $"<key>DeviceID</key><integer>{deviceId}</integer>" +
                            $"<key>PortNumber</key><integer>{ToNetworkOrder(62078)}</integer>";
                        var connected = SendMuxRequest(connection,
                            "<key>MessageType</key><string>Connect</string>" + connectExtra);
                        if (GetInteger(GetRootDictionary(connected), "Number") != 0) continue;
                        var values = SendLockdownGetValue(connection);
                        result[DriverConstants.NormalizeSerial(serial)] = new AppleDeviceMetadata(
                            GetString(values, "DeviceName"),
                            GetString(values, "ProductType"),
                            GetString(values, "ProductVersion"));
                    }
                    catch (Exception error)
                    {
                        DriverLogger.Write($"Lockdown metadata unavailable for {serial}: {error.Message}");
                    }
                }
            }
            catch (Exception error)
            {
                DriverLogger.Write($"usbmux metadata port {port} unavailable: {error.Message}");
            }
        }
        return result;
    }

    private static TcpClient Connect(int port)
    {
        var client = new TcpClient { ReceiveTimeout = 1500, SendTimeout = 1500 };
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(750));
        client.ConnectAsync(IPAddress.Loopback, port, cancellation.Token)
            .GetAwaiter().GetResult();
        return client;
    }

    private static XDocument SendMuxRequest(TcpClient client, string body)
    {
        var xml = BuildPlist(
            "<key>BundleID</key><string>com.openai.iphonemirror.driver</string>" +
            "<key>ClientVersionString</key><string>iPhoneMirror.Driver 1.0</string>" +
            body +
            "<key>ProgName</key><string>iPhoneMirror.Driver</string>" +
            "<key>kLibUSBMuxVersion</key><integer>3</integer>");
        var payload = Encoding.UTF8.GetBytes(xml);
        var header = new byte[16];
        BinaryPrimitives.WriteUInt32LittleEndian(header, checked((uint)(16 + payload.Length)));
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(4), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(8), 8);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(12), 1);
        var stream = client.GetStream();
        stream.Write(header);
        stream.Write(payload);
        ReadExactly(stream, header);
        var length = BinaryPrimitives.ReadUInt32LittleEndian(header);
        if (length is < 16 or > MaximumPacketSize)
            throw new InvalidDataException("Invalid usbmux packet length.");
        if (BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(8)) != 8)
            throw new InvalidDataException("Unexpected usbmux message type.");
        var response = new byte[length - 16];
        ReadExactly(stream, response);
        return ParsePlist(response);
    }

    private static XElement SendLockdownGetValue(TcpClient client)
    {
        var payload = Encoding.UTF8.GetBytes(BuildPlist(
            "<key>Label</key><string>iPhoneMirror.Driver</string>" +
            "<key>Request</key><string>GetValue</string>"));
        var stream = client.GetStream();
        var header = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(header, checked((uint)payload.Length));
        stream.Write(header);
        stream.Write(payload);
        ReadExactly(stream, header);
        var length = BinaryPrimitives.ReadUInt32BigEndian(header);
        if (length is 0 or > MaximumPacketSize)
            throw new InvalidDataException("Invalid lockdownd packet length.");
        var response = new byte[length];
        ReadExactly(stream, response);
        var document = ParsePlist(response);
        return GetDictionary(document, "Value");
    }

    private static string BuildPlist(string body) =>
        "<?xml version=\"1.0\" encoding=\"UTF-8\"?>" +
        "<plist version=\"1.0\"><dict>" + body + "</dict></plist>";

    private static XDocument ParsePlist(byte[] bytes)
    {
        using var stream = new MemoryStream(bytes, writable: false);
        using var reader = XmlReader.Create(stream, new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Ignore,
            XmlResolver = null,
            MaxCharactersInDocument = MaximumPacketSize,
            IgnoreComments = true,
            IgnoreWhitespace = true,
        });
        return XDocument.Load(reader, LoadOptions.None);
    }

    private static IEnumerable<XElement> GetArrayDictionaries(XDocument document, string key)
    {
        var array = FindValue(document.Root?.Element("dict"), key, "array");
        return array?.Elements("dict") ?? [];
    }

    private static XElement GetDictionary(XElement dictionary, string key) =>
        FindValue(dictionary, key, "dict") ?? new XElement("dict");

    private static XElement GetDictionary(XDocument document, string key) =>
        GetDictionary(document.Root?.Element("dict") ?? new XElement("dict"), key);

    private static XElement GetRootDictionary(XDocument document) =>
        document.Root?.Element("dict") ?? new XElement("dict");

    private static string GetString(XElement dictionary, string key) =>
        FindValue(dictionary, key, "string")?.Value ?? string.Empty;

    private static long GetInteger(XElement dictionary, string key) =>
        long.TryParse(FindValue(dictionary, key, "integer")?.Value, out var value)
            ? value
            : 0;

    private static XElement? FindValue(XElement? dictionary, string key, string type)
    {
        if (dictionary is null) return null;
        var elements = dictionary.Elements().ToArray();
        for (var index = 0; index + 1 < elements.Length; index++)
        {
            if (elements[index].Name.LocalName != "key" || elements[index].Value != key)
                continue;
            return elements[index + 1].Name.LocalName == type ? elements[index + 1] : null;
        }
        return null;
    }

    private static void ReadExactly(Stream stream, Span<byte> buffer)
    {
        var offset = 0;
        while (offset < buffer.Length)
        {
            var read = stream.Read(buffer[offset..]);
            if (read == 0) throw new EndOfStreamException("Apple service closed the connection.");
            offset += read;
        }
    }

    private static ushort ToNetworkOrder(ushort value) =>
        BinaryPrimitives.ReverseEndianness(value);
}
