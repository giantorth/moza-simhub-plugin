using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using MozaPlugin.Devices.MBooster;
using MozaPlugin.Resources;

namespace MozaPlugin.UI
{
    /// <summary>
    /// The mBooster tab's two real hardware calibration ROUTINES (travel and
    /// motor rotor-locate) plus the plain Virtual Damping pair (cmdId 0xAD)
    /// that Pit House pushes alongside Segmented Damping.
    ///
    /// The routines themselves live in
    /// <see cref="MBoosterCalibrationRunner"/> on the registry, not here: both
    /// soft-reboot the pedal, so a run has to survive both the CDC outage and
    /// this settings panel being closed. This file is only the buttons, the
    /// status line and the gating.
    /// </summary>
    public partial class SettingsControl
    {
        private MBoosterCalibrationRunner? _mboosterCalRunner;

        /// <summary>Subscribe to the runner's progress once, lazily — the
        /// registry creates it on first use.</summary>
        private MBoosterCalibrationRunner? EnsureMBoosterCalRunner(bool create)
        {
            var registry = _plugin?.MBoosterRegistry;
            if (registry == null) return null;
            var runner = create ? registry.CalibrationRunner : registry.CalibrationRunnerOrNull;
            if (runner == null || ReferenceEquals(runner, _mboosterCalRunner)) return runner;
            if (_mboosterCalRunner != null) _mboosterCalRunner.ProgressChanged -= OnMBoosterCalProgress;
            _mboosterCalRunner = runner;
            runner.ProgressChanged += OnMBoosterCalProgress;
            return runner;
        }

        /// <summary>Runner ticks on its own timer thread — marshal to the UI.</summary>
        private void OnMBoosterCalProgress()
        {
            try { Dispatcher.BeginInvoke((Action)RefreshMBoosterCalUi); }
            catch { }
        }

        private void MBoosterTravelCalButton_Click(object sender, RoutedEventArgs e)
            => StartMBoosterCalibration(MBoosterCalKind.Travel);

        private void MBoosterMotorCalButton_Click(object sender, RoutedEventArgs e)
            => StartMBoosterCalibration(MBoosterCalKind.Motor);

        private void StartMBoosterCalibration(MBoosterCalKind kind)
        {
            var controller = CurrentMBoosterController();
            if (controller == null) return;
            var runner = EnsureMBoosterCalRunner(create: true);
            if (runner == null) return;

            // The running routine's own button doubles as Cancel — no third
            // control, and cancelling still sends the stop frame and the
            // reboot, so the pedal can never be left in calibration mode.
            var running = runner.Snapshot();
            if (running.IsRunning)
            {
                if (running.Kind == kind) runner.Cancel();
                return;
            }

            string error;
            bool ok = kind == MBoosterCalKind.Travel
                ? runner.StartTravelCalibration(controller, _mboosterEffectPedalIndex, out error)
                : runner.StartMotorCalibration(controller, _mboosterEffectPedalIndex, out error);
            if (!ok)
            {
                SetMBoosterCalStatus(string.Format(Strings.Status_CalibrationFailed, error));
                return;
            }
            RefreshMBoosterCalUi();
        }

