using System.Runtime.InteropServices;

namespace CouchMode.Ui;

/// <summary>
/// Rounded window corners, via the compositor rather than a clipping region.
///
/// Setting a rounded <see cref="Control.Region"/> would work everywhere but looks wrong: a
/// Region is a hard-edged mask with no antialiasing, so the curve comes out visibly stepped,
/// and the window's shadow follows the square outline. Asking DWM to round the window gets the
/// same corners the OS gives every other app, shadow included.
///
/// Windows 11 only. On Windows 10 the call fails harmlessly and the window stays square, which
/// is the correct look on that version anyway.
/// </summary>
internal static class WindowCorners
{
    private const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;

    private const int DWMWCP_DEFAULT = 0;
    private const int DWMWCP_ROUND = 2;
    private const int DWMWCP_ROUNDSMALL = 3;

    /// <summary>Round a window's corners. <paramref name="radius"/> only chooses large or small.</summary>
    public static void Round(Form form, int radius = 0)
    {
        void Apply()
        {
            try
            {
                int preference = radius is > 0 and <= 8 ? DWMWCP_ROUNDSMALL : DWMWCP_ROUND;
                DwmSetWindowAttribute(form.Handle, DWMWA_WINDOW_CORNER_PREFERENCE,
                                      ref preference, sizeof(int));
            }
            catch (Exception ex)
            {
                // Pre-Windows-11, or dwmapi missing. Square corners are not worth failing over.
                Log.Warn($"Rounded corners unavailable: {ex.Message}");
            }
        }

        if (form.IsHandleCreated) Apply();
        else form.HandleCreated += (_, _) => Apply();
    }

    [DllImport("dwmapi.dll", PreserveSig = true)]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute,
                                                    ref int value, int size);
}
