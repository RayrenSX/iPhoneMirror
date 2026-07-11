using IPhoneMirror.App.Interop;

namespace IPhoneMirror.App.Controls;

/// <summary>
/// Serializes ownership of the single native D3D11 preview renderer.
/// The most recently activated host owns the renderer; closing it restores
/// rendering to the previous host.
/// </summary>
internal static class PreviewAttachmentCoordinator
{
    private static readonly object Gate = new();
    private static readonly List<nint> Hosts = [];

    internal static bool Activate(nint window)
    {
        if (window == 0) return false;

        lock (Gate)
        {
            Hosts.Remove(window);
            Hosts.Add(window);
            if (NativeCore.AttachPreviewWindow(window)) return true;

            Hosts.Remove(window);
            AttachLastHost();
            return false;
        }
    }

    internal static void Unregister(nint window)
    {
        if (window == 0) return;

        lock (Gate)
        {
            var wasActive = Hosts.Count > 0 && Hosts[^1] == window;
            Hosts.Remove(window);
            if (!wasActive) return;

            NativeCore.DetachPreviewWindow();
            AttachLastHost();
        }
    }

    internal static bool Refresh(nint window)
    {
        if (window == 0) return false;

        lock (Gate)
        {
            // A refresh request belongs to a concrete host. If another host
            // currently owns the single swap chain, hand ownership back first
            // instead of reporting success for refreshing the wrong window.
            if (Hosts.Count == 0 || Hosts[^1] != window)
                return ActivateWhileLocked(window);

            return NativeCore.ForcePreviewRefresh() ||
                NativeCore.AttachPreviewWindow(window);
        }
    }

    private static bool ActivateWhileLocked(nint window)
    {
        Hosts.Remove(window);
        Hosts.Add(window);
        if (NativeCore.AttachPreviewWindow(window)) return true;
        Hosts.Remove(window);
        AttachLastHost();
        return false;
    }

    private static void AttachLastHost()
    {
        while (Hosts.Count > 0)
        {
            if (NativeCore.AttachPreviewWindow(Hosts[^1])) return;
            Hosts.RemoveAt(Hosts.Count - 1);
        }
    }
}
