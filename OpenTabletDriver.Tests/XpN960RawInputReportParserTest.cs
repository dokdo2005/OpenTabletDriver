using System;
using OpenTabletDriver.Configurations.Parsers.XP_Pen;
using OpenTabletDriver.Plugin.Tablet;
using Xunit;

namespace OpenTabletDriver.Tests
{
    public class XpN960RawInputReportParserTest
    {
        private readonly XpN960RawInputReportParser parser = new();

        [Fact]
        public void Parse_ObservedContactReport_DecodesPositionPressureAndCandidateButton()
        {
            var report = Assert.IsType<TabletReport>(parser.Parse(Convert.FromHexString("0AC1E9073C00F202")));

            Assert.Equal(2025, report.Position.X);
            Assert.Equal(60, report.Position.Y);
            Assert.Equal((uint)754, report.Pressure);
            Assert.Single(report.PenButtons);
            Assert.False(report.PenButtons[0]);
        }

        [Fact]
        public void Parse_ObservedCandidateBarrelReport_MapsOnlyTheCandidateBit()
        {
            var report = Assert.IsType<TabletReport>(parser.Parse(Convert.FromHexString("0AC4E50331060000")));

            Assert.Equal(997, report.Position.X);
            Assert.Equal(1585, report.Position.Y);
            Assert.Equal((uint)0, report.Pressure);
            Assert.True(report.PenButtons[0]);
        }

        [Fact]
        public void Parse_OutOfRangeReport_ReleasesThePipeline()
        {
            Assert.IsType<OutOfRangeReport>(parser.Parse(Convert.FromHexString("0A80E500B5010000")));
        }

        [Fact]
        public void Parse_UnexpectedReport_IsIgnored()
        {
            Assert.Null(parser.Parse([0x0A, 0xC0]));
            Assert.Null(parser.Parse(Convert.FromHexString("07C0000000000000")));
        }
    }
}
