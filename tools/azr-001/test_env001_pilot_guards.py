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


# ------------------------------------------------------------ NTF-RECIPIENT-NOTICE-PILOT-WIRING-2026-10-31 (pure tests)
SYNTHETIC_SCOPE = "/subscriptions/00000000-0000-4000-8000-000000000000/resourceGroups/rg-pv-pilot"
SYNTHETIC_ACS_ID = SYNTHETIC_SCOPE + "/providers/Microsoft.Communication/communicationServices/acs-pv-synthetic"
OWNER_GUIDE = REPO_ROOT / "docs" / "operations" / "env-001-pilot" / "guia-owner-avisos.md"


def az_role_definition(key: str, **changes) -> dict:
    """The `az role definition list` shape of a reviewed custom role, with optional changes."""
    role = guards.CUSTOM_ROLES[key]
    definition = {
        "id": SYNTHETIC_SCOPE.split("/resourceGroups/")[0] + "/providers/Microsoft.Authorization/roleDefinitions/"
              + ("11111111-1111-4111-8111-111111111111" if key == "recipient-notice-unwrap" else "22222222-2222-4222-8222-222222222222"),
        "roleName": role.name,
        "roleType": "CustomRole",
        "description": "synthetic",
        "assignableScopes": [SYNTHETIC_SCOPE],
        "permissions": [{"actions": list(role.actions), "notActions": [], "dataActions": list(role.data_actions),
                         "notDataActions": []}],
    }
    definition.update(changes)
    return definition


class RecipientNoticeSettingsTests(unittest.TestCase):
    """AI-24 recipient_notices.switches.pilot and W11 item 7: the notice switches, the messaging settings and the public
    tracking base URL are platform-managed, and the contact lookup keys are sensitive."""

    def test_switches_messaging_and_public_tracking_cannot_be_set_from_the_settings_file(self):
        for name, value in {
            "Notifications__RecipientNotices__Enabled": "true",
            "Notifications__RecipientNotices__Gate004DecisionId": "GATE-004-SYNTHETIC",
            "notifications__recipientnotices__enabled": "true",
            "Messaging__WhatsApp__Provider": "MetaCloudApi",
            "Messaging__Email__Provider": "AzureCommunicationServices",
            "Messaging__WhatsApp__Webhook__Enabled": "true",
            "Messaging__Email__Events__Enabled": "true",
            "PublicTracking__PublicBaseUrl": "https://elsewhere.example.test",
            "PublicTracking__CurrentLinkKeyVersion": "2",
        }.items():
            with self.subTest(name=name):
                failures = guards.validate_settings([{"name": name, "value": value}], allow_sentinel=False)
                self.assertIn(f"setting {name} is platform-managed by apps.bicep and cannot be overridden", failures)
        entries = guards.load_settings(REPO_ROOT / guards.SETTINGS_FILE)
        self.assertFalse([e["name"] for e in entries if e["name"].casefold().startswith(
            ("notifications__recipientnotices__", "messaging__", "publictracking__"))])
        # The NTF-001 outbox processor settings stay owner business settings.
        self.assertEqual([], guards.validate_settings([{"name": "Notifications__BatchSize", "value": "10"}] + SYNTHETIC_ELIGIBILITY,
                                                      allow_sentinel=False))

    def test_contact_lookup_keys_are_sensitive(self):
        self.assertTrue(guards.SENSITIVE_SETTINGS.match("ContactLookup__Keys__1"))
        self.assertFalse(guards.SENSITIVE_SETTINGS.match("ContactLookup__CurrentKeyVersion"))
        self.assertFalse(guards.SENSITIVE_SETTINGS.match(guards.RECIPIENT_NOTICE_KEY_SETTING))

    def test_every_recipient_notice_secret_is_mapped_and_either_generated_or_written_by_the_owner(self):
        generated, owner = set(guards.RECIPIENT_NOTICE_GENERATED_SECRETS), set(guards.RECIPIENT_NOTICE_OWNER_SECRETS)
        self.assertEqual(7, len(generated))
        self.assertEqual(3, len(owner))
        self.assertFalse(generated & owner)
        api = set(guards.REQUIRED_SECRET_MAPPINGS[guards.API_APP].values())
        worker = set(guards.REQUIRED_SECRET_MAPPINGS[guards.WORKER_APP].values())
        self.assertEqual(generated | owner, (api | worker) & (generated | owner))
        self.assertNotIn(guards.WHATSAPP_TOKEN_SECRET, api)
        self.assertFalse(guards.WEBHOOK_SECRETS & worker)
        self.assertEqual({guards.TRACKING_LINK_SECRET, guards.CONTACT_LOOKUP_SECRET, guards.WHATSAPP_PHONE_NUMBER_ID_SECRET},
                         set(guards.SHARED_NON_CREDENTIAL_SECRETS))
        self.assertEqual(guards.SHARED_NON_CREDENTIAL_SECRETS | {guards.TELEMETRY_PSEUDONYM_SECRET}, api & worker)


class RecipientNoticeGateResolutionTests(unittest.TestCase):
    """AI-24 recipient_notices.switches.gate_decisions: only `Gate resolution` rows of GATE-004 and GATE-007 turn the
    recipient notices on (P28); a scoping row admits the pilot deploy, never the notices."""

    LOG = (
        "| Date | ID | Type | Decision | Impacted files | Approved by |\n"
        "|---|---|---|---|---|---|\n"
        "| 2026-11-01 | GATE-004-SYNTHETIC-RESOLUTION | Gate resolution | synthetic | none | test |\n"
        "| 2026-11-01 | GATE-007-SYNTHETIC-RESOLUTION | Gate resolution | synthetic | none | test |\n"
        "| 2026-11-01 | GATE-007-SYNTHETIC-SCOPING | Gate scoping | synthetic | none | test |\n"
        "| 2026-11-01 | GATE-004-SYNTHETIC-TWICE | Gate resolution | synthetic | none | test |\n"
        "| 2026-11-01 | GATE-004-SYNTHETIC-TWICE | Gate resolution | synthetic | none | test |\n"
    )

    def test_resolution_rows_are_accepted(self):
        self.assertEqual([], guards.validate_gate_resolution(self.LOG, "004", "GATE-004-SYNTHETIC-RESOLUTION"))
        self.assertEqual([], guards.validate_gate_resolution(self.LOG, "007", "GATE-007-SYNTHETIC-RESOLUTION"))

    def test_anything_else_is_refused(self):
        cases = {
            ("007", "GATE-007-SYNTHETIC-SCOPING"): "not 'Gate resolution'",
            ("004", "GATE-007-SYNTHETIC-RESOLUTION"): "missing or malformed",
            ("004", "GATE-004-UNKNOWN"): "not recorded",
            ("004", "GATE-004-SYNTHETIC-TWICE"): "appears 2 times",
            ("004", ""): "missing or malformed",
            ("004", "gate-004-synthetic-resolution"): "missing or malformed",
            ("012", "GATE-012-PILOT-SCOPE"): "unsupported gate",
        }
        for (gate, decision_id), fragment in cases.items():
            with self.subTest(decision_id=decision_id):
                failures = guards.validate_gate_resolution(self.LOG, gate, decision_id)
                self.assertEqual(1, len(failures), failures)
                self.assertIn(fragment, failures[0])

    def test_the_real_decision_log_resolves_neither_gate_yet(self):
        rows = guards.decision_log_rows((REPO_ROOT / guards.DECISION_LOG).read_text(encoding="utf-8"))
        self.assertFalse([row[1] for row in rows if row[1].startswith(("GATE-004-", "GATE-007-")) and row[2] == "Gate resolution"])


class CustomRoleCheckTests(unittest.TestCase):
    """W5 and W6: the workflow refuses a recipient-notice custom role that is missing, duplicated, named differently,
    assignable elsewhere, or that holds any action beyond the reviewed ones (never listKeys or regenerateKey)."""

    def check(self, key, definitions):
        return guards.custom_role_failures(key, definitions, SYNTHETIC_SCOPE)

    def test_the_reviewed_roles_pass(self):
        for key in guards.RECIPIENT_NOTICE_CUSTOM_ROLES:
            with self.subTest(key=key):
                self.assertEqual([], self.check(key, [az_role_definition(key)]))

    def test_the_reviewed_actions_are_the_decided_ones(self):
        unwrap, sender = (guards.CUSTOM_ROLES[k] for k in guards.RECIPIENT_NOTICE_CUSTOM_ROLES)
        self.assertEqual(((), ("Microsoft.KeyVault/vaults/keys/read", "Microsoft.KeyVault/vaults/keys/unwrap/action")),
                         (unwrap.actions, unwrap.data_actions))
        self.assertEqual((("Microsoft.Communication/CommunicationServices/Read", "Microsoft.Communication/CommunicationServices/Write",
                           "Microsoft.Communication/EmailServices/write"), ()), (sender.actions, sender.data_actions))
        for role in (unwrap, sender):
            for action in role.actions + role.data_actions:
                self.assertFalse(any(f in action.casefold() for f in guards.FORBIDDEN_CUSTOM_ROLE_ACTIONS), action)

    def test_drift_is_refused(self):
        unwrap = guards.CUSTOM_ROLES["recipient-notice-unwrap"]
        sender = guards.CUSTOM_ROLES["email-sender"]
        cases = {
            "missing": ("email-sender", [], "must exist exactly once"),
            "twice": ("email-sender", [az_role_definition("email-sender")] * 2, "must exist exactly once"),
            "renamed": ("email-sender", [az_role_definition("email-sender", roleName="Paquetenvia Pilot Email")], "not named exactly"),
            "list keys": ("email-sender", [az_role_definition("email-sender", permissions=[{
                "actions": [*sender.actions, "Microsoft.Communication/CommunicationServices/ListKeys/action"],
                "notActions": [], "dataActions": [], "notDataActions": []}])], "never grant listKeys"),
            "regenerate key": ("email-sender", [az_role_definition("email-sender", permissions=[{
                "actions": [*sender.actions, "Microsoft.Communication/CommunicationServices/RegenerateKey/action"],
                "notActions": [], "dataActions": [], "notDataActions": []}])], "never grant listKeys"),
            "wildcard": ("email-sender", [az_role_definition("email-sender", permissions=[{
                "actions": ["Microsoft.Communication/*"], "notActions": [], "dataActions": [], "notDataActions": []}])],
                "actions must be exactly"),
            "wrap": ("recipient-notice-unwrap", [az_role_definition("recipient-notice-unwrap", permissions=[{
                "actions": [], "notActions": [], "dataActions": [*unwrap.data_actions, "Microsoft.KeyVault/vaults/keys/wrap/action"],
                "notDataActions": []}])], "dataActions must be exactly"),
            "no unwrap": ("recipient-notice-unwrap", [az_role_definition("recipient-notice-unwrap", permissions=[{
                "actions": [], "notActions": [], "dataActions": ["Microsoft.KeyVault/vaults/keys/read"], "notDataActions": []}])],
                "dataActions must be exactly"),
            "two blocks": ("recipient-notice-unwrap", [az_role_definition("recipient-notice-unwrap", permissions=[
                {"actions": [], "dataActions": list(unwrap.data_actions)}, {"actions": [], "dataActions": []}])],
                "exactly one permission block"),
            "condition": ("recipient-notice-unwrap", [az_role_definition("recipient-notice-unwrap", permissions=[{
                "actions": [], "notActions": [], "dataActions": list(unwrap.data_actions), "notDataActions": [],
                "condition": "true"}])], "no condition"),
            "subscription scope": ("recipient-notice-unwrap", [az_role_definition(
                "recipient-notice-unwrap", assignableScopes=[SYNTHETIC_SCOPE.split("/resourceGroups/")[0]])], "only in the pilot resource group"),
            "built-in": ("recipient-notice-unwrap", [az_role_definition("recipient-notice-unwrap", roleType="BuiltInRole")], "custom role"),
        }
        for name, (key, definitions, fragment) in cases.items():
            with self.subTest(name=name):
                failures = self.check(key, definitions)
                self.assertTrue(any(fragment in failure for failure in failures), failures)
                self.assertFalse(any(SYNTHETIC_SCOPE in failure for failure in failures), failures)

    def test_cli_exit_codes_never_print_an_id(self):
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "role.json"
            for definitions, code in (([az_role_definition("email-sender")], 0), ([], 1)):
                path.write_text(json.dumps(definitions), encoding="utf-8")
                result = subprocess.run([sys.executable, guards.__file__, "custom-role-check", "--role", "email-sender",
                                         "--definitions-json", str(path), "--scope", SYNTHETIC_SCOPE],
                                        capture_output=True, text=True)
                self.assertEqual(code, result.returncode, result.stdout)
                self.assertNotIn("00000000-0000-4000-8000", result.stdout + result.stderr)
            refused = subprocess.run([sys.executable, guards.__file__, "custom-role-check", "--role", "blob-tag-reader",
                                      "--definitions-json", str(path), "--scope", SYNTHETIC_SCOPE], capture_output=True, text=True)
            self.assertEqual(2, refused.returncode)


class RecipientNoticeOwnerGuideTests(unittest.TestCase):
    """W5, W6 and W11 item 11: the owner guide creates each custom role with exactly the actions the workflow checks, re-creates
    the deployer's condition with the new role ids, holds names only (no value), and is linked from the runbook."""

    @classmethod
    def setUpClass(cls):
        cls.text = OWNER_GUIDE.read_text(encoding="utf-8")

    def test_each_custom_role_is_created_with_exactly_the_reviewed_actions(self):
        blocks = re.findall(r"<<EOF\n(\{\n.*?\n\})\nEOF", self.text, re.S)
        created = {}
        for block in blocks:
            definition = json.loads(block.replace("${RG_ID}", SYNTHETIC_SCOPE))
            created[definition["Name"]] = definition
        for key in guards.RECIPIENT_NOTICE_CUSTOM_ROLES:
            role = guards.CUSTOM_ROLES[key]
            with self.subTest(role=role.name):
                definition = created[role.name]
                self.assertEqual([SYNTHETIC_SCOPE], definition["AssignableScopes"])
                self.assertEqual(([], []), (definition["NotActions"], definition["NotDataActions"]))
                as_listed = az_role_definition(key, permissions=[{
                    "actions": definition["Actions"], "notActions": definition["NotActions"],
                    "dataActions": definition["DataActions"], "notDataActions": definition["NotDataActions"]}],
                    assignableScopes=definition["AssignableScopes"])
                self.assertEqual([], guards.custom_role_failures(key, [as_listed], SYNTHETIC_SCOPE))
                self.assertIn(f'--name "{role.name}"', self.text)

    def test_the_deployer_condition_admits_the_new_role_ids(self):
        allowed = re.search(r'^ALLOWED="([^"]+)"$', self.text, re.M)
        self.assertIsNotNone(allowed)
        ids = [part.strip() for part in allowed.group(1).split(",")]
        built_in = [i for i in ids if not i.startswith("${")]
        self.assertEqual(sorted(guards.ALLOWED_ROLE_IDS), sorted(built_in))
        self.assertEqual(["${SENDER_ID}", "${TAG_READER_ID}", "${UNWRAP_ID}"], sorted(i for i in ids if i.startswith("${")))
        self.assertIn('--role "Role Based Access Control Administrator"', self.text)

    def test_the_guide_holds_names_and_never_values(self):
        emails = set(re.findall(r"[A-Za-z0-9._%+-]+@[A-Za-z0-9-]+(?:\.[A-Za-z0-9-]+)+", self.text))
        self.assertEqual({"avisos@paquetenvia.com"}, emails)
        guids = set(re.findall(r"[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}", self.text))
        self.assertLessEqual(guids, set(guards.ALLOWED_ROLE_IDS))
        self.assertNotRegex(self.text, r"/subscriptions/[0-9a-f-]{36}")
        for url in (guards.EMAIL_EVENTS_ENDPOINT, "https://paquetenvia.com/api/v1/messaging/whatsapp/webhook"):
            self.assertIn(url, self.text)

    def test_the_guide_is_linked_from_the_runbook_and_guide_a(self):
        for document in ("README.md", "guia-owner-seccion-a.md", "bootstrap.md"):
            with self.subTest(document=document):
                self.assertIn("guia-owner-avisos.md", (OWNER_GUIDE.parent / document).read_text(encoding="utf-8"))


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
            '          image="$(build api deploy/azure/Dockerfile.api)"\n',
            '          image="$(build api deploy/azure/Dockerfile.api)"\n'
            '          image="$(build tool deploy/azure/Dockerfile.tool)"\n', 1)
        missing = self.workflow.replace('          image="$(build worker deploy/azure/Dockerfile.worker)"\n', "", 1)
        for case, workflow in (("fifth image", extra), ("worker not built", missing)):
            with self.subTest(case=case):
                self.assertNotEqual(self.workflow, workflow)
                failures = guards.pilot_image_user_failures(self.root, workflow)
                self.assertEqual(1, len(failures), failures)
                self.assertIn("the workflow must build exactly the images", failures[0])


