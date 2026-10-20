"""Tests for the ENV-001 pilot guards (tools/azr-001/env001_pilot_guards.py).

Pure tests (SCRAM known answer, settings validation) always run. The template tests compile the real
`deploy/azure/pilot/*.bicep` with the pinned Bicep CLI when it is available (`BICEP_BIN`, or
`$RUNNER_TEMP/bicep` as installed by the Foundation/PR Validation azr-static steps) and then prove
that each owner decision is enforced by mutating the compiled ARM output.
"""

from __future__ import annotations

import base64
import copy
import hashlib
import hmac
import json
import os
import re
import shutil
import subprocess
import sys
import tempfile
import unittest
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))

import env001_pilot_guards as guards  # noqa: E402

REPO_ROOT = Path(__file__).resolve().parents[2]

# GATE-010-VEHICLE-ELIGIBILITY-PILOT-2026-10-20 (owner answers G4, H2 and H3): the per-order capacity of each vehicle
# type (packages, total grams, grams of the heaviest package, length, width and height in millimetres; dimensions
# optional) and its required documents, each with an expiry date (no NonExpiringDocumentTypes, no BACKGROUND_CHECK).
APPROVED_CAPACITY = {
    "MOTORCYCLE": (5, 20000, 10000, 400, 400, 350),
    "CAR": (10, 100000, 20000, 900, 800, 600),
    "VAN": (20, 400000, 20000, 2000, 1200, 1200),
    "BICYCLE": (3, 10000, 10000, 450, 400, 300),
    "WALKER": (2, 5000, 5000, 450, 400, 300),
}
MOTOR_VEHICLE_DOCUMENTS = ("IDENTITY", "DRIVER_LICENSE", "VEHICLE_CARD", "INSURANCE")
APPROVED_DOCUMENTS = {
    "MOTORCYCLE": MOTOR_VEHICLE_DOCUMENTS,
    "CAR": MOTOR_VEHICLE_DOCUMENTS,
    "VAN": MOTOR_VEHICLE_DOCUMENTS,
    "BICYCLE": ("IDENTITY",),
    "WALKER": ("IDENTITY",),
}


def without_eligibility(entries):
    """The settings with every Drivers__Eligibility entry removed."""
    return [entry for entry in entries if not guards.is_eligibility_name(entry["name"])]

# A complete, synthetic driver eligibility policy: the values OrderTransitionGuardsApiFactory.DriverEligibilitySettings
# gives the API in its tests. Never the owner's rules, which apps.settings.json carries since
# GATE-010-VEHICLE-ELIGIBILITY-PILOT-2026-10-20 (PilotApiStartupConfigurationTests starts the pilot API with them).
SYNTHETIC_CAPACITY = {
    "MaximumPackageCount": "4",
    "MaximumTotalWeightGrams": "4000",
    "MaximumSinglePackageWeightGrams": "2000",
    "MaximumLengthMillimeters": "500",
    "MaximumWidthMillimeters": "400",
    "MaximumHeightMillimeters": "300",
}
SYNTHETIC_ELIGIBILITY = [{"name": "Drivers__Eligibility__NonExpiringDocumentTypes__0", "value": "IDENTITY"}] + [
    entry
    for vehicle in ("MOTORCYCLE", "CAR", "VAN", "BICYCLE", "WALKER")
    for entry in [{"name": f"Drivers__Eligibility__RequiredDocumentTypesByVehicleType__{vehicle}__0", "value": "IDENTITY"}]
    + [{"name": f"Drivers__Eligibility__VehicleCapacity__{vehicle}__{field}", "value": value}
       for field, value in SYNTHETIC_CAPACITY.items()]
]


def _bicep() -> str | None:
    candidates = [os.environ.get("BICEP_BIN"), os.path.join(os.environ.get("RUNNER_TEMP", ""), "bicep"), shutil.which("bicep")]
    return next((c for c in candidates if c and os.path.isfile(c) and os.access(c, os.X_OK)), None)


class ScramVerifierTests(unittest.TestCase):
    def test_rfc7677_known_answer(self):
        # RFC 7677 section 3: user "user", password "pencil".
        verifier = guards.scram_sha256_verifier("pencil", salt=base64.b64decode("W22ZaJ0SNY7soEsUEjb6gQ=="))
        self.assertEqual(
            "SCRAM-SHA-256$4096:W22ZaJ0SNY7soEsUEjb6gQ==$WG5d8oPm3OtcPnkdi4Uo7BkeZkBFzpcXkuLmtbsT4qY=:"
            "wfPLwcE6nTWhTAmQ7tl2KeoiWGPlZqQxSrmfPwDl2dU=",
            verifier,
        )
        stored, server = (base64.b64decode(k) for k in verifier.rsplit("$", 1)[1].split(":"))
        auth_message = (
            b"n=user,r=rOprNGfwEbeRWgbNEkqO,"
            b"r=rOprNGfwEbeRWgbNEkqO%hvYDpWUa2RaTCAfuxFIlj)hNlF$k0,s=W22ZaJ0SNY7soEsUEjb6gQ==,i=4096,"
            b"c=biws,r=rOprNGfwEbeRWgbNEkqO%hvYDpWUa2RaTCAfuxFIlj)hNlF$k0"
        )
        self.assertEqual(b"6rriTRBi23WpRR/wtup+mMhUZUn/dB5nLTJRsjl95G4=", base64.b64encode(hmac.new(server, auth_message, hashlib.sha256).digest()))
        proof = base64.b64decode("dHzbZapWIk4jUhN+Ute9ytag9zjfMHgsqmmiz7AndVQ=")
        client_key = bytes(a ^ b for a, b in zip(proof, hmac.new(stored, auth_message, hashlib.sha256).digest()))
        self.assertEqual(stored, hashlib.sha256(client_key).digest())

    def test_random_salt_and_migrator_format(self):
        first = guards.scram_sha256_verifier("x" * 64)
        second = guards.scram_sha256_verifier("x" * 64)
        self.assertNotEqual(first, second)
        pattern = r"^SCRAM-SHA-256\$4096:[A-Za-z0-9+/]{22}==\$[A-Za-z0-9+/]{43}=:[A-Za-z0-9+/]{43}=$"
        self.assertRegex(first, pattern)

    def test_cli_rejects_short_passwords(self):
        result = subprocess.run([sys.executable, guards.__file__, "scram-verifier"], input="short", capture_output=True, text=True)
        self.assertEqual(2, result.returncode)
        self.assertEqual("", result.stdout)


class SettingsTests(unittest.TestCase):
    def test_repository_settings_are_well_formed_and_carry_the_driver_eligibility_rules(self):
        # POLICY-VERSIONS-PER-ORG-2026-10-02 removed the last two OWNER_DECISION_REQUIRED settings (the global
        # assignment and eligibility policy versions) and GATE-010-VEHICLE-ELIGIBILITY-PILOT-2026-10-20 added the
        # driver eligibility rules the pilot API needs to start: the static guard (P17) and the deploy (settings-check)
        # both accept the file. Without the rules the deploy still stops, once per vehicle type.
        entries = guards.load_settings(REPO_ROOT / guards.SETTINGS_FILE)
        self.assertEqual([], guards.validate_settings(entries, allow_sentinel=True))
        self.assertEqual([], guards.validate_settings(entries, allow_sentinel=False))
        failures = guards.validate_settings(without_eligibility(entries), allow_sentinel=False)
        self.assertEqual(len(guards.ELIGIBILITY_VEHICLE_TYPES), len(failures), failures)
        self.assertTrue(all(failure.startswith("driver eligibility rules for vehicle type ") for failure in failures), failures)
        self.assertEqual([], guards.validate_settings(without_eligibility(entries), allow_sentinel=True))
        self.assertEqual(
            [], guards.validate_settings(without_eligibility(entries) + SYNTHETIC_ELIGIBILITY, allow_sentinel=False))
        self.assertTrue(guards.validate_settings([{"name": "Finance__OperationalTimeZone", "value": guards.OWNER_SENTINEL}], allow_sentinel=False))

    def test_platform_managed_mock_and_secret_settings_are_rejected(self):
        cases = {
            "Authentication__Provider": "AuthCenter",
            "ConnectionStrings__Paqueteria": "Host=x",
            "Locations__PiiProtector": "AzureKeyVault",
            "Realtime__Backplane": "Redis",
            "Pricing__ApiKey": "abc",
            "Pricing__Mode": "Mock",
            "Pricing__Engine": "mock",
            "Pricing__Source": " SYNTHETIC ",
        }
        for name, value in cases.items():
            with self.subTest(name=name):
                self.assertTrue(guards.validate_settings([{"name": name, "value": value}], allow_sentinel=False))
        self.assertTrue(guards.validate_settings([{"name": "Finance__OperationalTimeZone", "value": "America/Mazatlan", "extra": 1}], allow_sentinel=False))
        self.assertTrue(guards.validate_settings([{"name": "A__B", "value": "1"}, {"name": "A__B", "value": "2"}], allow_sentinel=False))
        self.assertEqual([], guards.validate_settings(
            [{"name": "Finance__OperationalTimeZone", "value": "America/Mazatlan"}] + SYNTHETIC_ELIGIBILITY, allow_sentinel=False))
        self.assertTrue(guards.validate_settings([{"name": "Finance__OperationalTimeZone", "value": "owner_decision_required"}], allow_sentinel=False))

    def test_the_telemetry_export_cannot_be_switched_or_redirected_from_the_settings_file(self):
        # OBS-003: the export switch, its connection string and the pseudonym key version are reviewed apps.bicep values.
        for name, value in {
            "TelemetryExport__Enabled": "true",
            "TelemetryExport__ConnectionString": "InstrumentationKey=00000000-0000-0000-0000-000000000001",
            "TelemetryPseudonym__CurrentKeyVersion": "2",
        }.items():
            with self.subTest(name=name):
                failures = guards.validate_settings([{"name": name, "value": value}], allow_sentinel=False)
                self.assertIn(f"setting {name} is platform-managed by apps.bicep and cannot be overridden", failures)
        self.assertTrue(guards.validate_settings([{"name": "TelemetryPseudonym__Keys__1", "value": "x"}], allow_sentinel=False))

    def test_the_masked_call_bridge_cannot_be_enabled_from_the_settings_file(self):
        # VOICE-001-PROVIDER-TWILIO-2026-10-11: Voice stays Disabled in the pilot; every Voice__ key is refused.
        for name, value in {
            "Voice__Provider": "Twilio",
            "Voice__Twilio__WebhookBaseUri": "https://pilot.example.test",
            "Voice__Twilio__Gate007DecisionId": "GATE-007-PILOT",
        }.items():
            with self.subTest(name=name):
                failures = guards.validate_settings([{"name": name, "value": value}], allow_sentinel=False)
                self.assertIn(f"setting {name} is platform-managed by apps.bicep and cannot be overridden", failures)
        entries = guards.load_settings(REPO_ROOT / guards.SETTINGS_FILE)
        self.assertFalse(any(entry["name"].startswith("Voice__") for entry in entries))

    def test_dispatch_contacts_are_empty_by_default_and_checked_before_deploying(self):
        # DRV-DISPATCH-CONTACT-2026-10-12: the owner gives the number later; until then the file has no entry and
        # the driver app shows no "Llamar a despacho". The numbers below are synthetic.
        entries = guards.load_settings(REPO_ROOT / guards.SETTINGS_FILE)
        self.assertFalse(any(entry["name"].startswith("Drivers__DispatchContacts") for entry in entries))

        def contact(index, organization, phone):
            return [
                {"name": f"Drivers__DispatchContacts__{index}__OrganizationId", "value": organization},
                {"name": f"Drivers__DispatchContacts__{index}__PhoneE164", "value": phone},
            ]

        first = "3f2504e0-4f89-41d3-9a0c-0305e82c3301"
        second = "22222222-2222-4222-8222-222222222222"
        number = "+522000000001"
        valid = contact(0, first, number) + contact(1, second, number)
        self.assertEqual([], guards.validate_settings(valid + SYNTHETIC_ELIGIBILITY, allow_sentinel=False))

        invalid = {
            "uppercase id": contact(0, first.upper(), number),
            "nil id": contact(0, "00000000-0000-0000-0000-000000000000", number),
            "id without hyphens": contact(0, first.replace("-", ""), number),
            "no country code": contact(0, first, "2000000001"),
            "spaces": contact(0, first, "+52 2000000001"),
            "first digit 1": contact(0, first, "+521000000001"),
            "other country": contact(0, first, "+12025550100"),
            "trailing line break": contact(0, first, number + "\n"),
            "missing number": contact(0, first, number)[:1],
            "missing organization": contact(0, first, number)[1:],
            "organization twice": contact(0, first, number) + contact(1, first, "+522000000002"),
            "gap": contact(0, first, number) + contact(2, second, number),
            "unknown member": contact(0, first, number) + [{"name": "Drivers__DispatchContacts__0__Label", "value": "x"}],
            # Review of #235: a mistyped entry never makes the guard print its value.
            "number as a JSON number": contact(0, first, number)[:1]
            + [{"name": "Drivers__DispatchContacts__0__PhoneE164", "value": 522000000009}],
            "extra member": contact(0, first, number)[:1]
            + [{"name": "Drivers__DispatchContacts__0__PhoneE164", "value": "+522000000009", "slotSetting": False}],
        }
        for case, settings in invalid.items():
            with self.subTest(case=case):
                failures = guards.validate_settings(settings + SYNTHETIC_ELIGIBILITY, allow_sentinel=False)
                self.assertTrue(failures)
                self.assertTrue(all("DispatchContacts" in failure for failure in failures), failures)
                self.assert_no_value_echoed(settings, failures)

        # Review of #235: .NET binds configuration names in any casing, so another casing is refused (and its value
        # still checked), a case variant of a name counts as the same name, and an unverified name is never echoed.
        lowercase = [
            {"name": "drivers__dispatchcontacts__0__organizationid", "value": first},
            {"name": "drivers__dispatchcontacts__0__phonee164", "value": number},
        ]
        failures = guards.validate_settings(lowercase + SYNTHETIC_ELIGIBILITY, allow_sentinel=False)
        self.assertEqual(2, sum("must be spelled exactly Drivers__DispatchContacts__<n>__" in f for f in failures), failures)
        self.assertTrue(all(f.startswith(("setting #1 ", "setting #2 ")) for f in failures), failures)
        self.assert_no_value_echoed(lowercase, failures)

        cased = {
            "lowercase names with bad values": contact(0, first, number) + [
                {"name": "drivers__dispatchcontacts__1__organizationid", "value": "not-a-uuid-2000000009"},
                {"name": "drivers__dispatchcontacts__1__phonee164", "value": "2000000009"},
            ],
            "the same name in another casing": contact(0, first, number)
            + [{"name": "DRIVERS__DISPATCHCONTACTS__0__PHONEE164", "value": "+522000000002"}],
            "an index long enough to be a number": contact(0, first, number)
            + [{"name": "Drivers__DispatchContacts__2000000009__PhoneE164", "value": number}],
            "a number typed as a name": contact(0, first, number) + [{"name": "+522000000009", "value": "x"}],
        }
        for case, settings in cased.items():
            with self.subTest(case=case):
                failures = guards.validate_settings(settings + SYNTHETIC_ELIGIBILITY, allow_sentinel=False)
                self.assertTrue(failures)
                self.assert_no_value_echoed(settings, failures)
        bad_values = guards.validate_settings(cased["lowercase names with bad values"], allow_sentinel=False)
        self.assertIn("Drivers__DispatchContacts__1__OrganizationId must be a canonical lowercase UUID", bad_values)
        self.assertIn("Drivers__DispatchContacts__1__PhoneE164 must be +52 and ten digits, the first one 2-9", bad_values)
        self.assertTrue(any("declared twice" in f for f in guards.validate_settings(
            cased["the same name in another casing"], allow_sentinel=False)))

    def test_setting_names_are_compared_ignoring_case(self):
        # .NET configuration keys ignore case: a platform-managed, removed or repeated name in another casing is the
        # same setting for the API, so the guard refuses it as such.
        cases = {
            "voice__provider": "is platform-managed by apps.bicep",
            "connectionstrings__Paqueteria": "is platform-managed by apps.bicep",
            "pricing__pricingpolicyversion": "was removed by PRC-POLICY-VERSION-PER-ORG",
        }
        for name, fragment in cases.items():
            with self.subTest(name=name):
                failures = guards.validate_settings([{"name": name, "value": "1"}], allow_sentinel=False)
                self.assertTrue(any(fragment in failure for failure in failures), failures)
        failures = guards.validate_settings(
            [{"name": "Finance__OperationalTimeZone", "value": "America/Mazatlan"},
             {"name": "finance__operationaltimezone", "value": "America/Mexico_City"}] + SYNTHETIC_ELIGIBILITY,
            allow_sentinel=False)
        self.assertEqual(["setting finance__operationaltimezone declared twice (configuration names ignore case)"], failures)

    def assert_no_value_echoed(self, settings, failures):
        """No failure carries a phone-like digit run or any submitted value of eight or more characters."""
        for failure in failures:
            self.assertIsNone(re.search(r"\d{7,}", failure), failure)
            for entry in settings:
                value = entry.get("value") if isinstance(entry, dict) else None
                if value is not None and len(str(value).strip()) >= 8:
                    self.assertNotIn(str(value).strip(), failure)

    def test_removed_global_pricing_policy_version_is_rejected(self):
        # PRC-POLICY-VERSION-PER-ORG: the version comes from each organization's tariff rules.
        failures = guards.validate_settings([{"name": "Pricing__PricingPolicyVersion", "value": "PRC-PILOT-v1"}], allow_sentinel=False)
        self.assertTrue(any("PRC-POLICY-VERSION-PER-ORG" in failure for failure in failures))
        entries = guards.load_settings(REPO_ROOT / guards.SETTINGS_FILE)
        self.assertNotIn("Pricing__PricingPolicyVersion", {entry["name"] for entry in entries})

    def test_removed_global_assignment_and_eligibility_policy_versions_are_rejected(self):
        # POLICY-VERSIONS-PER-ORG-2026-10-02: each organization versions its own assignment and driver
        # eligibility policies, so neither global setting may come back, with any value.
        entries = guards.load_settings(REPO_ROOT / guards.SETTINGS_FILE)
        for name in ("Dispatch__AssignmentPolicyVersion", "Drivers__Eligibility__PolicyVersion"):
            for value in ("piloto-2026-10-v1", guards.OWNER_SENTINEL):
                with self.subTest(name=name, value=value):
                    failures = guards.validate_settings([{"name": name, "value": value}], allow_sentinel=True)
                    self.assertTrue(any("POLICY-VERSIONS-PER-ORG-2026-10-02" in failure for failure in failures))
            self.assertNotIn(name, {entry["name"] for entry in entries})