        /// <summary>
        /// Button enable/visibility and the status line. Called from
        /// RefreshMBoosterTab, from the passive-pedal gate, and on every
        /// runner progress event.
        /// </summary>
        private void RefreshMBoosterCalUi()
        {
            if (MBoosterCalButtonsPanel == null) return;
            var runner = EnsureMBoosterCalRunner(create: false);
            var status = runner?.Snapshot() ?? default;
            var controller = CurrentMBoosterController();

            // A routine reboots the whole unit, so while one runs the OTHER
            // button is dead on every pedal — not just the one being
            // calibrated. The running routine's own button becomes Cancel.
            bool running = status.IsRunning;
            bool usable = controller != null && controller.IsConnected
                          && controller.IsAxisMotorized(_mboosterEffectPedalIndex);
            bool travelRunning = running && status.Kind == MBoosterCalKind.Travel;
            bool motorRunning = running && status.Kind == MBoosterCalKind.Motor;
            MBoosterTravelCalButton.IsEnabled = travelRunning || (usable && !running);
            MBoosterMotorCalButton.IsEnabled = motorRunning || (usable && !running);
            MBoosterTravelCalButton.Content = travelRunning
                ? Strings.Button_Stop : Strings.Button_TravelCalibration;
            MBoosterMotorCalButton.Content = motorRunning
                ? Strings.Button_Stop : Strings.Button_MotorCalibration;

            if (!running && status.Step == MBoosterCalStep.Idle)
            {
                MBoosterCalStatus.Visibility = Visibility.Collapsed;
                return;
            }

            // Only narrate the run that belongs to the pedal on screen.
            bool mine = controller != null && status.Identity != null
                        && string.Equals(controller.Identity, status.Identity, StringComparison.OrdinalIgnoreCase)
                        && status.AxisIndex == _mboosterEffectPedalIndex;
            if (!mine && running)
            {
                SetMBoosterCalStatus(Strings.Hint_MBoosterCalBusyElsewhere);
                return;
            }
            if (!mine)
            {
                MBoosterCalStatus.Visibility = Visibility.Collapsed;
                return;
            }

            SetMBoosterCalStatus(DescribeMBoosterCal(status, runner));
        }

        private string DescribeMBoosterCal(MBoosterCalibrationRunner.Status status,
                                           MBoosterCalibrationRunner? runner)
        {
            switch (status.Step)
            {
                case MBoosterCalStep.Done:
                    return Strings.Status_Done;
                case MBoosterCalStep.Failed:
                    return string.Format(Strings.Status_CalibrationFailed, status.Message);
                case MBoosterCalStep.MotorRebooting:
                case MBoosterCalStep.Rebooting:
                case MBoosterCalStep.Verifying:
                    return Strings.Hint_MBoosterCalRebooting;
                default:
                    break;
            }

            // The firmware narrates both routines on its own debug channel, so
            // show what it actually said rather than only a countdown.
            string note = runner?.FirmwareNote ?? string.Empty;
            string format = status.Kind == MBoosterCalKind.Motor
                ? Strings.Hint_MBoosterMotorCal
                : Strings.Hint_MBoosterTravelCal;
            string text = string.Format(format, status.SecondsRemaining);
            return note.Length > 0 ? text + "  " + note : text;
        }

        private void SetMBoosterCalStatus(string text)
        {
            MBoosterCalStatus.Text = text;
            MBoosterCalStatus.Visibility = Visibility.Visible;
        }

        // ===== Plain Virtual Damping (cmdId 0xAD, press/release selectors) ===
        //
        // A register set of its own, NOT the 0xB7 per-segment fields: the
        // firmware log prints `virtual_damping_press` / `_release` for these
        // and `virtual_damping_press1..3` / `_release1..3` for the segments,
        // out of the same Pit House write burst (2026-09-08 captures). Both
        // selectors are independent values here, unlike Natural Friction's
        // two selectors which always carry the same number.
        //
        // One handler for both sliders — which one moved is read off the
        // sender, so the pair stays a single push key and a drag on either
        // coalesces the same way every other Pedal Feel write does.
        private void MBoosterDampingSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            bool isPress = ReferenceEquals(sender, MBoosterDampingPressSlider);
            var box = isPress ? MBoosterDampingPressValue : MBoosterDampingReleaseValue;
            OnIntSliderChanged(e.NewValue, box, "", v =>
            {
                var s = CurrentMBoosterEffectTarget();
                if (s == null) return;
                if (isPress) s.DampingPressPct = v;
                else s.DampingReleasePct = v;
                int raw = global::MozaPlugin.Protocol.MozaMBoosterProtocol.EncodeFrictionPct(v);
                string command = isPress ? "mbooster-brake-damping-press" : "mbooster-brake-damping-release";
                QueueMBoosterPedalFeelPush(isPress ? "damping-press" : "damping-release",
                    (c, dev) => c.SendIntWrite(command, raw, dev));
            });
        }
    }
}
