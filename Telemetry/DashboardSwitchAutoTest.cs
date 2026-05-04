using System;
using System.Collections.Generic;
using System.Linq;

namespace MozaPlugin.Telemetry
{
    /// <summary>
    /// Single-switch auto test harness. Flow:
    ///   1. Wait for initial subscription to settle.
    ///   2. Enable TestMode for <see cref="PreSwitchTestMs"/> — captures the
    ///      starting dashboard's wire-level test pattern as baseline.
    ///   3. Determine target slot: opposite of the persisted
    ///      <see cref="MozaPluginSettings.AutoTestLastSlot"/>. Defaults to
    ///      slot 1 (alphabetical second dash) if no persisted state.
    ///   4. SendDashboardSwitch + wait for renegotiate.
    ///   5. TestMode for <see cref="PostSwitchTestMs"/> on the new dashboard.
    ///   6. Persist the new slot, finish.
    ///
    /// On each launch the test alternates direction so debugging captures
    /// cover both A→B and B→A switch behavior across runs.
    /// Triggered by <see cref="MozaPluginSettings.EnableAutoTestOnConnect"/>.
    /// </summary>
    internal sealed class DashboardSwitchAutoTest
    {
        private enum State
        {
            Idle, PreSwitchTest, SwitchPending, WaitRenegotiate,
            PostSwitchTest, Done,
        }

        private readonly TelemetrySender _sender;
        private State _state = State.Idle;
        private int _elapsedMs;
        private int _targetSlot = -1;
        private int _startSlot = -1;
        private int _prevSubscriptionGen;
        private int _framesAtPhaseStart;
        private IReadOnlyList<string>? _dashList;

        private const int IdleTimeoutMs = 30000;
        private const int PreSwitchTestMs = 6000;
        private const int RenegotiateTimeoutMs = 10000;
        private const int PostSwitchTestMs = 6000;

        public DashboardSwitchAutoTest(TelemetrySender sender)
        {
            _sender = sender;
        }

        public void Tick(int tickMs)
        {
            _elapsedMs += tickMs;
            switch (_state)
            {
                case State.Idle: TickIdle(); break;
                case State.PreSwitchTest: TickPreSwitchTest(); break;
                case State.SwitchPending: TickSwitchPending(); break;
                case State.WaitRenegotiate: TickWaitRenegotiate(); break;
                case State.PostSwitchTest: TickPostSwitchTest(); break;
                case State.Done: break;
            }
        }

        private void TickIdle()
        {
            int gen = _sender.SubscriptionGen;
            if (gen == 0) return; // wait for first subscription

            // Resolve dash list from wheel state, fall back to cache sorted.
            _dashList = _sender.WheelState?.ConfigJsonList;
            if (_dashList == null || _dashList.Count < 2)
            {
                var cache = _sender.DashCache;
                if (cache != null)
                {
                    var sorted = cache.CachedNames
                        .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
                        .ToList();
                    if (sorted.Count >= 2)
                        _dashList = sorted;
                }
            }
            if (_dashList == null || _dashList.Count < 2)
            {
                if (_elapsedMs >= IdleTimeoutMs)
                {
                    MozaLog.Debug("[Moza] AUTO-TEST: <2 dashboards after 30s, skipping");
                    _state = State.Done;
                }
                return;
            }

            // Pick target slot opposite of last run's slot. First run picks 1.
            int lastSlot = MozaPlugin.Instance?.Settings?.AutoTestLastSlot ?? -1;
            _targetSlot = lastSlot == 0 ? 1 : 0;
            if (_targetSlot >= _dashList.Count) _targetSlot = 0;

            // Resolve current slot from active profile name (best effort).
            string currentName = _sender.ActiveProfileName ?? "";
            _startSlot = -1;
            for (int i = 0; i < _dashList.Count; i++)
            {
                if (string.Equals(_dashList[i], currentName,
                    StringComparison.OrdinalIgnoreCase))
                {
                    _startSlot = i;
                    break;
                }
            }

            // If we're already on the target, flip to the other.
            if (_startSlot == _targetSlot)
                _targetSlot = (_targetSlot + 1) % _dashList.Count;

            _prevSubscriptionGen = gen;
            _framesAtPhaseStart = _sender.FramesSent;
            _sender.TestMode = true;
            _elapsedMs = 0;

            string startName = _startSlot >= 0 && _startSlot < _dashList.Count
                ? _dashList[_startSlot] : currentName;
            string targetName = _dashList[_targetSlot];
            Transition(State.PreSwitchTest,
                $"start=\"{startName}\"(slot={_startSlot}) " +
                $"target=\"{targetName}\"(slot={_targetSlot}) " +
                $"subGen={gen}");
        }