class DriverEligibilitySettingsTests(unittest.TestCase):
    """The pilot API (Drivers__Provider and Routing__Provider PostgreSql) refuses to start without a driver eligibility
    policy for every vehicle type (PilotApiStartupConfigurationTests.MissingEligibilityStartFailures). The rules are the
    owner's (GATE-010-VEHICLE-ELIGIBILITY-PILOT-2026-10-20): settings-check stops while any is missing and holds every
    entry present to the API's start validation."""

    VEHICLES = ("MOTORCYCLE", "CAR", "VAN", "BICYCLE", "WALKER")
    DOCUMENT = "Drivers__Eligibility__RequiredDocumentTypesByVehicleType__{}__{}"
    CAPACITY = "Drivers__Eligibility__VehicleCapacity__{}__{}"

    def required_names(self, vehicle):
        return [self.DOCUMENT.format(vehicle, 0)] + [self.CAPACITY.format(vehicle, field) for field in SYNTHETIC_CAPACITY]

    @staticmethod
    def with_value(entries, name, value):
        return [dict(entry, value=value) if entry["name"] == name else entry for entry in entries]

    @staticmethod
    def settings_check(path):
        return subprocess.run([sys.executable, guards.__file__, "settings-check", "--file", str(path)],
                              capture_output=True, text=True)

    def test_the_repository_file_passes_the_deploy_check(self):
        result = self.settings_check(REPO_ROOT / guards.SETTINGS_FILE)
        self.assertEqual(0, result.returncode, result.stdout)
        self.assertEqual("ENV001_SETTINGS=PASS", result.stdout.strip())

    def test_the_repository_file_carries_exactly_the_approved_rules(self):
        # GATE-010-VEHICLE-ELIGIBILITY-PILOT-2026-10-20: G4, H2 "Aprobar la tabla (Recommended)" and H3 "Aprobar la
        # lista (Recommended)", applied as approved. Changing a value is a new owner decision, so it fails here first.
        expected = {}
        for vehicle in self.VEHICLES:
            for index, document in enumerate(APPROVED_DOCUMENTS[vehicle]):
                expected[self.DOCUMENT.format(vehicle, index)] = document
            for field, value in zip(SYNTHETIC_CAPACITY, APPROVED_CAPACITY[vehicle]):
                expected[self.CAPACITY.format(vehicle, field)] = str(value)
            expected[self.CAPACITY.format(vehicle, "RequireDimensions")] = "false"
        entries = guards.load_settings(REPO_ROOT / guards.SETTINGS_FILE)
        eligibility = [entry for entry in entries if guards.is_eligibility_name(entry["name"])]
        self.assertEqual(len(expected), len(eligibility))
        self.assertEqual(expected, {entry["name"]: entry["value"] for entry in eligibility})
        self.assertFalse(any("NonExpiringDocumentTypes" in entry["name"] for entry in eligibility))
        self.assertFalse(any(entry["value"] == "BACKGROUND_CHECK" for entry in eligibility))
        self.assertIn({"name": "Finance__OperationalTimeZone", "value": "America/Mazatlan"}, entries)
        # Length, width and height are compared axis by axis, so every row keeps length >= width >= height.
        for vehicle, (_, total, single, length, width, height) in APPROVED_CAPACITY.items():
            with self.subTest(vehicle=vehicle):
                self.assertLessEqual(single, total)
                self.assertGreaterEqual(length, width)
                self.assertGreaterEqual(width, height)

    def test_without_its_rules_the_file_stops_the_deploy_naming_each_vehicle_type_and_setting(self):
        entries = without_eligibility(guards.load_settings(REPO_ROOT / guards.SETTINGS_FILE))
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "apps.settings.json"
            path.write_text(json.dumps(entries), encoding="utf-8")
            result = self.settings_check(path)
        self.assertEqual(1, result.returncode, result.stdout)
        lines = result.stdout.splitlines()
        self.assertEqual("ENV001_SETTINGS=FAIL", lines[-1])
        self.assertEqual(len(self.VEHICLES), len(lines) - 1, lines)
        for vehicle, line in zip(self.VEHICLES, lines):
            self.assertTrue(line.startswith(
                f"STOP_FOR_OWNER_DECISION: driver eligibility rules for vehicle type {vehicle} are missing: "), line)
            for name in self.required_names(vehicle):
                self.assertIn(name, line)

    def test_a_complete_policy_passes(self):
        entries = without_eligibility(guards.load_settings(REPO_ROOT / guards.SETTINGS_FILE)) + SYNTHETIC_ELIGIBILITY
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "apps.settings.json"
            path.write_text(json.dumps(entries), encoding="utf-8")
            result = self.settings_check(path)
        self.assertEqual(0, result.returncode, result.stdout)
        self.assertEqual("ENV001_SETTINGS=PASS", result.stdout.strip())

        # Several document types per vehicle, every document type, the optional members and the largest value each
        # .NET type holds pass too.
        richer = SYNTHETIC_ELIGIBILITY + [
            {"name": self.DOCUMENT.format("CAR", index), "value": document}
            for index, document in enumerate(guards.ELIGIBILITY_DOCUMENT_TYPES[1:], start=1)
        ] + [
            {"name": "Drivers__Eligibility__NonExpiringDocumentTypes__1", "value": "BACKGROUND_CHECK"},
            {"name": self.CAPACITY.format("VAN", "RequireDimensions"), "value": "true"},
            {"name": self.CAPACITY.format("WALKER", "RequireDimensions"), "value": "false"},
        ]
        for field in SYNTHETIC_CAPACITY:
            richer = self.with_value(richer, self.CAPACITY.format("MOTORCYCLE", field),
                                     str(guards.ELIGIBILITY_CAPACITY_FIELDS[field]))
        self.assertEqual("9223372036854775807", next(
            entry["value"] for entry in richer if entry["name"] == self.CAPACITY.format("MOTORCYCLE", "MaximumTotalWeightGrams")))
        self.assertEqual([], guards.validate_settings(richer, allow_sentinel=False))

    def test_each_missing_setting_is_named(self):
        for vehicle in self.VEHICLES:
            for name in self.required_names(vehicle):
                with self.subTest(name=name):
                    entries = [entry for entry in SYNTHETIC_ELIGIBILITY if entry["name"] != name]
                    # The static guard (P17) waits for the owner like it does for OWNER_DECISION_REQUIRED.
                    self.assertEqual([], guards.validate_settings(entries, allow_sentinel=True))
                    failures = guards.validate_settings(entries, allow_sentinel=False)
                    self.assertEqual(1, len(failures), failures)
                    self.assertTrue(failures[0].startswith(
                        f"driver eligibility rules for vehicle type {vehicle} are missing: {name}"), failures)
                    for other in self.required_names(vehicle):
                        if other != name:
                            self.assertNotIn(other, failures[0])

        # Every document type of a vehicle gone: the list may not be empty.
        entries = [entry for entry in SYNTHETIC_ELIGIBILITY + [{"name": self.DOCUMENT.format("VAN", 1), "value": "OTHER"}]
                   if not entry["name"].startswith("Drivers__Eligibility__RequiredDocumentTypesByVehicleType__VAN__")]
        failures = guards.validate_settings(entries, allow_sentinel=False)
        self.assertEqual(["driver eligibility rules for vehicle type VAN are missing: "
                          f"{self.DOCUMENT.format('VAN', 0)} (at least one required document type)"], [f.split(". ")[0] for f in failures])

        # OWNER_DECISION_REQUIRED in place of a value is the owner's placeholder: still missing.
        name = self.CAPACITY.format("VAN", "MaximumWidthMillimeters")
        entries = self.with_value(SYNTHETIC_ELIGIBILITY, name, guards.OWNER_SENTINEL)
        self.assertEqual([], guards.validate_settings(entries, allow_sentinel=True))
        failures = guards.validate_settings(entries, allow_sentinel=False)
        self.assertIn(f"setting {name} still awaits an owner decision ({guards.OWNER_SENTINEL})", failures)
        self.assertTrue(any(f.startswith(f"driver eligibility rules for vehicle type VAN are missing: {name}.") for f in failures),
                        failures)

    def test_present_settings_follow_the_api_start_validation(self):
        car_document = self.DOCUMENT.format("CAR", 0)
        non_expiring = "Drivers__Eligibility__NonExpiringDocumentTypes__0"

        def car(field, value):
            return self.with_value(SYNTHETIC_ELIGIBILITY, self.CAPACITY.format("CAR", field), value)

        def extra(name, value):
            return SYNTHETIC_ELIGIBILITY + [{"name": name, "value": value}]

        # Each of these makes the API refuse to start (its validation, or binding a number that does not fit).
        api_failures = {
            "unknown document type": self.with_value(SYNTHETIC_ELIGIBILITY, car_document, "PASSPORT"),
            "document type in lowercase": self.with_value(SYNTHETIC_ELIGIBILITY, car_document, "identity"),
            "empty document type": self.with_value(SYNTHETIC_ELIGIBILITY, car_document, ""),
            "unknown non-expiring document type": self.with_value(SYNTHETIC_ELIGIBILITY, non_expiring, "PASSPORT"),
            "no packages": car("MaximumPackageCount", "0"),
            "negative weight": car("MaximumTotalWeightGrams", "-1"),
            "not a whole number": car("MaximumLengthMillimeters", "500.5"),
            "package count beyond int": car("MaximumPackageCount", "2147483648"),
            "weight beyond long": car("MaximumTotalWeightGrams", "9223372036854775808"),
            "single package heavier than the total": car("MaximumSinglePackageWeightGrams", "4001"),
            "RequireDimensions not a bool": extra(self.CAPACITY.format("CAR", "RequireDimensions"), "yes"),
            "unknown vehicle type": extra(self.CAPACITY.format("TRUCK", "MaximumPackageCount"), "4"),
            "vehicle type in lowercase": extra(self.DOCUMENT.format("car", 1), "IDENTITY"),
        }
        # The API would bind these, but the guard accepts one spelling only: plain digits, the exact names and numbered
        # lists, so a typo never passes silently.
        strict_spelling = {
            "padded number": car("MaximumWidthMillimeters", " 400"),
            "unknown capacity setting": extra(self.CAPACITY.format("CAR", "MaximumVolumeLiters"), "10"),
            "list without an index": extra("Drivers__Eligibility__RequiredDocumentTypesByVehicleType__CAR", "IDENTITY"),
            "index that is not a number": extra(self.DOCUMENT.format("CAR", "first"), "IDENTITY"),
            "another member": extra("Drivers__Eligibility__MaximumDistanceKilometers", "10"),
            "member name in another casing": [
                dict(entry, name=entry["name"].lower().replace("__car__", "__CAR__"))
                if entry["name"] == self.CAPACITY.format("CAR", "MaximumPackageCount") else entry
                for entry in SYNTHETIC_ELIGIBILITY],
        }
        for case, entries in {**api_failures, **strict_spelling}.items():
            for allow_sentinel in (True, False):
                with self.subTest(case=case, allow_sentinel=allow_sentinel):
                    failures = guards.validate_settings(entries, allow_sentinel=allow_sentinel)
                    self.assertTrue(failures)
                    self.assertTrue(all("Eligibility" in f or "eligibility" in f for f in failures), failures)

        failures = guards.validate_settings(api_failures["single package heavier than the total"], allow_sentinel=True)
        self.assertEqual(["Drivers__Eligibility__VehicleCapacity__CAR__MaximumSinglePackageWeightGrams may not exceed "
                          "MaximumTotalWeightGrams"], failures)
        failures = guards.validate_settings(strict_spelling["member name in another casing"], allow_sentinel=False)
        self.assertEqual(["setting drivers__eligibility__vehiclecapacity__CAR__maximumpackagecount must be spelled exactly "
                          "Drivers__Eligibility__VehicleCapacity__CAR__MaximumPackageCount (configuration names ignore "
                          "case, so another casing still reaches the API)"], failures)
        failures = guards.validate_settings(api_failures["vehicle type in lowercase"], allow_sentinel=False)
        self.assertEqual(["setting Drivers__Eligibility__RequiredDocumentTypesByVehicleType__car__1 names no vehicle type "
                          "the API knows (MOTORCYCLE, CAR, VAN, BICYCLE, WALKER, in capitals)"], failures)

    def test_the_guard_mirrors_the_api_start_validation(self):
        """Drift check: the vehicle types, document types and capacity members are the ones the C# validators and every
        class bound to Drivers:Eligibility use, and the pilot API runs both providers that validate them."""
        for path in ("src/Modules/Drivers/Drivers.Infrastructure/DependencyInjection.cs",
                     "src/Modules/Routing/Routing.Infrastructure/DependencyInjection.cs"):
            with self.subTest(path=path):
                source = (REPO_ROOT / path).read_text(encoding="utf-8")
                vehicles = re.search(r"\bVehicleTypes = \[([^\]]*)\]", source)
                documents = re.search(r"\bDocumentTypes =\s*\[([^\]]*)\]", source)
                self.assertIsNotNone(vehicles)
                self.assertIsNotNone(documents)
                self.assertEqual(guards.ELIGIBILITY_VEHICLE_TYPES, tuple(re.findall(r'"([A-Z_]+)"', vehicles.group(1))))
                self.assertEqual(guards.ELIGIBILITY_DOCUMENT_TYPES, tuple(re.findall(r'"([A-Z_]+)"', documents.group(1))))
                # An empty document list fails the API, so the guard requires one document type per vehicle.
                self.assertRegex(source, r"\b[dD]ocument(Type)?s\.Count == 0")

        expected = {field: "int" if maximum == 2**31 - 1 else "long"
                    for field, maximum in guards.ELIGIBILITY_CAPACITY_FIELDS.items()}
        expected[guards.ELIGIBILITY_REQUIRE_DIMENSIONS] = "bool"
        for path, name in (
            ("src/Modules/Drivers/Drivers.Infrastructure/DriversOptions.cs", "VehicleCapacityOptions"),
            ("src/Modules/Routing/Routing.Infrastructure/RoutingOptions.cs", "RoutingVehicleCapacityOptions"),
            ("src/Modules/Dispatch/Dispatch.Infrastructure/DispatchOptions.cs", "DispatchVehicleCapacityOptions"),
            ("src/Modules/Orders/Orders.Infrastructure/Orders/PostgreSqlOrderTransitionEligibility.cs",
             "OrderTransitionVehicleCapacityOptions"),
        ):
            with self.subTest(path=path):
                source = (REPO_ROOT / path).read_text(encoding="utf-8")
                body = re.search(r"public sealed class " + name + r"\n\{\n(.*?)\n\}", source, re.DOTALL)
                self.assertIsNotNone(body)
                properties = {member: kind for kind, member in
                              re.findall(r"public (\w+) (\w+) \{ get; set; \}", body.group(1))}
                self.assertEqual(expected, properties)

        # The synthetic policy that passes settings-check here is the one the OrderTransitionGuards API tests start the
        # API with, so the guard and the API agree on a complete policy as well as on a missing one (the repository
        # file itself starts the pilot API in PilotApiStartupConfigurationTests).
        fixture = (REPO_ROOT / "tests/Paqueteria.IntegrationTests/Orders/OrderTransitionGuardsHttpFixture.cs").read_text(
            encoding="utf-8")
        body = re.search(r"Dictionary<string, string\?> CreateEligibility\(\)\n    \{\n(.*?)\n    \}\n", fixture, re.DOTALL)
        self.assertIsNotNone(body)
        vehicles = re.search(r"foreach \(var vehicle in new\[\] \{([^}]*)\}\)", body.group(1))
        self.assertIsNotNone(vehicles)
        vehicles = re.findall(r'"([A-Z]+)"', vehicles.group(1))
        fixed = re.findall(r'\["(Drivers:Eligibility:[^"]+)"\] = "([^"]*)"', body.group(1))
        templated = re.findall(r'settings\[\$"(Drivers:Eligibility:[^"]+)"\] = "([^"]*)"', body.group(1))
        self.assertTrue(fixed and templated)
        expected = {(key.replace(":", "__"), value) for key, value in fixed}
        expected |= {(key.replace("{vehicle}", vehicle).replace(":", "__"), value)
                     for key, value in templated for vehicle in vehicles}
        self.assertEqual(expected, {(entry["name"], entry["value"]) for entry in SYNTHETIC_ELIGIBILITY})

        bicep = (REPO_ROOT / guards.PILOT_DIR / "apps.bicep").read_text(encoding="utf-8")
        api_env = re.search(r"^var apiEnv = concat\(.*?\n\], businessSettings\)", bicep, re.MULTILINE | re.DOTALL)
        self.assertIsNotNone(api_env)
        for provider in ("Drivers__Provider", "Routing__Provider"):
            self.assertRegex(api_env.group(0), r"name: '" + provider + r"'\s*\n\s*value: 'PostgreSql'")


