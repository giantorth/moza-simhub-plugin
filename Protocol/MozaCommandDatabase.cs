using System.Collections.Generic;

namespace MozaPlugin.Protocol
{
    /// <summary>
    /// Pre-built command definitions from docs/serial.md.
    /// </summary>
    public static class MozaCommandDatabase
    {
        private static readonly Dictionary<string, MozaCommand> _commands = new Dictionary<string, MozaCommand>();
        public static IReadOnlyDictionary<string, MozaCommand> Commands => _commands;

        static MozaCommandDatabase()
        {
            // ===== WHEELBASE (device: base, read group 40, write group 41) =====
            AddCommand("base-limit",              "base", 40, 41, new byte[] { 1 },      2, "int");
            AddCommand("base-ffb-strength",       "base", 40, 41, new byte[] { 2 },      2, "int");
            AddCommand("base-inertia",            "base", 40, 41, new byte[] { 4 },      2, "int");
            AddCommand("base-damper",             "base", 40, 41, new byte[] { 7 },      2, "int");
            AddCommand("base-friction",           "base", 40, 41, new byte[] { 8 },      2, "int");
            AddCommand("base-spring",             "base", 40, 41, new byte[] { 9 },      2, "int");
            AddCommand("base-speed",              "base", 40, 41, new byte[] { 10 },     2, "int");
            AddCommand("base-road-sensitivity",   "base", 40, 41, new byte[] { 12 },     2, "int");
            AddCommand("base-protection",         "base", 40, 41, new byte[] { 13 },     2, "int");
            AddCommand("base-equalizer1",         "base", 40, 41, new byte[] { 14 },     2, "int");
            AddCommand("base-equalizer2",         "base", 40, 41, new byte[] { 15 },     2, "int");
            AddCommand("base-equalizer3",         "base", 40, 41, new byte[] { 16 },     2, "int");
            AddCommand("base-equalizer4",         "base", 40, 41, new byte[] { 17 },     2, "int");
            AddCommand("base-torque",             "base", 40, 41, new byte[] { 18 },     2, "int");
            AddCommand("base-natural-inertia",    "base", 40, 41, new byte[] { 19 },     2, "int");
            AddCommand("base-equalizer5",         "base", 40, 41, new byte[] { 20 },     2, "int");
            AddCommand("base-natural-inertia-enable", "base", 40, 41, new byte[] { 22 }, 2, "int");
            AddCommand("base-max-angle",          "base", 40, 41, new byte[] { 23 },     2, "int");
            AddCommand("base-ffb-reverse",        "base", 40, 41, new byte[] { 24 },     2, "int");
            AddCommand("base-speed-damping",      "base", 40, 41, new byte[] { 25 },     2, "int");
            AddCommand("base-speed-damping-point","base", 40, 41, new byte[] { 26 },     2, "int");
            AddCommand("base-soft-limit-strength","base", 40, 41, new byte[] { 27 },     2, "int");
            AddCommand("base-soft-limit-retain",  "base", 40, 41, new byte[] { 28 },     2, "int");
            AddCommand("base-temp-strategy",      "base", 40, 41, new byte[] { 30 },     2, "int");
            AddCommand("base-soft-limit-stiffness","base",40, 41, new byte[] { 31 },     2, "int");
            AddCommand("base-equalizer6",         "base", 40, 41, new byte[] { 44 },     2, "int");
            AddCommand("base-protection-mode",    "base", 40, 41, new byte[] { 45 },     2, "int");
            AddCommand("base-ffb-disable",        "base", 40, 41, new byte[] { 254 },    2, "int");

            // FFB curve (read group 40, write group 41)
            AddCommand("base-ffb-curve-x1", "base", 40, 41, new byte[] { 34, 1 }, 1, "int");
            AddCommand("base-ffb-curve-x2", "base", 40, 41, new byte[] { 34, 2 }, 1, "int");
            AddCommand("base-ffb-curve-x3", "base", 40, 41, new byte[] { 34, 3 }, 1, "int");
            AddCommand("base-ffb-curve-x4", "base", 40, 41, new byte[] { 34, 4 }, 1, "int");
            AddCommand("base-ffb-curve-y1", "base", 40, 41, new byte[] { 34, 5 }, 1, "int");
            AddCommand("base-ffb-curve-y2", "base", 40, 41, new byte[] { 34, 6 }, 1, "int");
            AddCommand("base-ffb-curve-y3", "base", 40, 41, new byte[] { 34, 7 }, 1, "int");
            AddCommand("base-ffb-curve-y4", "base", 40, 41, new byte[] { 34, 8 }, 1, "int");
            AddCommand("base-ffb-curve-y5", "base", 40, 41, new byte[] { 34, 9 }, 1, "int");

            // Wheelbase telemetry (read group 43, read-only)
            AddCommand("base-state",       "base", 43, 0xFF, new byte[] { 1 }, 2, "int");
            AddCommand("base-state-err",   "base", 43, 0xFF, new byte[] { 2 }, 2, "int");
            AddCommand("base-mcu-temp",    "base", 43, 0xFF, new byte[] { 4 }, 2, "int");
            AddCommand("base-mosfet-temp", "base", 43, 0xFF, new byte[] { 5 }, 2, "int");
            AddCommand("base-motor-temp",  "base", 43, 0xFF, new byte[] { 6 }, 2, "int");

            // Wheelbase calibration (write group 42)
            AddCommand("base-calibration", "base", 0xFF, 42, new byte[] { 1 }, 2, "int");

            // ===== MAIN DEVICE (device: main, read/write group 31) =====
            AddCommand("main-set-compat-mode",   "main", 0xFF, 31, new byte[] { 19 }, 1, "int");
            AddCommand("main-get-compat-mode",   "main", 31, 0xFF, new byte[] { 23 }, 1, "int");
            AddCommand("main-set-ble-mode",      "main", 0xFF, 31, new byte[] { 71 }, 1, "int");
            AddCommand("main-get-ble-mode",      "main", 31, 0xFF, new byte[] { 70 }, 1, "int");
            AddCommand("main-set-led-status",    "main", 0xFF, 31, new byte[] { 9 },  1, "int");
            AddCommand("main-get-led-status",    "main", 31, 0xFF, new byte[] { 8 },  1, "int");
            AddCommand("main-set-work-mode",     "main", 0xFF, 31, new byte[] { 51 }, 1, "int");
            AddCommand("main-get-work-mode",     "main", 31, 0xFF, new byte[] { 52 }, 1, "int");
            AddCommand("main-set-interpolation", "main", 0xFF, 31, new byte[] { 76 }, 1, "int");
            AddCommand("main-get-interpolation", "main", 31, 0xFF, new byte[] { 77 }, 1, "int");
            AddCommand("main-set-spring-gain",   "main", 0xFF, 31, new byte[] { 78, 8 },  1, "int");
            AddCommand("main-set-damper-gain",   "main", 0xFF, 31, new byte[] { 78, 9 },  1, "int");
            AddCommand("main-set-inertia-gain",  "main", 0xFF, 31, new byte[] { 78, 10 }, 1, "int");
            AddCommand("main-set-friction-gain", "main", 0xFF, 31, new byte[] { 78, 11 }, 1, "int");
            AddCommand("main-get-spring-gain",   "main", 31, 0xFF, new byte[] { 79, 8 },  1, "int");
            AddCommand("main-get-damper-gain",   "main", 31, 0xFF, new byte[] { 79, 9 },  1, "int");
            AddCommand("main-get-inertia-gain",  "main", 31, 0xFF, new byte[] { 79, 10 }, 1, "int");
            AddCommand("main-get-friction-gain", "main", 31, 0xFF, new byte[] { 79, 11 }, 1, "int");

            // ===== PEDALS (read group 35, write group 36) =====
            AddCommand("pedals-throttle-dir", "pedals", 35, 36, new byte[] { 1 }, 2, "int");
            AddCommand("pedals-throttle-min", "pedals", 35, 36, new byte[] { 2 }, 2, "int");
            AddCommand("pedals-throttle-max", "pedals", 35, 36, new byte[] { 3 }, 2, "int");
            AddCommand("pedals-brake-dir",    "pedals", 35, 36, new byte[] { 4 }, 2, "int");
            AddCommand("pedals-brake-min",    "pedals", 35, 36, new byte[] { 5 }, 2, "int");
            AddCommand("pedals-brake-max",    "pedals", 35, 36, new byte[] { 6 }, 2, "int");
            AddCommand("pedals-brake-angle-ratio", "pedals", 35, 36, new byte[] { 26 }, 4, "float");
            AddCommand("pedals-clutch-dir",   "pedals", 35, 36, new byte[] { 7 }, 2, "int");
            AddCommand("pedals-clutch-min",   "pedals", 35, 36, new byte[] { 8 }, 2, "int");
            AddCommand("pedals-clutch-max",   "pedals", 35, 36, new byte[] { 9 }, 2, "int");

            // Pedal output curves (4-byte float, read 35 / write 36)
            AddCommand("pedals-throttle-y1", "pedals", 35, 36, new byte[] { 14 }, 4, "float");
            AddCommand("pedals-throttle-y2", "pedals", 35, 36, new byte[] { 15 }, 4, "float");
            AddCommand("pedals-throttle-y3", "pedals", 35, 36, new byte[] { 16 }, 4, "float");
            AddCommand("pedals-throttle-y4", "pedals", 35, 36, new byte[] { 17 }, 4, "float");
            AddCommand("pedals-throttle-y5", "pedals", 35, 36, new byte[] { 27 }, 4, "float");
            AddCommand("pedals-brake-y1",    "pedals", 35, 36, new byte[] { 18 }, 4, "float");
            AddCommand("pedals-brake-y2",    "pedals", 35, 36, new byte[] { 19 }, 4, "float");
            AddCommand("pedals-brake-y3",    "pedals", 35, 36, new byte[] { 20 }, 4, "float");
            AddCommand("pedals-brake-y4",    "pedals", 35, 36, new byte[] { 21 }, 4, "float");
            AddCommand("pedals-brake-y5",    "pedals", 35, 36, new byte[] { 28 }, 4, "float");
            AddCommand("pedals-clutch-y1",   "pedals", 35, 36, new byte[] { 22 }, 4, "float");
            AddCommand("pedals-clutch-y2",   "pedals", 35, 36, new byte[] { 23 }, 4, "float");
            AddCommand("pedals-clutch-y3",   "pedals", 35, 36, new byte[] { 24 }, 4, "float");
            AddCommand("pedals-clutch-y4",   "pedals", 35, 36, new byte[] { 25 }, 4, "float");
            AddCommand("pedals-clutch-y5",   "pedals", 35, 36, new byte[] { 29 }, 4, "float");

            // Pedal calibration (write-only, group 38)
            AddCommand("pedals-throttle-cal-start", "pedals", 0xFF, 38, new byte[] { 12 }, 2, "int");
            AddCommand("pedals-throttle-cal-stop",  "pedals", 0xFF, 38, new byte[] { 16 }, 2, "int");
            AddCommand("pedals-brake-cal-start",    "pedals", 0xFF, 38, new byte[] { 13 }, 2, "int");
            AddCommand("pedals-brake-cal-stop",     "pedals", 0xFF, 38, new byte[] { 17 }, 2, "int");
            AddCommand("pedals-clutch-cal-start",   "pedals", 0xFF, 38, new byte[] { 14 }, 2, "int");
            AddCommand("pedals-clutch-cal-stop",    "pedals", 0xFF, 38, new byte[] { 18 }, 2, "int");

            // Pedal outputs (read group 37, read-only)
            AddCommand("pedals-throttle-output", "pedals", 37, 0xFF, new byte[] { 1 }, 2, "int");
            AddCommand("pedals-brake-output",    "pedals", 37, 0xFF, new byte[] { 2 }, 2, "int");
            AddCommand("pedals-clutch-output",   "pedals", 37, 0xFF, new byte[] { 3 }, 2, "int");

            // ===== HUB (read group 100, read-only) =====
            // Command IDs match foxblat/boxflat serial.yml
            AddCommand("hub-base-power",    "hub", 100, 0xFF, new byte[] { 2 },    2, "int");
            AddCommand("hub-port1-power",   "hub", 100, 0xFF, new byte[] { 3 },    2, "int");
            AddCommand("hub-port2-power",   "hub", 100, 0xFF, new byte[] { 4 },    2, "int");
            AddCommand("hub-port3-power",   "hub", 100, 0xFF, new byte[] { 5, 1 }, 1, "int");
            AddCommand("hub-pedals1-power", "hub", 100, 0xFF, new byte[] { 6 },    2, "int");
            AddCommand("hub-pedals2-power", "hub", 100, 0xFF, new byte[] { 7 },    2, "int");
            AddCommand("hub-pedals3-power", "hub", 100, 0xFF, new byte[] { 8 },    2, "int");

            // ===== TELEMETRY OUTPUT =====
            AddCommand("dash-send-telemetry",           "dash",  0xFF, 65, new byte[] { 253, 222 }, 4, "int");
            AddCommand("wheel-send-rpm-telemetry",      "wheel", 0xFF, 63, new byte[] { 26, 0 },    4, "array");
            AddCommand("wheel-send-buttons-telemetry",  "wheel", 0xFF, 63, new byte[] { 26, 1 },    2, "array");
            AddCommand("wheel-telemetry-rpm-colors",    "wheel", 0xFF, 63, new byte[] { 25, 0 },   20, "array");
            AddCommand("wheel-telemetry-button-colors", "wheel", 0xFF, 63, new byte[] { 25, 1 },   20, "array");
            AddCommand("wheel-old-send-telemetry",      "wheel", 0xFF, 65, new byte[] { 253, 222 }, 4, "int");

            // ===== WHEEL IDENTITY (read-only, bytes=0 → request sends cmd ID only) =====
            AddCommand("wheel-model-name",  "wheel",  7, 0xFF, new byte[] { 1 }, 0, "array");
            AddCommand("wheel-sw-version",  "wheel", 15, 0xFF, new byte[] { 1 }, 0, "array");
            AddCommand("wheel-hw-version",  "wheel",  8, 0xFF, new byte[] { 1 }, 0, "array");
            AddCommand("wheel-hw-sub",      "wheel",  8, 0xFF, new byte[] { 2 }, 0, "array");
            AddCommand("wheel-serial-a",    "wheel", 16, 0xFF, new byte[] { 0 }, 0, "array");
            AddCommand("wheel-serial-b",    "wheel", 16, 0xFF, new byte[] { 1 }, 0, "array");
            // PitHouse-style extended identity probes (groups 0x02/0x04/0x05/0x06/0x09/0x11).
            // Empty-cmd requests (cmd_id=[], bytes=0) hit the request-as-probe path;
            // non-empty cmds use standard cmd_id prefix. Read-only, write_group=0xFF.
            AddCommand("wheel-presence",       "wheel",  9, 0xFF, new byte[] { },                 0, "array"); // 0x09 → reply `00 01` (1 sub-device)
            AddCommand("wheel-device-presence","wheel",  2, 0xFF, new byte[] { },                 0, "array"); // 0x02 → reply `02` (protocol ver?)
            AddCommand("wheel-device-type",    "wheel",  4, 0xFF, new byte[] { },                 0, "array"); // 0x04 → reply `01 02 04 06` (no cmd echo in reply)
            AddCommand("wheel-capabilities",   "wheel",  5, 0xFF, new byte[] { },                 0, "array"); // 0x05 → reply `01 02 1f 01` (no cmd echo in reply)
            AddCommand("wheel-mcu-uid",        "wheel",  6, 0xFF, new byte[] { },                 0, "array"); // 0x06 → 12-byte STM32 UID
            AddCommand("wheel-identity-11",    "wheel", 17, 0xFF, new byte[] { 4 },               0, "array"); // 0x11 cmd=04 → reply `04 01`

            // ===== WHEEL SETTINGS (read group 64, write group 63) =====
            AddCommand("wheel-brightness",         "wheel", 64, 63, new byte[] { 1 },          1, "int");
            AddCommand("wheel-rpm-timings",        "wheel", 64, 63, new byte[] { 2 },         10, "array");
            AddCommand("wheel-paddles-mode",       "wheel", 64, 63, new byte[] { 3 },          1, "int");
            AddCommand("wheel-rpm-indicator-mode", "wheel", 64, 63, new byte[] { 4 },          1, "int");
            AddCommand("wheel-stick-mode",         "wheel", 64, 63, new byte[] { 5 },          2, "int");
            AddCommand("wheel-stick-mode-new",     "wheel", 0xFF, 63, new byte[] { 5 },        1, "int");
            AddCommand("wheel-set-rpm-display-mode","wheel",0xFF,63, new byte[] { 7 },         1, "int");
            AddCommand("wheel-get-rpm-display-mode","wheel", 64, 0xFF, new byte[] { 8 },       1, "int");
            AddCommand("wheel-clutch-point",       "wheel", 64, 63, new byte[] { 9 },          1, "int");
            AddCommand("wheel-knob-mode",          "wheel", 64, 63, new byte[] { 10 },         1, "int");
            // Per-encoder rotary signal mode (newer firmware). Each knob independently: 0=Buttons, 1=Knob.
            for (byte i = 0; i < 5; i++)
                AddCommand($"wheel-knob-signal-mode{i}", "wheel", 64, 63, new byte[] { 42, i }, 1, "int");
            AddCommand("wheel-paddle-adaptive-mode","wheel",64, 63, new byte[] { 11 },         1, "int");
            AddCommand("wheel-paddle-button-mode", "wheel", 64, 63, new byte[] { 13 },         1, "int");
            AddCommand("wheel-rpm-interval",       "wheel", 64, 63, new byte[] { 22 },         4, "int");
            AddCommand("wheel-rpm-mode",           "wheel", 64, 63, new byte[] { 23 },         1, "int");

            // Wheel RPM threshold values (read group 64, write group 63)
            for (byte i = 0; i < 10; i++)
                AddCommand($"wheel-rpm-value{i + 1}", "wheel", 64, 63, new byte[] { 24, i }, 2, "int");

            // Wheel RPM LED colors (read group 64, write group 63, id [31, 0, 0xFF, index]).
            // Group 0 spec max = 25 LEDs. 11..25 beyond any shipping wheel, exposed for diagnostics.
            for (byte i = 0; i < 25; i++)
                AddCommand($"wheel-rpm-color{i + 1}", "wheel", 64, 63, new byte[] { 31, 0, 0xFF, i }, 3, "array");

            // Wheel button colors (id [31, 1, 0xFF, index]). Group 1 spec max = 16 LEDs.
            for (byte i = 0; i < 16; i++)
                AddCommand($"wheel-button-color{i + 1}", "wheel", 64, 63, new byte[] { 31, 1, 0xFF, i }, 3, "array");

            // Per-rotary-knob LED colors (W17 CS Pro = 4 knobs, W18 KS Pro = 5 knobs).
            // Wire: [27, <group>, <role>] + [R, G, B]. group = 1..5 (knob N — group 0 is RPM),
            // role 0=background (idle), 1=primary (active). Write-only — wheel echoes via
            // WheelEchoPrefixes entries for (0x3F, 0x17, 0x27, 0x01..0x05).
            for (byte k = 1; k <= 5; k++)
            {
                AddCommand($"wheel-knob{k}-bg-color",      "wheel", 0xFF, 63, new byte[] { 27, k, 0 }, 3, "array");
                AddCommand($"wheel-knob{k}-primary-color", "wheel", 0xFF, 63, new byte[] { 27, k, 1 }, 3, "array");
            }

            // Extended LED groups (2=Single/28 LEDs, 3=Rotary/56 LEDs, 4=Ambient/12 LEDs).
            // Per-LED color: [31, G, 0xFF, index] — same wire format as groups 0/1.
            // Brightness: [27, G, 0xFF]. Mode: [28, G]. Presence probed via brightness read.
            foreach (var (g, n) in new[] { ((byte)2, 28), ((byte)3, 56), ((byte)4, 12) })
            {
                AddCommand($"wheel-group{g}-brightness", "wheel", 64, 63, new byte[] { 27, g, 0xFF }, 1, "int");
                AddCommand($"wheel-group{g}-mode",       "wheel", 64, 63, new byte[] { 28, g },       1, "int");
                for (byte i = 0; i < n; i++)
                    AddCommand($"wheel-group{g}-color{i + 1}", "wheel", 64, 63, new byte[] { 31, g, 0xFF, i }, 3, "array");
            }

            // LEGACY: wheel-flag-color{1..6} on device 0x17 / write group 63 / id [21, 2, i].
            // RS21 parameter DB has no wheel-body flag commands; flag LEDs live on the
            // Meter sub-device (device 0x14 / write group 50). Use dash-flag-color{1..6}
            // (defined below at line ~238) instead — same wire bytes as MeterSetCfg_SetFlagGroupColor.
            // Kept commented for reference / rollback on very old firmware.
            // for (byte i = 0; i < 6; i++)
            //     AddCommand($"wheel-flag-color{i + 1}", "wheel", 64, 63, new byte[] { 21, 2, i }, 3, "array");

            // Wheel RPM blink colors (write-only, id [15, index])
            for (byte i = 0; i < 10; i++)
                AddCommand($"wheel-rpm-blink-color{i + 1}", "wheel", 0xFF, 63, new byte[] { 15, i }, 3, "array");

            // Wheel brightness (by zone: 0=rpm, 1=buttons, 2=flags)
            AddCommand("wheel-rpm-brightness",     "wheel", 64, 63, new byte[] { 27, 0, 0xFF }, 1, "int");
            AddCommand("wheel-buttons-brightness",  "wheel", 64, 63, new byte[] { 27, 1, 0xFF }, 1, "int");
            // LEGACY: wheel-flags-brightness on device 0x17. Replaced by dash-flags-brightness
            // (Meter sub-device, write group 50, id [10, 2] — MeterSetCfg_SetFlagGroupBrightness_o).
            // AddCommand("wheel-flags-brightness",    "wheel", 64, 63, new byte[] { 27, 2, 0xFF }, 1, "int");

            // Wheel telemetry mode and idle effects
            AddCommand("wheel-telemetry-mode",          "wheel", 64, 63, new byte[] { 28, 0 },  1, "int");
            AddCommand("wheel-telemetry-idle-effect",   "wheel", 64, 63, new byte[] { 29, 0 },  1, "int");
            AddCommand("wheel-buttons-idle-effect",     "wheel", 64, 63, new byte[] { 29, 1 },  1, "int");
            AddCommand("wheel-telemetry-idle-interval", "wheel", 0xFF, 63, new byte[] { 30, 0 }, 3, "int");
            AddCommand("wheel-buttons-idle-interval",   "wheel", 0xFF, 63, new byte[] { 30, 1 }, 3, "int");

            // Wheel idle settings
            AddCommand("wheel-idle-mode",    "wheel", 64, 63, new byte[] { 32 },       1, "int");
            AddCommand("wheel-idle-timeout", "wheel", 64, 63, new byte[] { 33 },       2, "int");
            AddCommand("wheel-idle-speed",   "wheel", 64, 63, new byte[] { 34, 0 },    2, "int");
            AddCommand("wheel-idle-color",   "wheel", 64, 63, new byte[] { 36, 255, 1, 255 }, 3, "array");

            // Old wheel (ES) colors
            for (byte i = 0; i < 10; i++)
                AddCommand($"wheel-old-rpm-color{i + 1}", "wheel", 64, 63, new byte[] { 21, 0, i }, 3, "array");
            AddCommand("wheel-old-rpm-brightness", "wheel", 64, 63, new byte[] { 20, 0 }, 1, "int");

            // ===== DASHBOARD SETTINGS (read group 51, write group 50) =====
            AddCommand("dash-rpm-timings",         "dash", 51, 50, new byte[] { 5 },       10, "array");
            AddCommand("dash-rpm-display-mode",    "dash", 51, 50, new byte[] { 7 },        1, "int");
            AddCommand("dash-rpm-brightness",      "dash", 51, 50, new byte[] { 10, 0 },    1, "int");
            AddCommand("dash-flags-brightness",    "dash", 51, 50, new byte[] { 10, 2 },    1, "int");
            AddCommand("dash-rpm-interval",        "dash", 51, 50, new byte[] { 12 },       4, "int");
            AddCommand("dash-rpm-mode",            "dash", 51, 50, new byte[] { 13 },       1, "int");
            AddCommand("dash-rpm-indicator-mode",  "dash", 51, 50, new byte[] { 17, 0 },    1, "int");
            AddCommand("dash-flags-indicator-mode", "dash", 51, 50, new byte[] { 17, 2 },   1, "int");

            // Dash RPM threshold values (read group 51, write group 50, id [14, index])
            for (byte i = 0; i < 10; i++)
                AddCommand($"dash-rpm-value{i + 1}", "dash", 51, 50, new byte[] { 14, i }, 4, "int");

            // Dash RPM LED colors (id [11, 0, index])
            for (byte i = 0; i < 10; i++)
                AddCommand($"dash-rpm-color{i + 1}", "dash", 51, 50, new byte[] { 11, 0, i }, 3, "array");

            // Dash RPM blink colors (write-only, id [9, index])
            for (byte i = 0; i < 10; i++)
                AddCommand($"dash-rpm-blink-color{i + 1}", "dash", 0xFF, 50, new byte[] { 9, i }, 3, "array");

            // Dash flag colors (id [11, 2, index])
            for (byte i = 0; i < 6; i++)
                AddCommand($"dash-flag-color{i + 1}", "dash", 51, 50, new byte[] { 11, 2, i }, 3, "array");

            // Dash flag default colors (write-only, id [8, 0])
            AddCommand("dash-flag-colors", "dash", 0xFF, 50, new byte[] { 8, 0 }, 18, "array");

            // ===== HANDBRAKE (device: handbrake, read group 91, write group 92) =====
            AddCommand("handbrake-direction",        "handbrake", 91, 92, new byte[] { 1 },  2, "int");
            AddCommand("handbrake-min",              "handbrake", 91, 92, new byte[] { 2 },  2, "int");
            AddCommand("handbrake-max",              "handbrake", 91, 92, new byte[] { 3 },  2, "int");
            AddCommand("handbrake-mode",             "handbrake", 91, 92, new byte[] { 11 }, 2, "int");
            AddCommand("handbrake-button-threshold", "handbrake", 91, 92, new byte[] { 10 }, 2, "int");

            // Handbrake output curve (4-byte float, read 91 / write 92)
            AddCommand("handbrake-y1", "handbrake", 91, 92, new byte[] { 5 }, 4, "float");
            AddCommand("handbrake-y2", "handbrake", 91, 92, new byte[] { 6 }, 4, "float");
            AddCommand("handbrake-y3", "handbrake", 91, 92, new byte[] { 7 }, 4, "float");
            AddCommand("handbrake-y4", "handbrake", 91, 92, new byte[] { 8 }, 4, "float");
            AddCommand("handbrake-y5", "handbrake", 91, 92, new byte[] { 9 }, 4, "float");

            // Handbrake calibration (write-only, group 94)
            AddCommand("handbrake-cal-start", "handbrake", 0xFF, 94, new byte[] { 3 }, 2, "int");
            AddCommand("handbrake-cal-stop",  "handbrake", 0xFF, 94, new byte[] { 4 }, 2, "int");

            // ===== AB9 ACTIVE SHIFTER (device: ab9, dev id 0x12, group 0x1F write / 0x1F read) =====
            // Per docs/AB9-poc-plan.md and docs/moza-protocol.md § "AB9 active shifter".
            // Wire format: 7E 03 1F 12 <cmdHi> <cmdLo> <value> <checksum>. Single-byte
            // payload for sliders/mode. Response group on the wire is 0x9F; the parser
            // toggles bit7 back to 0x1F before matching, so both Read- and WriteGroup
            // are 0x1F here.
            AddCommand("ab9-mode",                 "ab9", 0x1F, 0x1F, new byte[] { 0xD3, 0x00 }, 1, "int");
            AddCommand("ab9-mech-resistance",      "ab9", 0x1F, 0x1F, new byte[] { 0xD6, 0x00 }, 1, "int");
            AddCommand("ab9-spring",               "ab9", 0x1F, 0x1F, new byte[] { 0xAF, 0x00 }, 1, "int");
            AddCommand("ab9-natural-damping",      "ab9", 0x1F, 0x1F, new byte[] { 0xB0, 0x00 }, 1, "int");
            AddCommand("ab9-natural-friction",     "ab9", 0x1F, 0x1F, new byte[] { 0xB2, 0x00 }, 1, "int");
            AddCommand("ab9-max-torque-limit",     "ab9", 0x1F, 0x1F, new byte[] { 0xA9, 0x00 }, 1, "int");
        }

        private static void AddCommand(string name, string device, byte readGroup, byte writeGroup,
            byte[] commandId, int payloadBytes, string payloadType)
        {
            _commands[name] = new MozaCommand(name, device, readGroup, writeGroup,
                commandId, payloadBytes, payloadType);
        }

        public static MozaCommand? Get(string name)
        {
            return _commands.TryGetValue(name, out var cmd) ? cmd : null;
        }
    }
}
