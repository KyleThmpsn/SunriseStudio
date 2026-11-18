using System.Numerics;
using System.Runtime.InteropServices;

namespace SunriseStudio.Viewport;

/**
 * A DualSense read from its HID reports, which XInput does not carry. A background thread keeps the newest report;
 * Read turns it into a reading. The controller is looked for again after it disconnects.
 */
internal static class DualSense
{
    // Sony's vendor id and the DualSense and DualSense Edge product ids, as a device path spells them.
    private const string Vendor = "054c";
    private static readonly string[] Products = ["0ce6", "0df2"];
    // A Bluetooth HID device's path carries the HID service's id.
    private const string BluetoothService = "00001124-0000-1000-8000-00805f9b34fb";
    // Over Bluetooth the controller sends a short report until software asks for the full one, which has this id.
    private const byte FullReport = 0x31;
    // Where each report keeps its four stick bytes, its two trigger bytes and its two button bytes.
    private static readonly Layout Usb = new(1, 5, 8), BluetoothShort = new(1, 8, 5), BluetoothFull = new(2, 6, 9);
    // The first button byte's low half is the d-pad as a compass point clockwise from north, this value when released.
    private const int DpadMask = 0x0F, DpadReleased = 8, NorthEast = 1, SouthEast = 3, SouthWest = 5, NorthWest = 7;
    // R1 and Options in the second button byte.
    private const byte RightShoulder = 0x02, Options = 0x20;
    // A stick byte's centre, and the most bytes a report holds.
    private const float StickCentre = 127.5f;
    private const int ReportSize = 128;
    // Raw Input's code for a HID device and its request for a device's path.
    private const uint HidDevice = 2, DeviceName = 0x20000007;
    // A missing controller is looked for this often, in milliseconds.
    private const long SearchInterval = 1000;
    private static readonly object Gate = new();
    private static readonly byte[] Report = new byte[ReportSize];
    private static int length;
    private static bool listening, bluetooth;
    private static long searched = -SearchInterval;

    private readonly record struct Layout(int Sticks, int Triggers, int Buttons);

    /** False when no DualSense is connected or it has sent no report yet. */
    public static bool Read(out PadReading pad)
    {
        pad = default;
        lock (Gate)
        {
            if (!listening)
            {
                if (Environment.TickCount64 - searched < SearchInterval) return false;
                searched = Environment.TickCount64;
                if (Find() is not { } path) return false;
                listening = true;
                bluetooth = path.Contains(BluetoothService, StringComparison.OrdinalIgnoreCase);
                new Thread(() => Listen(path)) { IsBackground = true, Name = "DualSense" }.Start();
                return false;
            }
            var layout = !bluetooth ? Usb : Report[0] == FullReport ? BluetoothFull : BluetoothShort;
            if (length < Math.Max(layout.Triggers, layout.Buttons) + 2) return false;
            int dpad = Report[layout.Buttons] & DpadMask;
            byte buttons = Report[layout.Buttons + 1];
            pad = new(Stick(layout.Sticks), Stick(layout.Sticks + 2), Report[layout.Triggers] / (float)byte.MaxValue,
                Report[layout.Triggers + 1] / (float)byte.MaxValue, dpad is <= NorthEast or NorthWest,
                dpad is >= SouthEast and <= SouthWest, (buttons & Options) != 0, (buttons & RightShoulder) != 0);
            return true;
        }
    }

    /** A stick's two bytes as -1 to 1 with up and right positive; the report counts down as positive. */
    private static Vector2 Stick(int at) =>
        new((Report[at] - StickCentre) / StickCentre, (StickCentre - Report[at + 1]) / StickCentre);

    /** Keeps the newest report until the controller disconnects. */
    private static void Listen(string path)
    {
        try
        {
            using var handle = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var stream = new FileStream(handle, FileAccess.Read, 0);
            var buffer = new byte[ReportSize];
            while (stream.Read(buffer, 0, buffer.Length) is > 0 and var read)
                lock (Gate) { buffer.AsSpan(0, read).CopyTo(Report); length = read; }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or NotSupportedException) { }
        finally { lock (Gate) { length = 0; listening = false; } }
    }

    /** The device path of the first connected DualSense, or null. */
    private static string? Find()
    {
        uint count = 0, size = (uint)Marshal.SizeOf<RawInputDevice>();
        if (GetRawInputDeviceList(null, ref count, size) != 0 || count == 0) return null;
        var devices = new RawInputDevice[count];
        if (GetRawInputDeviceList(devices, ref count, size) == uint.MaxValue) return null;
        foreach (var device in devices.AsSpan(0, (int)count))
        {
            if (device.Type != HidDevice) continue;
            uint characters = 0;
            GetRawInputDeviceInfoW(device.Handle, DeviceName, null, ref characters);
            if (characters == 0) continue;
            var name = new char[characters];
            if (GetRawInputDeviceInfoW(device.Handle, DeviceName, name, ref characters) is 0 or uint.MaxValue) continue;
            string path = new string(name).TrimEnd('\0');
            // USB writes the ids as vid_054c&pid_0ce6, Bluetooth as vid&0002054c_pid&0ce6.
            foreach (string product in Products)
                if (path.Contains($"vid_{Vendor}&pid_{product}", StringComparison.OrdinalIgnoreCase)
                    || path.Contains($"{Vendor}_pid&{product}", StringComparison.OrdinalIgnoreCase)) return path;
        }
        return null;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RawInputDevice
    {
        public IntPtr Handle;
        public uint Type;
    }

    [DllImport("user32.dll")]
    private static extern uint GetRawInputDeviceList([Out] RawInputDevice[]? devices, ref uint count, uint size);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern uint GetRawInputDeviceInfoW(IntPtr device, uint command, [Out] char[]? data, ref uint size);
}
