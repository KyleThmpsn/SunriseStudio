using System.Numerics;
using System.Runtime.InteropServices;

namespace SunriseStudio.Viewport;

/**
 * What a controller asks of the player camera this frame. Sticks are -1 to 1 past their dead zones, with up and right
 * positive; Rise is the right trigger less the left.
 */
public readonly record struct GamepadState(Vector2 Move, Vector2 Look, float Rise, bool Sprint, int SpeedStep, bool Menu)
{
    /** True while a stick or a trigger is held, which is what takes the camera. */
    public bool Steering => Move != Vector2.Zero || Look != Vector2.Zero || Rise != 0;
}

/**
 * A controller's sticks as -1 to 1 with up and right positive, its triggers as 0 to 1, and the buttons the camera
 * uses, whichever interface read them.
 */
internal readonly record struct PadReading(Vector2 Left, Vector2 Right, float LeftTrigger, float RightTrigger,
    bool Up, bool Down, bool Menu, bool Sprint);

/**
 * The first connected controller, read once a frame by Poll: an XInput controller, or a DualSense when there is
 * none. With neither connected, Current stays empty.
 */
public static class Gamepad
{
    // XInput's limit of four controllers, and the stick dead zones and trigger threshold its header recommends, as
    // shares of full throw.
    private const int Controllers = 4;
    private const float LeftDeadZone = 7849f / short.MaxValue, RightDeadZone = 8689f / short.MaxValue;
    private const float TriggerThreshold = 30f / byte.MaxValue;
    private const ushort DpadUp = 0x0001, DpadDown = 0x0002, Start = 0x0010, RightShoulder = 0x0200;
    // An empty slot is slow to ask, so a missing controller is looked for this often, in milliseconds.
    private const long SearchInterval = 1000;
    private static int connected = -1;
    private static long searched = -SearchInterval;
    private static PadReading held;

    /** This frame's state. */
    public static GamepadState Current { get; private set; }

    /** Reads the controller. Call once a frame. */
    public static void Poll()
    {
        Current = default;
        if (!ReadXInput(out var pad) && !DualSense.Read(out pad)) { held = default; return; }
        // A button counts once, on the frame it goes down.
        Current = new(Stick(pad.Left, LeftDeadZone), Stick(pad.Right, RightDeadZone),
            Trigger(pad.RightTrigger) - Trigger(pad.LeftTrigger), pad.Sprint,
            (pad.Up && !held.Up ? 1 : 0) - (pad.Down && !held.Down ? 1 : 0), pad.Menu && !held.Menu);
        held = pad;
    }

    /** False when no XInput controller is connected. */
    private static bool ReadXInput(out PadReading pad)
    {
        pad = default;
        if (connected < 0)
        {
            if (Environment.TickCount64 - searched < SearchInterval) return false;
            searched = Environment.TickCount64;
            for (int index = 0; index < Controllers && connected < 0; index++)
                if (Read(index, out _)) connected = index;
            if (connected < 0) return false;
        }
        if (!Read(connected, out var state)) { connected = -1; return false; }
        pad = new(new Vector2(state.LeftX, state.LeftY) / short.MaxValue,
            new Vector2(state.RightX, state.RightY) / short.MaxValue,
            state.LeftTrigger / (float)byte.MaxValue, state.RightTrigger / (float)byte.MaxValue,
            (state.Buttons & DpadUp) != 0, (state.Buttons & DpadDown) != 0, (state.Buttons & Start) != 0,
            (state.Buttons & RightShoulder) != 0);
        return true;
    }

    /** False when the slot holds no controller or this Windows has no XInput. */
    private static bool Read(int index, out XInputGamepad pad)
    {
        pad = default;
        try
        {
            if (XInputGetState((uint)index, out var state) != 0) return false;
            pad = state.Gamepad;
            return true;
        }
        catch (Exception error) when (error is DllNotFoundException or EntryPointNotFoundException) { return false; }
    }

    /** A stick past its dead zone, rescaled so the zone's edge is zero and full throw is one. */
    private static Vector2 Stick(Vector2 stick, float deadZone)
    {
        float length = stick.Length();
        if (length <= deadZone) return Vector2.Zero;
        return stick / length * Math.Min(1, (length - deadZone) / (1 - deadZone));
    }

    private static float Trigger(float value) =>
        value <= TriggerThreshold ? 0 : (value - TriggerThreshold) / (1 - TriggerThreshold);

    [StructLayout(LayoutKind.Sequential)]
    private struct XInputGamepad
    {
        public ushort Buttons;
        public byte LeftTrigger, RightTrigger;
        public short LeftX, LeftY, RightX, RightY;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct XInputState
    {
        public uint Packet;
        public XInputGamepad Gamepad;
    }

    [DllImport("xinput1_4.dll")]
    private static extern uint XInputGetState(uint index, out XInputState state);
}
