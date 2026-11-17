namespace IPhoneMirror.App.Services;

internal static class AppleProductNames
{
    // Share the complete offline catalog used for display-size negotiation.
    // The previous separate list stopped at iPhone 17 and omitted every iPad.
    private static readonly IReadOnlyDictionary<string, string> KnownProducts =
        AppleDisplayCatalog.Products.SelectMany(product => product.Identifiers.Select(id =>
            new KeyValuePair<string, string>(id, product.Name.Replace("Xʀ", "XR", StringComparison.Ordinal))))
            .ToDictionary(entry => entry.Key, entry => entry.Value, StringComparer.OrdinalIgnoreCase);

    internal static string Resolve(string? productType)
    {
        if (string.IsNullOrWhiteSpace(productType)) return string.Empty;
        return KnownProducts.TryGetValue(productType.Trim(), out var name) ? name : productType;
    }
}
