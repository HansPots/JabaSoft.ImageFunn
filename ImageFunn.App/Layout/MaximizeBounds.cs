using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace ImageFunn.App.Layout;

/// <summary>
/// Houdt een gemaximaliseerd venster binnen het WERKGEBIED van het scherm -
/// het scherm min de taakbalk.
///
/// Waarom dit nodig is: een venster zonder titelbalk (WindowStyle="None")
/// maximaliseert standaard over het hele scherm heen, taakbalk incluis, en
/// steekt er aan alle kanten ook nog een randbreedte overheen. Windows vraagt
/// vlak voor het maximaliseren aan het venster hoe groot en waar het mag
/// worden (WM_GETMINMAXINFO); die vraag wordt hier beantwoord met de maten
/// van het scherm waar het venster op staat.
///
/// Alleen de maximale maat en positie worden gezet, niet de maximale
/// SLEEPmaat: anders zou je het venster ook met de hand niet meer over twee
/// schermen kunnen uitrekken.
/// </summary>
public static class MaximizeBounds
{
    /// <summary>
    /// Koppelt de begrenzing aan een venster. Aanroepen in de constructor
    /// mag: hangt het venster er nog niet, dan wacht dit tot het zover is -
    /// er is pas een vensterhandvat om aan te haken zodra Windows het
    /// venster gemaakt heeft.
    /// </summary>
    public static void Apply(Window window)
    {
        ArgumentNullException.ThrowIfNull(window);

        if (new WindowInteropHelper(window).Handle != IntPtr.Zero)
        {
            Koppel(window);
            return;
        }

        window.SourceInitialized += (_, _) => Koppel(window);
    }

    private static void Koppel(Window window)
    {
        var bron = HwndSource.FromHwnd(new WindowInteropHelper(window).Handle);
        bron?.AddHook(Hook);
    }

    private const int WmGetMinMaxInfo = 0x0024;

    /// <summary>Geen scherm gevonden? Pak dan het dichtstbijzijnde.</summary>
    private const int MonitorDefaultToNearest = 0x0002;

    private static IntPtr Hook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg != WmGetMinMaxInfo)
        {
            return IntPtr.Zero;
        }

        var scherm = MonitorFromWindow(hwnd, MonitorDefaultToNearest);
        if (scherm == IntPtr.Zero)
        {
            return IntPtr.Zero;
        }

        var schermInfo = new MonitorInfo { cbSize = Marshal.SizeOf<MonitorInfo>() };
        if (!GetMonitorInfo(scherm, ref schermInfo))
        {
            return IntPtr.Zero;
        }

        var info = Marshal.PtrToStructure<MinMaxInfo>(lParam);

        // De positie is TEN OPZICHTE van de linkerbovenhoek van het scherm,
        // niet van het bureaublad - vandaar het verschil tussen werkgebied
        // en schermrechthoek. Op een scherm met de taakbalk bovenaan of
        // links is dat niet 0.
        info.ptMaxPosition.X = schermInfo.rcWork.Left - schermInfo.rcMonitor.Left;
        info.ptMaxPosition.Y = schermInfo.rcWork.Top - schermInfo.rcMonitor.Top;
        info.ptMaxSize.X = schermInfo.rcWork.Right - schermInfo.rcWork.Left;
        info.ptMaxSize.Y = schermInfo.rcWork.Bottom - schermInfo.rcWork.Top;

        Marshal.StructureToPtr(info, lParam, true);
        handled = true;
        return IntPtr.Zero;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, int dwFlags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MonitorInfo lpmi);

    [StructLayout(LayoutKind.Sequential)]
    private struct Punt
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MinMaxInfo
    {
        public Punt ptReserved;
        public Punt ptMaxSize;
        public Punt ptMaxPosition;
        public Punt ptMinTrackSize;
        public Punt ptMaxTrackSize;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Rechthoek
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo
    {
        public int cbSize;
        public Rechthoek rcMonitor;
        public Rechthoek rcWork;
        public int dwFlags;
    }
}
