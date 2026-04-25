using System;
using System.Threading;
using System.Timers;
using GameReaderCommon;
using MozaPlugin.Protocol;
using Timer = System.Timers.Timer;

namespace MozaPlugin.Telemetry
{
    /// <summary>
    /// Periodically encodes game data and sends telemetry frames to the wheel.
    ///
    /// Startup follows PitHouse's observed sequence (from USB capture analysis):
    ///   1. Open management session 0x01 + telemetry session 0x02 directly
    ///   2. Send session preamble (sub-message 1) then tier definition on telemetry session
    ///   3. Ack incoming channel data on telemetry session with fc:00 (~1 second)
    ///   4. Send 0x40 channel config burst (1E enables, 28:00, 28:01, 09:00, 28:02)
    ///   5. Begin 0x41 enable signal + 7d:23 telemetry with flag=0x00+tier
    ///
    /// Session allocation: mirrors PitHouse — sessions 0x01 (mgmt) and 0x02
    /// (telem, also becomes FlagByte) are hardcoded rather than probed. The
    /// session byte identifies which 7c:00 stream carries config data; the
    /// flag bytes inside tier definitions and telemetry frames are always
    /// 0-based (0x00, 0x01, 0x02), independent of the session byte.
    ///
    /// Each tier in the MultiStreamProfile runs at its own rate derived from package_level.
    /// Flag bytes are 0x00 + tier index (sorted by package_level ascending).
    /// </summary>
    public class TelemetrySender : IDisposable
    {
        private readonly MozaSerialConnection _connection;
        private Timer? _sendTimer;
        private TierState[]? _tiers;
        private volatile StatusDataBase? _latestGameData;
        private volatile bool _enabled;
        private int _tickCounter;
        private int _modeCounter;
        private int _slowCounter;
        private int _baseTickMs;  // Timer period derived from fastest tier's package_level
        private byte _sequenceCounter;
        private int _displayConfigPage;

        // Preamble state
        private bool _preambleComplete;
        private int _preambleTickTarget;
        private int _sessionAckSeq;

        // Port probing state
        private volatile byte _lastAckedSession;  // Set by OnMessageDuringPreamble when fc:00 arrives
        private readonly ManualResetEventSlim _ackReceived = new ManualResetEventSlim(false);

        // Upload handshake state (legacy, kept for test harness)
        private int _mgmtAckSeq;
        private readonly ManualResetEventSlim _mgmtResponseEvent = new ManualResetEventSlim(false);

        // File-transfer session state. The device initiates one or more sessions
        // in 0x04..0x0a with type=0x81 before we send sub-msg 1; it echoes paths
        // back (sub-msg 1 rsp), then acks the content push (sub-msg 2 rsp), then
        // sends type=0x00 end. Session number is dynamic per firmware:
        //   2025-11: wheel opens 0x04 device-init; plugin uploads on 0x04.
        //   2026-04: wheel still opens 0x04 device-init but new firmware also
        //            accepts uploads on other ports the host requests via
        //            7c:23 46. Tracked here as `_uploadSession`.
        private readonly SessionRegistry _sessions = new SessionRegistry();
        // Sessions in 0x04..0x0a that came up device-initiated. The first one
        // observed wins as the upload target unless overridden via
        // `UploadSessionOverride` (UI / test setting).
        private readonly System.Collections.Generic.HashSet<byte> _ftCandidateSessions = new();
        private byte _uploadSession = 0x04;  // default; updated when wheel device-inits a candidate session
        private readonly ManualResetEventSlim _uploadSessionOpened = new ManualResetEventSlim(false);
        private readonly ManualResetEventSlim _uploadSubMsg1Response = new ManualResetEventSlim(false);
        private readonly ManualResetEventSlim _uploadSubMsg2Response = new ManualResetEventSlim(false);
        private readonly ManualResetEventSlim _uploadEndReceived = new ManualResetEventSlim(false);
        private int _uploadInboundSeq;
        private int _uploadOutboundSeq;
        private int _uploadInboundMsgCount;

        /// <summary>
        /// Forces a specific session number for dashboard upload. 0 = auto
        /// (use first device-initiated session in 0x04..0x0a, falling back to
        /// 0x04). Set non-zero to override — useful for testing with new
        /// firmware that prefers 0x07 / 0x09.
        /// </summary>
        public byte UploadSessionOverride { get; set; } = 0;

        /// <summary>
        /// Wire format used to encode the upload sub-msg headers. Defaults to
        /// <see cref="FileTransferWireFormat.Legacy2025_11"/> for backward
        /// compatibility. Set to <see cref="FileTransferWireFormat.New2026_04"/>
        /// when targeting 2026-04+ firmware.
        /// </summary>
        public FileTransferWireFormat UploadWireFormat { get; set; }
            = FileTransferWireFormat.Legacy2025_11;

        // Session 0x09 configJson RPC state. Device proactively pushes its
        // dashboard state blob; we reply with the canonical dashboard library
        // list so the wheel updates its configJsonList (PitHouse's UI uses this
        // for library filtering / update-availability checks).
        private readonly ConfigJsonClient _configJson = new ConfigJsonClient();
        private int _session09InboundSeq;
        private int _session09OutboundSeq;
        private bool _session09ReplySent;
        public WheelDashboardState? WheelState => _configJson.LastState;

        /// <summary>
        /// Canonical dashboard library PitHouse would advertise to the wheel on
        /// session 0x09. Populated by the host from its known profile list. The
        /// wheel echoes these names back in its next state blob's
        /// <c>configJsonList</c>. Empty list disables the proactive reply.
        /// </summary>
        public System.Collections.Generic.IReadOnlyList<string> CanonicalDashboardList { get; set; }
            = System.Array.Empty<string>();

        // Display sub-device detection
        private volatile bool _displayDetected;
        private string _displayModelName = "";

        // Wheel channel catalog (parsed from incoming 7c:00 session data during preamble)
        private System.Collections.Generic.List<byte> _incomingSessionBuffer = new();
        private volatile System.Collections.Generic.List<string>? _wheelChannelCatalog;
        private volatile int _channelBufferLastActivityMs;

        // Upload-session inbound dir-listing buffer. After upload, wheel pushes
        // a fresh directory listing on the same session which lets us detect
        // when the upload is actually live on the device (rather than just
        // transmitted).
        private readonly SessionDataReassembler _uploadInbox = new();
        private volatile bool _uploadDirListingRefreshed;
        public bool Session04DirListingRefreshed => _uploadDirListingRefreshed;

        // RPC on 0x09/0x0a (host→device management RPCs such as completelyRemove).
        // Replies come back from device in same zlib envelope as configJson state.
        private int _rpcNextId = 1000;
        private readonly object _rpcLock = new object();
        private readonly System.Collections.Generic.Dictionary<int, ManualResetEventSlim> _rpcWaiters
            = new System.Collections.Generic.Dictionary<int, ManualResetEventSlim>();
        private readonly System.Collections.Generic.Dictionary<int, byte[]> _rpcReplies
            = new System.Collections.Generic.Dictionary<int, byte[]>();
        private readonly SessionDataReassembler _session0aInbox = new();
        // Session 0x03 inbound: 12-byte envelope tile-server state parser.
        private readonly TileServerStateParser _tileServerParser = new();
        public TileServerState? TileServerState => _tileServerParser.LastState;

        /// <summary>
        /// True if the wheel's internal Display sub-device responded to identity probe.
        /// Use this to gate dashboard telemetry features in the UI — wheels without
        /// a display (e.g. CS V2.1 with RPM LEDs only) won't have this set.
        /// </summary>
        public bool DisplayDetected => _displayDetected;

        /// <summary>Display sub-device model name, e.g. "Display". Empty if not detected.</summary>
        public string DisplayModelName => _displayModelName;

        // Pre-cached frames (built once, reused every tick)
        private byte[] _cachedEnableFrame = null!;
        private byte[] _cachedModeFrame = null!;
        private byte[] _cachedSequenceFrame = null!;
        private byte[][] _cachedHeartbeatFrames = null!;

        // Session ports determined during port probing.
        // MgmtPort = first acked port (session 0x01, used for dashboard upload).
        // FlagByte = second acked port (session 0x02, used for tier definitions and fc:00 acks).
        private byte _mgmtPort;
        public byte FlagByte { get; set; } = 0x02;
        public bool SendTelemetryMode { get; set; } = true;
        public bool SendSequenceCounter { get; set; } = true;
        public bool TestMode { get; set; } = false;

        /// <summary>
        /// Tier definition protocol variant.
        /// 0 = URL-based subscription (send channel URLs, wheel resolves compression).
        /// 2 = Compact numeric, single batch (flag bytes, channel indices, compression codes, bit widths).
        /// </summary>
        public int ProtocolVersion { get; set; } = 2;

        /// <summary>Channel URLs reported by the wheel during session startup. Null until parsed.</summary>
        public System.Collections.Generic.IReadOnlyList<string>? WheelChannelCatalog => _wheelChannelCatalog;

        /// <summary>Raw .mzdash file content for upload to the wheel. Set by ApplyTelemetrySettings.</summary>
        public byte[]? MzdashContent { get; set; }

        /// <summary>Dashboard name (used for logging). Set by ApplyTelemetrySettings.</summary>
        public string MzdashName { get; set; } = "";

        /// <summary>Whether to upload the dashboard to the wheel on startup.</summary>
        public bool UploadDashboard { get; set; } = true;