VALID_SERVER_NAMES = (
    "pg-pv-pilot-abcdefghijklm",
    "pg-pv-pilot-0123456789abc-r202610201310",
    "pg-pv-pilot-abcdefghijklm-drill-202610201310",
    "pg-pv-pilot-abcdefghijklm-a-b-c",
    "pg-pv-pilot-abcdefghijklm-" + "x" * 37,  # exactly 63 characters
)
INVALID_SERVER_NAMES = (
    "pg-pv-pilot-abcdefghijkl",  # 12-character suffix
    "pg-pv-pilot-abcdefghijklmn",  # 14 characters before a part
    "pg-pv-pilot-abcdefghijklm-",
    "pg-pv-pilot-abcdefghijklm--r1",
    "pg-pv-pilot-ABCDEFGHIJKLM",
    "PG-PV-PILOT-abcdefghijklm",
    "pg-pv-pilot-abcdefghijklm_r1",
    "pg-pv-pilot-abcdefghijklm r1",
    "pg-pv-pilot-abcdefghijklm\n",
    "pg-pv-other-abcdefghijklm",
    "pg-pv-azrdev-abcdefghijklm",
    "@pg-pv-pilot-abcdefghijklm",
    "pg-pv-pilot-abcdefghijklm-" + "x" * 38,  # 64 characters
    " ",
)


class PostgresServerNameTests(unittest.TestCase):
    """OPS-002-RESTORE-FOLLOWS-SERVER-2026-10-20: PILOT_POSTGRES_SERVER_NAME is empty (the first server) or a pilot server
    name, checked fail-closed before the workflow touches Azure; the drill script applies the same pattern."""

    def run_cli(self, value: str) -> subprocess.CompletedProcess:
        return subprocess.run([sys.executable, guards.__file__, "postgres-server-name", "--value", value],
                              capture_output=True, text=True)

    def test_empty_means_the_default_server(self):
        self.assertEqual([], guards.validate_postgres_server_name(""))
        result = self.run_cli("")
        self.assertEqual(0, result.returncode, result.stdout)
        self.assertIn("ENV001_POSTGRES_SERVER=PASS default", result.stdout)

    def test_pilot_server_names_pass(self):
        for value in VALID_SERVER_NAMES:
            with self.subTest(value=value):
                self.assertLessEqual(len(value), 63)
                self.assertEqual([], guards.validate_postgres_server_name(value))
                result = self.run_cli(value)
                self.assertEqual(0, result.returncode, result.stdout)
                self.assertIn(f"ENV001_POSTGRES_SERVER=PASS {value}", result.stdout)

    def test_anything_else_fails_closed_without_echoing_the_value(self):
        for value in INVALID_SERVER_NAMES + (None, 7):
            with self.subTest(value=value):
                failures = guards.validate_postgres_server_name(value)
                self.assertEqual(1, len(failures))
                self.assertIn("PILOT_POSTGRES_SERVER_NAME must be empty", failures[0])
                if isinstance(value, str):
                    result = self.run_cli(value)
                    self.assertEqual(1, result.returncode)
                    self.assertIn("ENV001_POSTGRES_SERVER=FAIL", result.stdout)
                    if value.strip():
                        self.assertNotIn(value.strip(), result.stdout + result.stderr)

    def test_the_drill_script_checks_the_same_pattern(self):
        script = (REPO_ROOT / guards.RESTORE_DRILL_SCRIPT).read_text(encoding="utf-8")
        line = next(line for line in script.splitlines() if guards.POSTGRES_SERVER_NAME_PATTERN in line)
        self.assertIn('"${SOURCE}" =~ ' + guards.POSTGRES_SERVER_NAME_PATTERN, line)
        probe = ('[[ "$1" =~ ' + guards.POSTGRES_SERVER_NAME_PATTERN + ' && ${#1} -le 63 ]] && printf "%s" "${BASH_REMATCH[1]}"')
        for value in VALID_SERVER_NAMES + INVALID_SERVER_NAMES:
            with self.subTest(value=value):
                result = subprocess.run(["bash", "-c", probe, "probe", value], capture_output=True, text=True)
                valid = not guards.validate_postgres_server_name(value)
                self.assertEqual(valid, result.returncode == 0)
                if valid:
                    self.assertEqual(value[:25], result.stdout, "group 1 is the base name: the prefix and the 13-character suffix")


class ConnectionHostTests(unittest.TestCase):
    """The workflow keeps a runtime credential only while its Host is the server in service; the host is read without
    ever printing the connection string."""

    PASSWORD = "ab" * 32  # a 64-character stand-in, like the workflow's hex passwords
    HOST = "pg-pv-pilot-abcdefghijklm.postgres.database.azure.com"

    def connection(self, host: str = HOST, key: str = "Host") -> str:
        return (f"{key}={host};Database=paqueteria;Username=pv_pilot_api;Password={self.PASSWORD};Maximum Pool Size=5;"
                "Minimum Pool Size=0;SSL Mode=VerifyFull;Timeout=15;Command Timeout=60")

    def test_the_host_is_read_from_the_workflow_format_and_npgsql_aliases(self):
        self.assertEqual(self.HOST, guards.connection_host(self.connection()))
        self.assertEqual(self.HOST, guards.connection_host(self.connection(key="host") + "\n"))
        self.assertEqual(self.HOST, guards.connection_host(self.connection(key=" Server ")))

    def test_missing_duplicated_or_foreign_hosts_fail_without_quoting_the_input(self):
        cases = {
            "no host": self.connection().replace(f"Host={self.HOST};", ""),
            "two hosts": self.connection() + f";Server={self.HOST}",
            "not a flexible server": self.connection(host="db.example.com"),
            "upper case": self.connection(host=self.HOST.upper()),
            "empty": "",
        }
        for name, text in cases.items():
            with self.subTest(name=name):
                with self.assertRaises(guards.GuardError) as raised:
                    guards.connection_host(text)
                self.assertNotIn(self.PASSWORD, str(raised.exception))
                result = subprocess.run([sys.executable, guards.__file__, "connection-host"], input=text, capture_output=True, text=True)
                self.assertEqual(2, result.returncode)
                self.assertEqual("", result.stdout)
                self.assertNotIn(self.PASSWORD, result.stderr)
        result = subprocess.run([sys.executable, guards.__file__, "connection-host"], input=self.connection(), capture_output=True, text=True)
        self.assertEqual((0, self.HOST + "\n"), (result.returncode, result.stdout))


# A stand-in for the Azure CLI: Key Vault secrets are files in $AZ_STATE/secrets, Container Apps revisions are
# $AZ_STATE/apps/<app> (the word active or inactive), and every call is logged to $AZ_STATE/calls without values.
FAKE_AZ = r'''#!/usr/bin/env python3
import json, os, re, sys
state = os.environ["AZ_STATE"]
args = sys.argv[1:]
def opt(name):
    return args[args.index(name) + 1] if name in args else None
with open(os.path.join(state, "calls"), "a", encoding="utf-8") as log:
    log.write(" ".join(a for a in args[:3]) + " " + (opt("--name") or "") + "\n")
secrets = os.path.join(state, "secrets")
if args[:3] == ["keyvault", "secret", "list"]:
    name = re.search(r"name=='([^']+)'", opt("--query")).group(1)
    print(1 if os.path.exists(os.path.join(secrets, name)) else 0)
elif args[:3] == ["keyvault", "secret", "show"]:
    path = os.path.join(secrets, opt("--name"))
    if not os.path.exists(path):
        print("ERROR: SecretNotFound", file=sys.stderr); sys.exit(3)
    print(open(path, encoding="utf-8").read())
elif args[:3] == ["keyvault", "secret", "set"]:
    with open(opt("--file"), encoding="utf-8") as source, open(os.path.join(secrets, opt("--name")), "w", encoding="utf-8") as target:
        target.write(source.read())
elif args[:2] == ["resource", "show"]:
    path = os.path.join(state, "acs-id")
    if opt("--resource-type") != "Microsoft.Communication/communicationServices" or not os.path.exists(path):
        print("ERROR: ResourceNotFound", file=sys.stderr); sys.exit(3)
    print(open(path, encoding="utf-8").read())
elif args[:3] == ["role", "definition", "list"]:
    path = os.path.join(state, "roles", opt("--name") + ".json")
    print(open(path, encoding="utf-8").read() if os.path.exists(path) else "[]")
elif args[:2] == ["containerapp", "list"]:
    name = re.search(r"name=='([^']+)'", opt("--query")).group(1)
    if os.path.exists(os.path.join(state, "apps", name)):
        print(name)
elif args[:2] == ["containerapp", "show"]:
    print(opt("--name") + "--rev1")
elif args[:3] == ["containerapp", "revision", "show"]:
    print("true" if open(os.path.join(state, "apps", opt("--name")), encoding="utf-8").read().strip() == "active" else "false")
elif args[:3] == ["containerapp", "revision", "activate"] or args[:3] == ["containerapp", "revision", "restart"]:
    if opt("--revision") != opt("--name") + "--rev1":
        sys.exit(4)
    if args[2] == "activate":
        open(os.path.join(state, "apps", opt("--name")), "w", encoding="utf-8").write("active")
else:
    print("unexpected az call: " + " ".join(args[:3]), file=sys.stderr); sys.exit(9)
'''


def workflow_step(predicate) -> dict:
    workflow = guards.load_yaml(REPO_ROOT / guards.PILOT_WORKFLOW)
    steps = [s for s in workflow["jobs"]["deploy"]["steps"] if predicate(s)]
    assert len(steps) == 1, steps
    return steps[0]


class WorkflowStepHarness(unittest.TestCase):
    """Runs one step of the pilot workflow as GitHub does (`bash --noprofile --norc -eo pipefail`), from the repository
    root, with the fake Azure CLI first on PATH."""

    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory(prefix="env001-step-")
        self.root = Path(self.tmp.name)
        for directory in ("bin", "state/secrets", "state/apps", "runner"):
            (self.root / directory).mkdir(parents=True)
        az = self.root / "bin" / "az"
        az.write_text(FAKE_AZ, encoding="utf-8")
        az.chmod(0o755)
        self.state = self.root / "state"
        self.output = self.root / "runner" / "github-output"

    def tearDown(self):
        self.tmp.cleanup()

    def run_step(self, step: dict, **env: str) -> subprocess.CompletedProcess:
        script = self.root / "step.sh"
        script.write_text(step["run"], encoding="utf-8")
        self.output.write_text("", encoding="utf-8")
        environment = dict(os.environ, PATH=f"{self.root / 'bin'}{os.pathsep}{os.environ.get('PATH', '')}", AZ_STATE=str(self.state),
                           RUNNER_TEMP=str(self.root / "runner"), GITHUB_OUTPUT=str(self.output), PILOT_RESOURCE_GROUP="rg-pv-pilot")
        environment.update(env)
        return subprocess.run(["bash", "--noprofile", "--norc", "-eo", "pipefail", str(script)], cwd=REPO_ROOT,
                              capture_output=True, text=True, env=environment)

    def outputs(self) -> dict[str, str]:
        return dict(line.split("=", 1) for line in self.output.read_text(encoding="utf-8").splitlines() if "=" in line)

    def secret(self, name: str) -> str:
        return (self.state / "secrets" / name).read_text(encoding="utf-8")

    def put_secret(self, name: str, value: str) -> None:
        (self.state / "secrets" / name).write_text(value, encoding="utf-8")


