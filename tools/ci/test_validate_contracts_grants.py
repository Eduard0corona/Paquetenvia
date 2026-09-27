"""Self-tests for the executor grant rules of docs/normative/v0.6/tools/validate_contracts.py.

The canonical AI-18 must satisfy the ADR-034 and OPS-003-CLEANUP-ROLE grant sets exactly, and any
widening must fail validation, including a grant that names an executor next to another grantee.
"""
from __future__ import annotations

import importlib.util
import pathlib
import sys
import unittest

REPOSITORY_ROOT = pathlib.Path(__file__).resolve().parents[2]
VALIDATOR_PATH = REPOSITORY_ROOT / "docs/normative/v0.6/tools/validate_contracts.py"
ROLE_MODEL_PATH = REPOSITORY_ROOT / "docs/normative/v0.6/database/AI-18_DATABASE_ROLE_MODEL.sql"


def load_validator():
    # The validator sits inside the hashed normative bundle; importing it must not leave a
    # __pycache__ directory there, or the manifest identity checks would see an undeclared file.
    previous = sys.dont_write_bytecode
    sys.dont_write_bytecode = True
    try:
        return _load_validator()
    finally:
        sys.dont_write_bytecode = previous


def _load_validator():
    spec = importlib.util.spec_from_file_location("validate_contracts", VALIDATOR_PATH)
    module = importlib.util.module_from_spec(spec)
    assert spec.loader is not None
    spec.loader.exec_module(module)
    return module


class ExecutorGrantRuleTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls) -> None:
        cls.validator = load_validator()
        cls.role_sql = ROLE_MODEL_PATH.read_text(encoding="utf-8")

    def test_canonical_role_model_satisfies_both_executor_grant_sets(self) -> None:
        self.assertEqual([], self.validator.executor_grant_errors(self.role_sql))

    def test_multi_grantee_grants_fail_for_either_executor(self) -> None:
        for widening in [
            "GRANT SELECT ON orders.order_events TO paqueteria_app, paqueteria_cleanup_executor;",
            "GRANT SELECT ON platform.audit_logs TO paqueteria_cleanup_executor,paqueteria_worker;",
            "GRANT INSERT ON orders.orders TO paqueteria_worker, paqueteria_lifecycle_executor;",
            "GRANT USAGE ON SCHEMA dispatch\n  TO paqueteria_app,\n     paqueteria_lifecycle_executor;",
        ]:
            with self.subTest(widening=widening):
                errors = self.validator.executor_grant_errors(self.role_sql + "\n" + widening + "\n")
                self.assertTrue(
                    any("outside its contract" in error for error in errors),
                    f"widening was not detected: {errors}",
                )

    def test_widening_by_role_membership_or_grant_option_fails(self) -> None:
        for widening in [
            "GRANT paqueteria_worker TO paqueteria_cleanup_executor;",
            "GRANT DELETE ON custody.proof_upload_sessions TO paqueteria_cleanup_executor WITH GRANT OPTION;",
        ]:
            with self.subTest(widening=widening):
                self.assertNotEqual(
                    [], self.validator.executor_grant_errors(self.role_sql + "\n" + widening + "\n")
                )

    def test_a_missing_contract_grant_fails(self) -> None:
        narrowed = self.role_sql.replace(
            "GRANT DELETE ON platform.idempotency_keys TO paqueteria_cleanup_executor;", ""
        )
        self.assertNotEqual(narrowed, self.role_sql)
        self.assertTrue(
            any("differ from the contract" in error for error in self.validator.executor_grant_errors(narrowed))
        )

    def test_commented_grants_are_not_counted(self) -> None:
        commented = self.role_sql + "\n-- GRANT SELECT ON orders.order_events TO paqueteria_cleanup_executor;\n"
        self.assertEqual([], self.validator.executor_grant_errors(commented))


if __name__ == "__main__":
    unittest.main()
