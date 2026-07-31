using System.Globalization;
using System.Xml;
using System.Xml.Linq;

namespace IPhoneMirror.DriverInstaller.Services;

internal sealed record AppleSoftwareUpdatePackage(
    string ProductId,
    Uri DownloadUri,
    long Size,
    DateTimeOffset PostDate);

internal static class AppleSoftwareUpdateCatalog
{
    internal const string CatalogUrl =
        "https://swscan.apple.com/content/catalogs/others/index-windows-1.sucatalog";
    internal const int MaximumCatalogBytes = 2 * 1024 * 1024;
    internal const long MaximumPackageBytes = 128L * 1024 * 1024;

    internal static AppleSoftwareUpdatePackage ParseLatestMobileDeviceSupport64(
        string content)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(content);
        if (content.Length > MaximumCatalogBytes)
            throw new InvalidDataException("The Apple update catalog is unexpectedly large.");

        using var text = new StringReader(content);
        using var reader = XmlReader.Create(text, new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Ignore,
            XmlResolver = null,
            MaxCharactersInDocument = MaximumCatalogBytes,
        });
        var document = XDocument.Load(reader, LoadOptions.None);
        var rootDictionary = document.Root?.Elements()
            .FirstOrDefault(element => element.Name.LocalName == "dict")
            ?? throw new InvalidDataException("The Apple update catalog has no root dictionary.");
        var products = GetDictionaryValue(rootDictionary, "Products");
        if (products?.Name.LocalName != "dict")
            throw new InvalidDataException("The Apple update catalog has no product list.");

        var candidates = new List<AppleSoftwareUpdatePackage>();
        foreach (var (productId, productValue) in EnumerateDictionary(products))
        {
            if (productValue.Name.LocalName != "dict") continue;
            var postDateValue = GetDictionaryValue(productValue, "PostDate")?.Value;
            if (!DateTimeOffset.TryParse(postDateValue, CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal, out var postDate))
                continue;
            var packages = GetDictionaryValue(productValue, "Packages");
            if (packages?.Name.LocalName != "array") continue;

            foreach (var package in packages.Elements()
                         .Where(element => element.Name.LocalName == "dict"))
            {
                var urlValue = GetDictionaryValue(package, "URL")?.Value;
                if (!Uri.TryCreate(urlValue, UriKind.Absolute, out var uri) ||
                    !uri.Host.Equals("swcdn.apple.com",
                        StringComparison.OrdinalIgnoreCase) ||
                    !uri.AbsolutePath.EndsWith("/AppleMobileDeviceSupport64.msi",
                        StringComparison.OrdinalIgnoreCase))
                    continue;
                if (!uri.Scheme.Equals(Uri.UriSchemeHttp,
                        StringComparison.OrdinalIgnoreCase) &&
                    !uri.Scheme.Equals(Uri.UriSchemeHttps,
                        StringComparison.OrdinalIgnoreCase))
                    continue;
                var sizeValue = GetDictionaryValue(package, "Size")?.Value;
                if (!long.TryParse(sizeValue, NumberStyles.None,
                        CultureInfo.InvariantCulture, out var size) ||
                    size <= 0 || size > MaximumPackageBytes)
                    continue;

                var secureUri = new UriBuilder(uri) { Scheme = Uri.UriSchemeHttps,
                    Port = -1 }.Uri;
                candidates.Add(new AppleSoftwareUpdatePackage(productId,
                    secureUri, size, postDate.ToUniversalTime()));
            }
        }

        return candidates
            .OrderByDescending(candidate => candidate.PostDate)
            .ThenByDescending(candidate => candidate.ProductId,
                StringComparer.Ordinal)
            .FirstOrDefault()
            ?? throw new InvalidDataException(
                "The Apple update catalog has no 64-bit mobile device support package.");
    }

    private static XElement? GetDictionaryValue(XElement dictionary, string key) =>
        EnumerateDictionary(dictionary).FirstOrDefault(entry =>
            string.Equals(entry.Key, key, StringComparison.Ordinal)).Value;

    private static IEnumerable<(string Key, XElement Value)> EnumerateDictionary(
        XElement dictionary)
    {
        var elements = dictionary.Elements().ToArray();
        for (var index = 0; index + 1 < elements.Length; index += 2)
        {
            if (elements[index].Name.LocalName != "key") continue;
            yield return (elements[index].Value, elements[index + 1]);
        }
    }
}