        /// <summary>
        /// Resolver invoked per frame for channels with a non-empty
        /// <see cref="ChannelDefinition.SimHubProperty"/>. Set by MozaPlugin before
        /// assigning <see cref="Profile"/>; bound into each TelemetryFrameBuilder at
        /// profile-assign time so there is no per-frame lookup cost.
        /// </summary>
        public Func<string, double>? PropertyResolver { get; set; }

        public MultiStreamProfile? Profile
        {
            get => _profile;
            set
            {
                _profile = value;
                if (value == null || value.Tiers.Count == 0)
                {
                    _tiers = null;
                    _baseTickMs = 33;
                    return;
                }

                _baseTickMs = value.Tiers[0].PackageLevel;

                _tiers = new TierState[value.Tiers.Count];
                for (int i = 0; i < value.Tiers.Count; i++)
                {
                    var tier = value.Tiers[i];
                    int tickInterval = Math.Max(1, tier.PackageLevel / _baseTickMs);
                    _tiers[i] = new TierState
                    {
                        Builder = new TelemetryFrameBuilder(tier, PropertyResolver),
                        TickInterval = tickInterval,
                    };
                }
            }
        }
        private MultiStreamProfile? _profile;

        private volatile int _framesSent;
        public int FramesSent => _framesSent;
        /// <summary>True between Start() and Stop(). Exposed for diagnostics panel.</summary>
        public bool Enabled => _enabled;
        public byte[]? LastFrameSent { get; private set; }
        public TelemetryDiagnostics Diagnostics { get; } = new TelemetryDiagnostics();

        public TelemetrySender(MozaSerialConnection connection)
        {
            _connection = connection;
        }

        // Serializes Start() against concurrent callers. Without this, two
        // Start() work items on the ThreadPool (e.g. rapid Test-button double-
        // click routing through StartTelemetryIfReady's QueueUserWorkItem) each
        // run Stop() then `new Timer()`; the losing thread's timer gets
        // orphaned but keeps OnTimerElapsed subscribed, multiplying the tick
        // rate for the lifetime of the session.
        private int _startInProgress;

        public void Start()
        {
            if (Interlocked.CompareExchange(ref _startInProgress, 1, 0) != 0)
            {
                SimHub.Logging.Current.Warn("[Moza] Start() ignored — already starting");
                return;
            }
            try
            {
                StartInner();
            }
            finally
            {
                Interlocked.Exchange(ref _startInProgress, 0);
            }
        }

        private void StartInner()
        {
            Stop();
            _enabled = true;
            _tickCounter = 0;
            _modeCounter = 0;
            _framesSent = 0;
            _sequenceCounter = 0;
            _slowCounter = 0;
            _displayConfigPage = 0;
            _preambleComplete = false;
            lock (_incomingSessionBuffer) { _incomingSessionBuffer.Clear(); }
            _wheelChannelCatalog = null;
            _sessionAckSeq = 0;
            _preambleTickTarget = Math.Max(1, 1000 / _baseTickMs);

            BuildCachedFrames();

            // Subscribe early so we catch fc:00 acks during port probing AND preamble
            _connection.MessageReceived += OnMessageDuringPreamble;

            // Probe for available ports and open sessions.
            // This may run on a background thread (dispatched by StartTelemetryIfReady)
            // so the serial read thread stays free to deliver fc:00 ack responses.
            ProbeAndOpenSessions();

            // Bail out if Stop() was called while we were probing
            if (!_enabled) return;

            // Upload the dashboard file to the wheel on session 0x01 (mgmt port).
            // PitHouse does this on every connection — the wheel may require a fresh
            // upload before accepting tier definitions or telemetry frames.
            if (UploadDashboard && MzdashContent != null && _mgmtPort != 0)
                SendDashboardUpload();

            if (!_enabled) return;

            // Open session 0x03 (doc [moza-protocol.md:620-625]: host opens 0x03
            // 150-450ms after 0x01/0x02 on new firmware). Sim stubs this but real
            // hardware expects it. Fire-and-forget: we don't rely on its ack.
            // Tile-server data push deferred until after tier def — pushing
            // immediately after open collided with the wheel's session 0x09
            // configJson state burst (under Wine SerialPort R/W contention),
            // costing 6 of 7 state chunks.
            SendSessionOpen(0x03, 0x03);

            // Wait for wheel's pre-tier-def channel registration dump to quiet
            // down before transmitting our tier definition. Sim/real wheel pushes
            // channel URLs on session 0x02 between session-open and tier-def;
            // sending tier def mid-dump risks the wheel rejecting it.
            WaitForChannelCatalogQuiet(quietMs: 200, timeoutMs: 2000);

            // Send the tier definition message on the telemetry session.
            // This tells the wheel how to decode each flag byte's bit-packed data:
            // channel indices, compression codes, and bit widths per tier.
            // Without this, the wheel cannot interpret 7d:23 telemetry frames.
            SendTierDefinition();

            // Push empty-state tile-server blob on session 0x03 (matches
            // PitHouse behaviour: always pushed on connect, wheel never
            // echoes back — host→wheel only). 12-byte envelope
            // `FF 01 00 [comp_sz+4 LE] FF 00 [uncomp_sz BE24]` + zlib.
            // Deferred to here so session 0x09 state push has completed
            // arriving first.
            SendTileServerState();

            // Probe the Display sub-device inside the wheel.
            // Pithouse sends this at t=9.97 (after telemetry starts at t=9.88).
            // The response tells us if the wheel has a built-in display.
            // Non-blocking: responses are caught by OnMessageDuringPreamble.
            SendDisplayProbe();

            // Final check before creating the timer — if Stop() was called during
            // tier definition or display probe, don't create an orphaned timer.
            if (!_enabled) return;

            double intervalMs = _baseTickMs;
            _sendTimer = new Timer(intervalMs) { AutoReset = true };
            _sendTimer.Elapsed += OnTimerElapsed;
            _sendTimer.Start();
        }

        public void Stop()
        {
            _enabled = false;
            _connection.MessageReceived -= OnMessageDuringPreamble;
            if (_sendTimer != null)
            {
                _sendTimer.Stop();
                _sendTimer.Elapsed -= OnTimerElapsed;
                _sendTimer.Dispose();
                _sendTimer = null;
            }
            // Drop anything already queued or sitting in the OS write buffer —
            // otherwise frames keep flowing to the wheel for ~1.4 s after stop
            // (16 KB WriteBufferSize at 115200 baud).
            _connection.FlushPendingWrites();
            try { _ackReceived.Reset(); } catch (ObjectDisposedException) { }
            try { _mgmtResponseEvent.Reset(); } catch (ObjectDisposedException) { }
            try { _uploadSessionOpened.Reset(); } catch (ObjectDisposedException) { }
            try { _uploadSubMsg1Response.Reset(); } catch (ObjectDisposedException) { }
            try { _uploadSubMsg2Response.Reset(); } catch (ObjectDisposedException) { }
            try { _uploadEndReceived.Reset(); } catch (ObjectDisposedException) { }
            _sessions.Reset();
            _ftCandidateSessions.Clear();
            _uploadSession = 0x04;
            _uploadInboundSeq = 0;
            _uploadOutboundSeq = 0;
            _uploadInboundMsgCount = 0;
            _session09InboundSeq = 0;
            _session09OutboundSeq = 0;
            _session09ReplySent = false;
            // Reset so StartTelemetryIfReady() won't skip us on re-enable
            _framesSent = 0;
        }

        public void UpdateGameData(StatusDataBase? data)
        {
            _latestGameData = data;
        }

        /// <summary>
        /// Drop queued and in-flight writes on the serial connection. Exposed so
        /// the UI Test Stop button can halt wire traffic immediately even when
        /// the sender itself is left running (telemetry remains enabled).
        /// </summary>
        public void FlushPendingOutput() => _connection.FlushPendingWrites();

        /// <summary>
        /// Stop the tick timer without tearing down session state. Use for UI
        /// Test Stop so the wheel goes quiet immediately; call Resume to kick
        /// the timer back on. Full Stop() is the destructive teardown path.
        /// </summary>
        public void Pause() => _sendTimer?.Stop();

        /// <summary>Re-enable a paused tick timer. No-op if never started.</summary>
        public void Resume() => _sendTimer?.Start();

        // ── Port probing ────────────────────────────────────────────────────