class WorkflowConnectionSecretsTests(WorkflowStepHarness):
    """OPS-002-RESTORE-FOLLOWS-SERVER-2026-10-20: the connections step rebuilds pg-migrate-connection for the server in
    service on every run and keeps each runtime credential only while its Host is that server; otherwise (or with
    rotate_runtime_logins) it writes a new password and its SCRAM verifier, which the logins job applies on that server.
    No secret value is ever printed except inside an ::add-mask:: command."""

    OLD_HOST = "pg-pv-pilot-abcdefghijklm.postgres.database.azure.com"
    NEW_HOST = "pg-pv-pilot-abcdefghijklm-r202610201310.postgres.database.azure.com"
    ROLES = ("api", "worker", "realtime")

    def setUp(self):
        super().setUp()
        self.step = workflow_step(lambda s: s.get("id") == "connections")
        self.assertEqual("${{ steps.platform.outputs.postgresHost }}", self.step["env"]["PGHOST_FQDN"])
        self.put_secret("pg-admin-password", "Pv1-" + "a" * 54)
        for name in ("authcenter-paquetenvia-client-secret", "google-maps-api-key", "public-tracking-link-key",
                     "paquetenvia-telemetry-pseudonym-key-1", *guards.RECIPIENT_NOTICE_OWNER_SECRETS):
            self.put_secret(name, f"owner-value-of-{name}-0123456789")

    def run_connections(self, host: str, rotate: str = "false") -> subprocess.CompletedProcess:
        return self.run_step(self.step, VAULT="kv-pvp-test", PGHOST_FQDN=host, PGADMIN_LOGIN="pvpilotadmin", ROTATE_RUNTIME_LOGINS=rotate)

    def runtime(self) -> dict[str, tuple[str, str]]:
        return {role: (self.secret(f"pg-{role}-runtime-connection"), self.secret(f"pg-{role}-login-verifier")) for role in self.ROLES}

    def assert_no_secret_printed(self, result: subprocess.CompletedProcess) -> None:
        values = [p.read_text(encoding="utf-8") for p in (self.state / "secrets").iterdir()]
        values += [v.split("Password=", 1)[1].split(";", 1)[0] for v in values if "Password=" in v]
        for line in (result.stdout + result.stderr).splitlines():
            if line.startswith("::add-mask::"):
                continue
            for value in values:
                self.assertNotIn(value, line)

    def assert_credential(self, role: str, host: str) -> None:
        connection, verifier = self.runtime()[role]
        self.assertEqual(host, guards.connection_host(connection))
        self.assertIn(f"Username=pv_pilot_{role};", connection)
        password = connection.split("Password=", 1)[1].split(";", 1)[0]
        self.assertRegex(password, r"^[0-9a-f]{64}$")
        iterations, rest = verifier.removeprefix("SCRAM-SHA-256$").split(":", 1)
        salt = base64.b64decode(rest.split("$", 1)[0])
        self.assertEqual(verifier, guards.scram_sha256_verifier(password, salt=salt, iterations=int(iterations)))

    def test_first_run_generates_every_credential_for_the_server(self):
        result = self.run_connections(self.OLD_HOST)
        self.assertEqual(0, result.returncode, result.stderr)
        self.assertEqual("true", self.outputs()["runtime_logins_changed"])
        for role in self.ROLES:
            self.assert_credential(role, self.OLD_HOST)
        self.assertEqual(self.OLD_HOST, guards.connection_host(self.secret("pg-migrate-connection")))
        self.assert_no_secret_printed(result)

    def test_same_server_keeps_every_credential(self):
        self.assertEqual(0, self.run_connections(self.OLD_HOST).returncode)
        before = self.runtime()
        result = self.run_connections(self.OLD_HOST)
        self.assertEqual(0, result.returncode, result.stderr)
        self.assertEqual(before, self.runtime())
        self.assertEqual("false", self.outputs()["runtime_logins_changed"])
        self.assertNotIn("Generated runtime login credential", result.stdout)
        # The kept values were read, and masked before anything else was printed.
        masks = [line for line in result.stdout.splitlines() if line.startswith("::add-mask::")]
        for connection, _ in before.values():
            self.assertIn("::add-mask::" + connection, masks)
            self.assertIn("::add-mask::" + connection.split("Password=", 1)[1].split(";", 1)[0], masks)
        self.assert_no_secret_printed(result)

    def test_a_new_server_regenerates_every_credential_and_the_migration_connection(self):
        self.assertEqual(0, self.run_connections(self.OLD_HOST).returncode)
        before = self.runtime()
        result = self.run_connections(self.NEW_HOST)
        self.assertEqual(0, result.returncode, result.stderr)
        self.assertEqual("true", self.outputs()["runtime_logins_changed"])
        after = self.runtime()
        for role in self.ROLES:
            with self.subTest(role=role):
                self.assertNotEqual(before[role][0].split("Password=")[1], after[role][0].split("Password=")[1])
                self.assertNotEqual(before[role][1], after[role][1])
                self.assert_credential(role, self.NEW_HOST)
                self.assertIn(f"The runtime login credential for {role} names another PostgreSQL server", result.stdout)
        self.assertEqual(self.NEW_HOST, guards.connection_host(self.secret("pg-migrate-connection")))
        self.assert_no_secret_printed(result)
        # Going back (the variable emptied again) follows the first server the same way.
        self.assertEqual(0, self.run_connections(self.OLD_HOST).returncode)
        for role in self.ROLES:
            self.assert_credential(role, self.OLD_HOST)

    def test_only_the_credential_on_another_server_is_regenerated(self):
        self.assertEqual(0, self.run_connections(self.NEW_HOST).returncode)
        before = self.runtime()
        self.put_secret("pg-worker-runtime-connection", before["worker"][0].replace(self.NEW_HOST, self.OLD_HOST))
        result = self.run_connections(self.NEW_HOST)
        self.assertEqual(0, result.returncode, result.stderr)
        after = self.runtime()
        self.assertEqual((before["api"], before["realtime"]), (after["api"], after["realtime"]))
        self.assertNotEqual(before["worker"][1], after["worker"][1])
        self.assert_credential("worker", self.NEW_HOST)
        self.assertEqual("true", self.outputs()["runtime_logins_changed"])

    def test_rotation_still_regenerates_every_credential_on_the_same_server(self):
        self.assertEqual(0, self.run_connections(self.OLD_HOST).returncode)
        before = self.runtime()
        result = self.run_connections(self.OLD_HOST, rotate="true")
        self.assertEqual(0, result.returncode, result.stderr)
        after = self.runtime()
        for role in self.ROLES:
            self.assertNotEqual(before[role][1], after[role][1])
            self.assert_credential(role, self.OLD_HOST)
        self.assertEqual("true", self.outputs()["runtime_logins_changed"])
        self.assert_no_secret_printed(result)

    def test_a_kept_credential_without_a_readable_host_stops_the_run(self):
        self.assertEqual(0, self.run_connections(self.OLD_HOST).returncode)
        broken = "Database=paqueteria;Username=pv_pilot_api;Password=" + "f" * 64
        self.put_secret("pg-api-runtime-connection", broken)
        verifier = self.secret("pg-api-login-verifier")
        result = self.run_connections(self.NEW_HOST)
        self.assertNotEqual(0, result.returncode)
        self.assertEqual((broken, verifier), (self.secret("pg-api-runtime-connection"), self.secret("pg-api-login-verifier")))
        self.assertNotIn("runtime_logins_changed", self.outputs())
        self.assert_no_secret_printed(result)

    def test_each_missing_meta_value_stops_the_run_naming_it(self):
        # W7 and W11 item 8 (NTF-RECIPIENT-NOTICE-PILOT-WIRING-2026-10-31): a mapped secret that does not exist stops the host.
        for name in guards.RECIPIENT_NOTICE_OWNER_SECRETS:
            with self.subTest(name=name):
                value = self.secret(name)
                (self.state / "secrets" / name).unlink()
                result = self.run_connections(self.OLD_HOST)
                self.assertNotEqual(0, result.returncode)
                self.assertIn(f"lacks {name}. Store the Meta value (guia-owner-avisos.md, paso 4) and re-run.", result.stdout)
                self.put_secret(name, value)
        self.assertEqual(0, self.run_connections(self.OLD_HOST).returncode)


class WorkflowAppRestartTests(WorkflowStepHarness):
    """Once stage 3 has applied the logins, the API and Worker run on the current credentials: a revision the real
    restore stopped is started (it reads the current secrets as it starts), a running one restarts only when a
    credential changed, and an app not deployed yet is left to stage 4."""

    APPS = ("ca-pv-pilot-api", "ca-pv-pilot-worker")

    def setUp(self):
        super().setUp()
        self.step = workflow_step(lambda s: "az containerapp revision restart" in str(s.get("run", "")))
        self.assertEqual("${{ steps.connections.outputs.runtime_logins_changed }}", self.step["env"]["RUNTIME_LOGINS_CHANGED"])

    def run_apps(self, changed: str, **states: str) -> list[str]:
        for app in self.APPS:
            path = self.state / "apps" / app
            state = states.get(app.split("-")[-1], "active")
            if state == "absent":
                path.unlink(missing_ok=True)
            else:
                path.write_text(state, encoding="utf-8")
        calls = self.state / "calls"
        calls.write_text("", encoding="utf-8")
        result = self.run_step(self.step, RUNTIME_LOGINS_CHANGED=changed)
        self.assertEqual(0, result.returncode, result.stderr)
        return [line.strip() for line in calls.read_text(encoding="utf-8").splitlines()
                if line.startswith(("containerapp revision activate", "containerapp revision restart"))]

    def test_changed_credentials_restart_both_running_apps(self):
        self.assertEqual([f"containerapp revision restart {app}" for app in self.APPS], self.run_apps("true"))

    def test_unchanged_credentials_leave_running_apps_alone(self):
        self.assertEqual([], self.run_apps("false"))
        self.assertEqual([], self.run_apps(""))

    def test_stopped_revisions_are_started_whatever_changed(self):
        for changed in ("true", "false"):
            with self.subTest(changed=changed):
                self.assertEqual([f"containerapp revision activate {app}" for app in self.APPS],
                                 self.run_apps(changed, api="inactive", worker="inactive"))
        self.assertEqual(["containerapp revision restart ca-pv-pilot-api", "containerapp revision activate ca-pv-pilot-worker"],
                         self.run_apps("true", worker="inactive"))

    def test_apps_not_deployed_yet_are_left_to_stage_4(self):
        self.assertEqual([], self.run_apps("true", api="absent", worker="absent"))
        self.assertEqual(["containerapp revision restart ca-pv-pilot-worker"], self.run_apps("true", api="absent"))

    def test_the_step_runs_after_the_logins_job_and_before_stage_4(self):
        names = [s.get("name", "") for s in guards.load_yaml(REPO_ROOT / guards.PILOT_WORKFLOW)["jobs"]["deploy"]["steps"]]
        index = names.index(self.step["name"])
        self.assertTrue(names[index - 1].startswith("Stage 3 - "), names[index - 1])
        self.assertTrue(names[index + 1].startswith("Stage 4 - "), names[index + 1])


class WorkflowRecipientNoticeSecretsTests(WorkflowStepHarness):
    """NTF-RECIPIENT-NOTICE-PILOT-WIRING-2026-10-31 (W7 "El workflow (Recommended)"): window 1 generates the seven
    recipient-notice secrets nobody types once, when missing, and never prints them; it never writes a Meta value."""

    def setUp(self):
        super().setUp()
        self.step = workflow_step(lambda s: s.get("id") == "generated")
        (self.state / "acs-id").write_text(SYNTHETIC_ACS_ID + "\n", encoding="utf-8")

    def run_generated(self) -> subprocess.CompletedProcess:
        return self.run_step(self.step, VAULT="kv-pvp-test", PILOT_ACS_NAME="acs-pv-synthetic")

    def generated(self) -> dict[str, str]:
        return {name: self.secret(name) for name in guards.RECIPIENT_NOTICE_GENERATED_SECRETS}

    def test_the_first_run_generates_the_seven_secrets_and_never_prints_them(self):
        result = self.run_generated()
        self.assertEqual(0, result.returncode, result.stderr)
        values = self.generated()
        self.assertEqual(32, len(base64.b64decode(values[guards.CONTACT_LOOKUP_SECRET], validate=True)))
        for name in set(guards.RECIPIENT_NOTICE_GENERATED_SECRETS) - {guards.CONTACT_LOOKUP_SECRET, guards.ACS_RESOURCE_ID_SECRET}:
            with self.subTest(name=name):
                self.assertRegex(values[name], r"^[0-9a-f]{64}$")
                self.assertIn(f"Generated {name}.", result.stdout)
        self.assertEqual(SYNTHETIC_ACS_ID, values[guards.ACS_RESOURCE_ID_SECRET])
        self.assertEqual(len(values), len(set(values.values())))
        for name in guards.RECIPIENT_NOTICE_OWNER_SECRETS:
            self.assertFalse((self.state / "secrets" / name).exists(), name)
        for value in values.values():
            self.assertNotIn(value, result.stdout + result.stderr)

    def test_a_second_run_keeps_every_value(self):
        self.assertEqual(0, self.run_generated().returncode)
        before = self.generated()
        (self.state / "acs-id").write_text(SYNTHETIC_ACS_ID.replace("acs-pv-synthetic", "acs-pv-other") + "\n", encoding="utf-8")
        result = self.run_generated()
        self.assertEqual(0, result.returncode, result.stderr)
        self.assertEqual(before, self.generated())
        self.assertNotIn("Generated", result.stdout)

    def test_an_unreadable_resource_id_stops_before_storing_it(self):
        (self.state / "acs-id").unlink()
        result = self.run_generated()
        self.assertNotEqual(0, result.returncode)
        self.assertFalse((self.state / "secrets" / guards.ACS_RESOURCE_ID_SECRET).exists())
        self.assertFalse((self.root / "runner" / "s").exists())


class WorkflowCustomRoleLookupTests(WorkflowStepHarness):
    """W5 and W6: before stage 1 the workflow finds each recipient-notice custom role by its exact name and stops unless it
    holds exactly the reviewed actions; the role definition ids go to stage 1 and stage 4 as step outputs."""

    def setUp(self):
        super().setUp()
        self.step = workflow_step(lambda s: s.get("id") == "roles")
        (self.state / "roles").mkdir()
        self.env = guards.load_yaml(REPO_ROOT / guards.PILOT_WORKFLOW)["env"]

    def write(self, key: str, definitions: list) -> None:
        name = guards.CUSTOM_ROLES[key].name
        (self.state / "roles" / f"{name}.json").write_text(json.dumps(definitions), encoding="utf-8")

    def run_roles(self) -> subprocess.CompletedProcess:
        return self.run_step(self.step, AZURE_SUBSCRIPTION_ID="00000000-0000-4000-8000-000000000000",
                             RECIPIENT_NOTICE_UNWRAP_ROLE=self.env["RECIPIENT_NOTICE_UNWRAP_ROLE"],
                             EMAIL_SENDER_ROLE=self.env["EMAIL_SENDER_ROLE"])

    def test_the_reviewed_roles_are_found_and_their_ids_handed_on(self):
        for key in guards.RECIPIENT_NOTICE_CUSTOM_ROLES:
            self.write(key, [az_role_definition(key)])
        result = self.run_roles()
        self.assertEqual(0, result.returncode, result.stdout + result.stderr)
        outputs = self.outputs()
        self.assertEqual(az_role_definition("recipient-notice-unwrap")["id"], outputs["unwrap_role"])
        self.assertEqual(az_role_definition("email-sender")["id"], outputs["sender_role"])

    def test_a_missing_or_broader_role_stops_the_run(self):
        self.write("recipient-notice-unwrap", [az_role_definition("recipient-notice-unwrap")])
        result = self.run_roles()
        self.assertNotEqual(0, result.returncode)
        self.assertIn("'Paquetenvia Pilot Email Sender' must exist exactly once", result.stdout)
        self.assertNotIn("sender_role", self.outputs())
        sender = guards.CUSTOM_ROLES["email-sender"]
        self.write("email-sender", [az_role_definition("email-sender", permissions=[{
            "actions": [*sender.actions, "Microsoft.Communication/CommunicationServices/ListKeys/action"],
            "notActions": [], "dataActions": [], "notDataActions": []}])])
        result = self.run_roles()
        self.assertNotEqual(0, result.returncode)
        self.assertIn("never grant listKeys", result.stdout)


class WorkflowEmailEventsPhaseTests(WorkflowStepHarness):
    """O8 and W11 item 5: the phase is the committed default of events.bicep, read from the compiled template; 'none'
    creates nothing, and 'subscribe' needs the apex bound."""

    def setUp(self):
        super().setUp()
        self.step = workflow_step(lambda s: s.get("id") == "events_phase")
        (self.root / "runner" / "arm").mkdir()

    def run_phase(self, phase: str, domain: str) -> subprocess.CompletedProcess:
        template = {"parameters": {"emailEventsPhase": {"type": "string", "defaultValue": phase,
                                                        "allowedValues": guards.EMAIL_EVENTS_PHASES}}}
        (self.root / "runner" / "arm" / "events.json").write_text(json.dumps(template), encoding="utf-8")
        return self.run_step(self.step, CUSTOM_DOMAIN_PHASE=domain, PILOT_HOST="paquetenvia.com")

    def test_the_repository_commits_none(self):
        source = (REPO_ROOT / guards.PILOT_DIR / "events.bicep").read_text(encoding="utf-8")
        self.assertEqual(["none"], re.findall(r"^param emailEventsPhase string = '([a-z]+)'$", source, re.M))

    def test_none_creates_nothing(self):
        for domain in ("none", "bind"):
            result = self.run_phase("none", domain)
            self.assertEqual(0, result.returncode, result.stderr)
            self.assertEqual("none", self.outputs()["phase"])
            self.assertIn("no Event Grid system topic or subscription", result.stdout)
        self.assertFalse((self.state / "calls").exists())

    def test_subscribe_needs_the_apex_bound(self):
        result = self.run_phase("subscribe", "none")
        self.assertNotEqual(0, result.returncode)
        self.assertIn("needs custom_domain_phase=bind", result.stdout)
        self.assertNotIn("phase", self.outputs())
        result = self.run_phase("subscribe", "bind")
        self.assertEqual(0, result.returncode, result.stderr)
        self.assertEqual("subscribe", self.outputs()["phase"])

    def test_any_other_value_stops(self):
        result = self.run_phase("true", "bind")
        self.assertNotEqual(0, result.returncode)
        self.assertNotIn("phase", self.outputs())


# A stand-in for the Azure CLI as the revision check calls it. $AZ_STATE/revisions.json holds, per app, the
# [latestRevisionName, latestReadyRevisionName] pairs that successive `az containerapp show` calls return, and per
# "<app>/<revision>" the {active, health, images} states that successive `az containerapp revision show` calls return;
# the last item of each list repeats. Only the step's two queries are answered; every call is logged to $AZ_STATE/calls.
FAKE_AZ_REVISIONS = r'''#!/usr/bin/env python3
import json, os, sys
state = os.environ["AZ_STATE"]
args = sys.argv[1:]
def opt(name):
    return args[args.index(name) + 1] if name in args else None
with open(os.path.join(state, "calls"), "a", encoding="utf-8") as log:
    log.write(" ".join(args[:3] if args[1] == "revision" else args[:2]) + " " + (opt("--name") or "") + " " + (opt("--revision") or "") + "\n")
path = os.path.join(state, "revisions.json")
data = json.load(open(path, encoding="utf-8"))
counts = data.setdefault("counts", {})
def next_item(key, items):
    index = counts.get(key, 0)
    counts[key] = index + 1
    json.dump(data, open(path, "w", encoding="utf-8"))
    return items[min(index, len(items) - 1)]
if opt("--resource-group") != "rg-pv-pilot" or opt("-o") != "json":
    print("unexpected resource group or output", file=sys.stderr); sys.exit(8)
if args[:2] == ["containerapp", "show"] and opt("--query") == "{latest: properties.latestRevisionName, ready: properties.latestReadyRevisionName}":
    latest, ready = next_item(opt("--name"), data["apps"][opt("--name")])
    print(json.dumps({"latest": latest, "ready": ready}, indent=2))
elif args[:3] == ["containerapp", "revision", "show"] and opt("--query") == (
        "{active: properties.active, health: properties.healthState, images: properties.template.containers[].image}"):
    key = opt("--name") + "/" + opt("--revision")
    print(json.dumps(next_item(key, data["revisions"][key]), indent=2))
else:
    print("unexpected az call: " + " ".join(args), file=sys.stderr); sys.exit(9)
'''


