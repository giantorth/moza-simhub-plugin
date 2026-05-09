using System;
using MozaPlugin.Telemetry;
using ISessionConsumer = MozaPlugin.Telemetry2.Sessions.ISessionConsumer;

namespace MozaPlugin.Telemetry2.Operations
{
    // Drives session 0x04 mzdash upload via the existing DashboardUploader factory.
    // The op tracks the wire-level state machine: wait for device-init, send sub-msg-1,
    // wait for echo, send sub-msg-2, wait for ack, send end marker.
    //
    // The big wire-format work (legacy 8B header / new 6B / new Type02) is done inside
    // FileTransferBuilder, which DashboardUploader composes. This op only sequences
    // the dance — when to send what, given device acks.
    public sealed class DashboardUploadOp : ISessionConsumer
    {
        public const byte SessionDefault = 0x04;

        public enum State
        {
            Idle,
            WaitDeviceInit,
            SubMsg1Sent,
            SubMsg2Sent,
            EndSent,
            Done,
            Failed,
        }

        public byte SessionByte { get; }
        public State Current { get; private set; } = State.Idle;
        public DashboardUploader.UploadPayload? Payload { get; private set; }
        public string? FailureReason { get; private set; }

        public event EventHandler<State>? StateChanged;

        public DashboardUploadOp(byte session = SessionDefault)
        {
            SessionByte = session;
        }

        // Begin a new upload. Caller has already built the payload (via DashboardUploader).
        // The op transitions to WaitDeviceInit and exposes the payload for the host to
        // chunk through the SessionEndpoint when the device opens session 0x04.
        public void Begin(DashboardUploader.UploadPayload payload)
        {
            if (Current != State.Idle && Current != State.Done && Current != State.Failed)
                throw new InvalidOperationException($"upload in progress (state={Current})");
            Payload = payload ?? throw new ArgumentNullException(nameof(payload));
            Current = State.WaitDeviceInit;
            StateChanged?.Invoke(this, Current);
        }

        public void Fail(string reason)
        {
            FailureReason = reason;
            Current = State.Failed;
            StateChanged?.Invoke(this, Current);
        }

        public void Reset()
        {
            Current = State.Idle;
            Payload = null;
            FailureReason = null;
        }

        // Device opens its end of the session — host's cue to send sub-msg-1.
        public void OnOpen(byte session, int openSeq)
        {
            if (session != SessionByte) return;
            if (Current == State.WaitDeviceInit)
            {
                Current = State.SubMsg1Sent; // host transitions when it dispatches the bytes
                StateChanged?.Invoke(this, Current);
            }
        }

        // Device echoes sub-msg-1 → cue to send sub-msg-2.
        // Device acks sub-msg-2 → cue to send end marker.
        public void OnData(byte session, int seq, byte[] payload)
        {
            if (session != SessionByte) return;
            // Without ground-truth decode of the device's intermediate echo/ack semantics
            // we treat any inbound data as a positive ack of the last-sent sub-message.
            // Real-wire shape decode is queued for a follow-up Phase-0.5 task; the host
            // can supply explicit transitions if needed.
            switch (Current)
            {
                case State.SubMsg1Sent:
                    Current = State.SubMsg2Sent;
                    StateChanged?.Invoke(this, Current);
                    break;
                case State.SubMsg2Sent:
                    Current = State.EndSent;
                    StateChanged?.Invoke(this, Current);
                    break;
            }
        }

        public void OnAck(byte session, int ackSeq) { }

        public void OnClose(byte session, int ackSeq)
        {
            if (session != SessionByte) return;
            if (Current == State.EndSent || Current == State.SubMsg2Sent)
            {
                Current = State.Done;
                StateChanged?.Invoke(this, Current);
            }
        }
    }
}
