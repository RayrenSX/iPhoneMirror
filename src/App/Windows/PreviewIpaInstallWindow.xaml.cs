using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using IPhoneMirror.App.Localization;
using IPhoneMirror.App.Services;
using IPhoneMirror.App.Services.Automation;
using Microsoft.Win32;

namespace IPhoneMirror.App.Windows;

public partial class PreviewIpaInstallWindow : IPhoneMirror.UI.Controls.RoundedWindow
{
    private readonly string[] _files;
    private readonly string _udid;
    private readonly Func<bool> _isCurrent;
    private readonly Func<string, CancellationToken, Task> _install;
    private readonly IIpaSigningSession _signing;
    private CancellationTokenSource? _operation;
    private string[] _profiles = [];
    private string? _authenticatedAccount;
    private bool _closePending;
    private string Mode => (SigningMode.SelectedItem as ComboBoxItem)?.Tag as string ?? "existing";

    internal PreviewIpaInstallWindow(string deviceName, string udid, string[] files, Func<bool> isCurrent,
        Func<string, CancellationToken, Task> install, IIpaSigningSession? signing = null)
    {
        _files = files; _udid = udid; _isCurrent = isCurrent; _install = install;
        _signing = signing ?? new IpaSigningSession();
        InitializeComponent();
        TargetText.Text = LocalizationService.Format("IpaDropTarget", deviceName, files.Length);
        FilesText.Text = string.Join(Environment.NewLine, files.Take(4).Select(Path.GetFileName));
        StatusText.Text = LocalizationService.Get("IpaReady");
        Closing += OnClosing;
        Closed += async (_, _) =>
        {
            ApplePassword.Clear(); CertificatePassword.Clear(); VerificationCode.Clear();
            await _signing.DisposeAsync();
        };
        UpdateMode();
    }