class WorkflowRevisionCheckTests(WorkflowStepHarness):
    """ENV-001 F-06: after stage 4 each of the API, the Worker and the Web must reach a latest revision that is ready,
    runs this run's digest, is active and Healthy. The poll is bounded (40 polls 15 s apart for the three apps
    together); a ready revision on another image is polled again within that budget, because right after stage 4 the
    control plane can still report the previous revision as the latest one. A failure names the app and the revision
    and points to its system log; nothing secret is read."""

    APPS = (guards.API_APP, guards.WORKER_APP, guards.WEB_APP)
    REGISTRY = "crpvpilotabcdefghijklm.azurecr.io"

    def setUp(self):
        super().setUp()
        self.step = workflow_step(lambda s: s.get("id") == "revisions")
        (self.root / "bin" / "az").write_text(FAKE_AZ_REVISIONS, encoding="utf-8")
        sleep = self.root / "bin" / "sleep"
        sleep.write_text('#!/bin/sh\necho "sleep $*" >> "${AZ_STATE}/calls"\n', encoding="utf-8")
        sleep.chmod(0o755)
        self.images = {app: f"{self.REGISTRY}/paquetenvia-{app.split('-')[-1]}@sha256:{digit * 64}"
                       for app, digit in zip(self.APPS, "abc")}

    def healthy(self, app: str, **changes) -> dict:
        return {"active": True, "health": "Healthy", "images": [self.images[app]], **changes}

    def run_check(self, apps: dict | None = None, revisions: dict | None = None, **env: str) -> subprocess.CompletedProcess:
        """Every app ready on the first poll with a healthy revision `<app>--new`, unless `apps`/`revisions` say otherwise."""
        state = {"apps": {app: [[f"{app}--new", f"{app}--new"]] for app in self.APPS},
                 "revisions": {f"{app}/{app}--new": [self.healthy(app)] for app in self.APPS}}
        state["apps"].update(apps or {})
        state["revisions"].update(revisions or {})
        (self.state / "revisions.json").write_text(json.dumps(state), encoding="utf-8")
        (self.state / "calls").write_text("", encoding="utf-8")
        images = {"API_IMAGE": self.images[guards.API_APP], "WORKER_IMAGE": self.images[guards.WORKER_APP],
                  "WEB_IMAGE": self.images[guards.WEB_APP]}
        images.update(env)
        return self.run_step(self.step, **images)

    def calls(self) -> list[str]:
        return [line.strip() for line in (self.state / "calls").read_text(encoding="utf-8").splitlines()]

    def sleeps(self) -> int:
        return sum(1 for line in self.calls() if line == "sleep 15")

    def assert_failed(self, result: subprocess.CompletedProcess, app: str, message: str) -> None:
        self.assertNotEqual(0, result.returncode)
        errors = [line for line in result.stdout.splitlines() if line.startswith("::error::")]
        self.assertEqual(1, len(errors), result.stdout)
        self.assertTrue(errors[0].startswith(f"::error::{app}: "), errors[0])
        self.assertIn(message, errors[0])
        self.assertIn(f"az containerapp logs show --name {app} --resource-group rg-pv-pilot --type system", errors[0])

    def test_the_step_reads_only_this_runs_images_and_runs_after_stage_4_and_before_the_smoke_test(self):
        self.assertEqual(guards.REVISION_IMAGE_ENV, self.step["env"])
        names = [s.get("name", "") for s in guards.load_yaml(REPO_ROOT / guards.PILOT_WORKFLOW)["jobs"]["deploy"]["steps"]]
        index = names.index(self.step["name"])
        self.assertLess(next(i for i, name in enumerate(names) if name.startswith("Stage 4 - ")), index)
        self.assertEqual("Smoke test same-origin routing", names[index + 1])

    def test_revisions_ready_on_the_first_poll_pass(self):
        result = self.run_check()
        self.assertEqual(0, result.returncode, result.stdout + result.stderr)
        self.assertEqual(0, self.sleeps())
        self.assertEqual({"api_revision": f"{guards.API_APP}--new", "worker_revision": f"{guards.WORKER_APP}--new",
                          "web_revision": f"{guards.WEB_APP}--new"}, self.outputs())
        for app in self.APPS:
            self.assertIn(f"{app}: revision {app}--new is ready, active and Healthy, and runs {self.images[app]}", result.stdout)
        self.assertNotIn("::error::", result.stdout)

    def test_revisions_that_become_ready_after_some_polls_pass(self):
        api, worker = guards.API_APP, guards.WORKER_APP
        result = self.run_check(apps={api: [[f"{api}--new", f"{api}--old"]] * 3 + [[f"{api}--new", f"{api}--new"]],
                                      worker: [[f"{worker}--new", None]] * 2 + [[f"{worker}--new", f"{worker}--new"]]})
        self.assertEqual(0, result.returncode, result.stdout + result.stderr)
        self.assertEqual(5, self.sleeps())
        self.assertIn(f"{api}: latest revision {api}--new is not ready (latest ready revision: {api}--old); polling again in 15 s",
                      result.stdout)
        self.assertIn(f"{worker}: latest revision {worker}--new is not ready (latest ready revision: none); polling again",
                      result.stdout)
        self.assertEqual(f"{api}--new", self.outputs()["api_revision"])
        # A revision is only read once it is the latest ready one.
        self.assertNotIn(f"containerapp revision show {api} {api}--old", self.calls())

    def test_a_revision_that_never_becomes_ready_fails_with_the_system_log_hint(self):
        api = guards.API_APP
        result = self.run_check(apps={api: [[f"{api}--new", f"{api}--old"]]})
        self.assert_failed(result, api, f"latest revision {api}--new is not ready (latest ready revision: {api}--old) "
                                        "after about 10 minutes; the previous revision, if any, is still serving.")
        self.assertEqual(40, self.sleeps())
        self.assertEqual(41, self.calls().count(f"containerapp show {api}"))
        # The run stops at the first app that fails; nothing is reported as serving.
        self.assertFalse(any(guards.WORKER_APP in line for line in self.calls()))
        self.assertEqual({}, self.outputs())

    def test_a_first_deploy_that_never_becomes_ready_fails(self):
        web = guards.WEB_APP
        result = self.run_check(apps={web: [[f"{web}--first", None]]})
        self.assert_failed(result, web, f"latest revision {web}--first is not ready (latest ready revision: none) after about 10 minutes")
        self.assertEqual({"api_revision", "worker_revision"}, set(self.outputs()))

    def test_the_three_apps_share_one_poll_budget(self):
        api, worker = guards.API_APP, guards.WORKER_APP
        result = self.run_check(apps={api: [[f"{api}--new", f"{api}--old"]] * 30 + [[f"{api}--new", f"{api}--new"]],
                                      worker: [[f"{worker}--new", f"{worker}--old"]]})
        self.assert_failed(result, worker, f"latest revision {worker}--new is not ready")
        self.assertEqual(40, self.sleeps())
        # Polls spent waiting for this run's image count against the same budget.
        result = self.run_check(revisions={f"{api}/{api}--new": [self.healthy(api, images=[self.old_image(api)])] * 30 + [self.healthy(api)],
                                           f"{worker}/{worker}--new": [self.healthy(worker, images=[self.old_image(worker)])]})
        self.assert_failed(result, worker, f"revision {worker}--new is ready but runs '{self.old_image(worker)}'")
        self.assertEqual(40, self.sleeps())

    def old_image(self, app: str) -> str:
        return f"{self.REGISTRY}/paquetenvia-{app.split('-')[-1]}@sha256:{'d' * 64}"

    def test_a_ready_revision_on_another_image_is_polled_again_until_this_runs_image_serves(self):
        api, worker = guards.API_APP, guards.WORKER_APP
        with self.subTest(case="the previous revision is still reported as latest and ready, then the new one"):
            result = self.run_check(apps={api: [[f"{api}--old", f"{api}--old"]] * 2 + [[f"{api}--new", f"{api}--new"]]},
                                    revisions={f"{api}/{api}--old": [self.healthy(api, images=[self.old_image(api)])]})
            self.assertEqual(0, result.returncode, result.stdout + result.stderr)
            self.assertEqual(2, self.sleeps())
            self.assertIn(f"{api}: revision {api}--old is ready but runs '{self.old_image(api)}', not this run's image "
                          f"({self.images[api]}); polling again in 15 s", result.stdout)
            self.assertEqual(f"{api}--new", self.outputs()["api_revision"])
            self.assertNotIn("::error::", result.stdout)
        with self.subTest(case="another image, then this run's image"):
            result = self.run_check(revisions={f"{worker}/{worker}--new": [self.healthy(worker, images=[self.old_image(worker)])] * 3
                                               + [self.healthy(worker)]})
            self.assertEqual(0, result.returncode, result.stdout + result.stderr)
            self.assertEqual(3, self.sleeps())
            self.assertEqual(f"{worker}--new", self.outputs()["worker_revision"])
            self.assertNotIn("::error::", result.stdout)

    def test_a_ready_revision_on_another_image_until_the_budget_ends_fails_naming_both_images(self):
        api, worker = guards.API_APP, guards.WORKER_APP
        old = self.old_image(worker)
        for images, shown in (([old], old), ([], ""), ([self.images[worker], old], f"{self.images[worker]} {old}")):
            with self.subTest(images=images):
                result = self.run_check(revisions={f"{worker}/{worker}--new": [self.healthy(worker, images=images)]})
                self.assert_failed(result, worker, f"revision {worker}--new is ready but runs '{shown}', not this run's image "
                                                   f"({self.images[worker]}) after about 10 minutes; the previous revision, "
                                                   "if any, is still serving.")
                self.assertEqual(40, self.sleeps())
                self.assertNotIn("worker_revision", self.outputs())
        with self.subTest(case="the previous revision is reported as latest and ready until the end"):
            result = self.run_check(apps={api: [[f"{api}--old", f"{api}--old"]]},
                                    revisions={f"{api}/{api}--old": [self.healthy(api, images=[self.old_image(api)])]})
            self.assert_failed(result, api, f"revision {api}--old is ready but runs '{self.old_image(api)}', not this run's image "
                                            f"({self.images[api]}) after about 10 minutes")
            self.assertEqual(40, self.sleeps())
            self.assertEqual({}, self.outputs())

    def test_an_unhealthy_or_inactive_revision_fails(self):
        web = guards.WEB_APP
        for changes, status in (({"health": "Unhealthy"}, "active=true, healthState=Unhealthy"),
                                ({"health": "None"}, "active=true, healthState=None"),
                                ({"health": None}, "active=true, healthState=none"),
                                ({"active": False}, "active=false, healthState=Healthy")):
            with self.subTest(changes=changes):
                result = self.run_check(revisions={f"{web}/{web}--new": [self.healthy(web, **changes)]})
                self.assert_failed(result, web, f"revision {web}--new is ready but {status} after about 10 minutes")
                self.assertEqual(40, self.sleeps())
                self.assertNotIn("web_revision", self.outputs())

    def test_a_revision_the_start_step_reactivated_passes_once_its_replicas_are_healthy(self):
        # Real restore (README §6.4): the start step activated the stopped revision before stage 4. When stage 4 creates
        # no new revision, the reactivated one is still the latest ready revision, and becomes Healthy once its
        # replicas pass their probes.
        api = guards.API_APP
        result = self.run_check(apps={api: [[f"{api}--old", f"{api}--old"]]},
                                revisions={f"{api}/{api}--old": [self.healthy(api, health="None"), self.healthy(api, health="Unhealthy"),
                                                                 self.healthy(api)]})
        self.assertEqual(0, result.returncode, result.stdout + result.stderr)
        self.assertEqual(2, self.sleeps())
        self.assertEqual(f"{api}--old", self.outputs()["api_revision"])

    def test_after_a_real_restore_the_new_revision_replaces_the_reactivated_one(self):
        # Real restore (README §6.4): stage 4 puts the apps back in single revision mode with this run's images; the
        # reactivated revision serves until the new one is ready, and only the new one is accepted.
        result = self.run_check(apps={app: [[f"{app}--new", f"{app}--old"]] * 2 + [[f"{app}--new", f"{app}--new"]]
                                      for app in (guards.API_APP, guards.WORKER_APP)})
        self.assertEqual(0, result.returncode, result.stdout + result.stderr)
        self.assertEqual(4, self.sleeps())
        self.assertEqual({f"{app}--new" for app in self.APPS}, set(self.outputs().values()))
        self.assertFalse(any(line.endswith("--old") and "revision show" in line for line in self.calls()))

    def test_an_image_that_is_not_digest_pinned_fails_before_reading_azure(self):
        for image in (f"{self.REGISTRY}/paquetenvia-api:{'a' * 40}", "", f"{self.REGISTRY}/paquetenvia-api@sha256:{'A' * 64}"):
            with self.subTest(image=image):
                result = self.run_check(API_IMAGE=image)
                self.assertNotEqual(0, result.returncode)
                self.assertIn(f"::error::{guards.API_APP}: this run has no digest-pinned image for it", result.stdout)
                self.assertEqual([], self.calls())


