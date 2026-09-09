#!/usr/bin/env python3
"""Mirror of Devices/Extensions/LegacyBaseDeviceMigration.Scan, for verifying
against a real SimHub install before deploying the plugin.

Reports what the plugin would decide for a given SimHub directory: which
instances look like the orphaned pre-1.6 wheelbase devices, and whether a
per-model wheelbase device already has ShakeIt effects enabled (which suppresses
the migration entirely).

Usage:
    python3 tools/legacy-lfe-scan-check.py "<SimHub install dir>"
"""
import json
import os
import sys

LEGACY_SHAKEIT_ID = "f208f60b-0050-4e83-a874-ae28dd13f7ab"
LEGACY_BASE_AMBIENT_ID = "b8361c60-1bbd-4497-8cb4-af5df7db7251"
LEGACY_BASE_LED_COUNT = 18


def matches(device_type_id, ident):
    low = device_type_id.lower()
    return low == ident or low.startswith(ident + "_")


def section(settings, composite_code, required_child):
    """Probe for the named composite child, else treat the node itself as the
    section; reject it when the shape marker is absent."""
    if not isinstance(settings, dict):
        return None
    node = settings.get(composite_code, settings)
    if not isinstance(node, dict) or not node:
        return None
    if required_child is not None and required_child not in node:
        return None
    return node


def containers_have_enabled(containers, depth=0):
    if depth > 8 or not isinstance(containers, list):
        return False
    for c in containers:
        if not isinstance(c, dict):
            continue
        # Only the container's own IsEnabled — SettingsStore channel activations
        # carry IsEnabled too, and oscillator 1 is on by default.
        if c.get("IsEnabled") is True:
            return True
        if containers_have_enabled(c.get("EffectsContainers"), depth + 1):
            return True
    return False


def any_effect_enabled(shakeit):
    if not isinstance(shakeit, dict):
        return False
    profiles = shakeit.get("Profiles")
    if not isinstance(profiles, list):
        return False
    if len(profiles) > 1:
        return True
    return any(containers_have_enabled((p or {}).get("EffectsContainers")) for p in profiles)


def main(argv):
    if len(argv) < 2:
        print(__doc__)
        return 2
    root = os.path.join(argv[1], "PluginsData", "Common", "Devices")
    if not os.path.isdir(root):
        print(f"No device store at {root}")
        return 1

    haptics = leds = None
    instance_id = ""
    per_model_configured = False

    for folder in sorted(os.listdir(root)):
        if folder.startswith("_"):
            continue
        path = os.path.join(root, folder, "settings.json")
        if not os.path.isfile(path):
            continue
        try:
            with open(path, encoding="utf-8-sig") as fh:
                doc = json.load(fh)
        except Exception as exc:                       # noqa: BLE001
            print(f"  skip {folder}: unreadable ({exc})")
            continue

        type_id = doc.get("DeviceTypeID") or ""
        name = doc.get("DeviceTypeName")
        settings = doc.get("Settings")
        print(f"  {folder}  {name!r}  {type_id}")

        if matches(type_id, LEGACY_SHAKEIT_ID):
            haptics = section(settings, "Haptics", "Profiles")
            print(f"    -> legacy ShakeIt haptics device, payload={'yes' if haptics else 'NO'}")
            if haptics and not instance_id:
                instance_id = folder
        elif matches(type_id, LEGACY_BASE_AMBIENT_ID):
            leds = section(settings, "LEDS", "ledModuleSettings")
            print(f"    -> legacy shared LED device, payload={'yes' if leds else 'NO'}")
            if leds and not instance_id:
                instance_id = folder
        elif isinstance(name, str) and name.startswith("MOZA "):
            # Stand-in for MozaDeviceConstants.GetBaseModelPrefix (the plugin
            # resolves the model registry; by name is close enough for a check).
            hap = section(settings, "Haptics", "Profiles")
            if hap and any_effect_enabled(hap):
                per_model_configured = True
                print("    -> per-model device WITH effects enabled")

    print()
    print(f"InstanceId              : {instance_id or '(none)'}")
    print(f"Haptics payload         : {'yes' if haptics else 'no'}")
    print(f"LED payload             : {'yes' if leds else 'no'} "
          f"(transfers only onto a {LEGACY_BASE_LED_COUNT}-LED base)")
    print(f"PerModelHapticsConfigured: {per_model_configured}")
    print()
    if per_model_configured or not (haptics or leds):
        print("VERDICT: no migration — nothing to import, or the user already migrated by hand.")
    else:
        print("VERDICT: migrate — route LFE to ShakeIt and import on the next device add.")
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv))
