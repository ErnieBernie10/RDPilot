namespace RDPilot.Client.Views;

internal static class RdpPointerInputMapper
{
    private const ushort PointerWheelNegativeFlag = 0x0100;
    private const int WheelDelta = 120;

    public const ushort PointerMoveFlag = 0x0800;
    public const ushort PointerDownFlag = 0x8000;
    public const ushort PointerButton1Flag = 0x1000;
    public const ushort PointerButton2Flag = 0x2000;
    public const ushort PointerButton3Flag = 0x4000;
    public const ushort PointerWheelFlag = 0x0200;
    public const ushort PointerHorizontalWheelFlag = 0x0400;

    public static ushort BuildWheelFlags(ushort wheelFlag, double delta)
    {
        if (delta == 0)
        {
            return 0;
        }

        var wheelDelta = (int)System.Math.Round(System.Math.Abs(delta) * WheelDelta);
        if (wheelDelta == 0)
        {
            wheelDelta = 1;
        }

        ushort flags = (ushort)(wheelFlag | System.Math.Min(wheelDelta, 0xFF));
        if (delta < 0)
        {
            flags |= PointerWheelNegativeFlag;
        }

        return flags;
    }
}