class ObservabilityParametersTests(unittest.TestCase):
    """OBS-002: the alert e-mail is an owner value; the file carries nothing else."""

    @staticmethod
    def document(**parameters):
        return {"$schema": "x", "contentVersion": "1.0.0.0", "parameters": {k: {"value": v} for k, v in parameters.items()}}

    def test_repository_parameters_are_well_formed_but_await_the_owner(self):
        document = json.loads((REPO_ROOT / guards.OBSERVABILITY_PARAMETERS_FILE).read_text(encoding="utf-8"))
        self.assertEqual(guards.OWNER_SENTINEL, document["parameters"]["alertEmailAddress"]["value"])
        self.assertEqual([], guards.validate_observability_parameters(document, allow_sentinel=True))
        failures = guards.validate_observability_parameters(document, allow_sentinel=False)
        self.assertTrue(any(guards.OWNER_SENTINEL in f for f in failures), failures)

    def test_a_single_email_and_documented_thresholds_pass(self):
        self.assertEqual([], guards.validate_observability_parameters(
            self.document(alertEmailAddress="ops@paquetenvia.com", outboxLagThresholdSeconds=600), allow_sentinel=False))

    def test_malformed_or_unexpected_parameters_fail(self):
        cases = {
            "not an address": self.document(alertEmailAddress="ops"),
            "two addresses": self.document(alertEmailAddress="a@x.com,b@x.com"),
            "padded": self.document(alertEmailAddress=" ops@x.com"),
            "missing": self.document(outboxLagThresholdSeconds=300),
            "unknown parameter": self.document(alertEmailAddress="ops@x.com", webhookUrl="https://x"),
            "wrong type": self.document(alertEmailAddress="ops@x.com", outboxLagThresholdSeconds="300"),
            "not a parameters file": {"alertEmailAddress": "ops@x.com"},
        }
        for name, document in cases.items():
            with self.subTest(name=name):
                self.assertTrue(guards.validate_observability_parameters(document, allow_sentinel=True))

    def test_cli_stops_for_the_owner_decision(self):
        result = subprocess.run([sys.executable, guards.__file__, "observability-check", "--file",
                                 str(REPO_ROOT / guards.OBSERVABILITY_PARAMETERS_FILE)], capture_output=True, text=True)
        self.assertEqual(1, result.returncode)
        self.assertIn("STOP_FOR_OWNER_DECISION", result.stdout)
        allowed = subprocess.run([sys.executable, guards.__file__, "observability-check", "--allow-owner-sentinel", "--file",
                                  str(REPO_ROOT / guards.OBSERVABILITY_PARAMETERS_FILE)], capture_output=True, text=True)
        self.assertEqual(0, allowed.returncode, allowed.stdout)


