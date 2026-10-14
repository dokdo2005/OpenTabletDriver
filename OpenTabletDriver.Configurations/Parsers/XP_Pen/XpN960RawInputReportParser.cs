using System.Diagnostics.CodeAnalysis;
using System.Numerics;
using OpenTabletDriver.Plugin.Tablet;

namespace OpenTabletDriver.Configurations.Parsers.XP_Pen
{
    /// <summary>Parser for XP-N960 reports delivered by XpN960RawInputHub only.</summary>
    [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicParameterlessConstructor)]
    public sealed class XpN960RawInputReportParser : IReportParser<IDeviceReport>
    {
        public IDeviceReport? Parse(byte[] data)
        {
            if (data.Length != 8 || data[0] != 0x0A)
                return null;

            const byte inRange = 0x40;
            const byte barrelButtonCandidate = 0x04;
            if ((data[1] & inRange) == 0)
                return new OutOfRangeReport(data);

            return new TabletReport
            {
                Raw = data,
                Position = new Vector2(data[2] | data[3] << 8, data[4] | data[5] << 8),
                Pressure = (uint)(data[6] | data[7] << 8),
                // The sole physical side button is only a candidate mapping until
                // repeated isolated testing confirms state bit 0x04.
                PenButtons = [(data[1] & barrelButtonCandidate) != 0]
            };
        }
    }
}
