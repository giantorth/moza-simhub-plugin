using MozaPlugin.Telemetry2.Wire;

namespace MozaPlugin.Telemetry2.Operations
{
    // Builds the FF kind=4 record that tells the wheel to display a different dashboard
    // slot. Sent on h2b session 0x02. The host pairs this with TierDefNegotiator.SetActiveDashboard
    // for the new dashboard so the next tier-def emission carries the new channel set.
    //
    // Wire format (from bridge-20260503-113616.jsonl, Phase 0):
    //   kind=4 size=12 value=[slot:u32LE][0:u32LE]
    public static class DashboardSwitchOp
    {
        public static FfRecord Build(uint slotIndex) => FfRecord.DashboardSwitch(slotIndex);
    }
}