    private void ModeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (IsInitialized) UpdateMode();
    }

    private void UpdateMode()
    {
        ApplePanel.Visibility = Mode == "apple_id" ? Visibility.Visible : Visibility.Collapsed;
        CertificatePanel.Visibility = Mode is "p12" or "pem" ? Visibility.Visible : Visibility.Collapsed;
        PrivateKeyPanel.Visibility = Mode == "pem" ? Visibility.Visible : Visibility.Collapsed;
        SigningOptions.Visibility = Mode == "existing" ? Visibility.Collapsed : Visibility.Visible;
        ModeHint.Text = LocalizationService.Get(Mode switch
        { "apple_id" => "IpaAppleIdHint", "existing" => "IpaExistingHint", _ => "IpaCertificateHint" });
    }

    private void CheckTarget()
    {
        if (!_isCurrent()) throw new AutomationException("CONTROL_NOT_AVAILABLE", "");
    }

    private async Task RunAsync(Func<CancellationToken, Task> operation)
    {
        if (_operation is not null) return;
        using var cancellation = new CancellationTokenSource();
        _operation = cancellation;
        OptionsPanel.IsEnabled = InstallButton.IsEnabled = false;
        Progress.Visibility = Visibility.Visible;
        CloseButton.SetResourceReference(ContentProperty, "Cancel");
        try { CheckTarget(); await operation(cancellation.Token); }
        catch (OperationCanceledException) { StatusText.Text = LocalizationService.Get("IpaCancelled"); }
        catch (Exception error) { StatusText.Text = ErrorText(error); }
        finally
        {
            _operation = null;
            OptionsPanel.IsEnabled = InstallButton.IsEnabled = true;
            Progress.Visibility = Visibility.Collapsed;
            CloseButton.SetResourceReference(ContentProperty, "Close");
            if (_closePending) Close();
        }
    }

    private async void LoginClick(object sender, RoutedEventArgs e) => await AuthenticateAsync(false);
    private async void VerifyClick(object sender, RoutedEventArgs e) => await AuthenticateAsync(true);

    private Task AuthenticateAsync(bool verify) => RunAsync(async cancellation =>
    {
        StatusText.Text = LocalizationService.Get("IpaSigningIn");
        var email = AppleId.Text.Trim();
        var result = await _signing.RequestAsync(new
        {
            action = verify ? "verify" : "login", email, password = ApplePassword.Password,
            code = VerificationCode.Text.Trim(), anisetteServer = AnisetteServer.Text.Trim(),
        }, cancellation);
        CheckTarget();
        var status = result.GetProperty("status").GetString();
        if (status == "2fa_required")
        {
            _authenticatedAccount = null;
            CodePanel.Visibility = Visibility.Visible;
            StatusText.Text = LocalizationService.Get("IpaCodeSent");
            VerificationCode.Focus();
        }
        else if (status == "authenticated")
        {
            _authenticatedAccount = email;
            CodePanel.Visibility = Visibility.Collapsed;
            ApplePassword.Clear(); VerificationCode.Clear();
            StatusText.Text = LocalizationService.Get("IpaSignedIn");
        }
        else throw new AutomationException("IPA_SIGNING_FAILED", "");
    });

    private async void InstallClick(object sender, RoutedEventArgs e) => await RunAsync(async cancellation =>
    {
        var mode = Mode;
        if (mode == "apple_id" && !string.Equals(_authenticatedAccount, AppleId.Text.Trim(), StringComparison.OrdinalIgnoreCase))
            throw new AutomationException("IPA_ACCOUNT_REQUIRED", "");
        var sent = 0;
        foreach (var source in _files)
        {
            cancellation.ThrowIfCancellationRequested(); CheckTarget();
            StatusText.Text = LocalizationService.Format("IpaChecking", Path.GetFileName(source));
            await _signing.RequestAsync(new { action = "inspect", path = source }, cancellation);
            var path = source;
            if (mode != "existing")
            {
                StatusText.Text = LocalizationService.Format("IpaSigning", Path.GetFileName(source));
                var result = await _signing.RequestAsync(new
                {
                    action = "sign", path = source, mode, udid = _udid, email = _authenticatedAccount,
                    certificate = CertificatePath.Text.Trim(), privateKey = PrivateKeyPath.Text.Trim(),
                    certificatePassword = CertificatePassword.Password, profiles = _profiles,
                    bundleId = BundleId.Text.Trim(), removeExtensions = RemoveExtensions.IsChecked == true,
                }, cancellation);
                path = result.GetProperty("path").GetString() ?? throw new InvalidDataException();
            }
            cancellation.ThrowIfCancellationRequested(); CheckTarget();
            StatusText.Text = LocalizationService.Format("IpaInstalling", Path.GetFileName(source), sent + 1, _files.Length);
            await _install(path, cancellation);
            sent++;
            StatusText.Text = LocalizationService.Format("IpaInstalled", sent, _files.Length);
        }
        InstallButton.Visibility = Visibility.Collapsed;
    });

    internal static string ErrorText(Exception error)
    {
        var key = error is AutomationException failure ? failure.Code switch
        {
            "IPA_INVALID_FILE" => "IpaInvalidFile",
            "IPA_BAD_CERTIFICATE" => "IpaBadCertificate",
            "IPA_CERTIFICATE_EXPIRED" => "IpaCertificateExpired",
            "IPA_PROFILE_REQUIRED" => "IpaProfileRequired",
            "IPA_PROFILE_EXPIRED" => "IpaProfileExpired",
            "IPA_PROFILE_CERTIFICATE_MISMATCH" => "IpaProfileCertificateMismatch",
            "IPA_PROFILE_DEVICE_MISMATCH" => "IpaProfileDeviceMismatch",
            "IPA_PROFILE_BUNDLE_MISMATCH" => "IpaProfileBundleMismatch",
            "IPA_ACCOUNT_REQUIRED" => "IpaAccountRequired",
            "IPA_CODE_REQUIRED" => "IpaCodeRequired",
            "IPA_INVALID_BUNDLE_ID" => "IpaInvalidBundleId",
            "IPA_INVALID_ANISETTE_SERVER" => "IpaInvalidAnisetteServer",
            "IPA_RUNTIME_UNAVAILABLE" => "IpaRuntimeUnavailable",
            "PREVIEW_FILES_CONTROL_REQUIRED" => "PreviewFilesControlRequired",
            "CONTROL_NOT_AVAILABLE" or "SESSION_CHANGED" => "PreviewFilesConnectionUnavailable",
            "CONTROL_LOCKED" or "MANAGEMENT_BUSY" => "DeviceHubPanelBusyHint",
            "APP_INSTALL_FAILED" or "SIGNATURE_OR_PERMISSION_DENIED" => "IpaInstallRejected",
            _ => null,
        } : null;
        return key is not null ? LocalizationService.Get(key) :
            LocalizationService.Format("IpaFailed", error.Message);
    }

    private void PickCertificateClick(object sender, RoutedEventArgs e) =>
        PickFile(CertificatePath, Mode == "p12" ? "PKCS#12|*.p12;*.pfx" : "Certificate|*.pem;*.cer;*.crt;*.der");
    private void PickPrivateKeyClick(object sender, RoutedEventArgs e) => PickFile(PrivateKeyPath, "Private key|*.pem;*.key;*.der");
    private void PickFile(TextBox field, string filter)
    {
        var picker = new OpenFileDialog { Filter = filter, CheckFileExists = true };
        if (picker.ShowDialog(this) == true) field.Text = picker.FileName;
    }
    private void PickProfilesClick(object sender, RoutedEventArgs e)
    {
        var picker = new OpenFileDialog { Filter = "Provisioning profile|*.mobileprovision;*.provisionprofile", Multiselect = true, CheckFileExists = true };
        if (picker.ShowDialog(this) != true) return;
        _profiles = picker.FileNames;
        ProfilePaths.Text = string.Join(Environment.NewLine, _profiles.Select(Path.GetFileName));
    }
    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (_operation is null) return;
        e.Cancel = true; _closePending = true; _operation.Cancel();
    }
    private void CloseClick(object sender, RoutedEventArgs e) => Close();
}
