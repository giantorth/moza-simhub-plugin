using System;
using System.Collections.Generic;
using System.Linq;

namespace MozaPlugin.Telemetry
{
    internal sealed class DashboardSwitchAutoTest
    {
        private enum State { Idle, WaitNegotiate, TestRunning, SwitchPending, WaitRenegotiate, Done }

        private readonly TelemetrySender _sender;
        private State _state = State.Idle;
        private int _elapsedMs;
        private int _dashIndex;
        private byte _prevFlagBase;
        private int _framesSentAtStart;
        private IReadOnlyList<string>? _dashList;
        private readonly List<string> _failed = new();
        private int _passed;

        private const int IdleTimeoutMs = 30000;
        private const int NegotiateTimeoutMs = 5000;
        private const int TestRunMs = 8000;
        private const int RenegotiateTimeoutMs = 10000;

        public DashboardSwitchAutoTest(TelemetrySender sender)
        {
            _sender = sender;
        }

        public void Tick(int tickMs)
        {
            _elapsedMs += tickMs;

            switch (_state)
            {
                case State.Idle:
                    TickIdle();
                    break;
                case State.WaitNegotiate:
                    TickWaitNegotiate();
                    break;
                case State.TestRunning:
                    TickTestRunning();
                    break;
                case State.SwitchPending:
                    TickSwitchPending();
                    break;
                case State.WaitRenegotiate:
                    TickWaitRenegotiate();
                    break;
                case State.Done:
                    break;
            }
        }

        private void TickIdle()
        {
            var flagBase = _sender.ActiveFlagBase;
            if (flagBase == null) return;

            _dashList = _sender.WheelState?.ConfigJsonList;
            if (_dashList == null || _dashList.Count < 2)
            {
                // ConfigJsonList from session 0x09 is fragile (dropped chunks
                // prevent reassembly). Fall back to DashboardCache names sorted
                // alphabetically — matches wheel's configJsonList ordering.
                var cache = _sender.DashCache;
                if (cache != null)
                {
                    var sorted = cache.CachedNames.OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();
                    if (sorted.Count >= 2)
                    {
                        MozaLog.Info($"[Moza] AUTO-TEST: using DashboardCache fallback ({sorted.Count} dashboards)");
                        _dashList = sorted;
                    }
                }
            }
            if (_dashList == null || _dashList.Count < 2)
            {
                if (_elapsedMs >= IdleTimeoutMs)
                {
                    MozaLog.Info("[Moza] AUTO-TEST: <2 dashboards after 30s, skipping");
                    _state = State.Done;
                }
                return;
            }

            _sender.TestMode = true;
            _dashIndex = 0;
            _elapsedMs = 0;
            _prevFlagBase = flagBase.Value;
            Transition(State.WaitNegotiate, $"dash=\"{_dashList[0]}\" slot=0");
        }

        private void TickWaitNegotiate()
        {
            var flagBase = _sender.ActiveFlagBase;
            if (flagBase != null)
            {
                _framesSentAtStart = _sender.FramesSent;
                _elapsedMs = 0;
                Transition(State.TestRunning,
                    $"dash=\"{CurrentDash()}\" slot={_dashIndex} " +
                    $"flagBase=0x{flagBase.Value:X2} " +
                    $"tiers={_sender.ActiveTierCount} " +
                    $"catalog={_sender.CatalogChannelCount}");
                return;
            }

            if (_elapsedMs >= NegotiateTimeoutMs)
            {
                _failed.Add(CurrentDash());
                MozaLog.Warn($"[Moza] AUTO-TEST: negotiate timeout dash=\"{CurrentDash()}\"");
                AdvanceOrFinish();
            }
        }

        private void TickTestRunning()
        {
            if (_elapsedMs < TestRunMs) return;

            int framesDelta = _sender.FramesSent - _framesSentAtStart;
            bool ok = framesDelta > 0;
            MozaLog.Info(
                $"[Moza] AUTO-TEST: test phase done dash=\"{CurrentDash()}\" " +
                $"slot={_dashIndex} frames={framesDelta} " +
                $"result={(ok ? "PASS" : "FAIL")}");

            if (ok) _passed++;
            else _failed.Add(CurrentDash());

            AdvanceOrFinish();
        }

        private void TickSwitchPending()
        {
            var dash = CurrentDash();
            MozaLog.Info(
                $"[Moza] AUTO-TEST: state=SWITCH_PENDING dash=\"{dash}\" slot={_dashIndex} " +
                $"prevFlag=0x{_prevFlagBase:X2}");

            var plugin = MozaPlugin.Instance;
            if (plugin != null)
            {
                plugin.Settings.TelemetryProfileName = dash;
                plugin.Settings.TelemetryMzdashPath = "";
                plugin.ApplyTelemetrySettings();
            }

            _sender.SendDashboardSwitch((uint)_dashIndex);
            _prevFlagBase = _sender.ActiveFlagBase ?? _prevFlagBase;
            _elapsedMs = 0;
            Transition(State.WaitRenegotiate, $"dash=\"{dash}\" slot={_dashIndex}");
        }

        private void TickWaitRenegotiate()
        {
            var flagBase = _sender.ActiveFlagBase;
            if (flagBase != null && flagBase.Value != _prevFlagBase)
            {
                _framesSentAtStart = _sender.FramesSent;
                _prevFlagBase = flagBase.Value;
                _elapsedMs = 0;
                Transition(State.TestRunning,
                    $"dash=\"{CurrentDash()}\" slot={_dashIndex} " +
                    $"flagBase=0x{flagBase.Value:X2} " +
                    $"tiers={_sender.ActiveTierCount} " +
                    $"catalog={_sender.CatalogChannelCount}");
                return;
            }

            if (_elapsedMs >= RenegotiateTimeoutMs)
            {
                _failed.Add(CurrentDash());
                MozaLog.Warn($"[Moza] AUTO-TEST: renegotiate timeout dash=\"{CurrentDash()}\"");
                AdvanceOrFinish();
            }
        }

        private void AdvanceOrFinish()
        {
            _dashIndex++;
            if (_dashIndex < (_dashList?.Count ?? 0))
            {
                _elapsedMs = 0;
                _state = State.SwitchPending;
            }
            else
            {
                Finish();
            }
        }

        private void Finish()
        {
            _sender.TestMode = false;
            int total = _passed + _failed.Count;
            string failedStr = _failed.Count > 0
                ? "[\"" + string.Join("\",\"", _failed) + "\"]"
                : "[]";
            MozaLog.Info(
                $"[Moza] AUTO-TEST: state=DONE passed={_passed}/{total} " +
                $"failed={failedStr}");
            _state = State.Done;
        }

        private string CurrentDash() =>
            _dashList != null && _dashIndex < _dashList.Count
                ? _dashList[_dashIndex]
                : "?";

        private void Transition(State next, string detail)
        {
            _state = next;
            MozaLog.Info($"[Moza] AUTO-TEST: state={next} {detail}");
        }
    }
}