class WebParametersTests(unittest.TestCase):
    """UI-001: the accepted terms/privacy versions are owner values (with the approved notice, GATE-007);
    the file carries exactly those two, in the AI-05 format, and the deploy fails closed without them."""

    @staticmethod
    def document(**parameters):
        return {"$schema": "x", "contentVersion": "1.0.0.0", "parameters": {k: {"value": v} for k, v in parameters.items()}}

    def test_repository_parameters_are_well_formed_but_await_the_owner(self):
        document = json.loads((REPO_ROOT / guards.WEB_PARAMETERS_FILE).read_text(encoding="utf-8"))
        self.assertEqual({"webTermsVersion", "webPrivacyVersion"}, set(document["parameters"]))
        for name in ("webTermsVersion", "webPrivacyVersion"):
            self.assertEqual(guards.OWNER_SENTINEL, document["parameters"][name]["value"])
        self.assertEqual([], guards.validate_web_parameters(document, allow_sentinel=True))
        failures = guards.validate_web_parameters(document, allow_sentinel=False)
        self.assertEqual(2, len(failures), failures)
        self.assertTrue(any("PAQUETERIA_TERMS_VERSION" in f and guards.OWNER_SENTINEL in f for f in failures), failures)
        self.assertTrue(any("PAQUETERIA_PRIVACY_VERSION" in f and guards.OWNER_SENTINEL in f for f in failures), failures)

    def test_versions_in_the_ai05_format_pass(self):
        for terms, privacy in (("2026-10-01", "2026-10-01"), ("terminos.v1", "aviso_privacidad-v1"), ("A" * 64, "z"), ("1", "a.B_c-9")):
            with self.subTest(terms=terms, privacy=privacy):
                self.assertEqual([], guards.validate_web_parameters(
                    self.document(webTermsVersion=terms, webPrivacyVersion=privacy), allow_sentinel=False))

    def test_malformed_missing_or_unexpected_values_fail(self):
        good = "2026-10-01"
        cases = {
            "empty terms": self.document(webTermsVersion="", webPrivacyVersion=good),
            "empty privacy": self.document(webTermsVersion=good, webPrivacyVersion=""),
            "65 characters": self.document(webTermsVersion="a" * 65, webPrivacyVersion=good),
            "space": self.document(webTermsVersion="v 1", webPrivacyVersion=good),
            "padded": self.document(webTermsVersion=good, webPrivacyVersion=" 2026-10-01"),
            "trailing newline": self.document(webTermsVersion="2026-10-01\n", webPrivacyVersion=good),
            "slash": self.document(webTermsVersion="2026/10/01", webPrivacyVersion=good),
            "colon": self.document(webTermsVersion=good, webPrivacyVersion="v:1"),
            "non-ascii": self.document(webTermsVersion="términos-1", webPrivacyVersion=good),
            "plus": self.document(webTermsVersion="v1+2", webPrivacyVersion=good),
            "missing privacy": self.document(webTermsVersion=good),
            "missing terms": self.document(webPrivacyVersion=good),
            "number": self.document(webTermsVersion=20261001, webPrivacyVersion=good),
            "null": self.document(webTermsVersion=None, webPrivacyVersion=good),
            "unknown parameter": self.document(webTermsVersion=good, webPrivacyVersion=good, webPricingVersion=good),
            "extra key": {"parameters": {"webTermsVersion": {"value": good, "reference": "x"}, "webPrivacyVersion": {"value": good}}},
            "not a parameters file": {"webTermsVersion": good, "webPrivacyVersion": good},
        }
        for name, document in cases.items():
            with self.subTest(name=name):
                self.assertTrue(guards.validate_web_parameters(document, allow_sentinel=True))

    def test_the_sentinel_never_passes_the_deploy_check(self):
        for value in (guards.OWNER_SENTINEL, "owner_decision_required", " OWNER_DECISION_REQUIRED "):
            with self.subTest(value=value):
                failures = guards.validate_web_parameters(
                    self.document(webTermsVersion=value, webPrivacyVersion="2026-10-01"), allow_sentinel=False)
                self.assertEqual(1, len(failures), failures)
                self.assertIn("PAQUETERIA_TERMS_VERSION", failures[0])

    def test_cli_stops_for_the_owner_decision(self):
        result = subprocess.run([sys.executable, guards.__file__, "web-check", "--file",
                                 str(REPO_ROOT / guards.WEB_PARAMETERS_FILE)], capture_output=True, text=True)
        self.assertEqual(1, result.returncode)
        self.assertIn("STOP_FOR_OWNER_DECISION: webTermsVersion (PAQUETERIA_TERMS_VERSION)", result.stdout)
        self.assertIn("STOP_FOR_OWNER_DECISION: webPrivacyVersion (PAQUETERIA_PRIVACY_VERSION)", result.stdout)
        self.assertIn("ENV001_WEB_VERSIONS=FAIL", result.stdout)
        allowed = subprocess.run([sys.executable, guards.__file__, "web-check", "--allow-owner-sentinel", "--file",
                                  str(REPO_ROOT / guards.WEB_PARAMETERS_FILE)], capture_output=True, text=True)
        self.assertEqual(0, allowed.returncode, allowed.stdout)

    def test_cli_passes_owner_values_and_fails_on_unreadable_files(self):
        with tempfile.TemporaryDirectory(prefix="env001-web-") as directory:
            path = Path(directory) / "web.parameters.json"
            path.write_text(json.dumps(self.document(webTermsVersion="2026-10-01", webPrivacyVersion="2026-10-01")), encoding="utf-8")
            ok = subprocess.run([sys.executable, guards.__file__, "web-check", "--file", str(path)], capture_output=True, text=True)
            self.assertEqual(0, ok.returncode, ok.stdout)
            self.assertIn("ENV001_WEB_VERSIONS=PASS", ok.stdout)
            path.write_text("{not json", encoding="utf-8")
            bad = subprocess.run([sys.executable, guards.__file__, "web-check", "--file", str(path)], capture_output=True, text=True)
            self.assertEqual(1, bad.returncode)
            missing = subprocess.run([sys.executable, guards.__file__, "web-check", "--file", str(Path(directory) / "none.json")],
                                     capture_output=True, text=True)
            self.assertEqual(1, missing.returncode)


SAMPLE_LOG = """# Decision log

| Date | ID | Type | Decision | Impacted files | Approved by |
|---|---|---|---|---|---|
| 2026-09-27 | GATE-007-PRIVACY-DRAFT | Legal process decision | Claude drafts the notice; resolves nothing (GATE-007 stays open) | GATE-007 | Owner |
| 2026-09-27 | PILOT-REAL-PEOPLE | Release decision | Real PII still waits for GATE-007 approval; resolves no gate | ENV-001 | Owner |
| 2026-08-04 | NTF-001-OWNER-001 | Product decision | Mentions GATE-004 and GATE-007 in passing | NTF-001 | Owner |
| 2026-09-28 | GATE-012-PILOT-SCOPE | Gate scoping | Mexico Central only \\| 14-day PITR | GATE-012 | Owner |
| 2026-09-29 | GATE-007-DUPLICATED | Gate resolution | first | GATE-007 | Owner |
| 2026-09-29 | GATE-007-DUPLICATED | Gate resolution | second | GATE-007 | Owner |
| 2026-09-29 | GATE-007-WRONG-TYPE | Gate decision | not a resolution type | GATE-007 | Owner |
| 2026-09-30 | GATE-007-APPROVED | Gate resolution | Approved with an unescaped | pipe later in the text | GATE-007 | Owner |
"""


class GateDecisionTests(unittest.TestCase):
    def check(self, gate, decision_id, text=SAMPLE_LOG):
        return guards.validate_gate_decision(text, gate, decision_id)

    def test_scoping_and_resolution_rows_are_accepted(self):
        self.assertEqual([], self.check("012", "GATE-012-PILOT-SCOPE"))
        self.assertEqual([], self.check("007", "GATE-007-APPROVED"))

    def test_rows_that_do_not_resolve_or_scope_the_gate_are_rejected(self):
        cases = {
            "GATE-007-PRIVACY-DRAFT": "Type 'Legal process decision'",
            "PILOT-REAL-PEOPLE": "not a GATE-007 decision",
            "NTF-001-OWNER-001": "not a GATE-007 decision",
            "": "missing or malformed",
            "GATE-007-UNKNOWN": "not recorded",
            "GATE-007-DUPLICATED": "appears 2 times",
            "GATE-007-WRONG-TYPE": "Type 'Gate decision'",
            "GATE-012-PILOT-SCOPE": "not a GATE-007 decision",
            "gate-007-approved": "missing or malformed",
        }
        for decision_id, fragment in cases.items():
            with self.subTest(decision_id=decision_id):
                failures = self.check("007", decision_id)
                self.assertEqual(1, len(failures), failures)
                self.assertIn(fragment, failures[0])

    def test_escaped_pipes_and_foreign_tables_do_not_shift_columns(self):
        self.assertEqual(["| a", "b |"], [c for c in guards.split_markdown_row("| \\| a | b \\| |")])
        other_table = "| ID | Type |\n|---|---|\n| GATE-012-PILOT-SCOPE | Gate scoping |\n"
        self.assertIn("table not found", self.check("012", "GATE-012-PILOT-SCOPE", other_table)[0])

    def test_real_decision_log_scopes_gate_012_for_the_pilot(self):
        text = (REPO_ROOT / guards.DECISION_LOG).read_text(encoding="utf-8")
        self.assertEqual([], guards.validate_gate_decision(text, "012", "GATE-012-PILOT-SCOPE"))
        self.assertTrue(guards.validate_gate_decision(text, "007", "GATE-007-PRIVACY-DRAFT"))
        self.assertTrue(guards.validate_gate_decision(text, "007", "PILOT-REAL-PEOPLE"))

    def test_cli_exit_codes(self):
        ok = subprocess.run([sys.executable, guards.__file__, "gate-decision", "--gate", "012", "--decision-id", "GATE-012-PILOT-SCOPE",
                             "--decision-log", str(REPO_ROOT / guards.DECISION_LOG)], capture_output=True, text=True)
        self.assertEqual(0, ok.returncode, ok.stdout)
        bad = subprocess.run([sys.executable, guards.__file__, "gate-decision", "--gate", "007", "--decision-id", "GATE-007-PRIVACY-DRAFT",
                              "--decision-log", str(REPO_ROOT / guards.DECISION_LOG)], capture_output=True, text=True)
        self.assertEqual(1, bad.returncode)
        self.assertIn("STOP_FOR_OWNER_DECISION", bad.stdout)


class NonRootImageTests(unittest.TestCase):
    """P24 (AZR-001-AC-064): the final stage of each of the four pilot images runs as a non-root USER."""

    DOTNET = "FROM mcr.microsoft.com/dotnet/sdk:10.0.101 AS build\nRUN dotnet publish -o /out\n\nFROM mcr.microsoft.com/dotnet/aspnet:10.0\nWORKDIR /app\nCOPY --from=build /out ./\n"
    NODE = "FROM node:24-bookworm-slim AS build\nRUN pnpm build\n\nFROM node:24-bookworm-slim\nWORKDIR /app\n"

    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory(prefix="env001-nonroot-")
        self.addCleanup(self.tmp.cleanup)
        self.root = Path(self.tmp.name)
        self.workflow = (REPO_ROOT / guards.PILOT_WORKFLOW).read_text(encoding="utf-8")
        for name in guards.PILOT_DOCKERFILES:
            body = self.NODE + "USER node\n" if name.endswith("Dockerfile.web") else self.DOTNET + "USER $APP_UID\n"
            self.write(name, body + 'ENTRYPOINT ["x"]\n')

    def write(self, name: str, text: str) -> None:
        path = self.root / name
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_text(text, encoding="utf-8")

    def failures(self) -> list[str]:
        return guards.pilot_image_user_failures(self.root, self.workflow)

    def test_repository_images_run_as_non_root(self):
        self.assertEqual([], guards.pilot_image_user_failures(REPO_ROOT, self.workflow))
        self.assertEqual(sorted(guards.PILOT_DOCKERFILES), sorted(guards.WORKFLOW_IMAGE_BUILD.findall(self.workflow)))

    def test_api_and_worker_without_their_user_line_fail(self):
        for name in ("deploy/azure/Dockerfile.api", "deploy/azure/Dockerfile.worker"):
            with self.subTest(name=name):
                text = (REPO_ROOT / name).read_text(encoding="utf-8")
                self.assertEqual(1, text.count("\nUSER $APP_UID\n"))
                failure = guards.dockerfile_non_root_failure(name, text.replace("\nUSER $APP_UID\n", "\n"))
                self.assertIn("final stage has no USER", failure or "")

    def test_conformant_fixture_passes(self):
        self.assertEqual([], self.failures())

    def test_root_users_fail(self):
        cases = {
            "no USER": self.DOTNET,
            "USER root": self.DOTNET + "USER root\n",
            "USER ROOT": self.DOTNET + "USER ROOT\n",
            "USER 0": self.DOTNET + "USER 0\n",
            "USER 0:0": self.DOTNET + "USER 0:0\n",
            "USER root:app": self.DOTNET + "USER root:app\n",
            "lowercase user root": self.DOTNET + "user root\n",
            "back to root": self.DOTNET + "USER $APP_UID\nRUN true\nUSER root\n",
            "USER only in the build stage": self.DOTNET.replace("RUN dotnet", "USER $APP_UID\nRUN dotnet"),
            "commented USER": self.DOTNET + "# USER $APP_UID\n",
            "unresolved variable": self.DOTNET + "USER $OTHER_UID\n",
            "APP_UID redeclared by ARG": self.DOTNET + "ARG APP_UID=0\nUSER $APP_UID\n",
            "APP_UID redeclared by ENV": self.DOTNET + "ENV A=1 APP_UID=0\nUSER ${APP_UID}\n",
            "escape directive": "# escape=`\n" + self.DOTNET + "USER $APP_UID\n",
        }
        for case, text in cases.items():
            with self.subTest(case=case):
                self.write("deploy/azure/Dockerfile.api", text + 'ENTRYPOINT ["x"]\n')
                failures = self.failures()
                self.assertEqual(1, len(failures), failures)
                self.assertTrue(failures[0].startswith("deploy/azure/Dockerfile.api"), failures)

    def test_app_uid_needs_an_official_dotnet_base(self):
        self.write("deploy/azure/pilot/Dockerfile.web", self.NODE + "USER $APP_UID\n")
        failures = self.failures()
        self.assertEqual(1, len(failures), failures)
        self.assertIn("not an official .NET image", failures[0])

    def test_accepted_non_root_forms(self):
        for user in ("$APP_UID", "${APP_UID}", "$APP_UID:$APP_UID", "app", "1654", "1654:1654", "app:app"):
            with self.subTest(user=user):
                self.write("deploy/azure/Dockerfile.worker", self.DOTNET + f"USER {user}\n")
                self.assertEqual([], self.failures())

    def test_continuations_and_comments_are_read_like_docker(self):
        text = self.DOTNET.replace("COPY --from=build /out ./\n", "COPY --from=build \\\n  # a comment inside the continuation\n\n  /out ./\n")
        self.write("deploy/azure/Dockerfile.api", text + "USER \\\n  $APP_UID\n")
        self.assertEqual([], self.failures())
        self.assertEqual(("COPY", "--from=build /out ./"), guards.dockerfile_instructions(text)[-1])

    def test_missing_dockerfile_fails(self):
        (self.root / "deploy/azure/pilot/Dockerfile.db-ops").unlink()
        self.assertEqual(["deploy/azure/pilot/Dockerfile.db-ops is missing"], self.failures())

    def test_workflow_must_build_exactly_the_checked_images(self):
        extra = self.workflow.replace(
            '          echo "api=$(build api deploy/azure/Dockerfile.api)" >> "${GITHUB_OUTPUT}"\n',
            '          echo "api=$(build api deploy/azure/Dockerfile.api)" >> "${GITHUB_OUTPUT}"\n'
            '          echo "tool=$(build tool deploy/azure/Dockerfile.tool)" >> "${GITHUB_OUTPUT}"\n', 1)
        missing = self.workflow.replace('          echo "worker=$(build worker deploy/azure/Dockerfile.worker)" >> "${GITHUB_OUTPUT}"\n', "", 1)
        for case, workflow in (("fifth image", extra), ("worker not built", missing)):
            with self.subTest(case=case):
                self.assertNotEqual(self.workflow, workflow)
                failures = guards.pilot_image_user_failures(self.root, workflow)
                self.assertEqual(1, len(failures), failures)
                self.assertIn("the workflow must build exactly the images", failures[0])


