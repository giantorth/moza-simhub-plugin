namespace MozaPlugin.Telemetry2.Sessions
{
    // Owns one or more sessions and exclusively handles their inbound frames.
    // Sessions are claimed/released through SessionDispatcher.
    //
    // Note: Telemetry/ has the same-named interface in MozaPlugin.Telemetry namespace.
    // This is a separate type in MozaPlugin.Telemetry2.Sessions so the new pipeline can
    // evolve independently. Both pipelines compile in parallel until Phase 9 cleanup.
    public interface ISessionConsumer
    {
        // Inbound data chunk (type 0x01) for a session this consumer owns.
        void OnData(byte session, int seq, byte[] payload);

        // FC:00 ack for a session this consumer owns.
        void OnAck(byte session, int ackSeq);

        // Device-initiated session open (type 0x81).
        void OnOpen(byte session, int openSeq);

        // Session close / end marker (type 0x00).
        void OnClose(byte session, int ackSeq);
    }
}