# A stand-in for the Azure CLI and the Docker CLI as the images step calls them, sharing one registry:
# $AZ_STATE/registry.json maps each repository to its tags, {tag: {"digest", "writeEnabled"}}. A tag listed in "hidden"
# exists but is not listed yet (it appeared after the step looked). FAKE_AZ_FAIL makes one `az acr repository`
# subcommand fail; FAKE_LOCK_IGNORED makes the lock a no-op; FAKE_BUILD_FAIL names a Dockerfile whose build fails;
# FAKE_PUSH_DRIFT makes the registry keep another digest than the pushed one. Every call is logged to $AZ_STATE/calls
# as a JSON array whose first item is "az" or "docker". Only the step's exact calls are answered.
FAKE_REGISTRY_CLI = r'''#!/usr/bin/env python3
import hashlib, json, os, sys
state = os.environ["AZ_STATE"]
tool = os.path.basename(sys.argv[0])
args = sys.argv[1:]
with open(os.path.join(state, "calls"), "a", encoding="utf-8") as log:
    log.write(json.dumps([tool] + args) + "\n")
path = os.path.join(state, "registry.json")
data = json.load(open(path, encoding="utf-8"))
repositories = data.setdefault("repositories", {})
hidden = set(data.get("hidden", []))
def save():
    json.dump(data, open(path, "w", encoding="utf-8"))
def fail(message, code=1):
    print(message, file=sys.stderr); sys.exit(code)
def visible(repository):
    return [tag for tag in repositories.get(repository, {}) if f"{repository}:{tag}" not in hidden]
REGISTRY, SERVER = os.environ["REGISTRY"], os.environ["REGISTRY_SERVER"]
QUIET = ["--only-show-errors", "--output", "json"]
if tool == "az":
    if args == ["acr", "login", "--name", REGISTRY, "--only-show-errors"]:
        sys.exit(0)
    if args[:2] != ["acr", "repository"]:
        fail("unexpected az call: " + " ".join(args), 9)
    command = args[2]
    if os.environ.get("FAKE_AZ_FAIL") == command:
        fail(f"ERROR: az acr repository {command} failed (stand-in)")
    if args[2:] == ["list", "--name", REGISTRY] + QUIET:
        print(json.dumps([name for name in repositories if visible(name)]))
    elif args[2:5] == ["show-tags", "--name", REGISTRY] and args[5] == "--repository" and args[7:] == QUIET:
        if not visible(args[6]):
            fail("ERROR: Error: repository name not known to registry.")
        print(json.dumps(visible(args[6])))
    elif args[2:5] in (["show", "--name", REGISTRY], ["update", "--name", REGISTRY]) and args[5] == "--image":
        repository, tag = args[6].split(":", 1)
        item = repositories.get(repository, {}).get(tag)
        if item is None:
            fail("ERROR: Error: the specified tag does not exist.")
        if command == "show" and args[7:] == QUIET:
            print(json.dumps({"name": tag, "digest": item["digest"], "signed": False,
                              "changeableAttributes": {"deleteEnabled": True, "listEnabled": True, "readEnabled": True,
                                                       "writeEnabled": item["writeEnabled"]}}, indent=2))
        elif command == "update" and args[7:] == ["--write-enabled", "false", "--only-show-errors", "--output", "none"]:
            if not os.environ.get("FAKE_LOCK_IGNORED"):
                item["writeEnabled"] = False
                save()
        else:
            fail("unexpected az call: " + " ".join(args), 9)
    else:
        fail("unexpected az call: " + " ".join(args), 9)
elif tool == "docker":
    local = data.setdefault("local", {})
    if args[0] == "build":
        dockerfile = args[args.index("--file") + 1]
        if dockerfile in os.environ.get("FAKE_BUILD_FAIL", "").split(","):
            fail(f"build of {dockerfile} failed (stand-in)")
        ref = args[args.index("--tag") + 1]
        local[ref] = "sha256:" + hashlib.sha256(json.dumps(args).encode()).hexdigest()
        save()
    elif args[0] == "push" and len(args) == 2:
        ref = args[1]
        repository, tag = ref[len(SERVER) + 1:].split(":", 1)
        existing = repositories.get(repository, {}).get(tag)
        if existing is not None and not existing["writeEnabled"]:
            fail("denied: The operation is disallowed on this registry, repository or image.")
        digest = local[ref]
        if os.environ.get("FAKE_PUSH_DRIFT"):
            digest = "sha256:" + "e" * 64
        repositories.setdefault(repository, {})[tag] = {"digest": digest, "writeEnabled": True}
        data.setdefault("pushed", []).append(ref)
        save()
    elif args[:3] == ["inspect", "--format", "{{index .RepoDigests 0}}"] and len(args) == 4:
        ref = args[3]
        if ref not in data.get("pushed", []):
            fail(f"Error: {ref} has no repository digest")
        print(ref.rsplit(":", 1)[0] + "@" + local[ref])
    else:
        fail("unexpected docker call: " + " ".join(args), 9)
'''


class WorkflowImageStepTests(WorkflowStepHarness):
    """ENV-001-ROLLBACK-REUSES-IMAGE-2026-10-21 (owner K2): when paquetenvia-<name>:<tested sha> is already in the pilot
    registry the images step reuses its digest, with no build and no push, so a rollback deploys the same bytes; a
    missing tag is built from the floating bases and pushed. Every tag is then locked (write-enabled false), so the
    registry refuses a later push over it, and read back. A registry read or a build that fails stops the step."""

    REGISTRY = "pvpilotabcdefghijklm"
    SERVER = "pvpilotabcdefghijklm.azurecr.io"
    SHA = "0123456789abcdef0123456789abcdef01234567"
    OTHER_SHA = "fedcba9876543210fedcba9876543210fedcba98"
    IMAGES = (("api", "api", "deploy/azure/Dockerfile.api"), ("worker", "worker", "deploy/azure/Dockerfile.worker"),
              ("dbops", "db-ops", "deploy/azure/pilot/Dockerfile.db-ops"), ("web", "web", "deploy/azure/pilot/Dockerfile.web"))

    def setUp(self):
        super().setUp()
        self.step = workflow_step(lambda s: s.get("id") == "images")
        for tool in ("az", "docker"):
            (self.root / "bin" / tool).write_text(FAKE_REGISTRY_CLI, encoding="utf-8")
            (self.root / "bin" / tool).chmod(0o755)
        git = self.root / "bin" / "git"
        git.write_text('#!/bin/sh\n[ "$*" = "rev-parse HEAD" ] && echo "${FAKE_HEAD}" && exit 0\nexit 9\n', encoding="utf-8")
        git.chmod(0o755)

    @staticmethod
    def digest(digit: str) -> str:
        return "sha256:" + digit * 64

    def run_images(self, repositories: dict | None = None, hidden: tuple[str, ...] = (), **env: str) -> subprocess.CompletedProcess:
        state = {"repositories": repositories or {}, "hidden": list(hidden)}
        (self.state / "registry.json").write_text(json.dumps(state), encoding="utf-8")
        (self.state / "calls").write_text("", encoding="utf-8")
        environment = {"REGISTRY": self.REGISTRY, "REGISTRY_SERVER": self.SERVER, "TESTED_GIT_SHA": self.SHA,
                       "FAKE_HEAD": self.SHA, "PILOT_TRACKING_SUPPORT_URL": ""}
        environment.update(env)
        return self.run_step(self.step, **environment)

    def registry(self) -> dict:
        return json.loads((self.state / "registry.json").read_text(encoding="utf-8"))

    def calls(self, tool: str) -> list[list[str]]:
        lines = (self.state / "calls").read_text(encoding="utf-8").splitlines()
        return [call[1:] for call in map(json.loads, lines) if call[0] == tool]

    def tag(self, digest: str, locked: bool = False) -> dict:
        return {"digest": digest, "writeEnabled": not locked}

    def every_image(self, locked: bool = False) -> dict:
        return {f"paquetenvia-{name}": {self.SHA: self.tag(self.digest(digit), locked)}
                for (_, name, _), digit in zip(self.IMAGES, "abcd")}

    def test_images_already_in_the_registry_are_reused_without_a_build_or_a_push(self):
        for locked in (True, False):  # False: a tag pushed before this decision is locked on reuse
            with self.subTest(locked=locked):
                result = self.run_images(self.every_image(locked))
                self.assertEqual(0, result.returncode, result.stdout + result.stderr)
                self.assertEqual([], self.calls("docker"))
                outputs = self.outputs()
                for (key, name, _), digit in zip(self.IMAGES, "abcd"):
                    reference = f"{self.SERVER}/paquetenvia-{name}@{self.digest(digit)}"
                    self.assertEqual(reference, outputs[key])
                    self.assertEqual("reused", outputs[f"{key}_source"])
                    self.assertIn(f"reusing {reference}\n", result.stderr)
                    self.assertNotIn(f"building {self.SERVER}/paquetenvia-{name}", result.stderr)
                    self.assertEqual(self.tag(self.digest(digit), locked=True),
                                     self.registry()["repositories"][f"paquetenvia-{name}"][self.SHA])
                self.assertNotIn("::error::", result.stdout + result.stderr)

    def test_a_missing_image_is_built_on_floating_bases_pushed_and_locked(self):
        result = self.run_images(PILOT_TRACKING_SUPPORT_URL="https://soporte.example.invalid/ayuda")
        self.assertEqual(0, result.returncode, result.stdout + result.stderr)
        builds = [call for call in self.calls("docker") if call[0] == "build"]
        self.assertEqual([dockerfile for _, _, dockerfile in self.IMAGES], [call[call.index("--file") + 1] for call in builds])
        for call in builds:
            self.assertEqual(["build", "--pull"], call[:2])
            self.assertIn(f"SOURCE_SHA={self.SHA}", call)
        self.assertIn("NEXT_PUBLIC_TRACKING_SUPPORT_URL=https://soporte.example.invalid/ayuda", builds[-1])
        registry, outputs = self.registry(), self.outputs()
        self.assertEqual([f"{self.SERVER}/paquetenvia-{name}:{self.SHA}" for _, name, _ in self.IMAGES], registry["pushed"])
        for key, name, _ in self.IMAGES:
            ref = f"{self.SERVER}/paquetenvia-{name}:{self.SHA}"
            pushed = registry["local"][ref]
            self.assertEqual(f"{self.SERVER}/paquetenvia-{name}@{pushed}", outputs[key])
            self.assertEqual("built", outputs[f"{key}_source"])
            self.assertIn(f"building {ref}\n", result.stderr)
            self.assertEqual(self.tag(pushed, locked=True), registry["repositories"][f"paquetenvia-{name}"][self.SHA])
        # An empty registry has nothing to list tags of; each tag is locked and read back right after its own push.
        sequence = [(call[0], call[3] if call[0] == "az" and call[2] == "repository" else call[2] if call[0] == "az" else call[1])
                    for call in map(json.loads, (self.state / "calls").read_text(encoding="utf-8").splitlines())]
        self.assertEqual([("az", "login"), ("az", "list")]
                         + [("docker", "build"), ("docker", "push"), ("docker", "inspect"), ("az", "update"), ("az", "show")] * 4,
                         sequence)

    def test_only_the_images_of_this_commit_that_are_missing_are_built(self):
        repositories = {"paquetenvia-api": {self.OTHER_SHA: self.tag(self.digest("1"), locked=True)},
                        "paquetenvia-worker": {self.OTHER_SHA: self.tag(self.digest("2"), locked=True),
                                               self.SHA: self.tag(self.digest("b"), locked=True)},
                        "paquetenvia-web": {self.SHA: self.tag(self.digest("d"), locked=True)}}
        result = self.run_images(repositories)
        self.assertEqual(0, result.returncode, result.stdout + result.stderr)
        outputs = self.outputs()
        self.assertEqual({"api": "built", "worker": "reused", "dbops": "built", "web": "reused"},
                         {key: outputs[f"{key}_source"] for key, _, _ in self.IMAGES})
        self.assertEqual([f"{self.SERVER}/paquetenvia-api:{self.SHA}", f"{self.SERVER}/paquetenvia-db-ops:{self.SHA}"],
                         self.registry()["pushed"])
        self.assertEqual(f"{self.SERVER}/paquetenvia-worker@{self.digest('b')}", outputs["worker"])
        # The other commit's image is untouched.
        self.assertEqual(self.tag(self.digest("1"), locked=True), self.registry()["repositories"]["paquetenvia-api"][self.OTHER_SHA])

    def test_a_push_over_an_existing_tag_is_refused(self):
        # The tag appeared after the step looked (it is not listed yet), so the step builds; the registry refuses the
        # push over the locked tag, and the step stops without reporting the image.
        api = {self.SHA: self.tag(self.digest("a"), locked=True)}
        for hidden, repositories in ((f"paquetenvia-api:{self.SHA}", {"paquetenvia-api": api}),
                                     (f"paquetenvia-api:{self.SHA}", {"paquetenvia-api": {**api, self.OTHER_SHA: self.tag(self.digest("1"))}})):
            with self.subTest(repositories=sorted(repositories["paquetenvia-api"])):
                result = self.run_images(repositories, hidden=(hidden,))
                self.assertNotEqual(0, result.returncode)
                self.assertIn("denied: The operation is disallowed on this registry, repository or image.", result.stderr)
                self.assertEqual(self.tag(self.digest("a"), locked=True), self.registry()["repositories"]["paquetenvia-api"][self.SHA])
                self.assertEqual({}, self.outputs())
                self.assertEqual([], [call for call in self.calls("az") if call[2:3] == ["update"]])

    def test_a_tag_that_does_not_read_back_locked_on_the_digest_stops_the_step(self):
        for env in ({"FAKE_LOCK_IGNORED": "1"}, {"FAKE_PUSH_DRIFT": "1"}):
            with self.subTest(env=env):
                result = self.run_images(**env)
                self.assertNotEqual(0, result.returncode)
                pushed = self.registry()["local"][f"{self.SERVER}/paquetenvia-api:{self.SHA}"]
                self.assertIn(f"::error::tag paquetenvia-api:{self.SHA} is not locked on {pushed} (README §6.7)", result.stderr)
                self.assertEqual({}, self.outputs())
        # A reused tag that does not lock stops the step too.
        result = self.run_images(self.every_image(), FAKE_LOCK_IGNORED="1")
        self.assertNotEqual(0, result.returncode)
        self.assertIn(f"::error::tag paquetenvia-api:{self.SHA} is not locked on {self.digest('a')}", result.stderr)
        self.assertEqual([], self.calls("docker"))

    def test_a_registry_read_that_fails_stops_the_step_before_any_build(self):
        for command in ("list", "show-tags", "show"):
            with self.subTest(command=command):
                result = self.run_images(self.every_image(locked=True), FAKE_AZ_FAIL=command)
                self.assertNotEqual(0, result.returncode)
                self.assertIn(f"az acr repository {command} failed (stand-in)", result.stderr)
                self.assertEqual([], self.calls("docker"))
                self.assertEqual({}, self.outputs())

    def test_a_failed_build_stops_the_step(self):
        # build() runs inside a command substitution; inherit_errexit makes its failure fail the step.
        result = self.run_images(self.every_image(locked=True) | {"paquetenvia-web": {}},
                                 FAKE_BUILD_FAIL="deploy/azure/pilot/Dockerfile.web")
        self.assertNotEqual(0, result.returncode)
        self.assertIn("build of deploy/azure/pilot/Dockerfile.web failed (stand-in)", result.stderr)
        self.assertNotIn("push", [call[0] for call in self.calls("docker")])
        outputs = self.outputs()
        self.assertNotIn("web", outputs)
        self.assertNotIn("web_source", outputs)
        self.assertEqual("reused", outputs["dbops_source"])

    def test_a_checkout_other_than_tested_git_sha_stops_before_the_registry(self):
        result = self.run_images(FAKE_HEAD=self.OTHER_SHA)
        self.assertNotEqual(0, result.returncode)
        self.assertIn("::error::checkout is not tested_git_sha", result.stdout)
        self.assertEqual([], self.calls("az") + self.calls("docker"))


# A stand-in for the Azure CLI as restore-drill.sh calls it. Servers are files in $AZ_STATE/servers; every call is
# logged to $AZ_STATE/calls (command and names only); FAKE_VERIFY_STATUS is the verify job's final status.
FAKE_DRILL_AZ = r'''#!/usr/bin/env python3
import json, os, sys
state = os.environ["AZ_STATE"]
args = sys.argv[1:]
def opt(*names):
    for name in names:
        if name in args:
            return args[args.index(name) + 1]
    return None
servers = os.path.join(state, "servers")
name = opt("--name", "-n")
with open(os.path.join(state, "calls"), "a", encoding="utf-8") as log:
    log.write(" ".join(args[:3]) + " " + (name or "") + (" source=" + opt("--source-server") if "--source-server" in args else "") + "\n")
if args[:3] == ["deployment", "group", "show"]:
    if name == "env001-security":
        print(json.dumps({"vaultName": {"value": "kv-pvp-abcdefghijklm"}}))
    else:
        print(json.dumps({"postgresName": {"value": os.environ["FAKE_SOURCE"]}, "postgresAdministratorLogin": {"value": "pvpilotadmin"}}))
elif args[:2] == ["identity", "show"]:
    print("00000000-0000-0000-0000-00000000000a")
elif args[:3] == ["postgres", "flexible-server", "show"]:
    if not os.path.exists(os.path.join(servers, name)):
        print("ERROR: ResourceNotFound", file=sys.stderr); sys.exit(3)
    if "--query" in args:
        print(name + ".postgres.database.azure.com")
elif args[:3] == ["postgres", "flexible-server", "restore"]:
    if not os.path.exists(os.path.join(servers, opt("--source-server"))):
        sys.exit(3)
    open(os.path.join(servers, name), "w").close()
elif args[:3] == ["postgres", "flexible-server", "delete"]:
    os.remove(os.path.join(servers, name))
elif args[:2] == ["keyvault", "show"]:
    print("/subscriptions/s/resourceGroups/rg-pv-pilot/providers/Microsoft.KeyVault/vaults/kv-pvp-abcdefghijklm")
elif args[:3] == ["keyvault", "secret", "show"]:
    print(os.environ["FAKE_ADMIN_PASSWORD"])
elif args[:3] in (["keyvault", "secret", "set"], ["keyvault", "secret", "set-attributes"]) or args[:2] == ["role", "assignment"] \
        or args[:3] == ["containerapp", "job", "update"]:
    pass
elif args[:3] == ["containerapp", "job", "start"]:
    print("job-pv-pilot-verify-exec1")
elif args[:4] == ["containerapp", "job", "execution", "show"]:
    print(os.environ["FAKE_VERIFY_STATUS"])
else:
    print("unexpected az call: " + " ".join(args[:4]), file=sys.stderr); sys.exit(9)
'''


