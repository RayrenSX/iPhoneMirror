using System.Text.Json;
using System.Windows.Controls;

namespace IPhoneMirror.App.Controls;

internal sealed partial class DeviceHubPanel
{
    private string? _documentScopesDevice;
    private bool _loadingDocumentScopes;
    private string? SelectedDocumentBundle => Option(FileRoot).StartsWith("documents:", StringComparison.Ordinal)
        ? Option(FileRoot)[10..] : null;

    private void ResetDocumentScopes()
    {
        _documentScopesDevice = null;
        _loadingDocumentScopes = true;
        try
        {
            FileRoot.SelectedIndex = 0;
            while (FileRoot.Items.Count > 2) FileRoot.Items.RemoveAt(FileRoot.Items.Count - 1);
        }
        finally { _loadingDocumentScopes = false; }
    }

    private object? DocumentArguments(string action, object? arguments)
    {
        if (SelectedDocumentBundle is not { } bundle || action is not ("list_dir" or "read_file" or "upload_file" or "delete_file")) return arguments;
        var scoped = JsonSerializer.SerializeToElement(arguments).EnumerateObject()
            .ToDictionary(p => p.Name, p => p.Value.Clone());
        scoped["root"] = JsonSerializer.SerializeToElement("documents");
        scoped["bundleId"] = JsonSerializer.SerializeToElement(bundle);
        return scoped;
    }

    private async Task LoadDocumentScopesAsync(Operation op)
    {
        if (_documentScopesDevice == op.Device?.Udid) return;
        var result = await op.Call("shared_document_apps");
        op.Check();
        _loadingDocumentScopes = true;
        try
        {
            FileRoot.SelectedIndex = 0;
            while (FileRoot.Items.Count > 2) FileRoot.Items.RemoveAt(FileRoot.Items.Count - 1);
            foreach (var app in result.GetProperty("apps").EnumerateArray())
                FileRoot.Items.Add(new ComboBoxItem
                {
                    Content = app.GetProperty("name").GetString() + " · " + L("SharedDocuments"),
                    DataContext = app.GetProperty("name").GetString(),
                    Tag = "documents:" + app.GetProperty("bundleId").GetString(),
                    ToolTip = app.GetProperty("bundleId").GetString()
                });
            _documentScopesDevice = op.Device?.Udid;
        }
        finally { _loadingDocumentScopes = false; }
    }

}