        /// <summary>
        /// Open management + telemetry sessions PitHouse-style: directly open
        /// session 0x01 (mgmt) and 0x02 (telem) rather than probing 48 ports.
        ///
        /// Why this isn't a probe loop: PitHouse never probes. It opens 0x01/0x02
        /// after a power-cycle and relies on them. The old 48-port probe existed
        /// to co-exist with a concurrent PitHouse instance, but SimHub + PitHouse
        /// can't share the serial port anyway, and the burst of 96 close+open
        /// frames at 4ms pacing saturated the write queue for 4s. During that
        /// window the <see cref="MozaPlugin.PollStatus"/> watchdog (2s interval,
        /// 3-miss threshold) would fire mid-handshake and reset the wheel state,
        /// looping forever before telemetry could start.
        ///
        /// Pre-probe blanket close is retained: if the previous SimHub instance
        /// crashed without sending end markers, the wheel firmware still holds
        /// sessions 0x01/0x02 as open and a fresh SendSessionOpen would be
        /// ignored. Closing 0x01..0x10 first reclaims any stale slot.
        /// </summary>
        private void ProbeAndOpenSessions()
        {
            if (!_connection.IsConnected)
                return;

            const byte MgmtSession = 0x01;
            const byte TelemSession = 0x02;
            const int OpenAckTimeoutMs = 500;

            // Reclaim any sessions left open by a prior SimHub crash/kill.
            SimHub.Logging.Current.Info("[Moza] Closing any stale sessions (0x01..0x10)...");
            for (byte port = 1; port <= 0x10; port++)
            {
                if (!_connection.IsConnected) return;
                SendSessionClose(port);
            }
            // Brief settle so the wheel processes the closes before we re-open.
            System.Threading.Thread.Sleep(100);

            byte mgmtPort = TryOpenSession(MgmtSession, OpenAckTimeoutMs);
            if (!_enabled || !_connection.IsConnected) return;
            byte telemetryPort = TryOpenSession(TelemSession, OpenAckTimeoutMs);

            _mgmtPort = mgmtPort;

            if (telemetryPort != 0)
            {
                FlagByte = telemetryPort;
                SimHub.Logging.Current.Info(
                    $"[Moza] Sessions opened: mgmt=0x{mgmtPort:X2} telem=0x{telemetryPort:X2}");
            }
            else if (mgmtPort != 0)
            {
                FlagByte = mgmtPort;
                SimHub.Logging.Current.Warn(
                    $"[Moza] Telem session 0x{TelemSession:X2} did not ack, using mgmt 0x{mgmtPort:X2} for telemetry");
            }
            else
            {
                // No acks — proceed anyway using PitHouse defaults. Real wheels
                // may silently accept data on 0x02 even without an explicit ack.
                FlagByte = TelemSession;
                SimHub.Logging.Current.Warn(
                    "[Moza] No session acks received, proceeding with defaults mgmt=0x01 telem=0x02");
                _mgmtPort = MgmtSession;
            }
        }

        /// <summary>
        /// Send a SESSION_OPEN for the given session byte and wait up to
        /// <paramref name="timeoutMs"/> for a matching fc:00 ack. Returns the
        /// session byte on success, 0 on timeout.
        /// </summary>
        private byte TryOpenSession(byte session, int timeoutMs)
        {
            _ackReceived.Reset();
            _lastAckedSession = 0;

            SendSessionOpen(session, session);

            var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
            while (true)
            {
                int remaining = (int)(deadline - DateTime.UtcNow).TotalMilliseconds;
                if (remaining <= 0 || !_ackReceived.Wait(remaining))
                    return 0;

                if (_lastAckedSession == session)
                    return session;

                // Stale ack (different session) — discard and keep waiting.
                SimHub.Logging.Current.Debug(
                    $"[Moza] OpenSession 0x{session:X2}: ignoring stale ack for 0x{_lastAckedSession:X2}");
                _ackReceived.Reset();
                _lastAckedSession = 0;
            }
        }

        /// <summary>
        /// Wait for the wheel's pre-tier-def channel registration burst to stop
        /// arriving. Polls <see cref="_channelBufferLastActivityMs"/> — once the
        /// last activity is older than <paramref name="quietMs"/>, we assume the
        /// wheel is done pushing its channel URLs.
        /// </summary>
        private void WaitForChannelCatalogQuiet(int quietMs, int timeoutMs)
        {
            int deadline = Environment.TickCount + timeoutMs;
            while (Environment.TickCount < deadline)
            {
                if (!_enabled || !_connection.IsConnected) return;
                int lastAct = _channelBufferLastActivityMs;
                int idle = lastAct == 0 ? 0 : Environment.TickCount - lastAct;
                int bufCount;
                lock (_incomingSessionBuffer) bufCount = _incomingSessionBuffer.Count;
                if (bufCount > 0 && idle >= quietMs)
                    return;
                Thread.Sleep(20);
            }
        }

        /// <summary>
        /// Build a new <see cref="MultiStreamProfile"/> with only the channels
        /// whose <c>Url</c> appears in <paramref name="catalog"/>. Tiers that
        /// end up empty are dropped. URL match is case-insensitive and also
        /// accepts catalog entries matching the last path segment (the wheel
        /// sometimes advertises bare names where the profile uses a full URL).
        /// </summary>
        private static MultiStreamProfile FilterProfileToCatalog(
            MultiStreamProfile profile,
            System.Collections.Generic.IReadOnlyList<string> catalog)
        {
            var set = new System.Collections.Generic.HashSet<string>(
                StringComparer.OrdinalIgnoreCase);
            foreach (var entry in catalog)
            {
                if (string.IsNullOrEmpty(entry)) continue;
                set.Add(entry);
                int slash = entry.LastIndexOf('/');
                if (slash >= 0 && slash < entry.Length - 1)
                    set.Add(entry.Substring(slash + 1));
            }

            bool ChannelMatches(ChannelDefinition ch)
            {
                if (set.Contains(ch.Url)) return true;
                int slash = ch.Url.LastIndexOf('/');
                if (slash >= 0 && slash < ch.Url.Length - 1
                    && set.Contains(ch.Url.Substring(slash + 1))) return true;
                return false;
            }

            var result = new MultiStreamProfile
            {
                Name = profile.Name,
                PageCount = profile.PageCount,
            };
            foreach (var tier in profile.Tiers)
            {
                var kept = new System.Collections.Generic.List<ChannelDefinition>();
                foreach (var ch in tier.Channels)
                    if (ChannelMatches(ch)) kept.Add(ch);
                if (kept.Count == 0) continue;
                result.Tiers.Add(new DashboardProfile
                {
                    Name = tier.Name,
                    Channels = kept,
                    PackageLevel = tier.PackageLevel,
                    TotalBits = tier.TotalBits,
                    TotalBytes = tier.TotalBytes,
                });
            }
            // If filter removed everything, fall back to the original rather
            // than shipping an empty tier def (wheel would reject it anyway).
            return result.Tiers.Count == 0 ? profile : result;
        }

        /// <summary>
        /// Send the tier definition message on the telemetry session.
        /// This is the critical config data that tells the wheel firmware how to
        /// decode each flag byte's bit-packed telemetry data: which channels are
        /// in each tier, their compression codes, and bit widths.
        ///
        /// Pithouse sends this as 7c:00 data chunks (type=0x01) on session 0x02
        /// during the first ~1s after session open. Without it, the wheel silently
        /// ignores all 7d:23 telemetry frames.
        /// </summary>
        private void SendTierDefinition()
        {
            var profile = _profile;
            if (profile == null || profile.Tiers.Count == 0)
                return;
            if (!_connection.IsConnected)
                return;

            // Intersect profile channels with wheel's advertised catalog so we
            // don't reference channels the wheel can't decode. Older firmware
            // rejects the entire tier-def on unknown channels; 2025-11 drops
            // them silently but still wastes frame bits. No-op when catalog is
            // empty (parse failure or wheel didn't push registrations).
            var catalog = _wheelChannelCatalog;
            if (catalog != null && catalog.Count > 0)
            {
                var filtered = FilterProfileToCatalog(profile, catalog);
                int originalCh = 0, filteredCh = 0;
                foreach (var t in profile.Tiers) originalCh += t.Channels.Count;
                foreach (var t in filtered.Tiers) filteredCh += t.Channels.Count;
                if (filteredCh < originalCh)
                    SimHub.Logging.Current.Info(
                        $"[Moza] Tier def filtered to wheel catalog: " +
                        $"{filteredCh}/{originalCh} channels retained");
                profile = filtered;
            }

            int seq = 3; // Match Pithouse's starting seq for config data

            if (ProtocolVersion == 0)
            {
                // Version 0: URL-based subscription.
                // The sentinel (0xFF) and tag 0x03 (value=1) are inline in the message.
                // No separate tag 0x07/0x03 preamble needed.
                byte[] message = TierDefinitionBuilder.BuildV0UrlSubscription(profile);
                var frames = TierDefinitionBuilder.ChunkMessage(message, FlagByte, ref seq);

                int channelCount = 0;
                foreach (var t in profile.Tiers) channelCount += t.Channels.Count;
                SimHub.Logging.Current.Info(
                    $"[Moza] Sending v0 URL subscription: " +
                    $"{message.Length} bytes in {frames.Count} chunks " +
                    $"on session 0x{FlagByte:X2} ({channelCount} channels)");

                foreach (var frame in frames)
                    _connection.Send(frame);
            }
            else
            {
                // Version 2: compact numeric tier definitions.
                // Sub-message 1 preamble: tag 0x07 (version=2), tag 0x03 (value=0).
                byte[] preambleMsg = new byte[]
                {
                    0x07, 0x04, 0x00, 0x00, 0x00, 0x02, 0x00, 0x00, 0x00,
                    0x03, 0x00, 0x00, 0x00, 0x00
                };
                var preambleFrames = TierDefinitionBuilder.ChunkMessage(preambleMsg, FlagByte, ref seq);
                foreach (var frame in preambleFrames)
                    _connection.Send(frame);

                byte[] message = TierDefinitionBuilder.BuildTierDefinitionMessage(profile, 0x00);
                var frames = TierDefinitionBuilder.ChunkMessage(message, FlagByte, ref seq);

                SimHub.Logging.Current.Info(
                    $"[Moza] Sending v2 tier definition: flagBase=0x00, " +
                    $"preamble ({preambleFrames.Count} chunks)" +
                    $" + {message.Length} bytes in {frames.Count} chunks " +
                    $"on session 0x{FlagByte:X2} ({profile.Tiers.Count} tiers)");

                foreach (var frame in frames)
                    _connection.Send(frame);
            }
        }