class RestoreDrillScriptTests(unittest.TestCase):
    """restore-drill.sh restores the server in service (the postgresName output, which follows
    PILOT_POSTGRES_SERVER_NAME) into a new server named after the base name: a drill `<base>-drill-<UTC minute>`, deleted
    unless --keep; a real restore (--real) `<base>-r<UTC minute>`, kept only when `assert` succeeds. The source server
    is never deleted, an existing target is never touched, and the administrator password is never printed."""

    BASE = "pg-pv-pilot-abcdefghijklm"
    PASSWORD = "Pv1-" + "s" * 54

    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory(prefix="env001-drill-")
        self.root = Path(self.tmp.name)
        for directory in ("bin", "pilot", "state/servers"):
            (self.root / directory).mkdir(parents=True)
        for name, text in (("az", FAKE_DRILL_AZ), ("sleep", "#!/bin/sh\nexit 0\n")):
            (self.root / "bin" / name).write_text(text, encoding="utf-8")
            (self.root / "bin" / name).chmod(0o755)
        shutil.copy(REPO_ROOT / guards.RESTORE_DRILL_SCRIPT, self.root / "pilot" / "restore-drill.sh")
        (self.root / "pilot" / "kv-firewall.sh").write_text("#!/usr/bin/env bash\nexit 0\n", encoding="utf-8")
        self.state = self.root / "state"

    def tearDown(self):
        self.tmp.cleanup()

    def drill(self, source: str, *flags: str, status: str = "Succeeded", servers: tuple[str, ...] = ()) -> tuple[subprocess.CompletedProcess, list[str]]:
        for server in (source,) + servers:
            (self.state / "servers" / server).touch()
        (self.state / "calls").write_text("", encoding="utf-8")
        env = dict(os.environ, PATH=f"{self.root / 'bin'}{os.pathsep}{os.environ.get('PATH', '')}", AZ_STATE=str(self.state),
                   FAKE_SOURCE=source, FAKE_VERIFY_STATUS=status, FAKE_ADMIN_PASSWORD=self.PASSWORD)
        result = subprocess.run(["bash", str(self.root / "pilot" / "restore-drill.sh"), "--resource-group", "rg-pv-pilot",
                                 "--restore-time", "2026-10-20T13:10:00Z", *flags], capture_output=True, text=True, env=env)
        self.assertNotIn(self.PASSWORD, result.stdout + result.stderr)
        calls = [line.strip() for line in (self.state / "calls").read_text(encoding="utf-8").splitlines()]
        return result, calls

    def servers(self) -> set[str]:
        return {p.name for p in (self.state / "servers").iterdir()}

    def target(self, result) -> str:
        return re.search(r"target=(\S+)", result.stdout).group(1)

    def test_a_drill_restores_the_server_in_service_and_deletes_its_copy(self):
        for source in (self.BASE, f"{self.BASE}-r202610011200", f"{self.BASE}-drill-202610011200"):
            with self.subTest(source=source):
                result, calls = self.drill(source)
                self.assertEqual(0, result.returncode, result.stderr)
                target = self.target(result)
                self.assertRegex(target, rf"^{self.BASE}-drill-\d{{12}}$")
                self.assertIn(f"postgres flexible-server restore {target} source={source}", calls)
                self.assertIn(f"postgres flexible-server delete {target}", calls)
                self.assertEqual({source}, self.servers())
                (self.state / "servers" / source).unlink()

    def test_keep_leaves_the_drill_server(self):
        result, calls = self.drill(self.BASE, "--keep", status="Failed")
        self.assertNotEqual(0, result.returncode)
        self.assertEqual({self.BASE, self.target(result)}, self.servers())

    def test_a_real_restore_is_kept_only_when_assert_succeeds(self):
        result, calls = self.drill(f"{self.BASE}-r202610011200", "--real")
        self.assertEqual(0, result.returncode, result.stderr)
        target = self.target(result)
        self.assertRegex(target, rf"^{self.BASE}-r\d{{12}}$")
        self.assertEqual([], guards.validate_postgres_server_name(target))
        self.assertEqual({f"{self.BASE}-r202610011200", target}, self.servers())
        self.assertIn(f"restored_server_kept={target} source_unchanged={self.BASE}-r202610011200", result.stdout)
        self.assertIn(f"next_step=set the azure-pilot variable PILOT_POSTGRES_SERVER_NAME to {target}", result.stdout)
        self.assertFalse(any(" delete " in f" {call} " for call in calls if call.startswith("postgres")))

    def test_a_failed_real_restore_deletes_only_its_new_server(self):
        result, calls = self.drill(self.BASE, "--real", status="Failed")
        self.assertNotEqual(0, result.returncode)
        self.assertIn(f"postgres flexible-server delete {self.target(result)}", calls)
        self.assertEqual({self.BASE}, self.servers())
        self.assertNotIn("restored_server_kept", result.stdout)

    def test_an_existing_target_or_a_foreign_source_stops_before_any_restore(self):
        existing = subprocess.run(["date", "-u", "+%Y%m%d%H%M"], capture_output=True, text=True).stdout.strip()
        # Both the current and the next minute exist, so the check holds even if the minute turns during the run.
        later = subprocess.run(["date", "-u", "-d", "+1 minute", "+%Y%m%d%H%M"], capture_output=True, text=True).stdout.strip()
        taken = (f"{self.BASE}-r{existing}", f"{self.BASE}-r{later}")
        result, calls = self.drill(self.BASE, "--real", servers=taken)
        self.assertEqual(1, result.returncode)
        self.assertIn("already exists", result.stderr)
        self.assertFalse(any(call.startswith(("postgres flexible-server restore", "postgres flexible-server delete")) for call in calls))
        self.assertEqual({self.BASE, *taken}, self.servers())
        for source in ("null", "pg-pv-other-abcdefghijklm", f"{self.BASE}-", "PG-PV-PILOT-ABCDEFGHIJKLM"):
            with self.subTest(source=source):
                result, calls = self.drill(source)
                self.assertEqual(1, result.returncode)
                self.assertIn("no pilot PostgreSQL server name", result.stderr)
                self.assertFalse(any(call.startswith("postgres") for call in calls))
                (self.state / "servers" / source).unlink()

    def test_keep_and_real_are_exclusive(self):
        result, calls = self.drill(self.BASE, "--keep", "--real")
        self.assertEqual(2, result.returncode)
        self.assertEqual([], calls)


