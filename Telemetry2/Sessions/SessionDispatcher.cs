using System.Collections.Generic;

namespace MozaPlugin.Telemetry2.Sessions
{
    // Central dispatch for session-layer frames. Each session byte maps to at most one
    // ISessionConsumer owner; frames for unclaimed sessions are silently dropped.
    //
    // Ported from Telemetry/SessionDispatcher.cs verbatim. Behavior identical;
    // namespace changed to MozaPlugin.Telemetry2.Sessions and the Diagnostics dependency
    // removed (eviction is silent in the new tree — host-level diagnostics will surface
    // it through wire-trace if needed).
    public sealed class SessionDispatcher
    {
        private readonly object _lock = new object();
        private readonly Dictionary<byte, ISessionConsumer> _owners = new Dictionary<byte, ISessionConsumer>();

        // Claim exclusive ownership. If already owned by another consumer, the previous
        // owner is evicted. Same consumer claiming again is a no-op.
        public void Claim(byte session, ISessionConsumer owner)
        {
            lock (_lock)
            {
                _owners[session] = owner;
            }
        }

        // Release only if current owner matches. Idempotent if not owned.
        public void Release(byte session, ISessionConsumer owner)
        {
            lock (_lock)
            {
                if (_owners.TryGetValue(session, out ISessionConsumer? cur) && cur == owner)
                    _owners.Remove(session);
            }
        }

        // Release every session this consumer owns.
        public void ReleaseAll(ISessionConsumer owner)
        {
            lock (_lock)
            {
                var toRemove = new List<byte>();
                foreach (var kvp in _owners)
                    if (kvp.Value == owner) toRemove.Add(kvp.Key);
                foreach (var s in toRemove) _owners.Remove(s);
            }
        }

        public ISessionConsumer? GetOwner(byte session)
        {
            lock (_lock)
                return _owners.TryGetValue(session, out ISessionConsumer? owner) ? owner : null;
        }

        public void DispatchData(byte session, int seq, byte[] payload)
        {
            ISessionConsumer? owner;
            lock (_lock) _owners.TryGetValue(session, out owner);
            owner?.OnData(session, seq, payload);
        }

        public void DispatchAck(byte session, int ackSeq)
        {
            ISessionConsumer? owner;
            lock (_lock) _owners.TryGetValue(session, out owner);
            owner?.OnAck(session, ackSeq);
        }

        public void DispatchOpen(byte session, int openSeq)
        {
            ISessionConsumer? owner;
            lock (_lock) _owners.TryGetValue(session, out owner);
            owner?.OnOpen(session, openSeq);
        }

        public void DispatchClose(byte session, int ackSeq)
        {
            ISessionConsumer? owner;
            lock (_lock) _owners.TryGetValue(session, out owner);
            owner?.OnClose(session, ackSeq);
        }

        public void Reset()
        {
            lock (_lock) _owners.Clear();
        }

        // Diagnostic snapshot of current claims (session byte → consumer type name).
        public IReadOnlyDictionary<byte, string> Snapshot()
        {
            lock (_lock)
            {
                var copy = new Dictionary<byte, string>();
                foreach (var kvp in _owners)
                    copy[kvp.Key] = kvp.Value.GetType().Name;
                return copy;
            }
        }
    }
}