@unittest.skipUnless(_bicep(), "pinned Bicep CLI not available (set BICEP_BIN); the azr-static CI job installs it")
class TemplateGuardTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.tmp = tempfile.TemporaryDirectory(prefix="env001-guards-")
        cls.arm = Path(cls.tmp.name) / "arm"
        cls.arm.mkdir()
        for template in sorted((REPO_ROOT / guards.PILOT_DIR).glob("*.bicep")):
            subprocess.run([_bicep(), "build", str(template), "--outdir", str(cls.arm)], check=True, capture_output=True)
        cls.compiled = {p.stem: json.loads(p.read_text(encoding="utf-8")) for p in cls.arm.glob("*.json")}

    @classmethod
    def tearDownClass(cls):
        cls.tmp.cleanup()

    def context(self, mutate=None, workflow_text=None) -> guards.Context:
        ctx = guards.load_context(REPO_ROOT, self.arm)
        ctx.templates = copy.deepcopy(self.compiled)
        if mutate:
            mutate(ctx.templates)
        if workflow_text is not None:
            ctx.workflow_text = workflow_text
            ctx.workflow = guards.yaml.safe_load(workflow_text)
            if True in ctx.workflow:
                ctx.workflow["on"] = ctx.workflow.pop(True)
        return ctx

    def results(self, ctx: guards.Context) -> dict[int, guards.GuardResult]:
        return {r.number: r for r in guards.run_guards(ctx)}

    def assert_fails(self, number: int, ctx: guards.Context, fragment: str):
        results = guards.run_guards(ctx)
        failing = [r for r in results if not r.passed]
        self.assertTrue(any(r.number == number and fragment in r.detail for r in failing), [(r.number, r.status, r.detail) for r in failing])

    @staticmethod
    def resource(templates, template: str, resource_type: str, name_fragment: str = ""):
        raw = templates[template]["resources"]
        items = raw.values() if isinstance(raw, dict) else raw
        return next(r for r in items if r.get("type") == resource_type and name_fragment in str(r.get("name")) and not r.get("existing"))

    def test_repository_templates_pass_every_guard(self):
        results = guards.run_guards(self.context())
        self.assertEqual([], [(r.number, r.status, r.detail) for r in results if not r.passed])
        self.assertEqual(len(guards.GUARDS), len(results))

    def test_public_postgres_fails(self):
        def mutate(t):
            self.resource(t, "platform", "Microsoft.DBforPostgreSQL/flexibleServers")["properties"]["network"]["publicNetworkAccess"] = "Enabled"
        self.assert_fails(11, self.context(mutate), "private access")

    def test_second_api_replica_fails(self):
        def mutate(t):
            self.resource(t, "apps", "Microsoft.App/containerApps", "ca-pv-pilot-api")["properties"]["template"]["scale"]["maxReplicas"] = 2
        self.assert_fails(8, self.context(mutate), "exactly one replica")

    def test_route_drift_fails(self):
        def drop_hubs(t):
            rules = self.resource(t, "apps", "Microsoft.App/managedEnvironments/httpRouteConfigs")["properties"]["rules"]
            rules[0]["routes"] = [r for r in rules[0]["routes"] if r["match"].get("pathSeparatedPrefix") != "/hubs"]
        self.assert_fails(10, self.context(drop_hubs), "API rule must match")

        def web_first(t):
            rules = self.resource(t, "apps", "Microsoft.App/managedEnvironments/httpRouteConfigs")["properties"]["rules"]
            rules.reverse()
        self.assert_fails(10, self.context(web_first), "first rule must target the API")

        def external_api(t):
            self.resource(t, "apps", "Microsoft.App/containerApps", "ca-pv-pilot-api")["properties"]["configuration"]["ingress"]["external"] = True
        self.assert_fails(10, self.context(external_api), "must be internal")

    def test_mock_or_synthetic_value_fails(self):
        for value in ("Mock", "mock", "Synthetic"):
            def mutate(t, value=value):
                t["apps"]["variables"]["productionEnv"].append({"name": "Notifications__Channel", "value": value})
            with self.subTest(value=value):
                self.assert_fails(7, self.context(mutate), value)

    def test_container_apps_secrets_and_secret_refs_are_forbidden(self):
        def kv_reference(t):
            api = self.resource(t, "apps", "Microsoft.App/containerApps", "ca-pv-pilot-api")
            api["properties"]["configuration"]["secrets"] = [
                {"name": "pg-api-conn", "keyVaultUrl": "https://kv/secrets/pg-api-runtime-connection", "identity": "x"}]
        self.assert_fails(6, self.context(kv_reference), "declares Container Apps secrets")

        def secret_ref(t):
            t["apps"]["variables"]["productionEnv"].append({"name": "Some__Setting", "secretRef": "x"})
        self.assert_fails(6, self.context(secret_ref), "uses secretRef")

        def plain_connection(t):
            t["apps"]["variables"]["productionEnv"].append({"name": "ConnectionStrings__Paqueteria", "value": "Host=x"})
        self.assert_fails(6, self.context(plain_connection), "sensitive setting ConnectionStrings__Paqueteria")

    @staticmethod
    def edit(t, template: str, old: str, new: str, count: int = 1):
        """Edit the compiled ARM text (Bicep inlines env arrays that use reference() into expressions)."""
        raw = json.dumps(t[template])
        assert raw.count(old) >= 1, old
        t[template] = json.loads(raw.replace(old, new, count))

    def test_key_vault_mappings_must_match_least_privilege_and_rbac(self):
        def foreign_secret(t):
            self.edit(t, "apps", "'value', 'paquetenvia-email-lookup-key-1'", "'value', 'pg-migrate-connection'")
        self.assert_fails(6, self.context(foreign_secret), "differ from the least-privilege set")

        def rbac_drift(t):
            t["apps"]["variables"]["apiSecretNames"] = [n for n in t["apps"]["variables"]["apiSecretNames"]
                                                        if n != "paquetenvia-email-lookup-key-1"]
        self.assert_fails(6, self.context(rbac_drift), "apps.apiSecretNames")

        def logins_on_migrate_identity(t):
            t["jobs"]["variables"]["migrateSecretNames"] = list(t["jobs"]["variables"]["loginsSecretNames"])
        self.assert_fails(6, self.context(logins_on_migrate_identity), "jobs.migrateSecretNames")

        def duplicate_key(t):
            self.edit(t, "apps", "'KeyVaultSecrets__Mappings__1__ConfigurationKey', 'value', 'ConnectionStrings:Paqueteria')",
                      "'KeyVaultSecrets__Mappings__1__ConfigurationKey', 'value', 'ConnectionStrings:PaqueteriaWorker')")
        self.assert_fails(6, self.context(duplicate_key), "duplicates")

        def worker_second_secret(t):
            self.edit(t, "apps", "'KeyVaultSecrets__Mappings__1__SecretName', 'value', 'pg-worker-runtime-connection')",
                      "'KeyVaultSecrets__Mappings__1__SecretName', 'value', 'pg-worker-custody-connection')")
        self.assert_fails(6, self.context(worker_second_secret), "differ from the least-privilege set")

        def duplicated_grant(t):
            t["apps"]["variables"]["workerSecretNames"] = ["pg-worker-runtime-connection", "pg-worker-runtime-connection"]
        self.assert_fails(6, self.context(duplicated_grant), "apps.workerSecretNames")

        def no_vault_uri(t):
            raw = json.dumps(t["jobs"])
            start = raw.index("createObject('name', 'KeyVaultSecrets__VaultUri'")
            end = raw.index("createObject('name', 'KeyVaultSecrets__Mappings__0__SecretName'", start)
            t["jobs"] = json.loads(raw[:start] + raw[end:])
        self.assert_fails(6, self.context(no_vault_uri), "without KeyVaultSecrets__VaultUri")

    def test_key_vault_must_deny_public_traffic(self):
        def allow(t):
            self.resource(t, "security", "Microsoft.KeyVault/vaults")["properties"]["networkAcls"]["defaultAction"] = "Allow"
        self.assert_fails(13, self.context(allow), "defaultAction must be Deny")

        def trusted_services(t):
            self.resource(t, "security", "Microsoft.KeyVault/vaults")["properties"]["networkAcls"]["bypass"] = "AzureServices"
        self.assert_fails(13, self.context(trusted_services), "bypass must be None")

        def standing_ip(t):
            self.resource(t, "security", "Microsoft.KeyVault/vaults")["properties"]["networkAcls"]["ipRules"] = [{"value": "1.2.3.4/32"}]
        self.assert_fails(13, self.context(standing_ip), "standing ipRules")

        def no_subnet(t):
            self.resource(t, "security", "Microsoft.KeyVault/vaults")["properties"]["networkAcls"]["virtualNetworkRules"] = []
        self.assert_fails(13, self.context(no_subnet), "Container Apps subnet")

        text = (REPO_ROOT / guards.PILOT_WORKFLOW).read_text(encoding="utf-8")
        self.assert_fails(13, self.context(workflow_text=text.replace("kv-firewall.sh close", "true")), "temporary Key Vault firewall")

    def test_storage_and_defender_fail_closed(self):
        def shared_key(t):
            self.resource(t, "platform", "Microsoft.Storage/storageAccounts")["properties"]["allowSharedKeyAccess"] = True
        self.assert_fails(12, self.context(shared_key), "allowSharedKeyAccess")

        def no_scan(t):
            self.resource(t, "platform", "Microsoft.Security/defenderForStorageSettings")["properties"]["malwareScanning"]["onUpload"]["isEnabled"] = False
        self.assert_fails(12, self.context(no_scan), "malware scanning")

    def test_proof_soft_delete_is_30_days_with_versioning_off(self):
        """ADP-001-PHOTO-SOFT-DELETE-30D-2026-10-20: blob and container soft delete exactly 30 days, versioning off
        (an overwrite is kept as a soft-deleted snapshot), no permanent delete."""
        def service(t):
            return self.resource(t, "platform", "Microsoft.Storage/storageAccounts/blobServices")["properties"]

        compiled = service(self.compiled)
        self.assertIs(False, compiled["isVersioningEnabled"])
        for policy in guards.PROOF_SOFT_DELETE_POLICIES:
            self.assertEqual({"enabled": True, "days": 30}, compiled[policy])
        self.assertEqual(30, guards.PROOF_SOFT_DELETE_DAYS)

        fragment = "must be enabled for exactly 30 days"
        mutations = {
            "blob back to 7 days": (lambda t: service(t)["deleteRetentionPolicy"].update(days=7), fragment),
            "blob longer than decided": (lambda t: service(t)["deleteRetentionPolicy"].update(days=31), fragment),
            "blob days as text": (lambda t: service(t)["deleteRetentionPolicy"].update(days="30"), fragment),
            "blob days as a parameter": (lambda t: service(t)["deleteRetentionPolicy"].update(days="[parameters('days')]"), fragment),
            "blob soft delete off": (lambda t: service(t)["deleteRetentionPolicy"].update(enabled=False), fragment),
            "blob policy removed": (lambda t: service(t).pop("deleteRetentionPolicy"), "deleteRetentionPolicy must be enabled"),
            "container back to 7 days": (lambda t: service(t)["containerDeleteRetentionPolicy"].update(days=7), fragment),
            "container soft delete off": (lambda t: service(t)["containerDeleteRetentionPolicy"].update(enabled=False), fragment),
            "container policy removed": (lambda t: service(t).pop("containerDeleteRetentionPolicy"),
                                         "containerDeleteRetentionPolicy must be enabled"),
            "permanent delete": (lambda t: service(t)["deleteRetentionPolicy"].update(allowPermanentDelete=True), "must not allow permanent delete"),
            "versioning on": (lambda t: service(t).update(isVersioningEnabled=True), "blob versioning must be off"),
            "versioning unset": (lambda t: service(t).pop("isVersioningEnabled"), "blob versioning must be off"),
        }
        for name, (mutate, expected) in mutations.items():
            with self.subTest(name=name):
                self.assert_fails(12, self.context(mutate), expected)

        def no_blob_service(t):
            t["platform"]["resources"].remove(self.resource(t, "platform", "Microsoft.Storage/storageAccounts/blobServices"))
        self.assert_fails(12, self.context(no_blob_service), "expected one blob service, found 0")

        def second_blob_service(t):
            t["platform"]["resources"].append(copy.deepcopy(self.resource(t, "platform", "Microsoft.Storage/storageAccounts/blobServices")))
        self.assert_fails(12, self.context(second_blob_service), "expected one blob service, found 2")

    def test_forbidden_resources_and_roles_fail(self):
        def redis(t):
            t["platform"]["resources"].append({"type": "Microsoft.Cache/redis", "apiVersion": "2024-03-01", "name": "r"})
        self.assert_fails(1, self.context(redis), "Microsoft.Cache/redis")

        def owner(t):
            assignment = copy.deepcopy(self.resource(t, "platform", "Microsoft.Authorization/roleAssignments"))
            assignment["properties"]["roleDefinitionId"] = "[subscriptionResourceId('Microsoft.Authorization/roleDefinitions', '8e3af657-a8ff-443c-a75c-2fe8c4bcb635')]"
            t["platform"]["resources"].append(assignment)
        self.assert_fails(14, self.context(owner), "allowlist")

    def test_migration_job_must_use_the_bridge_and_apps_never_migrate(self):
        def no_bridge(t):
            job = self.resource(t, "jobs", "Microsoft.App/jobs", "job-pv-pilot-migrate")
            job["properties"]["template"]["containers"][0]["args"].remove("--azure-ownership-bridge")
        self.assert_fails(9, self.context(no_bridge), "--azure-ownership-bridge")

        def startup_migration(t):
            api = self.resource(t, "apps", "Microsoft.App/containerApps", "ca-pv-pilot-api")
            api["properties"]["template"]["containers"][0]["command"] = ["dotnet", "/app/migrator/Paqueteria.DatabaseMigrator.dll"]
        self.assert_fails(9, self.context(startup_migration), "runs the migrator at startup")

    def test_budget_and_log_cap_are_required(self):
        def no_cap(t):
            del self.resource(t, "platform", "Microsoft.OperationalInsights/workspaces")["properties"]["workspaceCapping"]
        self.assert_fails(15, self.context(no_cap), "daily ingestion cap")

        def raise_budget(t):
            t["platform"]["parameters"]["budgetAmount"]["maxValue"] = 500
        self.assert_fails(15, self.context(raise_budget), "100 USD")

    def test_cleanups_must_stay_enabled_with_contract_values(self):
        def disable(t):
            raw = json.dumps(t["apps"])
            raw = raw.replace("createObject('name', 'OutboxRetention__DryRun', 'value', 'false')", "createObject('name', 'OutboxRetention__DryRun', 'value', 'true')")
            t["apps"] = json.loads(raw)
        self.assert_fails(19, self.context(disable), "OutboxRetention__DryRun=false")

        def shorten(t):
            raw = json.dumps(t["apps"])
            raw = raw.replace("createObject('name', 'OutboxRetention__Enabled', 'value', 'true')",
                              "createObject('name', 'OutboxRetention__Enabled', 'value', 'true'), createObject('name', 'OutboxRetention__Business__DeadRetention', 'value', '1.00:00:00')")
            t["apps"] = json.loads(raw)
        self.assert_fails(19, self.context(shorten), "OutboxRetention__Business__DeadRetention")
        self.assertTrue(guards.validate_settings([{"name": "OutboxRetention__DryRun", "value": "true"}], allow_sentinel=False))

    def test_adp_contract_is_wired(self):
        def drop(t):
            t["apps"]["variables"]["proofStorageEnv"] = [e for e in t["apps"]["variables"]["proofStorageEnv"] if e["name"] != "ProofStorage__ThreatScanner"]
        self.assert_fails(16, self.context(drop), "ProofStorage__ThreatScanner")

    def test_workflow_triggers_environment_and_provenance(self):
        text = (REPO_ROOT / guards.PILOT_WORKFLOW).read_text(encoding="utf-8")
        self.assert_fails(2, self.context(workflow_text=text.replace("on:\n  workflow_dispatch:", "on:\n  push:\n  workflow_dispatch:", 1)), "workflow_dispatch")
        self.assert_fails(3, self.context(workflow_text=text.replace("environment: azure-pilot", "environment: azure-dev", 1)), "azure-pilot")
        self.assert_fails(4, self.context(workflow_text=text.replace("azr001_static_guards.py deploy-gate", "true", 1)), "deploy-gate")
        self.assert_fails(18, self.context(workflow_text=text.replace("deploy/azure/pilot/Dockerfile.web", "deploy/azure/Dockerfile.web")), "pilot/Dockerfile.web")
        # P24: the shared web image (no USER) is not one of the four checked images, so building it fails too.
        self.assert_fails(24, self.context(workflow_text=text.replace("deploy/azure/pilot/Dockerfile.web", "deploy/azure/Dockerfile.web")), "must build exactly the images")
        without_yaml = text.replace("      - name: Install guard dependencies\n        run: python -m pip install PyYAML==6.0.3\n\n      # ENV-001 acceptance", "      # ENV-001 acceptance", 1)
        self.assertNotEqual(text, without_yaml)
        self.assert_fails(20, self.context(workflow_text=without_yaml), "job deploy step")
        pinned = "azure/login@7184910d9eb2b1c5e48f7073824a90609bb9b6d6"
        self.assertIn(pinned, text)
        self.assert_fails(3, self.context(workflow_text=text.replace(pinned, "azure/login@v2", 1)), "pinned")

    def test_obs002_alerts_fail_closed(self):
        def rules(t):
            return t["observability"]["variables"]["rules"]

        def default_email(t):
            t["observability"]["parameters"]["alertEmailAddress"]["defaultValue"] = "ops@example.com"
        self.assert_fails(21, self.context(default_email), "without a default")

        def webhook(t):
            group = self.resource(t, "observability", "Microsoft.Insights/actionGroups")
            group["properties"]["webhookReceivers"] = [{"name": "hook", "serviceUri": "https://example.com"}]
        self.assert_fails(21, self.context(webhook), "only by e-mail")

        def one_minute(t):
            rules(t)[0]["frequency"] = "PT1M"
        self.assert_fails(21, self.context(one_minute), "evaluationFrequency PT1M")

        def all_five_minutes(t):
            for rule in rules(t):
                rule["frequency"] = "PT5M"
                rule["window"] = "PT15M"
        self.assert_fails(21, self.context(all_five_minutes), "above the 5.00 USD")

        def window_shorter_than_frequency(t):
            rules(t)[0]["window"] = "PT5M"
        self.assert_fails(21, self.context(window_shorter_than_frequency), "windowSize PT5M")

        def dropped_rule(t):
            t["observability"]["variables"]["rules"] = rules(t)[1:]
        self.assert_fails(21, self.context(dropped_rule), "must be exactly")

        def payload_property(t):
            self.edit(t, "observability", "e.State.Dead", "e.State.PayloadJson")
        self.assert_fails(21, self.context(payload_property), "PayloadJson")

        def foreign_table(t):
            self.edit(t, "observability", "ContainerAppSystemLogs_CL", "AppRequests_CL")
        self.assert_fails(21, self.context(foreign_table), "AppRequests_CL")

        def stateless(t):
            self.resource(t, "observability", "Microsoft.Insights/scheduledQueryRules")["properties"]["autoMitigate"] = False
        self.assert_fails(21, self.context(stateless), "stateful")

        text = (REPO_ROOT / guards.PILOT_WORKFLOW).read_text(encoding="utf-8")
        self.assert_fails(21, self.context(workflow_text=text.replace("observability-check", "true")), "observability.parameters.json")

    def test_web_acceptance_versions_fail_closed(self):
        def web(t):
            return self.resource(t, "apps", "Microsoft.App/containerApps", "ca-pv-pilot-web")

        def default_terms(t):
            t["apps"]["parameters"]["webTermsVersion"]["defaultValue"] = "2026-10-01"
        self.assert_fails(22, self.context(default_terms), "webTermsVersion as a string parameter")

        def no_max_length(t):
            del t["apps"]["parameters"]["webPrivacyVersion"]["maxLength"]
        self.assert_fails(22, self.context(no_max_length), "webPrivacyVersion as a string parameter")

        def literal_value(t):
            env = web(t)["properties"]["template"]["containers"][0]["env"]
            next(e for e in env if e["name"] == "PAQUETERIA_TERMS_VERSION")["value"] = "2026-10-01"
        self.assert_fails(22, self.context(literal_value), "PAQUETERIA_TERMS_VERSION from parameters('webTermsVersion')")

        def dropped(t):
            container = web(t)["properties"]["template"]["containers"][0]
            container["env"] = [e for e in container["env"] if e["name"] != "PAQUETERIA_PRIVACY_VERSION"]
        self.assert_fails(22, self.context(dropped), "PAQUETERIA_PRIVACY_VERSION")

        def on_the_api(t):
            t["apps"]["variables"]["productionEnv"].append({"name": "PAQUETERIA_TERMS_VERSION", "value": "x"})
        self.assert_fails(22, self.context(on_the_api), "must not receive PAQUETERIA_TERMS_VERSION")

        text = (REPO_ROOT / guards.PILOT_WORKFLOW).read_text(encoding="utf-8")
        not_checked = text.replace("web-check --file tested/deploy/azure/pilot/web.parameters.json", "true")
        self.assertNotEqual(text, not_checked)
        self.assert_fails(22, self.context(workflow_text=not_checked), "web.parameters.json")
        not_deployed = text.replace('"@deploy/azure/pilot/web.parameters.json" \\\n', "")
        self.assertNotEqual(text, not_deployed)
        self.assert_fails(22, self.context(workflow_text=not_deployed), "web.parameters.json")

    def test_api_realtime_dispatcher_reads_its_own_credential_and_never_the_workers(self):
        """RTM-API-REALTIME-DISPATCHER-ROLE-2026-10-12: own Key Vault connection, per-secret RBAC, login generated."""
        ctx = self.context()
        api = ctx.workload(guards.API_APP)
        self.assertEqual("PostgreSql", api.value("Realtime__OutboxDispatcher__Provider"))
        self.assertEqual("pg-realtime-runtime-connection", guards.secret_mappings(api)["ConnectionStrings:PaqueteriaRealtime"])
        self.assertIn("pg-realtime-runtime-connection", ctx.templates["apps"]["variables"]["apiSecretNames"])
        self.assertNotIn("pg-worker-runtime-connection", ctx.templates["apps"]["variables"]["apiSecretNames"])
        logins = ctx.workload(guards.LOGINS_JOB)
        self.assertEqual("pg-realtime-login-verifier", guards.secret_mappings(logins)["PAQUETERIA_REALTIME_LOGIN_VERIFIER"])

        def unmapped(t):
            self.edit(t, "apps", "'KeyVaultSecrets__Mappings__5__ConfigurationKey', 'value', 'ConnectionStrings:PaqueteriaRealtime')",
                      "'KeyVaultSecrets__Mappings__5__ConfigurationKey', 'value', 'ConnectionStrings:PaqueteriaOther')")
        self.assert_fails(23, self.context(unmapped), "does not map ConnectionStrings:PaqueteriaRealtime")
        self.assert_fails(6, self.context(unmapped), "differ from the least-privilege set")

        def worker_credential(t):
            self.edit(t, "apps",
                      "'KeyVaultSecrets__Mappings__5__SecretName', 'value', 'pg-realtime-runtime-connection')",
                      "'KeyVaultSecrets__Mappings__5__SecretName', 'value', 'pg-worker-runtime-connection')")
            self.edit(t, "apps", "'KeyVaultSecrets__Mappings__5__ConfigurationKey', 'value', 'ConnectionStrings:PaqueteriaRealtime')",
                      "'KeyVaultSecrets__Mappings__5__ConfigurationKey', 'value', 'ConnectionStrings:PaqueteriaWorker')")
        self.assert_fails(23, self.context(worker_credential), "the Worker credential must never reach the API")
        self.assert_fails(23, self.context(worker_credential), "api maps Worker secrets")

        def worker_reads_realtime(t):
            self.edit(t, "apps", "'KeyVaultSecrets__Mappings__1__SecretName', 'value', 'pg-worker-runtime-connection')",
                      "'KeyVaultSecrets__Mappings__1__SecretName', 'value', 'pg-realtime-runtime-connection')")
        self.assert_fails(23, self.context(worker_reads_realtime), "only the API's realtime dispatchers read it")

        def rbac_missing(t):
            t["apps"]["variables"]["apiSecretNames"] = [n for n in t["apps"]["variables"]["apiSecretNames"]
                                                        if n != "pg-realtime-runtime-connection"]
        self.assert_fails(6, self.context(rbac_missing), "apps.apiSecretNames")

        def verifier_missing(t):
            t["jobs"]["variables"]["loginsSecretNames"] = [n for n in t["jobs"]["variables"]["loginsSecretNames"]
                                                           if n != "pg-realtime-login-verifier"]
        self.assert_fails(6, self.context(verifier_missing), "jobs.loginsSecretNames")

        text = (REPO_ROOT / guards.PILOT_WORKFLOW).read_text(encoding="utf-8")
        not_generated = text.replace("for role in api worker realtime; do", "for role in api worker; do")
        self.assertNotEqual(text, not_generated)
        self.assert_fails(23, self.context(workflow_text=not_generated), "api, worker and realtime runtime logins")

    def test_obs003_application_insights_and_the_export_switch_fail_closed(self):
        def component(t):
            return self.resource(t, "platform", "Microsoft.Insights/components")

        def classic(t):
            del component(t)["properties"]["WorkspaceResourceId"]
        self.assert_fails(21, self.context(classic), "workspace-based")

        def own_ingestion(t):
            component(t)["properties"]["IngestionMode"] = "ApplicationInsights"
        self.assert_fails(21, self.context(own_ingestion), "workspace-based")

        def unmasked(t):
            component(t)["properties"]["DisableIpMasking"] = True
        self.assert_fails(21, self.context(unmasked), "IP masking")

        def ingestion_sampling(t):
            component(t)["properties"]["SamplingPercentage"] = 100
        self.assert_fails(21, self.context(ingestion_sampling), "SamplingPercentage")

        def elsewhere(t):
            component(t)["location"] = "eastus"
        self.assert_fails(21, self.context(elsewhere), "region")

        def second(t):
            t["observability"]["resources"].append(copy.deepcopy(component(t)))
        self.assert_fails(21, self.context(second), "exactly one Application Insights component")

        def switched_by_parameter(t):
            self.edit(t, "apps", "'TelemetryExport__Enabled', 'value', 'false'", "'TelemetryExport__Enabled', 'value', parameters('publicHost')", count=2)
        self.assert_fails(21, self.context(switched_by_parameter), "TelemetryExport__Enabled to the literal")

        def foreign_destination(t):
            self.edit(t, "apps", "'TelemetryExport__ConnectionString', 'value', reference(", "'TelemetryExport__ConnectionString', 'value', concat('InstrumentationKey=x', reference(", count=1)
        self.assert_fails(21, self.context(foreign_destination), "TelemetryExport__ConnectionString from the pilot Application Insights")

        # Another component in the resource group (created by hand, or by another template) is not the managed one.
        def other_component(t):
            self.edit(t, "apps", "resourceId('Microsoft.Insights/components', format('appi-pv-pilot-{0}', variables('suffix')))",
                      "resourceId('Microsoft.Insights/components', format('appi-pv-other-{0}', variables('suffix')))")
        self.assert_fails(21, self.context(other_component), "from the pilot Application Insights component appi-pv-pilot-<suffix>")

        def literal_component(t):
            self.edit(t, "apps", "resourceId('Microsoft.Insights/components', format('appi-pv-pilot-{0}', variables('suffix')))",
                      "resourceId('Microsoft.Insights/components', 'appi-pv-pilot-handmade')")
        self.assert_fails(21, self.context(literal_component), "from the pilot Application Insights component appi-pv-pilot-<suffix>")

        def other_suffix(t):
            t["apps"]["variables"]["suffix"] = "[uniqueString(resourceGroup().id)]"
        self.assert_fails(21, self.context(other_suffix), "same suffix as platform.bicep")

        def renamed_component(t):
            component(t)["name"] = "[format('appi-other-{0}', variables('suffix'))]"
        self.assert_fails(21, self.context(renamed_component), "managed appi-pv-pilot-<suffix> component")

        def sampler_override(t):
            self.edit(t, "apps", "createObject('name', 'TelemetryPseudonym__CurrentKeyVersion', 'value', '1')",
                      "createObject('name', 'TelemetryPseudonym__CurrentKeyVersion', 'value', '1'), createObject('name', 'OTEL_TRACES_SAMPLER', 'value', 'always_on')")
        self.assert_fails(21, self.context(sampler_override), "unexpected telemetry settings")

        def web_exports(t):
            web = self.resource(t, "apps", "Microsoft.App/containerApps", "ca-pv-pilot-web")
            web["properties"]["template"]["containers"][0]["env"].append({"name": "APPLICATIONINSIGHTS_CONNECTION_STRING", "value": "x"})
        self.assert_fails(21, self.context(web_exports), "ca-pv-pilot-web must not receive telemetry settings")

        def query_reads_requests(t):
            self.edit(t, "observability", "ContainerAppSystemLogs_CL", "AppRequests | union ContainerAppSystemLogs_CL")
        self.assert_fails(21, self.context(query_reads_requests), "Application Insights table AppRequests")

        def query_reads_pseudonym(t):
            self.edit(t, "observability", "e.State.Dead", "e.State.OrganizationPseudonym")
        self.assert_fails(21, self.context(query_reads_pseudonym), "OrganizationPseudonym")

        text = (REPO_ROOT / guards.PILOT_WORKFLOW).read_text(encoding="utf-8")
        unchecked = text.replace("if ! has_secret paquetenvia-telemetry-pseudonym-key-1; then", "if false; then", 1)
        self.assertNotEqual(text, unchecked)
        self.assert_fails(21, self.context(workflow_text=unchecked), "lacks paquetenvia-telemetry-pseudonym-key-1")

    def test_obs003_pseudonym_key_is_read_by_the_api_and_the_worker_only_from_key_vault(self):
        def worker_without_key(t):
            t["apps"]["variables"]["workerSecretNames"] = ["pg-worker-runtime-connection"]
        self.assert_fails(6, self.context(worker_without_key), "apps.workerSecretNames")

        def key_in_environment(t):
            self.edit(t, "apps", "createObject('name', 'TelemetryPseudonym__CurrentKeyVersion', 'value', '1')",
                      "createObject('name', 'TelemetryPseudonym__CurrentKeyVersion', 'value', '1'), createObject('name', 'TelemetryPseudonym__Keys__1', 'value', 'inline')")
        self.assert_fails(6, self.context(key_in_environment), "sensitive setting TelemetryPseudonym__Keys__1")

        def api_maps_a_worker_secret_instead(t):
            self.edit(t, "apps", "'value', 'paquetenvia-telemetry-pseudonym-key-1'", "'value', 'pg-worker-runtime-connection'")
        self.assert_fails(6, self.context(api_maps_a_worker_secret_instead), "differ from the least-privilege set")

        # The shared pseudonym key is not a Worker credential: P23 still refuses every other Worker secret in the API.
        results = {r.number: r for r in guards.run_guards(self.context())}
        self.assertTrue(results[23].passed, results[23].detail)

    def test_observability_template_is_required(self):
        def drop(t):
            del t["observability"]
        self.assert_fails(0, self.context(drop), "observability")

        def unknown_insights_type(t):
            t["observability"]["resources"].append({"type": "Microsoft.Insights/diagnosticSettings", "apiVersion": "2021-05-01-preview", "name": "d"})
        self.assert_fails(1, self.context(unknown_insights_type), "Microsoft.Insights/diagnosticSettings")

        # OBS-003: no new alert without its own decision; a metric alert on the exported metrics is refused too. Since
        # SCL-007-POSTGRES-CONNECTION-ALERT-2026-10-14 the type is allowed in observability.bicep for the two
        # PostgreSQL connection alerts only, so P21 refuses any other metric alert.
        def metric_alert(t):
            t["observability"]["resources"].append({"type": "Microsoft.Insights/metricAlerts", "apiVersion": "2018-03-01", "name": "m"})
        self.assert_fails(21, self.context(metric_alert), "metric alerts must be exactly")

        def metric_alert_on_application_insights(t):
            alert = copy.deepcopy(self.resource(t, "observability", "Microsoft.Insights/metricAlerts", "connections-warning"))
            alert["name"] = "mar-pv-pilot-appi-requests"
            alert["properties"]["scopes"] = ["[resourceId('Microsoft.Insights/components', format('appi-pv-pilot-{0}', variables('suffix')))]"]
            t["observability"]["resources"].append(alert)
        self.assert_fails(21, self.context(metric_alert_on_application_insights), "metric alerts must be exactly")

    def test_scl007_postgres_connection_alerts_fail_closed(self):
        """SCL-007-POSTGRES-CONNECTION-ALERT-2026-10-14: two metric alerts on the pilot server's active_connections,
        28 (Sev 2) and 33 (Sev 1), Maximum over 5 minutes every minute, stateful, through ag-pv-pilot-ops."""
        def warning(t):
            return self.resource(t, "observability", "Microsoft.Insights/metricAlerts", "connections-warning")

        def critical(t):
            return self.resource(t, "observability", "Microsoft.Insights/metricAlerts", "connections-critical")

        def criterion(alert):
            return alert["properties"]["criteria"]["allOf"][0]

        compiled = self.context().templates["observability"]
        alerts = {r["name"]: r for r in compiled["resources"] if r.get("type") == "Microsoft.Insights/metricAlerts"}
        self.assertEqual({"mar-pv-pilot-pg-connections-warning", "mar-pv-pilot-pg-connections-critical"},
                         {name for name in alerts if "-connections-" in name})
        for name, (severity, threshold) in guards.REQUIRED_CONNECTION_ALERTS.items():
            properties = alerts[name]["properties"]
            self.assertEqual((severity, threshold), (properties["severity"], criterion(alerts[name])["threshold"]))
            self.assertEqual(("PT1M", "PT5M", "Maximum", "GreaterThanOrEqual", "active_connections"),
                             (properties["evaluationFrequency"], properties["windowSize"], criterion(alerts[name])["timeAggregation"],
                              criterion(alerts[name])["operator"], criterion(alerts[name])["metricName"]))
        # 80 % and 95 % of the 35 B1ms user connections, rounded down (AI-15 connection_pool_utilization 0.8 and 0.95).
        self.assertEqual((28, 33), (guards.POSTGRES_USER_CONNECTIONS * 80 // 100, guards.POSTGRES_USER_CONNECTIONS * 95 // 100))

        def in_platform(t):
            t["platform"]["resources"].append(copy.deepcopy(warning(t)))
        self.assert_fails(1, self.context(in_platform), "allowed only in observability.bicep")

        def dropped(t):
            t["observability"]["resources"].remove(critical(t))
        self.assert_fails(21, self.context(dropped), "metric alerts must be exactly")

        def duplicated(t):
            t["observability"]["resources"].append(copy.deepcopy(warning(t)))
        self.assert_fails(21, self.context(duplicated), "metric alerts must be exactly")

        mutations = {
            "lower threshold": (lambda t: criterion(warning(t)).update(threshold=27), "active_connections >= 28"),
            "higher threshold": (lambda t: criterion(critical(t)).update(threshold=35), "active_connections >= 33"),
            "fractional threshold": (lambda t: criterion(warning(t)).update(threshold=28.0), "active_connections >= 28"),
            "average": (lambda t: criterion(warning(t)).update(timeAggregation="Average"), "Maximum of active_connections"),
            "strictly greater": (lambda t: criterion(critical(t)).update(operator="GreaterThan"), "active_connections >= 33"),
            "other metric": (lambda t: criterion(warning(t)).update(metricName="connections_failed"), "active_connections >= 28"),
            "dimension": (lambda t: criterion(warning(t)).update(dimensions=[{"name": "ServerName", "operator": "Include", "values": ["*"]}]),
                          "no dimension"),
            "dynamic threshold": (lambda t: criterion(critical(t)).update(criterionType="DynamicThresholdCriterion"), "active_connections >= 33"),
            "second criterion": (lambda t: warning(t)["properties"]["criteria"]["allOf"].append(copy.deepcopy(criterion(warning(t)))),
                                 "no dimension or other criterion"),
            "multi-resource criteria": (lambda t: warning(t)["properties"]["criteria"].update(
                {"odata.type": "Microsoft.Azure.Monitor.MultipleResourceMultipleMetricCriteria"}), "active_connections >= 28"),
            "swapped severity": (lambda t: warning(t)["properties"].update(severity=1), "must have severity 2"),
            "critical as Sev 0": (lambda t: critical(t)["properties"].update(severity=0), "must have severity 1"),
            "every 5 minutes": (lambda t: warning(t)["properties"].update(evaluationFrequency="PT5M"), "every minute (PT1M)"),
            "15-minute window": (lambda t: critical(t)["properties"].update(windowSize="PT15M"), "over 5 minutes (PT5M)"),
            "stateless": (lambda t: warning(t)["properties"].update(autoMitigate=False), "stateful"),
            "disabled": (lambda t: critical(t)["properties"].update(enabled=False), "enabled and stateful"),
            "scope Application Insights": (lambda t: warning(t)["properties"].update(
                scopes=["[resourceId('Microsoft.Insights/components', format('appi-pv-pilot-{0}', variables('suffix')))]"]),
                "only the pilot PostgreSQL server"),
            "scope another server": (lambda t: critical(t)["properties"].update(
                scopes=["[resourceId('Microsoft.DBforPostgreSQL/flexibleServers', 'pg-pv-other')]"]), "only the pilot PostgreSQL server"),
            "scope the resource group": (lambda t: warning(t)["properties"].update(scopes=["[resourceGroup().id]"]),
                                         "only the pilot PostgreSQL server"),
            "multi-resource target": (lambda t: warning(t)["properties"].update(
                targetResourceType="Microsoft.DBforPostgreSQL/flexibleServers", targetResourceRegion="mexicocentral"), "unexpected properties"),
            "webhook": (lambda t: critical(t)["properties"]["actions"][0].update(webHookProperties={"route": "x"}),
                        "only through the pilot action group"),
            "other action group": (lambda t: warning(t)["properties"].update(
                actions=[{"actionGroupId": "[resourceId('Microsoft.Insights/actionGroups', 'ag-other')]"}]), "only through the pilot action group"),
            "no action": (lambda t: critical(t)["properties"].update(actions=[]), "only through the pilot action group"),
            "copy loop": (lambda t: warning(t).update(copy={"name": "c", "count": 1}), "one literal global resource"),
            "regional": (lambda t: critical(t).update(location="mexicocentral"), "one literal global resource"),
            "hard-coded e-mail": (lambda t: warning(t)["properties"].update(description="Write to ops@example.com"),
                                  "must not contain an e-mail address"),
            "other SKU": (lambda t: self.resource(t, "platform", "Microsoft.DBforPostgreSQL/flexibleServers")["sku"].update(name="Standard_B2s"),
                          "revisit them with the server SKU"),
            "other suffix": (lambda t: t["observability"]["variables"].update(suffix="[uniqueString(resourceGroup().id)]"), "same suffix as platform.bicep"),
        }
        for name, (mutate, fragment) in mutations.items():
            with self.subTest(name=name):
                self.assert_fails(21, self.context(mutate), fragment)

        # An e-mail anywhere in the observability template, not only in the alerts, is refused.
        def email_in_workbook(t):
            self.edit(t, "observability", "Paquetenvia pilot operations (OBS-002)", "Paquetenvia pilot operations (ops@example.com)")
        self.assert_fails(21, self.context(email_in_workbook), "observability.bicep must not contain an e-mail address")

        # The six metric alert series (the two connection alerts and, since SCL-007-POSTGRES-RESOURCE-ALERTS-2026-10-20,
        # the four resource alerts) count against the 5 USD bound (0.10 each at worst): log alerts at 4.95 USD pass
        # alone, not with them.
        def log_alerts_at_4_95(t):
            for rule in t["observability"]["variables"]["rules"][:4]:
                rule["frequency"] = "PT10M"
                rule["window"] = "PT15M"
        self.assert_fails(21, self.context(log_alerts_at_4_95), "alert rules cost 5.55 USD per month")

    def test_scl007_postgres_resource_alerts_fail_closed(self):
        """SCL-007-POSTGRES-RESOURCE-ALERTS-2026-10-20: four more metric alerts on the pilot server, one level each
        (severity 2), every minute, stateful, through ag-pv-pilot-ops: CPU Average over 30 minutes >= 70, memory Maximum
        over 5 minutes >= 80, storage Maximum over 5 minutes >= 70 and CPU credits Minimum over 5 minutes <= 72. With the
        two connection alerts, six metric alerts in all; each drift fails closed."""
        # name -> (metric, aggregation, operator symbol, threshold, window, the metric's dimension on Microsoft Learn).
        rules = {
            "mar-pv-pilot-pg-cpu-warning": ("cpu_percent", "Average", ">=", 70, "PT30M", "ServerName"),
            "mar-pv-pilot-pg-memory-warning": ("memory_percent", "Maximum", ">=", 80, "PT5M", "ServerName"),
            "mar-pv-pilot-pg-storage-warning": ("storage_percent", "Maximum", ">=", 70, "PT5M", "ServerName"),
            "mar-pv-pilot-pg-cpu-credits-warning": ("cpu_credits_remaining", "Minimum", "<=", 72, "PT5M", "LogicalServerName"),
        }
        operators = {">=": "GreaterThanOrEqual", "<=": "LessThanOrEqual"}

        def alert(t, name):
            return self.resource(t, "observability", "Microsoft.Insights/metricAlerts", name)

        def criterion(resource):
            return resource["properties"]["criteria"]["allOf"][0]

        compiled = self.context().templates["observability"]
        alerts = {r["name"]: r for r in compiled["resources"] if r.get("type") == "Microsoft.Insights/metricAlerts"}
        self.assertEqual(sorted(["mar-pv-pilot-pg-connections-warning", "mar-pv-pilot-pg-connections-critical", *rules]), sorted(alerts))
        self.assertEqual(sorted(alerts), sorted(guards.REQUIRED_METRIC_ALERTS))
        for name, (metric, aggregation, symbol, threshold, window, _) in rules.items():
            properties = alerts[name]["properties"]
            self.assertEqual(
                (2, True, True, "PT1M", window, [guards.POSTGRES_SERVER_ID], [{"actionGroupId": guards.PILOT_ACTION_GROUP_ID}], "global"),
                (properties["severity"], properties["enabled"], properties["autoMitigate"], properties["evaluationFrequency"],
                 properties["windowSize"], properties["scopes"], properties["actions"], alerts[name]["location"]))
            self.assertEqual({
                "criterionType": "StaticThresholdCriterion",
                "name": metric.replace("_", "-"),
                "metricNamespace": "Microsoft.DBforPostgreSQL/flexibleServers",
                "metricName": metric,
                "timeAggregation": aggregation,
                "operator": operators[symbol],
                "threshold": threshold,
            }, criterion(alerts[name]))

        # CPU takes the AI-15 db_cpu_utilization warning and window; 70 % of the 32 GiB disk is 22.4 GiB; 72 is 25 % of
        # the 288 credits a B1ms banks at most.
        contract = guards.yaml.safe_load(
            (REPO_ROOT / "docs/normative/v0.6/specs/AI-15_SCALABILITY_CONTRACT.yaml").read_text(encoding="utf-8"))
        signal = next(item for item in contract["scaling_signals"]["database"] if item["metric"] == "db_cpu_utilization")
        self.assertEqual((70, "PT30M"), (round(signal["warning"] * 100), "PT" + signal["window"].upper()))
        self.assertEqual((22.4, 72), (guards.POSTGRES_STORAGE_GB * 70 / 100, guards.POSTGRES_MAX_CPU_CREDITS * 25 // 100))

        other_aggregation = {"Average": "Maximum", "Maximum": "Average", "Minimum": "Average"}
        strict = {">=": "GreaterThan", "<=": "LessThan"}
        other_window = {"PT30M": "PT15M", "PT5M": "PT15M"}
        for name, (metric, aggregation, symbol, threshold, window, dimension) in rules.items():
            def rule(t, name=name):
                return alert(t, name)

            expected = f"{metric} {symbol} {threshold}"
            window_message = f"over {guards._iso_minutes(window)} minutes ({window})"
            mutations = {
                "threshold + 1": (lambda t: criterion(rule(t)).update(threshold=threshold + 1), expected),
                "threshold - 1": (lambda t: criterion(rule(t)).update(threshold=threshold - 1), expected),
                "fractional threshold": (lambda t: criterion(rule(t)).update(threshold=float(threshold)), expected),
                "other aggregation": (lambda t: criterion(rule(t)).update(timeAggregation=other_aggregation[aggregation]),
                                      f"{aggregation} of {metric}"),
                "strict operator": (lambda t: criterion(rule(t)).update(operator=strict[symbol]), expected),
                "opposite operator": (lambda t: criterion(rule(t)).update(
                    operator="LessThanOrEqual" if symbol == ">=" else "GreaterThanOrEqual"), expected),
                "other metric": (lambda t: criterion(rule(t)).update(metricName="cpu_percent" if metric != "cpu_percent" else "memory_percent"),
                                 expected),
                "other namespace": (lambda t: criterion(rule(t)).update(metricNamespace="Microsoft.DBforMySQL/flexibleServers"), expected),
                "dimension": (lambda t: criterion(rule(t)).update(dimensions=[{"name": dimension, "operator": "Include", "values": ["*"]}]),
                              "no dimension"),
                "dynamic threshold": (lambda t: criterion(rule(t)).update(criterionType="DynamicThresholdCriterion"), expected),
                "second criterion": (lambda t: rule(t)["properties"]["criteria"]["allOf"].append(copy.deepcopy(criterion(rule(t)))),
                                     "no dimension or other criterion"),
                "multi-resource criteria": (lambda t: rule(t)["properties"]["criteria"].update(
                    {"odata.type": "Microsoft.Azure.Monitor.MultipleResourceMultipleMetricCriteria"}), expected),
                "severity 1": (lambda t: rule(t)["properties"].update(severity=1), "must have severity 2"),
                "severity 3": (lambda t: rule(t)["properties"].update(severity=3), "must have severity 2"),
                "every 5 minutes": (lambda t: rule(t)["properties"].update(evaluationFrequency="PT5M"), "every minute (PT1M)"),
                "other window": (lambda t: rule(t)["properties"].update(windowSize=other_window[window]), window_message),
                "one-hour window": (lambda t: rule(t)["properties"].update(windowSize="PT1H"), window_message),
                "stateless": (lambda t: rule(t)["properties"].update(autoMitigate=False), "stateful"),
                "disabled": (lambda t: rule(t)["properties"].update(enabled=False), "enabled and stateful"),
                "scope Application Insights": (lambda t: rule(t)["properties"].update(
                    scopes=["[resourceId('Microsoft.Insights/components', format('appi-pv-pilot-{0}', variables('suffix')))]"]),
                    "only the pilot PostgreSQL server"),
                "scope another server": (lambda t: rule(t)["properties"].update(
                    scopes=["[resourceId('Microsoft.DBforPostgreSQL/flexibleServers', 'pg-pv-other')]"]), "only the pilot PostgreSQL server"),
                "scope the resource group": (lambda t: rule(t)["properties"].update(scopes=["[resourceGroup().id]"]),
                                             "only the pilot PostgreSQL server"),
                "two scopes": (lambda t: rule(t)["properties"]["scopes"].append("[resourceGroup().id]"), "only the pilot PostgreSQL server"),
                "multi-resource target": (lambda t: rule(t)["properties"].update(
                    targetResourceType="Microsoft.DBforPostgreSQL/flexibleServers", targetResourceRegion="mexicocentral"),
                    "unexpected properties"),
                "webhook": (lambda t: rule(t)["properties"]["actions"][0].update(webHookProperties={"route": "x"}),
                            "only through the pilot action group"),
                "other action group": (lambda t: rule(t)["properties"].update(
                    actions=[{"actionGroupId": "[resourceId('Microsoft.Insights/actionGroups', 'ag-other')]"}]),
                    "only through the pilot action group"),
                "no action": (lambda t: rule(t)["properties"].update(actions=[]), "only through the pilot action group"),
                "copy loop": (lambda t: rule(t).update(copy={"name": "c", "count": 1}), "one literal global resource"),
                "regional": (lambda t: rule(t).update(location="mexicocentral"), "one literal global resource"),
                # Assembled from parts so no address literal is committed (example.com is reserved, RFC 2606).
                "hard-coded e-mail": (lambda t: rule(t)["properties"].update(description="Write to ops" + "@" + "example.com"),
                                      "must not contain an e-mail address"),
                "dropped": (lambda t: t["observability"]["resources"].remove(rule(t)), "metric alerts must be exactly"),
                "duplicated": (lambda t: t["observability"]["resources"].append(copy.deepcopy(rule(t))), "metric alerts must be exactly"),
                "renamed": (lambda t: rule(t).update(name=name + "-x"), "metric alerts must be exactly"),
            }
            for case, (mutate, fragment) in mutations.items():
                with self.subTest(rule=name, case=case):
                    self.assert_fails(21, self.context(mutate), fragment)

            def in_platform(t, name=name):
                t["platform"]["resources"].append(copy.deepcopy(alert(t, name)))
            with self.subTest(rule=name, case="in platform.bicep"):
                self.assert_fails(1, self.context(in_platform), "allowed only in observability.bicep")

        # One level only (D2-a): a critical level for any of them is a seventh rule and is refused.
        def cpu_critical(t):
            extra = copy.deepcopy(alert(t, "mar-pv-pilot-pg-cpu-warning"))
            extra["name"] = "mar-pv-pilot-pg-cpu-critical"
            extra["properties"]["severity"] = 1
            criterion(extra)["threshold"] = 85
            t["observability"]["resources"].append(extra)
        self.assert_fails(21, self.context(cpu_critical), "metric alerts must be exactly")

        # The thresholds belong to the B1ms server (288 CPU credits at most) and to its 32 GiB disk.
        def other_sku(t):
            self.resource(t, "platform", "Microsoft.DBforPostgreSQL/flexibleServers")["sku"].update(name="Standard_B2s")
        self.assert_fails(21, self.context(other_sku), "the CPU credits alert (72) is 25 % of its 288 credits")

        def other_disk(t):
            self.resource(t, "platform", "Microsoft.DBforPostgreSQL/flexibleServers")["properties"]["storage"].update(storageSizeGB=64)
        self.assert_fails(21, self.context(other_disk), "revisit it with the disk size, found 64")

        def no_disk_size(t):
            del self.resource(t, "platform", "Microsoft.DBforPostgreSQL/flexibleServers")["properties"]["storage"]["storageSizeGB"]
        self.assert_fails(21, self.context(no_disk_size), "revisit it with the disk size, found None")


if __name__ == "__main__":
    unittest.main()