class ServerNameSettingTests(unittest.TestCase):
    """OPS-002-RESTORE-FOLLOWS-SERVER-2026-10-20: no file names the pilot PostgreSQL server except through the setting. In
    deploy/, .github/ and tools/ the name prefix appears only in the default definition (the postgresServerName default
    of platform.bicep and observability.bicep), the guards' one prefix constant and the drill script's format check; in
    the operations and development docs every line that names it also names PILOT_POSTGRES_SERVER_NAME."""

    DEFAULT_DEFINITION = "param postgresServerName string = 'pg-pv-pilot-${uniqueString(subscription().id, resourceGroup().id)}'"

    def allowed(self, path: str, line: str) -> bool:
        if path in ("deploy/azure/pilot/platform.bicep", "deploy/azure/pilot/observability.bicep"):
            return line == self.DEFAULT_DEFINITION
        if path == "deploy/azure/pilot/restore-drill.sh":
            return guards.POSTGRES_SERVER_NAME_PATTERN in line
        if path == "tools/azr-001/env001_pilot_guards.py":
            return line == f'POSTGRES_SERVER_PREFIX = "{guards.POSTGRES_SERVER_PREFIX}"'
        if path.startswith("docs/"):
            return guards.POSTGRES_SERVER_VARIABLE in line
        return False

    def test_the_server_is_named_only_through_the_setting(self):
        self.assertEqual("pg-pv-pilot-", guards.POSTGRES_SERVER_PREFIX)
        found, offending = [], []
        roots = ("deploy", ".github", "tools", "docs/operations", "docs/development")
        for root in roots:
            for path in sorted((REPO_ROOT / root).rglob("*")):
                relative = path.relative_to(REPO_ROOT).as_posix()
                if not path.is_file() or "__pycache__" in relative or relative == "tools/azr-001/test_env001_pilot_guards.py" \
                        or "/bin/" in relative or "/obj/" in relative:
                    continue
                try:
                    text = path.read_text(encoding="utf-8")
                except UnicodeDecodeError:
                    continue
                for number, line in enumerate(text.splitlines(), 1):
                    if guards.POSTGRES_SERVER_PREFIX in line:
                        found.append(relative)
                        if not self.allowed(relative, line.strip()):
                            offending.append(f"{relative}:{number}: {line.strip()}")
        self.assertEqual([], offending)
        for required in ("deploy/azure/pilot/platform.bicep", "deploy/azure/pilot/observability.bicep",
                         "deploy/azure/pilot/restore-drill.sh", "tools/azr-001/env001_pilot_guards.py"):
            self.assertEqual(1, found.count(required), required)


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

    # ------------------------------------------------------------------ OPS-002-RESTORE-FOLLOWS-SERVER-2026-10-20
    def test_the_default_server_name_is_exactly_the_first_server(self):
        """An existing deployment changes nothing: the parameter default is the name the template computed before,
        format('pg-pv-pilot-{0}', suffix) with suffix = uniqueString(subscription().id, resourceGroup().id)."""
        before = "[format('pg-pv-pilot-{0}', variables('suffix'))]"
        for name in ("platform", "observability"):
            template = self.compiled[name]
            suffix = template["variables"]["suffix"]
            self.assertEqual("[uniqueString(subscription().id, resourceGroup().id)]", suffix)
            default = template["parameters"]["postgresServerName"]["defaultValue"]
            self.assertEqual(before.replace("variables('suffix')", suffix[1:-1]), default)
            self.assertEqual(guards.POSTGRES_SERVER_DEFAULT, default)
        server = self.resource(self.compiled, "platform", "Microsoft.DBforPostgreSQL/flexibleServers")
        self.assertEqual("[parameters('postgresServerName')]", server["name"])
        self.assertEqual("[parameters('postgresServerName')]", self.compiled["platform"]["outputs"]["postgresName"]["value"])
        alerts = [r for r in self.compiled["observability"]["resources"] if r.get("type") == "Microsoft.Insights/metricAlerts"]
        self.assertEqual(6, len(alerts))
        self.assertTrue(all(a["properties"]["scopes"] == [guards.POSTGRES_SERVER_ID] for a in alerts))

    def test_p25_server_setting_fails_closed(self):
        def server(t):
            return self.resource(t, "platform", "Microsoft.DBforPostgreSQL/flexibleServers")

        template_mutations = {
            "other default": (lambda t: t["platform"]["parameters"]["postgresServerName"].update(
                defaultValue="[format('pg-pv-pilot-{0}-r1', uniqueString(subscription().id, resourceGroup().id))]"), "default is exactly"),
            "observability default differs": (lambda t: t["observability"]["parameters"]["postgresServerName"].update(
                defaultValue="[format('pg-pv-pilot-{0}', uniqueString(resourceGroup().id))]"), "observability.bicep must take"),
            "no length limit": (lambda t: t["platform"]["parameters"]["postgresServerName"].pop("maxLength"), "3-63 character"),
            "no parameter": (lambda t: t["observability"]["parameters"].pop("postgresServerName"), "observability.bicep must take"),
            "literal name": (lambda t: server(t).update(name="[format('pg-pv-pilot-{0}', variables('suffix'))]"),
                             "must declare the only PostgreSQL server"),
            "second server": (lambda t: t["observability"]["resources"].append(copy.deepcopy(server(t))), "must declare the only"),
            "name spelled in jobs": (lambda t: t["jobs"]["variables"].update(server="pg-pv-pilot-x"), "jobs.bicep spells pg-pv-pilot-"),
            "name output": (lambda t: t["platform"]["outputs"]["postgresName"].update(value="pg"), "must output postgresName and postgresHost"),
            "host output": (lambda t: t["platform"]["outputs"]["postgresHost"].update(value="[variables('suffix')]"), "must output postgresName"),
        }
        for name, (mutate, fragment) in template_mutations.items():
            with self.subTest(name=name):
                self.assert_fails(25, self.context(mutate), fragment)

        text = (REPO_ROOT / guards.PILOT_WORKFLOW).read_text(encoding="utf-8")
        validate = 'postgres-server-name --value "${PILOT_POSTGRES_SERVER_NAME:-}"'
        workflow_mutations = {
            "variable not read": (("PILOT_POSTGRES_SERVER_NAME: ${{ vars.PILOT_POSTGRES_SERVER_NAME }}", "PILOT_POSTGRES_SERVER_NAME: ''"),
                                  "must read PILOT_POSTGRES_SERVER_NAME once"),
            "read twice": (("      CUSTOM_DOMAIN_PHASE: ${{ inputs.custom_domain_phase }}\n      ROTATE",
                            "      CUSTOM_DOMAIN_PHASE: ${{ inputs.custom_domain_phase }}\n      X: ${{ vars.PILOT_POSTGRES_SERVER_NAME }}\n      ROTATE"),
                           "must read PILOT_POSTGRES_SERVER_NAME once"),
            "not validated": ((validate, "true"), "must validate PILOT_POSTGRES_SERVER_NAME"),
            "no adoption check": (("postgres-adoption-check --name", "true --name"), "postgres-adoption-check before stage 2"),
            "other API version": (("--api-version 2024-08-01", "--api-version 2025-08-01"), "the template's API version 2024-08-01"),
            "not passed to platform": (('parameters["postgresServerName"]', 'parameters["other"]'), "must carry postgresServerName"),
            "host not checked": (('"${name}.postgres.database.azure.com"', '"${name}"'), "must export postgresName"),
            "alerts on the default": (('"postgresServerName=${POSTGRES_NAME}"', '"other=${POSTGRES_NAME}"'), "stage 5 must pass"),
            "alerts from another source": (("POSTGRES_NAME: ${{ steps.platform.outputs.postgresName }}\n        run: |\n          set -euo pipefail\n          python3",
                                            "POSTGRES_NAME: ${{ vars.X }}\n        run: |\n          set -euo pipefail\n          python3"), "stage 5 must pass"),
            "host never compared": (('if [[ "${kept_host}" == "${PGHOST_FQDN}" ]]; then continue; fi', "continue"),
                                    "keep a runtime credential only while its host"),
            "no change output": (('echo "runtime_logins_changed=${changed}" >> "${GITHUB_OUTPUT}"', "true"), "report runtime_logins_changed"),
            "stopped apps stay stopped": (("az containerapp revision activate", "true"), "start a stopped API or Worker revision"),
            "restart not tied to the change": (("RUNTIME_LOGINS_CHANGED: ${{ steps.connections.outputs.runtime_logins_changed }}",
                                                "RUNTIME_LOGINS_CHANGED: ${{ inputs.rotate_runtime_logins }}"), "restart a running one"),
        }
        for name, ((old, new), fragment) in workflow_mutations.items():
            with self.subTest(name=name):
                self.assertIn(old, text)
                self.assert_fails(25, self.context(workflow_text=text.replace(old, new, 1)), fragment)
        # Validation must come before the first Azure call.
        block = ("      - name: Validate the PostgreSQL server setting (PILOT_POSTGRES_SERVER_NAME)\n        shell: bash\n"
                 f"        run: python3 ./tools/azr-001/env001_pilot_guards.py {validate}\n\n")
        self.assertIn(block, text)
        late = text.replace(block, "", 1).replace("      - name: Stage 1 - ", block + "      - name: Stage 1 - ", 1)
        self.assert_fails(25, self.context(workflow_text=late), "before azure/login")
        # Starting or restarting the apps must wait for the logins job and come before stage 4.
        restart = text[text.index("      # The apps read their Key Vault secrets once at start."):text.index("      - name: Stage 4 - ")]
        alerts = "      # OPS-002-RESTORE-FOLLOWS-SERVER-2026-10-20: the SCL-007 alerts watch"
        moved = text.replace(restart, "", 1).replace(alerts, restart + alerts, 1)
        self.assertNotEqual(text, moved)
        self.assert_fails(25, self.context(workflow_text=moved), "after the logins job (stage 3) and before stage 4")

        # The drill script must restore the server in service and check its name with the guard's pattern.
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            (root / guards.PILOT_DIR).mkdir(parents=True)
            script = (REPO_ROOT / guards.RESTORE_DRILL_SCRIPT).read_text(encoding="utf-8")
            for old, new in ((guards.POSTGRES_SERVER_NAME_PATTERN, "^pg-.*$"), (".postgresName.value", ".other.value")):
                (root / guards.RESTORE_DRILL_SCRIPT).write_text(script.replace(old, new), encoding="utf-8")
                ctx = self.context()
                ctx.repo_root = root
                self.assertFalse(guards.guard_25_postgres_server_setting(ctx).passed, old)

    def test_p26_revision_check_fails_closed(self):
        text = (REPO_ROOT / guards.PILOT_WORKFLOW).read_text(encoding="utf-8")
        mutations = {
            "no revision step": (("        id: revisions\n", "        id: other\n"), "one deploy step (id revisions)"),
            "another app's image": (("WEB_IMAGE: ${{ steps.images.outputs.web }}\n        run: |\n          set -euo pipefail\n          polls_left",
                                     "WEB_IMAGE: ${{ steps.images.outputs.api }}\n        run: |\n          set -euo pipefail\n          polls_left"),
                                    "must read exactly this run's images"),
            "latest never compared with ready": (('[[ -n "${latest}" && "${latest}" == "${ready}" ]]', '[[ -n "${latest}" ]]'),
                                                 "must poll, assert and hint"),
            "image not compared": (('[[ "${images}" != "${image}" ]]', '[[ -z "${images}" ]]'), "must poll, assert and hint"),
            "health not asserted": (('[[ "${active}" == "true" && "${health}" == "Healthy" ]]', '[[ "${active}" == "true" ]]'),
                                    "must poll, assert and hint"),
            "unbounded poll": (("polls_left=40\n", "polls_left=4000\n"), "must poll, assert and hint"),
            "no system-log hint": (("--type system (README §6.7)", "(README §6.7)"), "must poll, assert and hint"),
            "an app left unchecked": (('          check_revision web_revision ca-pv-pilot-web "${WEB_IMAGE}"\n', ""), "must end with"),
            "allowed to fail": (("        id: revisions\n", "        id: revisions\n        continue-on-error: true\n"),
                                "must always run"),
            "conditional": (("        id: revisions\n", "        id: revisions\n        if: success()\n"), "must always run"),
            "summary only on success": (("      - name: Summary and DNS records for the owner\n        if: always()\n",
                                         "      - name: Summary and DNS records for the owner\n"), "always-run summary"),
            "summary without the db-ops digest": (("| \\`${DBOPS_IMAGE:-none}\\` |", "| |"), "always-run summary"),
            "another image fails at once": ((guards.REVISION_IMAGE_NOT_YET + "\n",
                                             guards.REVISION_IMAGE_NOT_YET + "\n                  return 1\n"),
                                            "polled again, not failed at once"),
            "another image fails the app": ((guards.REVISION_IMAGE_NOT_YET + "\n",
                                             guards.REVISION_IMAGE_NOT_YET + '\n                  fail "${app}" "${status}."\n'),
                                            "polled again, not failed at once"),
            "summary without the serving revision": (("API_REVISION: ${{ steps.revisions.outputs.api_revision }}",
                                                      "API_REVISION: ${{ steps.revisions.outputs.worker_revision }}"),
                                                     "always-run summary"),
        }
        for name, ((old, new), fragment) in mutations.items():
            with self.subTest(name=name):
                self.assertEqual(1, text.count(old), old)
                self.assert_fails(26, self.context(workflow_text=text.replace(old, new, 1)), fragment)
        # The check must come after stage 4 and before the smoke test.
        check = text[text.index("      # ENV-001 F-06: a successful stage 4"):text.index("      - name: Smoke test same-origin routing")]
        summary = "      - name: Summary and DNS records for the owner\n"
        stage_4 = "      - name: Stage 4 - "
        for moved in (text.replace(check, "", 1).replace(summary, check + summary, 1),
                      text.replace(check, "", 1).replace(stage_4, check + stage_4, 1)):
            self.assertNotEqual(text, moved)
            self.assert_fails(26, self.context(workflow_text=moved), "after stage 4 and before the smoke test")

    def test_p27_image_reuse_and_tag_lock_fail_closed(self):
        text = (REPO_ROOT / guards.PILOT_WORKFLOW).read_text(encoding="utf-8")
        reuse = "must reuse an existing tag, else build and push, then lock it"
        push_block = ('              docker push "${ref}" >&2\n'
                      '              digest="$(docker inspect --format \'{{index .RepoDigests 0}}\' "${ref}")"\n'
                      '              digest="${digest##*@}"\n'
                      '            fi\n')
        mutations = {
            "no images step": (("        id: images\n", "        id: other\n"), "one deploy step (id images)"),
            "the reuse branch never taken": (('            if [[ -n "${digest}" ]]; then\n', "            if false; then\n"), reuse),
            "no tag lookup": (('--repository "${repository}" --only-show-errors --output json)"', '--repository "${repository}")"'), reuse),
            "build failures ignored": (("          shopt -s inherit_errexit\n", ""), reuse),
            "push in both branches": ((push_block, push_block.replace('              docker push "${ref}" >&2\n', "")
                                       .replace("            fi\n", '            fi\n            docker push "${ref}" >&2\n')), reuse),
            "tag not locked": (("--write-enabled false \\\n", "\\\n"), reuse),
            "lock not read back": ((".changeableAttributes.writeEnabled", ".changeableAttributes.readEnabled"), reuse),
            "source not reported": (('echo "${name//-/}_source=${source}" >> "${GITHUB_OUTPUT}"', 'true'), reuse),
            "a second push": (('          echo "web=${image}" >> "${GITHUB_OUTPUT}"\n',
                               '          echo "web=${image}" >> "${GITHUB_OUTPUT}"\n          docker push "${REGISTRY_SERVER}/x:y"\n'),
                              "docker push exactly once"),
            "an unlock": (('          echo "web=${image}" >> "${GITHUB_OUTPUT}"\n',
                           '          echo "web=${image}" >> "${GITHUB_OUTPUT}"\n'
                           '          az acr repository update --name "${REGISTRY}" --image "x:y" --write-enabled true\n'),
                          "az acr repository update exactly once"),
            "a deletion": (('          echo "web=${image}" >> "${GITHUB_OUTPUT}"\n',
                            '          echo "web=${image}" >> "${GITHUB_OUTPUT}"\n          az acr repository delete --name "${REGISTRY}" --image "x:y" --yes\n'),
                           "never unlock, delete, untag or import"),
            "web not reported": (('          echo "web=${image}" >> "${GITHUB_OUTPUT}"\n', ""), "right after its build() call"),
            "registry from elsewhere": (("          REGISTRY: ${{ steps.platform.outputs.registryName }}\n", "          REGISTRY: other\n"),
                                        "the images step must read exactly"),
            "allowed to fail": (("        id: images\n", "        id: images\n        continue-on-error: true\n"), "must always run"),
            "summary without the sources": (("${WEB_SOURCE:-no image}", "no image"), "built or reused"),
        }
        for name, ((old, new), fragment) in mutations.items():
            with self.subTest(name=name):
                self.assertEqual(1, text.count(old), old)
                self.assert_fails(27, self.context(workflow_text=text.replace(old, new, 1)), fragment)

    def restored_server(self, **changes):
        """A restored server as `az resource show --api-version 2024-08-01` describes it (ARM GET)."""
        base = "/subscriptions/00000000-0000-0000-0000-000000000001/resourceGroups/rg-pv-pilot/providers/Microsoft.Network"
        server = {
            "id": "/subscriptions/00000000-0000-0000-0000-000000000001/resourceGroups/rg-pv-pilot/providers/"
                  "Microsoft.DBforPostgreSQL/flexibleServers/pg-pv-pilot-abcdefghijklm-r202610201310",
            "name": "pg-pv-pilot-abcdefghijklm-r202610201310",
            "type": "Microsoft.DBforPostgreSQL/flexibleServers",
            "location": "Mexico Central",
            "sku": {"name": "Standard_B1ms", "tier": "Burstable"},
            "properties": {
                "administratorLogin": "pvpilotadmin", "version": "18", "state": "Ready", "replicationRole": "Primary",
                "fullyQualifiedDomainName": "pg-pv-pilot-abcdefghijklm-r202610201310.postgres.database.azure.com",
                "storage": {"storageSizeGB": 32, "autoGrow": "Enabled", "tier": "P4"},
                "backup": {"backupRetentionDays": 14, "geoRedundantBackup": "Disabled"},
                "highAvailability": {"mode": "Disabled"},
                "network": {"publicNetworkAccess": "Disabled",
                            "delegatedSubnetResourceId": f"{base}/virtualNetworks/vnet-pv-pilot/subnets/snet-postgres",
                            "privateDnsZoneArmResourceId": f"{base}/privateDnsZones/pv-pilot.private.postgres.database.azure.com"},
            },
        }
        for path, value in changes.items():
            target = server
            *parents, leaf = path.split(".")
            for key in parents:
                target = target[key]
            target[leaf] = value
        return server

    def adoption(self, server, name="pg-pv-pilot-abcdefghijklm-r202610201310"):
        return guards.postgres_adoption_failures(name, server, self.compiled["platform"],
                                                 subscription_id="00000000-0000-0000-0000-000000000001",
                                                 resource_group="rg-pv-pilot", region="mexicocentral")

    def test_a_restored_server_in_the_template_network_is_adopted(self):
        self.assertEqual([], self.adoption(self.restored_server()))
        # Resource ids compare without case, as ARM does; sku, HA, retention and parameters are the template's to set.
        upper = self.restored_server(**{"properties.network.privateDnsZoneArmResourceId":
                                        self.restored_server()["properties"]["network"]["privateDnsZoneArmResourceId"].upper(),
                                        "sku.name": "Standard_B2s", "properties.highAvailability.mode": "SameZone",
                                        "properties.backup.backupRetentionDays": 7, "properties.storage.storageSizeGB": 32})
        self.assertEqual([], self.adoption(upper))

    def test_a_server_the_template_cannot_update_in_place_is_refused(self):
        cases = {
            "other login": ({"properties.administratorLogin": "someoneelse"}, "another administrator login"),
            "other version": ({"properties.version": "17"}, "runs PostgreSQL 17"),
            "public access": ({"properties.network.publicNetworkAccess": "Enabled"}, "private access"),
            "other subnet": ({"properties.network.delegatedSubnetResourceId": "/subscriptions/00000000-0000-0000-0000-000000000001/"
                              "resourceGroups/rg-pv-pilot/providers/Microsoft.Network/virtualNetworks/vnet-pv-pilot/subnets/snet-other"},
                             "not in the template's delegated subnet"),
            "other DNS zone": ({"properties.network.privateDnsZoneArmResourceId": "/subscriptions/00000000-0000-0000-0000-000000000001/"
                                "resourceGroups/rg-pv-pilot/providers/Microsoft.Network/privateDnsZones/x.private.postgres.database.azure.com"},
                               "not in the template's private DNS zone"),
            "other resource group": ({"properties.network.delegatedSubnetResourceId": "/subscriptions/00000000-0000-0000-0000-000000000001/"
                                      "resourceGroups/rg-other/providers/Microsoft.Network/virtualNetworks/vnet-pv-pilot/subnets/snet-postgres"},
                                     "delegated subnet"),
            "geo backup": ({"properties.backup.geoRedundantBackup": "Enabled"}, "geo-redundant backup Enabled"),
            "grown disk": ({"properties.storage.storageSizeGB": 64}, "a disk never shrinks"),
            "stopped": ({"properties.state": "Stopped"}, "is Stopped, not Ready"),
            "replica": ({"properties.replicationRole": "AsyncReplica"}, "read replica"),
            "other region": ({"location": "East US"}, "not mexicocentral"),
            "other server": ({"name": "pg-pv-pilot-abcdefghijklm"}, "the description is of"),
        }
        for name, (changes, fragment) in cases.items():
            with self.subTest(name=name):
                failures = self.adoption(self.restored_server(**changes))
                self.assertTrue(any(fragment in f for f in failures), failures)
        self.assertEqual(["x: the server description is not an ARM resource"], self.adoption({"name": "x"}, name="x"))

    def test_adoption_check_cli(self):
        with tempfile.TemporaryDirectory() as directory:
            server, platform = Path(directory) / "server.json", Path(directory) / "platform.json"
            platform.write_text(json.dumps(self.compiled["platform"]), encoding="utf-8")

            def run(name, document):
                server.write_text(json.dumps(document), encoding="utf-8")
                return subprocess.run([sys.executable, guards.__file__, "postgres-adoption-check", "--name", name, "--server-json", str(server),
                                       "--platform-arm", str(platform), "--subscription-id", "00000000-0000-0000-0000-000000000001",
                                       "--resource-group", "rg-pv-pilot"], capture_output=True, text=True)

            ok = run("pg-pv-pilot-abcdefghijklm-r202610201310", self.restored_server())
            self.assertEqual(0, ok.returncode, ok.stdout)
            self.assertIn("ENV001_POSTGRES_ADOPTION=PASS pg-pv-pilot-abcdefghijklm-r202610201310", ok.stdout)
            bad = run("pg-pv-pilot-abcdefghijklm-r202610201310", self.restored_server(**{"properties.backup.geoRedundantBackup": "Enabled"}))
            self.assertEqual(1, bad.returncode)
            self.assertIn("ENV001_POSTGRES_ADOPTION=FAIL", bad.stdout)
            malformed = run("PG", self.restored_server())
            self.assertEqual(1, malformed.returncode)
            self.assertIn("PILOT_POSTGRES_SERVER_NAME must be empty", malformed.stdout)
            server.write_text("{", encoding="utf-8")
            unreadable = subprocess.run([sys.executable, guards.__file__, "postgres-adoption-check", "--name", "pg-pv-pilot-abcdefghijklm",
                                         "--server-json", str(server), "--platform-arm", str(platform), "--subscription-id", "s",
                                         "--resource-group", "rg"], capture_output=True, text=True)
            self.assertEqual(1, unreadable.returncode)

    # ------------------------------------------------- NTF-RECIPIENT-NOTICE-PILOT-WIRING-2026-10-31 (P16, P23, P28 to P30)
    GATE_LOG = RecipientNoticeGateResolutionTests.LOG

    def notice_assignment(self, t, fragment: str):
        """The role assignment on recipient-notice-kek whose JSON holds fragment."""
        return next(r for r in t["security"]["resources"] if r.get("type") == "Microsoft.Authorization/roleAssignments"
                    and "'recipient-notice-kek'" in str(r.get("scope")) and fragment in json.dumps(r))

    def with_gate_ids(self, t, gate004="GATE-004-SYNTHETIC-RESOLUTION", gate007="GATE-007-SYNTHETIC-RESOLUTION"):
        self.edit(t, "apps", "createObject('name', 'Notifications__WorkerId', 'value', 'pilot-worker')",
                  "createObject('name', 'Notifications__WorkerId', 'value', 'pilot-worker'), "
                  f"createObject('name', 'Notifications__RecipientNotices__Gate004DecisionId', 'value', '{gate004}'), "
                  f"createObject('name', 'Notifications__RecipientNotices__Gate007DecisionId', 'value', '{gate007}')")

    def test_the_repository_wires_recipient_notices_with_every_switch_off(self):
        ctx = self.context()
        results = self.results(ctx)
        for number in (16, 23, 28, 29, 30):
            self.assertTrue(results[number].passed, (number, results[number].detail))
        self.assertIn("every switch off", results[28].detail)
        api, worker = ctx.workload(guards.API_APP), ctx.workload(guards.WORKER_APP)
        self.assertEqual({k: v for k, v in guards.REQUIRED_SECRET_MAPPINGS[guards.API_APP].items()}, guards.secret_mappings(api))
        self.assertEqual({k: v for k, v in guards.REQUIRED_SECRET_MAPPINGS[guards.WORKER_APP].items()}, guards.secret_mappings(worker))
        for workload in (api, worker):
            self.assertEqual("1", workload.value("ContactLookup__CurrentKeyVersion"))
            self.assertFalse([k for k in workload.env if k.startswith(("Notifications__RecipientNotices__", "Messaging__"))])
            self.assertNotIn(guards.NOTICE_KEY_PROTECTOR, workload.env)
        self.assertEqual("1", worker.value("PublicTracking__CurrentLinkKeyVersion"))
        self.assertEqual("none", self.compiled["events"]["parameters"]["emailEventsPhase"]["defaultValue"])
        key = self.resource(self.compiled, "security", "Microsoft.KeyVault/vaults/keys", "recipient-notice-kek")["properties"]
        self.assertEqual(("RSA", 3072, ["wrapKey", "unwrapKey"]), (key["kty"], key["keySize"], key["keyOps"]))

    def test_p28_any_switch_provider_notice_copy_or_subscription_needs_both_resolution_rows(self):
        fragment = "without the GATE-004 and GATE-007 resolution rows"
        on = {
            "notices": ("Notifications__RecipientNotices__Enabled", "true"),
            "notices any case": ("notifications__recipientnotices__enabled", "True"),
            "whatsapp provider": ("Messaging__WhatsApp__Provider", "MetaCloudApi"),
            "email provider": ("Messaging__Email__Provider", "AzureCommunicationServices"),
            "whatsapp webhook": ("Messaging__WhatsApp__Webhook__Enabled", "true"),
            "email events": ("Messaging__Email__Events__Enabled", "true"),
            "notice copies": ("Locations__NoticeKeyProtector", "AzureKeyVault"),
        }
        for label, (name, value) in on.items():
            def switch(t, name=name, value=value):
                t["apps"]["variables"]["productionEnv"].append({"name": name, "value": value})
            with self.subTest(label=label):
                self.assert_fails(28, self.context(switch), fragment)
                ctx = self.context(lambda t, switch=switch: (switch(t), self.with_gate_ids(t)))
                ctx.decision_log = self.GATE_LOG
                self.assertTrue(self.results(ctx)[28].passed, self.results(ctx)[28].detail)
                scoped = self.context(lambda t, switch=switch: (switch(t), self.with_gate_ids(t, gate007="GATE-007-SYNTHETIC-SCOPING")))
                scoped.decision_log = self.GATE_LOG
                self.assert_fails(28, scoped, "not 'Gate resolution'")

        def subscribe(t):
            t["events"]["parameters"]["emailEventsPhase"]["defaultValue"] = "subscribe"
        self.assert_fails(28, self.context(subscribe), "events.bicep emailEventsPhase=subscribe")
        self.assert_fails(30, self.context(subscribe), "needs the API's Messaging__Email__Events__Enabled=true")

        def explicit_off(t):
            t["apps"]["variables"]["productionEnv"].extend([
                {"name": "Notifications__RecipientNotices__Enabled", "value": "false"},
                {"name": "Messaging__WhatsApp__Provider", "value": "Disabled"},
                {"name": "Locations__NoticeKeyProtector", "value": "Disabled"}])
        self.assertTrue(self.results(self.context(explicit_off))[28].passed)

        def unknown_gate_ids(t):
            self.with_gate_ids(t, gate004="GATE-004-NOT-RECORDED")
        self.assert_fails(28, self.context(unknown_gate_ids), "GATE-004-NOT-RECORDED is not recorded")

        def gate_ids_in_the_api(t):
            t["apps"]["variables"]["productionEnv"].append({"name": "Notifications__RecipientNotices__Gate004DecisionId",
                                                            "value": "GATE-004-SYNTHETIC-RESOLUTION"})
        self.assert_fails(28, self.context(gate_ids_in_the_api), "only the Worker reads the gate decision ids")

    def test_p16_both_hosts_name_the_recipient_notice_key_never_the_pii_key(self):
        for host in ("ca-pv-pilot-api", "ca-pv-pilot-worker"):
            def pii_key(t, host=host):
                raw = json.dumps(self.resource(t, "apps", "Microsoft.App/containerApps", host))
                old = "createObject('name', 'PiiProtection__RecipientNoticeKey__KeyId', 'value', parameters('recipientNoticeKeyUri'))"
                assert raw.count(old) == 1, host
                resources = t["apps"]["resources"]
                index = resources.index(self.resource(t, "apps", "Microsoft.App/containerApps", host))
                resources[index] = json.loads(raw.replace(old, old.replace("recipientNoticeKeyUri", "piiKeyUri")))
            with self.subTest(host=host):
                self.assert_fails(16, self.context(pii_key), f"{host} must set PiiProtection__RecipientNoticeKey__KeyId")

    def test_p23_no_whatsapp_token_in_the_api_and_no_webhook_secret_in_the_worker(self):
        def api_token(t):
            self.edit(t, "apps", "'KeyVaultSecrets__Mappings__8__SecretName', 'value', 'whatsapp-app-secret')",
                      "'KeyVaultSecrets__Mappings__8__SecretName', 'value', 'whatsapp-cloud-api-token')")
        self.assert_fails(23, self.context(api_token), "the WhatsApp token never reaches the API")

        def worker_webhook(t):
            self.edit(t, "apps", "'KeyVaultSecrets__Mappings__6__SecretName', 'value', 'whatsapp-phone-number-id')",
                      "'KeyVaultSecrets__Mappings__6__SecretName', 'value', 'whatsapp-webhook-verify-token')")
        self.assert_fails(23, self.context(worker_webhook), "worker maps webhook secrets ['whatsapp-webhook-verify-token']")
        self.assert_fails(6, self.context(worker_webhook), "differ from the least-privilege set")

        def contact_key_in_environment(t):
            t["apps"]["variables"]["productionEnv"].append({"name": "ContactLookup__Keys__1", "value": "inline"})
        self.assert_fails(6, self.context(contact_key_in_environment), "sensitive setting ContactLookup__Keys__1")

    def test_p29_each_key_role_belongs_to_one_host_and_one_key(self):
        def api_unwraps(t):
            self.notice_assignment(t, "keyVaultReader")["properties"]["roleDefinitionId"] = (
                "[subscriptionResourceId('Microsoft.Authorization/roleDefinitions', variables('roles').keyVaultCryptoServiceEncryptionUser)]")
        self.assert_fails(29, self.context(api_unwraps), "recipient-notice-kek must be held exactly by")

        def worker_reader(t):
            self.notice_assignment(t, "recipientNoticeUnwrapRoleDefinitionId")["properties"]["roleDefinitionId"] = (
                "[subscriptionResourceId('Microsoft.Authorization/roleDefinitions', variables('roles').keyVaultReader)]")
        self.assert_fails(29, self.context(worker_reader), "recipient-notice-kek must be held exactly by")

        def unwrap_on_the_pii_key(t):
            assignment = self.notice_assignment(t, "recipientNoticeUnwrapRoleDefinitionId")
            assignment["scope"] = assignment["scope"].replace("'recipient-notice-kek'", "'pii-kek'")
        failures = [r.detail for r in guards.run_guards(self.context(unwrap_on_the_pii_key)) if r.number == 29][0]
        self.assertIn("outside recipient-notice-kek", failures)
        self.assertIn("the Worker holds a role on pii-kek", failures)

        def reader_on_the_vault(t):
            extra = copy.deepcopy(self.notice_assignment(t, "keyVaultReader"))
            extra["scope"] = extra["scope"].replace("'recipient-notice-kek'", "'dataprotection-kek'")
            t["security"]["resources"].append(extra)
        self.assert_fails(29, self.context(reader_on_the_vault), "assigns Key Vault Reader outside recipient-notice-kek")

        def unwrap_role_in_apps(t):
            assignment = copy.deepcopy(self.notice_assignment(t, "recipientNoticeUnwrapRoleDefinitionId"))
            t["apps"]["resources"].append(assignment)
        self.assert_fails(14, self.context(unwrap_role_in_apps), "allowed only in security.bicep")

        def unknown_parameter_role(t):
            self.notice_assignment(t, "recipientNoticeUnwrapRoleDefinitionId")["properties"]["roleDefinitionId"] = "[parameters('anyRole')]"
        self.assert_fails(14, self.context(unknown_parameter_role), "unknown parameter")

    def test_p29_only_the_worker_sends_email_and_only_on_that_resource(self):
        def sender():
            return lambda t: next(r for r in t["apps"]["resources"] if r.get("type") == "Microsoft.Authorization/roleAssignments"
                                  and "acsEmailSenderRoleDefinitionId" in json.dumps(r))

        def api_sends(t):
            assignment = sender()(t)
            assignment["properties"]["principalId"] = assignment["properties"]["principalId"].replace("id-pv-pilot-worker", "id-pv-pilot-api")
        self.assert_fails(29, self.context(api_sends), "must be held only by id-pv-pilot-worker")

        def contributor(t):
            sender()(t)["properties"]["roleDefinitionId"] = (
                "[subscriptionResourceId('Microsoft.Authorization/roleDefinitions', 'b24988ac-6180-42a0-ab88-20f7382dd24c')]")
        self.assert_fails(14, self.context(contributor), "outside the pilot allowlist")
        self.assert_fails(29, self.context(contributor), "must be held only by id-pv-pilot-worker")

        def second_sender(t):
            t["apps"]["resources"].append(copy.deepcopy(sender()(t)))
        self.assert_fails(29, self.context(second_sender), "must be held only by id-pv-pilot-worker")

    def test_p29_workflow_names_checks_and_generates_fail_closed(self):
        text = (REPO_ROOT / guards.PILOT_WORKFLOW).read_text(encoding="utf-8")
        mutations = {
            "role renamed": (text.replace("EMAIL_SENDER_ROLE: Paquetenvia Pilot Email Sender", "EMAIL_SENDER_ROLE: Paquetenvia Email"),
                             "workflow env EMAIL_SENDER_ROLE must be exactly"),
            "role not checked": (text.replace("python3 ./tools/azr-001/env001_pilot_guards.py custom-role-check", "true"),
                                 "the roles step must look up and check each custom role"),
            "acs not validated": (text.replace('[[ "${PILOT_ACS_NAME}" =~ ^[A-Za-z0-9]([A-Za-z0-9-]{0,61}[A-Za-z0-9])?$ ]]', "true"),
                                  "must validate PILOT_ACS_NAME before azure/login"),
            "acs not checked": (text.replace("echo \"The Communication Services resource named by PILOT_ACS_NAME exists.\"", "true")
                                .replace("if ! az resource show --resource-group \"${PILOT_RESOURCE_GROUP}\" --name \"${PILOT_ACS_NAME}\" \\\n"
                                         "              --resource-type Microsoft.Communication/communicationServices --query id -o tsv --only-show-errors > /dev/null; then",
                                         "if false; then"),
                                "must check that PILOT_ACS_NAME names an existing"),
            "secret not generated": (text.replace("whatsapp-app-secret-previous \\\n", "\\\n"), "window 1 (step id generated) must generate"),
            "meta value generated": (text.replace("for name in whatsapp-webhook-verify-token ", "for name in whatsapp-cloud-api-token whatsapp-webhook-verify-token "),
                                     "must never generate the owner's Meta values"),
            "meta value not required": (text.replace("for name in whatsapp-cloud-api-token whatsapp-phone-number-id whatsapp-app-secret; do",
                                                     "for name in whatsapp-cloud-api-token whatsapp-phone-number-id; do"),
                                        "window 2 (step id connections) must stop"),
            "sender role not passed": (text.replace('"acsEmailSenderRoleDefinitionId=${SENDER_ROLE}" ', ""), "stage 4 must pass"),
            "acs name read twice": (text + "\n# ${{ vars.PILOT_ACS_NAME }}\n",
                                    "must read PILOT_ACS_NAME once"),
        }
        for label, (mutated, fragment) in mutations.items():
            with self.subTest(label=label):
                self.assertNotEqual(text, mutated)
                self.assert_fails(29, self.context(workflow_text=mutated), fragment)

    def test_p30_the_email_events_subscription_is_pinned(self):
        def subscription(t):
            return self.resource(t, "events", "Microsoft.EventGrid/systemTopics/eventSubscriptions")["properties"]

        def topic(t):
            return self.resource(t, "events", "Microsoft.EventGrid/systemTopics")

        mutations = {
            "endpoint": (lambda t: subscription(t)["destination"]["properties"].update(endpointUrl="https://paquetenvia.com/api/v1/other"),
                         "must deliver to the webhook"),
            "batching": (lambda t: subscription(t)["destination"]["properties"].update(maxEventsPerBatch=10), "one event per batch"),
            "header": (lambda t: subscription(t)["destination"]["properties"]["deliveryAttributeMappings"][0].update(name="X-Other"),
                       "static secret header"),
            "not secret": (lambda t: subscription(t)["destination"]["properties"]["deliveryAttributeMappings"][0]["properties"]
                           .update(isSecret=False), "static secret header"),
            "events": (lambda t: subscription(t)["filter"]["includedEventTypes"].append(
                "Microsoft.Communication.EmailEngagementTrackingReportReceived"), "filter exactly"),
            "schema": (lambda t: subscription(t).update(eventDeliverySchema="CloudEventSchemaV1_0"), "Event Grid event schema"),
            "always created": (lambda t: self.resource(t, "events", "Microsoft.EventGrid/systemTopics/eventSubscriptions").pop("condition"),
                               "must exist only when"),
            "topic always created": (lambda t: topic(t).pop("condition"), "exist only when"),
            "topic type": (lambda t: topic(t)["properties"].update(topicType="Microsoft.Storage.StorageAccounts"), "of type"),
            "phase values": (lambda t: t["events"]["parameters"]["emailEventsPhase"].update(allowedValues=["none", "subscribe", "always"]),
                             "allowed values"),
            "plain secret": (lambda t: t["events"]["parameters"]["emailEventsSecret"].update(type="string"), "@secure() parameter"),
        }
        for label, (mutate, fragment) in mutations.items():
            with self.subTest(label=label):
                self.assert_fails(30, self.context(mutate), fragment)

        def topic_elsewhere(t):
            t["apps"]["resources"].append(copy.deepcopy(topic(t)))
        self.assert_fails(1, self.context(topic_elsewhere), "allowed only in events.bicep")

        text = (REPO_ROOT / guards.PILOT_WORKFLOW).read_text(encoding="utf-8")
        mutations = {
            "phase passed": (text.replace('--parameters "@${RUNNER_TEMP}/events.parameters.json"',
                                          '--parameters "@${RUNNER_TEMP}/events.parameters.json" "emailEventsPhase=subscribe"'),
                             "must never pass emailEventsPhase"),
            "no bind": (text.replace('[[ "${CUSTOM_DOMAIN_PHASE}" == "bind" ]] || ', ""), "require custom_domain_phase=bind"),
            "firewall left open": (text.replace("if: always() && steps.events_phase.outputs.phase == 'subscribe'",
                                                "if: steps.events_phase.outputs.phase == 'subscribe'"), "always close it"),
            "always deployed": (text.replace("        if: steps.events_phase.outputs.phase == 'subscribe'\n        shell: bash\n        run: |\n"
                                             "          set -euo pipefail\n          trap 'shred -u \"${RUNNER_TEMP}/events.parameters.json\"",
                                             "        shell: bash\n        run: |\n          set -euo pipefail\n"
                                             "          trap 'shred -u \"${RUNNER_TEMP}/events.parameters.json\""),
                                "stage 6 must deploy events.json only for subscribe"),
            "no meta callback": (text.replace("https://${PILOT_HOST}/api/v1/messaging/whatsapp/webhook", "the Meta callback"),
                                 "the Meta callback https://"),
        }
        for label, (mutated, fragment) in mutations.items():
            with self.subTest(label=label):
                self.assertNotEqual(text, mutated)
                self.assert_fails(30, self.context(workflow_text=mutated), fragment)


if __name__ == "__main__":
    unittest.main()