        private void TickPreSwitchTest()
        {
            if (_elapsedMs < PreSwitchTestMs) return;

            int frames = _sender.FramesSent - _framesAtPhaseStart;
            string startName = _startSlot >= 0 && _dashList != null && _startSlot < _dashList.Count
                ? _dashList[_startSlot] : "?";
            MozaLog.Debug(
                $"[Moza] AUTO-TEST: pre-switch test done dash=\"{startName}\" " +
                $"frames={frames} {(frames > 0 ? "PASS" : "FAIL")}");

            _elapsedMs = 0;
            _state = State.SwitchPending;
        }

        private void TickSwitchPending()
        {
            if (_dashList == null) { _state = State.Done; return; }
            string targetName = _dashList[_targetSlot];

            var plugin = MozaPlugin.Instance;
            if (plugin != null)
            {
                plugin.Settings.TelemetryProfileName = targetName;
                plugin.Settings.TelemetryMzdashPath = "";
                plugin.ApplyTelemetrySettings();
            }

            // Capture gen BEFORE the switch — SendDashboardSwitch now emits
            // tier-def synchronously and bumps SubscriptionGen in the same
            // call, so reading gen after the call would skip the increment.
            _prevSubscriptionGen = _sender.SubscriptionGen;
            _sender.SendDashboardSwitch((uint)_targetSlot);
            _elapsedMs = 0;
            Transition(State.WaitRenegotiate,
                $"target=\"{targetName}\"(slot={_targetSlot})");
        }

        private void TickWaitRenegotiate()
        {
            int gen = _sender.SubscriptionGen;
            if (gen != _prevSubscriptionGen)
            {
                _prevSubscriptionGen = gen;
                _framesAtPhaseStart = _sender.FramesSent;
                _elapsedMs = 0;
                string targetName = _dashList?[_targetSlot] ?? "?";
                Transition(State.PostSwitchTest,
                    $"target=\"{targetName}\"(slot={_targetSlot}) " +
                    $"subGen={gen} " +
                    $"tiers={_sender.ActiveTierCount} " +
                    $"catalog={_sender.CatalogChannelCount}");
                return;
            }

            if (_elapsedMs >= RenegotiateTimeoutMs)
            {
                MozaLog.Warn(
                    $"[Moza] AUTO-TEST: renegotiate TIMEOUT after {RenegotiateTimeoutMs}ms — " +
                    $"target slot={_targetSlot} subGen still {_prevSubscriptionGen}");
                Finish(persistTarget: false);
            }
        }

        private void TickPostSwitchTest()
        {
            if (_elapsedMs < PostSwitchTestMs) return;

            int frames = _sender.FramesSent - _framesAtPhaseStart;
            string targetName = _dashList?[_targetSlot] ?? "?";
            bool ok = frames > 0;
            MozaLog.Debug(
                $"[Moza] AUTO-TEST: post-switch test done dash=\"{targetName}\" " +
                $"frames={frames} {(ok ? "PASS" : "FAIL")}");

            Finish(persistTarget: true);
        }

        private void Finish(bool persistTarget)
        {
            _sender.TestMode = false;
            if (persistTarget && _targetSlot >= 0)
            {
                var plugin = MozaPlugin.Instance;
                if (plugin?.Settings != null)
                {
                    plugin.Settings.AutoTestLastSlot = _targetSlot;
                    plugin.SaveSettings();
                    MozaLog.Debug(
                        $"[Moza] AUTO-TEST: persisted AutoTestLastSlot={_targetSlot} " +
                        "(next run will switch the other direction)");
                }
            }
            MozaLog.Debug("[Moza] AUTO-TEST: state=DONE");
            _state = State.Done;
        }

        private void Transition(State next, string detail)
        {
            _state = next;
            MozaLog.Debug($"[Moza] AUTO-TEST: state={next} {detail}");
        }
    }
}