        /// <summary>
        /// Probe the Display sub-device inside the wheel.
        /// Pithouse sends the same identity commands used for the main wheel
        /// (0x09, 0x04, 0x06, 0x02, 0x05) but via group 0x43 to route them
        /// through the SerialStream to the Display sub-module.
        ///
        /// Responses arrive asynchronously via OnMessageDuringPreamble:
        /// - 0x87 data=01 "Display" → model name (confirms display present)
        /// - 0x89 data=00:01 → presence check (1 sub-device)
        /// - 0x82 data=02 → product type
        /// </summary>

        /// <summary>
        /// Push an empty-state tile-server blob on session 0x03. Matches
        /// PitHouse behaviour observed in 5 captures — PitHouse sends this on
        /// every connect; wheel never pushes back (session 0x03 is host→wheel
        /// only). Envelope is the 12-byte variant (distinct from session
        /// 0x04/0x09 9-byte form). See § Session 0x03 tile-server envelope.
        /// </summary>
        private void SendTileServerState()
        {
            try
            {
                byte[] json = TileServerStateBuilder.BuildEmptyStateJson();
                byte[] payload = TileServerStateBuilder.BuildFullBlob(json);
                int seq = 1;
                var frames = TierDefinitionBuilder.ChunkMessage(payload, 0x03, ref seq);
                foreach (var frame in frames)
                    _connection.Send(frame);
                SimHub.Logging.Current.Info(
                    $"[Moza] Sent empty tile-server state on session 0x03: " +
                    $"{json.Length}B JSON → {payload.Length}B (12B env + zlib) → " +
                    $"{frames.Count} chunk(s)");
            }
            catch (Exception ex)
            {
                SimHub.Logging.Current.Debug($"[Moza] SendTileServerState failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Pick the file-transfer session number to upload on. Priority:
        /// <list type="number">
        ///   <item><see cref="UploadSessionOverride"/> if non-zero.</item>
        ///   <item>0x04 if the wheel device-initiated it (legacy behaviour, all
        ///   firmwares observed).</item>
        ///   <item>The first session in 0x04..0x0a the wheel device-initiated
        ///   (covers new firmware that may shift the file-transfer session).</item>
        ///   <item>0x04 fallback if no candidate seen yet — the upload waiter
        ///   will then either time out or proceed via host-initiated open.</item>
        /// </list>
        /// </summary>
        private byte ChooseUploadSession()
        {
            if (UploadSessionOverride != 0) return UploadSessionOverride;
            lock (_ftCandidateSessions)
            {
                if (_ftCandidateSessions.Contains((byte)0x04)) return 0x04;
                foreach (byte b in new byte[] { 0x05, 0x06, 0x07, 0x08, 0x09, 0x0a })
                    if (_ftCandidateSessions.Contains(b)) return b;
            }
            return 0x04;
        }

        /// <summary>
        /// Upload the .mzdash dashboard file via the file-transfer protocol.
        /// Wheel opens the upload session from its own side shortly after the
        /// host brings up mgmt + telemetry; we wait up to 2 s for that open,
        /// then send:
        ///
        ///   1. Sub-msg 1 (path registration) — wait for device echo (~6 chunks)
        ///   2. Sub-msg 2 (file content push) — wait for device ack (~6 chunks)
        ///   3. Type=0x00 end marker — wait for device end reply
        ///
        /// Sizing follows PitHouse's observed 64-byte max chunk size; CRC32 per
        /// chunk via <see cref="TierDefinitionBuilder.ChunkMessage"/>.
        ///
        /// The session number is dynamic. 2025-11 firmware uses 0x04; 2026-04
        /// firmware has been observed using 0x05 / 0x07 / 0x09 depending on
        /// what the host requests via 7c:23 46. Plugin selects via
        /// <see cref="ChooseUploadSession"/>.
        /// </summary>
        private void SendDashboardUpload()
        {
            var content = MzdashContent;
            if (content == null || content.Length == 0) return;
            if (!_connection.IsConnected) return;

            // Resolve the upload session before any logging / state reset, so
            // log lines reference the right port even if it changes mid-upload
            // (it shouldn't — once chosen it stays for this transfer).
            byte uploadSess = ChooseUploadSession();
            _uploadSession = uploadSess;

            // Wait for the device to open the chosen session. Real wheel opens
            // it ~40–400 ms after we open session 0x02 on a fresh connection.
            // On a mid-connection restart (dashboard change) the device may not
            // re-open — plugin's blanket close during handshake takes the
            // session down, but firmware/sim won't necessarily re-initiate.
            // Fall back to a host-initiated open so dashboard re-uploads work.
            if (!_uploadSessionOpened.Wait(500))
            {
                SimHub.Logging.Current.Info(
                    $"[Moza] Upload session 0x{uploadSess:X2} not opened by device within 500ms — host-opening for upload");
                SendSessionOpen(uploadSess, uploadSess);
                // Give the wheel a moment to process + ack before we start
                // pushing file-transfer chunks.
                if (!_uploadSessionOpened.Wait(500))
                {
                    // Even the host-initiated open didn't produce an observed
                    // open event. Proceed anyway — the wheel may silently accept
                    // data. The ack waiters below will surface any actual failure.
                    SimHub.Logging.Current.Info(
                        $"[Moza] Upload session 0x{uploadSess:X2} host-open had no confirmation; proceeding with upload");
                    _uploadSessionOpened.Set();
                }
            }

            // Skip-if-unchanged: if the wheel already reported this dashboard
            // as loaded (via session 0x09 state) and the MD5 matches, don't
            // re-upload. Saves ~1 s of handshake per reconnect.
            if (CanSkipUpload(content))
            {
                SimHub.Logging.Current.Info(
                    $"[Moza] Dashboard \"{MzdashName}\" already loaded on wheel (hash match) — skipping upload");
                return;
            }

            string dashboardName = !string.IsNullOrEmpty(MzdashName) ? MzdashName : "dashboard";
            uint token = DashboardUploader.PickToken();
            long tsMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            var upload = DashboardUploader.BuildUpload(content, dashboardName, token, tsMs, UploadWireFormat);

            SimHub.Logging.Current.Info(
                $"[Moza] Uploading dashboard \"{dashboardName}\" via session 0x{uploadSess:X2} " +
                $"(wire={UploadWireFormat}): " +
                $"raw={upload.UncompressedSize}B md5={upload.Md5Hex} token=0x{token:X8}");

            _uploadSubMsg1Response.Reset();
            _uploadSubMsg2Response.Reset();
            _uploadEndReceived.Reset();
            _uploadInboundMsgCount = 0;

            // Sub-msg 1: path registration.
            int seq1 = _uploadOutboundSeq + 1;
            var subMsg1Frames = TierDefinitionBuilder.ChunkMessage(
                upload.SubMsg1PathRegistration, uploadSess, ref seq1);
            foreach (var frame in subMsg1Frames)
            {
                if (!_enabled || !_connection.IsConnected) return;
                _connection.Send(frame);
            }
            _uploadOutboundSeq = seq1;

            // Wait for device's path echo (capture shows ~6 chunks, arrives within ~200ms).
            if (!_uploadSubMsg1Response.Wait(2000))
                SimHub.Logging.Current.Warn($"[Moza] Session 0x{uploadSess:X2} sub-msg 1 response timeout");

            // Sub-msg 2: file content push. May be split across multiple sub-msgs
            // for new-firmware uploads when the body exceeds 0xFFFF bytes (TODO:
            // true multi-sub-msg chunking; today this is single-element for both
            // formats — see FileTransferBuilder.BuildFileContentChunked).
            _uploadInboundMsgCount = 0; // reset so next threshold triggers sub-msg 2 event
            int seq2 = _uploadOutboundSeq + 1;
            for (int chunkIdx = 0; chunkIdx < upload.SubMsg2Chunks.Count; chunkIdx++)
            {
                var subMsg2 = upload.SubMsg2Chunks[chunkIdx];
                var subMsg2Frames = TierDefinitionBuilder.ChunkMessage(subMsg2, uploadSess, ref seq2);
                foreach (var frame in subMsg2Frames)
                {
                    if (!_enabled || !_connection.IsConnected) return;
                    _connection.Send(frame);
                }
            }
            _uploadOutboundSeq = seq2;

            if (!_uploadSubMsg2Response.Wait(3000))
                SimHub.Logging.Current.Warn($"[Moza] Session 0x{uploadSess:X2} sub-msg 2 response timeout");

            // End marker on the upload session.
            SendSessionEnd(uploadSess, (ushort)_uploadOutboundSeq);

            if (_uploadEndReceived.Wait(1000))
                SimHub.Logging.Current.Info($"[Moza] Dashboard upload complete (session 0x{uploadSess:X2} closed by device)");
            else
                SimHub.Logging.Current.Info("[Moza] Dashboard upload finished; device did not echo end marker within 1s");

            // Wheel's 2025-11 firmware fires a post-upload state refresh on
            // the upload session (updated directory listing) and session 0x09
            // (updated configJson state blob including the newly-uploaded
            // dashboard). Continue pumping so OnMessageDuringPreamble can ack
            // + consume those chunks before the preamble phase ends and the
            // handler detaches.
            int preRefreshCount = _uploadInboundMsgCount;
            Thread.Sleep(500);
            int refreshChunks = _uploadInboundMsgCount - preRefreshCount;
            if (refreshChunks > 0)
                SimHub.Logging.Current.Info(
                    $"[Moza] Session 0x{uploadSess:X2} post-upload state refresh: {refreshChunks} chunks");
        }

        /// <summary>
        /// Compare the active mzdash MD5 against the wheel's reported hash from
        /// its last session 0x09 state blob. Wheel stores hash as ASCII-hex of
        /// ASCII-hex of MD5 (observed: `33 63 31 64 ...` = ASCII of
        /// "3c1d..."). Returns true when the wheel already has this exact
        /// dashboard loaded in enableManager.
        /// </summary>
        private bool CanSkipUpload(byte[] content)
        {
            var state = _configJson.LastState;
            if (state == null || state.EnabledDashboards.Count == 0) return false;
            byte[] md5 = FileTransferBuilder.ComputeMd5(content);
            string md5Hex = FileTransferBuilder.Md5Hex(md5);
            string wireHash = AsciiHexOfAsciiHex(md5Hex);
            foreach (var entry in state.EnabledDashboards)
            {
                if (string.Equals(entry.Hash, wireHash, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            return false;
        }

        private static string AsciiHexOfAsciiHex(string ascii)
        {
            var sb = new System.Text.StringBuilder(ascii.Length * 2);
            foreach (var c in ascii) sb.Append(((byte)c).ToString("x2"));
            return sb.ToString();
        }

        /// <summary>
        /// Fire once per session: reply to the device's session 0x09 state blob
        /// with a <c>configJson()</c> canonical library list. Wheel uses this to
        /// refresh its <c>configJsonList</c> field, which PitHouse reads back
        /// from <see cref="WheelDashboardState.ConfigJsonList"/>. Sent as
        /// chunked 7c:00 data on session 0x09 with per-chunk CRC32.
        /// </summary>
        private void MaybeSendConfigJsonReply(WheelDashboardState state)
        {
            if (_session09ReplySent) return;
            if (CanonicalDashboardList == null || CanonicalDashboardList.Count == 0)
            {
                // Fall back to whatever the wheel currently reports — that
                // way the wheel's configJsonList survives at least one more
                // connect cycle unchanged. Skip if wheel sent nothing.
                if (state.ConfigJsonList == null || state.ConfigJsonList.Count == 0) return;
                CanonicalDashboardList = state.ConfigJsonList;
            }

            byte[] reply = ConfigJsonClient.BuildConfigJsonReply(CanonicalDashboardList);
            int seq = _session09OutboundSeq + 1;
            var frames = TierDefinitionBuilder.ChunkMessage(reply, 0x09, ref seq);
            foreach (var frame in frames)
            {
                if (!_enabled || !_connection.IsConnected) return;
                _connection.Send(frame);
            }
            _session09OutboundSeq = seq;
            _session09ReplySent = true;
            SimHub.Logging.Current.Info(
                $"[Moza] Sent configJson() reply on session 0x09: " +
                $"{CanonicalDashboardList.Count} dashboards, {frames.Count} chunks");
        }

        private int _session0aOutboundSeq;

        /// <summary>
        /// Send a host→wheel JSON RPC call on session 0x0a and optionally wait
        /// for the wheel's reply. Wire format matches configJson: 9-byte
        /// envelope ([flag=0x00][comp_size:u32 LE][uncomp_size:u32 LE]) wrapping
        /// a zlib stream of `{"<method>()": arg, "id": N}`. Reply has shape
        /// `{"id": N, "result": ...}` and is decoded by HandleRpcReply.
        /// Returns the decoded reply bytes on success, null on timeout.
        /// </summary>
        public byte[]? SendRpcCall(string method, object arg, int timeoutMs = 2000)
        {
            if (!_connection.IsConnected) return null;
            int id;
            var waiter = new ManualResetEventSlim(false);
            lock (_rpcLock)
            {
                id = _rpcNextId++;
                _rpcWaiters[id] = waiter;
            }
            byte[] envelope = BuildRpcCallEnvelope(method, arg, id);
            int seq = _session0aOutboundSeq + 1;
            var frames = TierDefinitionBuilder.ChunkMessage(envelope, 0x0a, ref seq);
            foreach (var frame in frames)
            {
                if (!_enabled || !_connection.IsConnected) { CleanupRpcWaiter(id); return null; }
                _connection.Send(frame);
            }
            _session0aOutboundSeq = seq;
            bool acked = waiter.Wait(timeoutMs);
            byte[]? reply = null;
            lock (_rpcLock)
            {
                _rpcReplies.TryGetValue(id, out reply);
                _rpcReplies.Remove(id);
                _rpcWaiters.Remove(id);
            }
            waiter.Dispose();
            return acked ? reply : null;
        }

        private void CleanupRpcWaiter(int id)
        {
            lock (_rpcLock)
            {
                if (_rpcWaiters.TryGetValue(id, out var w))
                {
                    _rpcWaiters.Remove(id);
                    try { w.Dispose(); } catch { }
                }
            }
        }

        private static byte[] BuildRpcCallEnvelope(string method, object arg, int id)
        {
            var root = new Newtonsoft.Json.Linq.JObject();
            root[$"{method}()"] = Newtonsoft.Json.Linq.JToken.FromObject(arg);
            root["id"] = id;
            string json = root.ToString(Newtonsoft.Json.Formatting.None);
            byte[] uncompressed = System.Text.Encoding.UTF8.GetBytes(json);
            byte[] compressed = ZlibCompress(uncompressed);
            var env = new byte[9 + compressed.Length];
            env[0] = 0x00;
            uint c = (uint)compressed.Length;
            env[1] = (byte)(c & 0xFF); env[2] = (byte)((c >> 8) & 0xFF);
            env[3] = (byte)((c >> 16) & 0xFF); env[4] = (byte)((c >> 24) & 0xFF);
            uint u = (uint)uncompressed.Length;
            env[5] = (byte)(u & 0xFF); env[6] = (byte)((u >> 8) & 0xFF);
            env[7] = (byte)((u >> 16) & 0xFF); env[8] = (byte)((u >> 24) & 0xFF);
            Array.Copy(compressed, 0, env, 9, compressed.Length);
            return env;
        }

        private static byte[] ZlibCompress(byte[] data)
        {
            using var output = new System.IO.MemoryStream();
            output.WriteByte(0x78);
            output.WriteByte(0x9C);
            using (var deflate = new System.IO.Compression.DeflateStream(
                output, System.IO.Compression.CompressionLevel.Optimal, leaveOpen: true))
                deflate.Write(data, 0, data.Length);
            uint a = 1, b = 0;
            for (int i = 0; i < data.Length; i++)
            {
                a = (a + data[i]) % 65521;
                b = (b + a) % 65521;
            }
            uint adler = (b << 16) | a;
            output.WriteByte((byte)((adler >> 24) & 0xFF));
            output.WriteByte((byte)((adler >> 16) & 0xFF));
            output.WriteByte((byte)((adler >> 8) & 0xFF));
            output.WriteByte((byte)(adler & 0xFF));
            return output.ToArray();
        }

        /// <summary>
        /// Decode a session 0x0a reply and route to the waiter by `id`. Method
        /// name is NOT inspected — replies route solely on integer id, which
        /// accommodates:
        /// - standard replies `{"id": N, "result": ...}`
        /// - method-keyed replies `{"<method>()": <value>, "id": N}`
        /// - empty-method replies `{"()": "", "id": N}` (observed on reset, 2026-04-21)
        /// Caller's `SendRpcCall` assigns any integer id; wheel echoes it back.
        /// </summary>
        private void HandleRpcReply(byte[] uncompressed)
        {
            try
            {
                string json = System.Text.Encoding.UTF8.GetString(uncompressed);
                var obj = Newtonsoft.Json.Linq.JObject.Parse(json);
                var idTok = obj["id"];
                if (idTok == null) return;
                int id = (int)idTok;
                lock (_rpcLock)
                {
                    _rpcReplies[id] = uncompressed;
                    if (_rpcWaiters.TryGetValue(id, out var waiter))
                        waiter.Set();
                }
            }
            catch (Exception ex)
            {
                SimHub.Logging.Current.Debug($"[Moza] RPC reply parse failed: {ex.Message}");
            }
        }

        private static int CountOccurrences(string haystack, string needle)
        {
            if (string.IsNullOrEmpty(haystack) || string.IsNullOrEmpty(needle)) return 0;
            int count = 0, idx = 0;
            while ((idx = haystack.IndexOf(needle, idx, StringComparison.Ordinal)) != -1)
            {
                count++;
                idx += needle.Length;
            }
            return count;
        }

        private void SendSessionEnd(byte session, ushort seq)
        {
            var end = new byte[]
            {
                MozaProtocol.MessageStart, 0x06,
                MozaProtocol.TelemetrySendGroup, MozaProtocol.DeviceWheel,
                0x7C, 0x00,
                session, 0x00,
                (byte)(seq & 0xFF), (byte)((seq >> 8) & 0xFF),
                0x00
            };
            end[end.Length - 1] = MozaProtocol.CalculateWireChecksum(end);
            _connection.Send(end);
        }

        private void SendDisplayProbe()
        {
            if (!_connection.IsConnected) return;

            // Heartbeat/ping first
            _connection.Send(BuildDisplayFrame(0x00));

            // Identity probe: 0x09 → 0x04 → 0x06 → 0x02 → 0x05
            _connection.Send(BuildDisplayFrame(0x09));
            _connection.Send(BuildDisplayFrameWithData(0x04, new byte[] { 0x00, 0x00, 0x00, 0x00 }));
            _connection.Send(BuildDisplayFrame(0x06));
            _connection.Send(BuildDisplayFrameWithData(0x02, new byte[] { 0x00 }));
            _connection.Send(BuildDisplayFrameWithData(0x05, new byte[] { 0x00, 0x00, 0x00, 0x00 }));

            // Version queries: 0x07, 0x0F, 0x11, 0x08, 0x10 (sub-device 1)
            _connection.Send(BuildDisplayFrameWithData(0x07, new byte[] { 0x01 }));
            _connection.Send(BuildDisplayFrameWithData(0x0F, new byte[] { 0x01 }));
            _connection.Send(BuildDisplayFrameWithData(0x11, new byte[] { 0x04 }));
            _connection.Send(BuildDisplayFrameWithData(0x08, new byte[] { 0x01 }));
            _connection.Send(BuildDisplayFrameWithData(0x10, new byte[] { 0x00 }));
        }

        private byte[] BuildDisplayFrame(byte cmd)
        {
            var frame = new byte[] { MozaProtocol.MessageStart, 0x01,
                MozaProtocol.TelemetrySendGroup, MozaProtocol.DeviceWheel,
                cmd, 0x00 };
            frame[5] = MozaProtocol.CalculateWireChecksum(frame);
            return frame;
        }

        private byte[] BuildDisplayFrameWithData(byte cmd, byte[] data)
        {
            var frame = new byte[4 + 1 + data.Length + 1]; // start+N+grp+dev + cmd + data + checksum
            frame[0] = MozaProtocol.MessageStart;
            frame[1] = (byte)(1 + data.Length); // N = cmd + data
            frame[2] = MozaProtocol.TelemetrySendGroup;
            frame[3] = MozaProtocol.DeviceWheel;
            frame[4] = cmd;
            Array.Copy(data, 0, frame, 5, data.Length);
            frame[frame.Length - 1] = MozaProtocol.CalculateWireChecksum(frame);
            return frame;
        }

        // ── Preamble message handling ───────────────────────────────────────

        /// <summary>
        /// Handle incoming messages during port probing and the ~1s preamble phase.
        /// Detects fc:00 session acks (for port probing) and acks incoming 7c:00
        /// channel data on the telemetry session.
        /// </summary>
        private void OnMessageDuringPreamble(byte[] data)
        {
            if (!_enabled)
                return;

            // data layout from MozaSerialConnection: [group, device, cmdPayload...]
            if (data.Length < 4)
                return;

            // Only process 0xC3 (response to 0x43) from device 0x71 (nibble-swapped 0x17)
            if (data[0] != 0xC3 || data[1] != 0x71)
                return;

            byte cmd1 = data[2];
            byte cmd2 = data[3];

            // fc:00 ack — signals a session open was accepted
            if (cmd1 == 0xFC && cmd2 == 0x00 && data.Length >= 5)
            {
                _lastAckedSession = data[4];
                _ackReceived.Set();
                return;
            }

            // 7c:00 data chunks — ack and buffer during preamble/upload
            if (cmd1 == 0x7C && cmd2 == 0x00 && data.Length >= 8)
            {
                byte session = data[4];
                byte type = data[5];

                // Device-initiated session open (type=0x81). Real wheel opens
                // 0x04/0x06/0x08/0x09/0x0A — mark as seen, ack the open, and
                // trigger any waiters that need that session up before sending.
                if (type == 0x81)
                {
                    int openSeq = data.Length >= 8 ? data[6] | (data[7] << 8) : 0;
                    var info = _sessions.GetOrCreate(session);
                    info.DeviceInitiated = true;
                    info.Port = (byte)(openSeq & 0xFF);
                    SendSessionAck(session, (ushort)openSeq);
                    // Track every device-init session in the file-transfer-
                    // eligible range so ChooseUploadSession() can pick. Once
                    // a candidate matches our chosen upload session, signal
                    // the upload-pump waiter.
                    if (session >= 0x04 && session <= 0x0a)
                    {
                        lock (_ftCandidateSessions) _ftCandidateSessions.Add(session);
                        if (session == _uploadSession || (UploadSessionOverride == 0 && session == 0x04))
                            _uploadSessionOpened.Set();
                    }
                    return;
                }

                if (type == 0x01)
                {
                    int seq = data[6] | (data[7] << 8);
                    byte[] chunkPayload = new byte[data.Length - 8];
                    Array.Copy(data, 8, chunkPayload, 0, chunkPayload.Length);

                    // Ack on the telemetry session
                    if (session == FlagByte)
                    {
                        if (seq > _sessionAckSeq)
                            _sessionAckSeq = seq;
                        SendSessionAck(FlagByte, (ushort)_sessionAckSeq);
                    }

                    // Ack on the management session (upload handshake)
                    if (session == _mgmtPort && _mgmtPort != 0)
                    {
                        if (seq > _mgmtAckSeq)
                            _mgmtAckSeq = seq;
                        SendSessionAck(_mgmtPort, (ushort)_mgmtAckSeq);
                        _mgmtResponseEvent.Set();
                    }

                    // File-transfer session: ack + count responses. Capture shows
                    // device sends sub-msg 1 echo (6 chunks) then sub-msg 2 ack
                    // (6 chunks) — simplest heuristic is to wait for a quiet period
                    // after some chunks, but sub-msg events fire once enough chunks
                    // arrive to assume the device replied. Session number is
                    // dynamic (see ChooseUploadSession).
                    if (session == _uploadSession)
                    {
                        SendSessionAck(session, (ushort)seq);
                        _uploadInboundSeq = seq;
                        _uploadInboundMsgCount++;
                        // After ~5 chunks on the upload session from the device,
                        // assume a sub-msg reply has fully arrived (capture shows
                        // 6 chunks per response). SendDashboardUpload resets the
                        // counter to 0 between sub-msg 1 and sub-msg 2, so both
                        // thresholds are 5.
                        if (_uploadInboundMsgCount >= 5 && !_uploadSubMsg1Response.IsSet)
                            _uploadSubMsg1Response.Set();
                        else if (_uploadInboundMsgCount >= 5 && !_uploadSubMsg2Response.IsSet)
                            _uploadSubMsg2Response.Set();

                        // 2025-11 firmware also pushes a zlib-compressed directory
                        // listing on the upload session (initial + post-upload
                        // refresh). Reassemble + decompress so the plugin can
                        // confirm the upload landed in the wheel's FS. Same
                        // 9-byte envelope as session 0x09 configJson state.
                        _uploadInbox.AddChunk(chunkPayload);
                        byte[]? dirBlob = _uploadInbox.TryDecompress();
                        if (dirBlob != null)
                        {
                            _uploadInbox.Clear();
                            _uploadDirListingRefreshed = true;
                            try
                            {
                                string json = System.Text.Encoding.UTF8.GetString(dirBlob);
                                SimHub.Logging.Current.Info(
                                    $"[Moza] Session 0x{session:X2} dir listing: {dirBlob.Length} bytes, " +
                                    $"children≈{CountOccurrences(json, "\"name\"")}");
                            }
                            catch (Exception ex)
                            {
                                SimHub.Logging.Current.Debug(
                                    $"[Moza] Session 0x{session:X2} dir listing decode: {ex.Message}");
                            }
                        }
                    }

                    // Session 0x09 configJson state blob. Parsing may return the
                    // freshly-decoded state when the reassembler completes a
                    // full blob; that's our trigger to send the host→device
                    // configJson() reply advertising our dashboard library.
                    if (session == 0x09)
                    {
                        SendSessionAck(0x09, (ushort)seq);
                        _session09InboundSeq = seq;
                        try
                        {
                            SimHub.Logging.Current.Info(
                                $"[Moza] session 0x09 inbound chunk: seq={seq} payload={chunkPayload.Length}B " +
                                $"first8={BitConverter.ToString(chunkPayload, 0, Math.Min(8, chunkPayload.Length))}");
                        }
                        catch { }
                        var state = _configJson.OnChunk(chunkPayload);
                        if (state != null) MaybeSendConfigJsonReply(state);
                    }

                    // Session 0x0a: RPC reply channel. Host sends `{method(): arg, id}`
                    // calls, wheel replies with `{id, result}` in the same zlib
                    // envelope. Reassemble and hand off to RPC waiters.
                    if (session == 0x0a)
                    {
                        SendSessionAck(0x0a, (ushort)seq);
                        _session0aInbox.AddChunk(chunkPayload);
                        byte[]? replyBlob = _session0aInbox.TryDecompress();
                        if (replyBlob != null)
                        {
                            _session0aInbox.Clear();
                            HandleRpcReply(replyBlob);
                        }
                    }

                    // Session 0x03: tile-server state channel. Host opens on
                    // connect; wheel may push tile-server state blobs using a
                    // 12-byte envelope (distinct from 0x04/0x09's 9-byte form):
                    //   FF 01 00 [comp_size+4 u32 LE] FF 00 [uncomp_size u24 BE]
                    // Feed through TileServerStateParser; ack to keep the
                    // session alive regardless.
                    if (session == 0x03)
                    {
                        SendSessionAck(0x03, (ushort)seq);
                        // Strip CRC from tail (last 4 bytes) before handing to parser
                        if (chunkPayload.Length >= 4)
                        {
                            byte[] net = new byte[chunkPayload.Length - 4];
                            Array.Copy(chunkPayload, 0, net, 0, net.Length);
                            var tile = _tileServerParser.OnChunk(net);
                            if (tile != null)
                            {
                                try
                                {
                                    SimHub.Logging.Current.Info(
                                        $"[Moza] Tile-server state received: root='{tile.Root}' " +
                                        $"version={tile.Version} games={tile.Games.Count} " +
                                        $"any_populated={tile.AnyPopulated}");
                                }
                                catch { /* logging optional */ }
                            }
                        }
                    }

                    // Buffer the chunk payload (strip CRC) for channel catalog parsing.
                    if (session == FlagByte && data.Length > 12 && !_preambleComplete)
                    {
                        byte[] raw = new byte[data.Length - 8];
                        Array.Copy(data, 8, raw, 0, raw.Length);
                        if (raw.Length >= 5)
                        {
                            int netLen = raw.Length - 4;
                            lock (_incomingSessionBuffer)
                            {
                                for (int k = 0; k < netLen; k++)
                                    _incomingSessionBuffer.Add(raw[k]);
                            }
                            _channelBufferLastActivityMs = Environment.TickCount;
                        }
                    }
                }

                // Type 0x00 = end marker
                if (type == 0x00)
                {
                    if (session == _mgmtPort) _mgmtResponseEvent.Set();
                    if (session == _uploadSession) _uploadEndReceived.Set();
                }

                return;
            }

            // Display sub-device responses (identity probe answers)
            // cmd byte is data[2], which is the response group byte (request | 0x80)
            // 0x87 = model name response (to 0x07 query)
            if (data[2] == 0x87 && data.Length >= 5 && data[3] == 0x01)
            {
                // data[3] = sub-device index (0x01), data[4..] = null-terminated ASCII name
                int nameLen = 0;
                for (int k = 4; k < data.Length && data[k] != 0; k++)
                    nameLen++;
                if (nameLen > 0)
                {
                    _displayModelName = System.Text.Encoding.ASCII.GetString(data, 4, nameLen);
                    _displayDetected = true;
                    SimHub.Logging.Current.Info($"[Moza] Display sub-device detected: \"{_displayModelName}\"");
                }
            }
        }

        /// <summary>
        /// Parse the wheel's channel catalog from the buffered incoming 7c:00 session data.
        /// The wheel sends tag 0x04 entries with channel URLs during the preamble.
        /// </summary>
        private void ParseWheelChannelCatalog()
        {
            byte[] buffer;
            lock (_incomingSessionBuffer)
            {
                if (_incomingSessionBuffer.Count == 0) return;
                buffer = _incomingSessionBuffer.ToArray();
            }

            var channels = new System.Collections.Generic.List<string>();
            int i = 0;
            while (i < buffer.Length)
            {
                byte tag = buffer[i];
                if (tag == 0xFF)
                {
                    i++;
                    continue;
                }
                if (i + 5 > buffer.Length) break;
                uint param = (uint)(buffer[i + 1] | (buffer[i + 2] << 8) |
                             (buffer[i + 3] << 16) | (buffer[i + 4] << 24));

                if (tag == 0x04 && i + 5 + (int)param <= buffer.Length && param > 1 && param < 200)
                {
                    // Channel URL: [ch_index:u8] [url:ASCII]
                    int urlLen = (int)param - 1;
                    string url = System.Text.Encoding.ASCII.GetString(buffer, i + 6, urlLen);
                    channels.Add(url);
                    i += 5 + (int)param;
                }
                else if (tag == 0x06)
                {
                    break; // end marker
                }
                else if (tag == 0x03 && param <= 8)
                {
                    i += 5 + (int)param;
                }
                else
                {
                    // Unknown tag — skip by trying param as size
                    if (param < 200)
                        i += 5 + (int)param;
                    else
                        break;
                }
            }

            if (channels.Count > 0)
            {
                _wheelChannelCatalog = channels;
                SimHub.Logging.Current.Info(
                    $"[Moza] Wheel channel catalog ({channels.Count}): {string.Join(", ", channels)}");
            }
        }

        // ── Timer loop ──────────────────────────────────────────────────────

        // Re-entry guard. System.Timers.Timer fires Elapsed on the ThreadPool,
        // so a handler that overruns its interval gets concurrent invocations.
        // Without this, _tickCounter/_slowCounter/_modeCounter all race and
        // non-coalesced one-shot frames (heartbeat, display_cfg) fire 2–3× the
        // intended rate. Stream-lane traffic is coalesced so it's immune, but
        // the counter races still skew scheduling. Drop overlapping ticks —
        // the missed tick's data is re-covered by the next tick's fresh
        // snapshot via the latest-wins stream slots.
        private int _tickInProgress;

        private void OnTimerElapsed(object sender, ElapsedEventArgs e)
        {
            if (Interlocked.CompareExchange(ref _tickInProgress, 1, 0) != 0)
                return;
            try
            {
                OnTimerElapsedInner();
            }
            finally
            {
                Interlocked.Exchange(ref _tickInProgress, 0);
            }
        }

        private void OnTimerElapsedInner()
        {
            if (!_enabled || !_connection.IsConnected)
                return;

            var tiers = _tiers;
            if (tiers == null || tiers.Length == 0)
                return;

            try
            {
                // Preamble phase: ~1 second of session ack processing + heartbeats.
                // No telemetry, enable, or channel config until preamble completes.
                if (!_preambleComplete)
                {
                    _tickCounter++;

                    int slowInterval = Math.Max(1, 1000 / _baseTickMs);
                    if (_tickCounter % slowInterval == 0)
                        SendHeartbeat();

                    if (_tickCounter >= _preambleTickTarget)
                    {
                        _preambleComplete = true;
                        // Handler stays subscribed: sessions 0x04/0x09/0x0a keep
                        // receiving data post-preamble (dir refresh, configJson
                        // updates, RPC replies). Detaching here broke RPC round
                        // trips that fire after the 1s preamble window.
                        // Channel-catalog buffer is guarded by !_preambleComplete
                        // so it stops accumulating automatically.

                        // Parse the wheel's channel catalog from buffered session data
                        ParseWheelChannelCatalog();

                        // Version 0: re-send URL subscription (Pithouse double-sends)
                        if (ProtocolVersion == 0)
                            SendTierDefinition();

                        // Channel config burst (matches Pithouse t=9.8-9.9)
                        SendChannelConfig();

                        _tickCounter = 0;
                        _modeCounter = 0;
                        _slowCounter = 0;
                    }
                    return;
                }

                // Active phase: telemetry + enable + periodic streams
                GameDataSnapshot snapshot = TestMode
                    ? default
                    : GameDataSnapshot.FromStatusData(_latestGameData);

                for (int i = 0; i < tiers.Length; i++)
                {
                    var tier = tiers[i];
                    if (_tickCounter % tier.TickInterval != 0)
                        continue;

                    byte flagByte = (byte)i;
                    byte[] frame = TestMode
                        ? tier.Builder.BuildTestFrame(flagByte)
                        : tier.Builder.BuildFrameFromSnapshot(snapshot, flagByte);
                    // Latest-wins per tier: if the last frame for this tier is still
                    // queued (e.g. write thread stalled under Wine syscall overhead),
                    // overwrite it so the wheel gets the freshest snapshot instead
                    // of a growing backlog.
                    if (i < 8)
                        _connection.SendStream((StreamKind)((int)StreamKind.TierDash0 + i), frame);
                    else
                        _connection.Send(frame);

                    if (i == 0)
                    {
                        LastFrameSent = frame;
                        _framesSent++;
                        Diagnostics.RecordFrame(frame);
                    }
                }

                _connection.SendStream(StreamKind.Enable, _cachedEnableFrame);

                if (SendSequenceCounter)
                    _connection.SendStream(StreamKind.Sequence, BuildSequenceCounterFrame());

                _tickCounter++;

                if (SendTelemetryMode && (_modeCounter++ % 10 == 0))
                    _connection.SendStream(StreamKind.Mode, _cachedModeFrame);

                int slow = Math.Max(1, 1000 / _baseTickMs);
                if (_slowCounter++ % slow == 0)
                {
                    SendHeartbeat();
                    SendDashKeepalive();
                    SendDisplayConfig();
                    SendStatusPush();
                }
            }
            catch (Exception ex)
            {
                SimHub.Logging.Current.Warn($"[Moza] Telemetry send error: {ex.Message}");
            }
        }

        // ── Session management ──────────────────────────────────────────────

        /// <summary>
        /// Send a type=0x00 end-marker on the given session. Used to reclaim sessions
        /// left open after a previous SimHub crash/kill, where End() did not run.
        /// If the session is already closed, the wheel silently ignores this frame.
        /// </summary>
        private void SendSessionClose(byte session)
        {
            // Length byte is the payload count (cmd + data, not incl. group/dev/cksum).
            // Payload is 6 bytes: 7C 00 <session> 00 <ack_lo> <ack_hi>. Must match
            // len=6 — a shorter frame with len=6 caused the wheel/sim to over-read
            // and corrupt the next frame in the stream, breaking the read loop.
            var frame = new byte[]
            {
                MozaProtocol.MessageStart, 0x06,
                MozaProtocol.TelemetrySendGroup, MozaProtocol.DeviceWheel,
                0x7C, 0x00,
                session, 0x00,          // type=0x00 (end marker)
                0x00, 0x00,             // ack_seq = 0 (LE)
                0x00                    // checksum placeholder
            };
            frame[frame.Length - 1] = MozaProtocol.CalculateWireChecksum(frame);
            _connection.Send(frame);
        }

        private void SendSessionOpen(byte session, byte port)
        {
            var frame = new byte[]
            {
                MozaProtocol.MessageStart, 0x0A,
                MozaProtocol.TelemetrySendGroup, MozaProtocol.DeviceWheel,
                0x7C, 0x00,
                session, 0x81,          // session byte + type (channel open)
                port, 0x00,             // seq = port (LE)
                port, 0x00,             // session_id = port (LE)
                0xFD, 0x02,             // receive_window = 765 (LE)
                0x00                    // checksum placeholder
            };
            frame[14] = MozaProtocol.CalculateWireChecksum(frame);
            _connection.Send(frame);
        }

        private void SendSessionAck(byte session, ushort ackSeq)
        {
            var frame = new byte[]
            {
                MozaProtocol.MessageStart, 0x05,
                MozaProtocol.TelemetrySendGroup, MozaProtocol.DeviceWheel,
                0xFC, 0x00,
                session,
                (byte)(ackSeq & 0xFF),
                (byte)(ackSeq >> 8),
                0x00
            };
            frame[9] = MozaProtocol.CalculateWireChecksum(frame);
            _connection.Send(frame);
        }

        // ── Channel configuration ───────────────────────────────────────────

        private void SendChannelConfig()
        {
            if (!_connection.IsConnected)
                return;

            var profile = _profile;
            if (profile == null || profile.Tiers.Count == 0)
                return;

            for (int page = 0; page <= 1; page++)
            {
                for (byte cc = 2; cc <= 5; cc++)
                    _connection.Send(BuildChannelEnableFrame((byte)page, cc));
            }

            // 28:00 = WheelGetCfg_GetMultiFunctionSwitch — query active dashboard mode
            // 28:01 = WheelGetCfg_GetMultiFunctionNum — query active page number
            // (rs21_parameter.db [64,40,0/1]). The wheel retains the last loaded
            // dashboard across disconnections; Pithouse reads the current state before
            // setting 28:02 (telemetry channel mode: 01=multi-channel, 00=RPM only).
            _connection.Send(BuildGroup40Frame3(0x28, 0x00, 0x00));
            _connection.Send(BuildGroup40Frame3(0x28, 0x01, 0x00));
            _connection.Send(BuildGroup40Frame(0x09, 0x00));
            _connection.Send(_cachedModeFrame);
        }

        private byte[] BuildChannelEnableFrame(byte page, byte channelIndex)
        {
            var frame = new System.Collections.Generic.List<byte>
            {
                MozaProtocol.MessageStart, 5,
                MozaProtocol.TelemetryModeGroup, MozaProtocol.DeviceWheel,
                0x1E, page,
                channelIndex, 0x00, 0x00,
            };
            frame.Add(MozaProtocol.CalculateWireChecksum(frame.ToArray()));
            return frame.ToArray();
        }

        private byte[] BuildGroup40Frame(byte cmd1, byte cmd2)
        {
            var frame = new System.Collections.Generic.List<byte>
            {
                MozaProtocol.MessageStart, 2,
                MozaProtocol.TelemetryModeGroup, MozaProtocol.DeviceWheel,
                cmd1, cmd2,
            };
            frame.Add(MozaProtocol.CalculateWireChecksum(frame.ToArray()));
            return frame.ToArray();
        }

        private byte[] BuildGroup40Frame3(byte cmd1, byte cmd2, byte cmd3)
        {
            var frame = new System.Collections.Generic.List<byte>
            {
                MozaProtocol.MessageStart, 3,
                MozaProtocol.TelemetryModeGroup, MozaProtocol.DeviceWheel,
                cmd1, cmd2, cmd3,
            };
            frame.Add(MozaProtocol.CalculateWireChecksum(frame.ToArray()));
            return frame.ToArray();
        }

        // ── Cached frame construction ───────────────────────────────────────

        private void BuildCachedFrames()
        {
            _cachedModeFrame = BuildStaticFrame(new byte[] {
                MozaProtocol.MessageStart, 4,
                MozaProtocol.TelemetryModeGroup, MozaProtocol.DeviceWheel,
                0x28, 0x02, 0x01, 0x00 });

            _cachedEnableFrame = BuildStaticFrame(new byte[] {
                MozaProtocol.MessageStart, 6,
                MozaProtocol.BaseSendTelemetry, MozaProtocol.DeviceWheel,
                0xFD, 0xDE, 0x00, 0x00, 0x00, 0x00 });

            _cachedSequenceFrame = BuildStaticFrame(new byte[] {
                MozaProtocol.MessageStart, 6,
                0x2D, MozaProtocol.DeviceBase,
                0xF5, 0x31, 0x00, 0x00, 0x00, 0x00 });

            _cachedHeartbeatFrames = new byte[13][];
            for (int i = 0; i < 13; i++)
            {
                byte dev = (byte)(18 + i);
                var frame = new byte[] { MozaProtocol.MessageStart, 0x00, 0x00, dev, 0x00 };
                frame[4] = MozaProtocol.CalculateWireChecksum(frame);
                _cachedHeartbeatFrames[i] = frame;
            }
        }

        private static byte[] BuildStaticFrame(byte[] body)
        {
            var frame = new byte[body.Length + 1];
            Array.Copy(body, 0, frame, 0, body.Length);
            frame[frame.Length - 1] = MozaProtocol.CalculateWireChecksum(body);
            return frame;
        }

        private byte[] BuildSequenceCounterFrame()
        {
            byte seq = _sequenceCounter++;
            _cachedSequenceFrame[9] = seq;
            _cachedSequenceFrame[10] = MozaProtocol.CalculateWireChecksum(
                _cachedSequenceFrame, _cachedSequenceFrame.Length - 1);
            // Return a copy: the write queue holds a reference until the write thread
            // drains it, and we mutate _cachedSequenceFrame on the next tick.
            var copy = new byte[_cachedSequenceFrame.Length];
            Array.Copy(_cachedSequenceFrame, copy, copy.Length);
            return copy;
        }

        // ── Periodic streams ────────────────────────────────────────────────

        public volatile int DetectedDeviceMask;

        private void SendHeartbeat()
        {
            int mask = DetectedDeviceMask;
            for (int i = 0; i < _cachedHeartbeatFrames.Length; i++)
            {
                if (mask == 0 || (mask & (1 << i)) != 0)
                    _connection.Send(_cachedHeartbeatFrames[i]);
            }
        }

        private void SendDashKeepalive()
        {
            // TelemetryServer periodic connection ping (group 0x43, N=1, data=0x00).
            // Pithouse sends to 0x14 (dash), 0x15, and 0x17 (wheel) every ~1.1s.
            // Distinct from group 0x00 heartbeats and SerialStream fc:00 acks.
            // Unclear whether the wheel requires this for telemetry to flow, but
            // Pithouse sends it consistently (~15× per session).
            foreach (byte dev in new byte[] { MozaProtocol.DeviceDash, 0x15, MozaProtocol.DeviceWheel })
            {
                var frame = new byte[] { MozaProtocol.MessageStart, 0x01, MozaProtocol.TelemetrySendGroup, dev, 0x00, 0x00 };
                frame[5] = MozaProtocol.CalculateWireChecksum(frame);
                _connection.Send(frame);
            }
        }

        private void SendDisplayConfig()
        {
            int pageCount = _profile?.PageCount ?? 1;
            int page = _displayConfigPage % pageCount;
            _displayConfigPage++;

            byte b2 = (byte)(0x05 + 2 * page);
            byte b4 = (byte)(0x03 + 2 * page);
            byte z  = (byte)(0x06 + 2 * page);

            var configFrame = new byte[] { MozaProtocol.MessageStart, 0x0A, MozaProtocol.TelemetrySendGroup,
                MozaProtocol.DeviceWheel, 0x7C, 0x27, 0x0F, 0x80, b2, 0x00, b4, 0x00, 0xFE, 0x01, 0x00 };
            configFrame[14] = MozaProtocol.CalculateWireChecksum(configFrame);
            _connection.Send(configFrame);

            // 7C:23 dashboard-activate: tells the wheel which dashboard pages are
            // active. PitHouse sends one per page interleaved with 7C:27 at ~1 Hz.
            byte ab2 = (byte)(0x07 + 2 * page);
            byte ab4 = (byte)(0x05 + 2 * page);
            var activateFrame = new byte[] { MozaProtocol.MessageStart, 0x0A, MozaProtocol.TelemetrySendGroup,
                MozaProtocol.DeviceWheel, 0x7C, 0x23, 0x46, 0x80, ab2, 0x00, ab4, 0x00, 0xFE, 0x01, 0x00 };
            activateFrame[14] = MozaProtocol.CalculateWireChecksum(activateFrame);
            _connection.Send(activateFrame);

            var configFrame2 = new byte[] { MozaProtocol.MessageStart, 0x06, MozaProtocol.TelemetrySendGroup,
                MozaProtocol.DeviceWheel, 0x7C, 0x27, 0x0F, 0x00, z, 0x00, 0x00 };
            configFrame2[10] = MozaProtocol.CalculateWireChecksum(configFrame2);
            _connection.Send(configFrame2);
        }

        private void SendStatusPush()
        {
            // Pithouse's fc:00 frames are purely reactive session acks — there is no
            // separate "active-phase status sender." The ack_seq tracks the highest
            // sequence received on this session. Sending periodically with the current
            // ack_seq is harmless (just re-acks the same point if no new data arrived).
            SendSessionAck(FlagByte, (ushort)_sessionAckSeq);
        }

        public void Dispose()
        {
            Stop();
            _ackReceived.Dispose();
            _mgmtResponseEvent.Dispose();
        }

        private class TierState
        {
            public TelemetryFrameBuilder Builder = null!;
            public int TickInterval;
        }
    }
}
