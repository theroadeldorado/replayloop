using System.Runtime.InteropServices;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Windows.ApplicationModel.DataTransfer;
using WinRT;
using WinRT.Interop;

namespace SwingLoop.Services;

/// <summary>Win32 glue WinUI 3 desktop apps need for pickers, share and window control.</summary>
public static class WindowInterop
{
    public static IntPtr Hwnd(Window w) => WindowNative.GetWindowHandle(w);

    public static AppWindow AppWindowOf(Window w) => AppWindow.GetFromWindowId(Win32Interop.GetWindowIdFromWindow(Hwnd(w)));

    /// <summary>Pickers and dialogs need an owner window in unpackaged apps.</summary>
    public static T WithOwner<T>(T picker, Window owner) where T : class
    {
        InitializeWithWindow.Initialize(picker, Hwnd(owner));
        return picker;
    }

    [ComImport]
    [Guid("3A3DCD6C-3EAB-43DC-BCDE-45671CE800C8")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDataTransferManagerInterop
    {
        IntPtr GetForWindow([In] IntPtr appWindow, [In] ref Guid riid);
        void ShowShareUIForWindow(IntPtr appWindow);
    }

    private static readonly Guid DataTransferManagerIid = new(0xa5caee9b, 0x8708, 0x49d1, 0x8d, 0x36, 0x67, 0xd2, 0x5a, 0x8d, 0xa0, 0x0c);

    public static DataTransferManager DataTransferManagerFor(Window w)
    {
        var interop = DataTransferManager.As<IDataTransferManagerInterop>();
        var iid = DataTransferManagerIid;
        IntPtr ptr = interop.GetForWindow(Hwnd(w), ref iid);
        return MarshalInterface<DataTransferManager>.FromAbi(ptr);
    }

    public static void ShowShareUI(Window w)
    {
        var interop = DataTransferManager.As<IDataTransferManagerInterop>();
        interop.ShowShareUIForWindow(Hwnd(w));
    }

    [DllImport("user32.dll")] private static extern void keybd_event(byte vk, byte scan, uint flags, UIntPtr extra);
    private const byte VkLWin = 0x5B, VkK = 0x4B;
    private const uint KeyUp = 0x0002;

    /// <summary>
    /// Opens the Windows "Cast" flyout (Win+K), which mirrors the whole screen
    /// to Miracast TVs and adapters.
    /// </summary>
    public static void OpenSystemCastFlyout()
    {
        keybd_event(VkLWin, 0, 0, UIntPtr.Zero);
        keybd_event(VkK, 0, 0, UIntPtr.Zero);
        keybd_event(VkK, 0, KeyUp, UIntPtr.Zero);
        keybd_event(VkLWin, 0, KeyUp, UIntPtr.Zero);
    }
}
