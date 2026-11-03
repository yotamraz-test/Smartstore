"""Functional (end-to-end, HTTP-driven) tests for TargetGroupEvaluatorTask.

Covers the milestone's core behaviors:
  1. Admin-triggered single-role run (CustomerRoleController.ApplyRules) produces
     correct CustomerRoleMapping rows (IsSystemMapping=true for matches only).
  2. Re-running ApplyRules is idempotent and reflects data changes (no stale
     mappings, no duplicate accumulation).
  3. Scoping: ApplyRules for role A never touches role B's mappings.
  4. The full/unscoped nightly task run (SchedulingController.RunJob) re-evaluates
     ALL active roles with active rule sets in a single execution.

Tests run in file order (pytest's default, no randomization plugin installed)
against a single shared fixture graph built once per module, because each
scenario builds on the mutations of the previous one (this mirrors how the
real nightly/admin-triggered task is actually used in sequence).

Every result is also printed as a RESULT::<test_id>::<json> line so the
harness driving this suite can record verbatim evidence via write_test_results.py.
"""
from __future__ import annotations

import json
import uuid

import pytest
import requests

import conftest as c

NS = f"ftrun_{uuid.uuid4().hex[:8]}"


def emit(test_id: str, payload: dict) -> None:
    print(f"RESULT::{test_id}::{json.dumps(payload, default=str)}")


@pytest.fixture(scope="module")
def admin_session() -> requests.Session:
    s = requests.Session()
    r = c.login(s)
    assert r.status_code == 200, f"Login POST failed: {r.status_code}"
    check = s.get(f"{c.BASE_URL}/admin", timeout=15)
    assert check.status_code == 200 and "logout" in check.text.lower(), "Admin session not authenticated"
    return s


@pytest.fixture(scope="module")
def env(admin_session: requests.Session):
    """Builds the full fixture graph once for this module, tears it down after."""
    state: dict = {}

    # Two independent custom (non-system) roles, created through the real admin API.
    state["role_a_id"] = c.create_customer_role(admin_session, f"{NS}_RoleA")
    state["role_b_id"] = c.create_customer_role(admin_session, f"{NS}_RoleB")

    # Customers seeded directly (CustomerNumber cannot be set via the admin Create
    # form because CustomerNumberMethod defaults to Disabled).
    state["c_a1"] = c.seed_customer(f"{NS}_a_1")
    state["c_a2"] = c.seed_customer(f"{NS}_a_2")
    state["c_b1"] = c.seed_customer(f"{NS}_b_1")
    state["c_x"] = c.seed_customer(f"{NS}_x_1")  # matches neither role, initially

    # RuleSets (direct DB -- the rule-builder AJAX UI is impractical to automate)
    # using StartsWith on CustomerNumber, linked to their respective roles.
    state["ruleset_a_id"] = c.seed_ruleset_with_rule(f"{NS}_ruleset_a", f"{NS}_a", state["role_a_id"])
    state["ruleset_b_id"] = c.seed_ruleset_with_rule(f"{NS}_ruleset_b", f"{NS}_b", state["role_b_id"])

    state["task_id"] = c.get_task_id()

    yield state

    # Teardown: namespaced records only, regardless of test outcome.
    for cid in (state["c_a1"], state["c_a2"], state["c_b1"], state["c_x"]):
        c.delete_customer(cid)
    c.delete_ruleset(state["ruleset_a_id"])
    c.delete_ruleset(state["ruleset_b_id"])
    c.delete_customer_role(state["role_a_id"])
    c.delete_customer_role(state["role_b_id"])


class TestSetup:
    """SETUP -- prerequisite fixture data is real and clean before any run."""

    def test_01_fixture_setup_is_clean(self, env):
        role_a_mappings = c.get_mappings_for_role(env["role_a_id"])
        role_b_mappings = c.get_mappings_for_role(env["role_b_id"])

        emit(
            "test_01_fixture_setup_is_clean",
            {
                "role_a_id": env["role_a_id"],
                "role_b_id": env["role_b_id"],
                "role_a_mappings_before": sorted(role_a_mappings),
                "role_b_mappings_before": sorted(role_b_mappings),
            },
        )

        assert role_a_mappings == set(), "Role A must start with zero system mappings"
        assert role_b_mappings == set(), "Role B must start with zero system mappings"


class TestApplyRulesSingleRole:
    """HAPPY_PATH -- CustomerRoleController.ApplyRules(id) end to end."""

    def test_02_apply_rules_role_b_creates_correct_mappings(self, admin_session, env):
        before_history = c.latest_history_id(env["task_id"])
        resp = c.apply_rules(admin_session, env["role_b_id"])
        history_row = c.wait_for_task_completion(env["task_id"], before_history)

        role_b_mappings = c.get_mappings_for_role(env["role_b_id"])
        role_a_mappings = c.get_mappings_for_role(env["role_a_id"])

        emit(
            "test_02_apply_rules_role_b_creates_correct_mappings",
            {
                "http_status": resp.status_code,
                "redirect_location": resp.headers.get("Location"),
                "task_history_row": history_row,
                "role_b_mappings": sorted(role_b_mappings),
                "role_a_mappings": sorted(role_a_mappings),
                "expected_role_b_customer": env["c_b1"],
            },
        )

        assert resp.status_code == 302
        assert history_row[4] is None, f"Task reported an error: {history_row[4]}"
        assert role_b_mappings == {(env["c_b1"], 1)}, "Only the matching customer should get an IsSystemMapping=1 row"
        assert role_a_mappings == set(), "Role A must remain untouched by a role-B-scoped run"

    def test_03_apply_rules_role_a_matches_and_role_b_untouched(self, admin_session, env):
        before_history = c.latest_history_id(env["task_id"])
        role_b_before = c.get_mappings_for_role(env["role_b_id"])

        resp = c.apply_rules(admin_session, env["role_a_id"])
        history_row = c.wait_for_task_completion(env["task_id"], before_history)

        role_a_mappings = c.get_mappings_for_role(env["role_a_id"])
        role_b_after = c.get_mappings_for_role(env["role_b_id"])

        emit(
            "test_03_apply_rules_role_a_matches_and_role_b_untouched",
            {
                "http_status": resp.status_code,
                "task_history_row": history_row,
                "role_a_mappings": sorted(role_a_mappings),
                "role_b_mappings_before": sorted(role_b_before),
                "role_b_mappings_after": sorted(role_b_after),
                "expected_role_a_customers": [env["c_a1"], env["c_a2"]],
            },
        )

        assert resp.status_code == 302
        assert history_row[4] is None, f"Task reported an error: {history_row[4]}"
        assert role_a_mappings == {(env["c_a1"], 1), (env["c_a2"], 1)}
        # Scoping: running ApplyRules for role A must not touch role B's mappings,
        # even though role B also has pre-existing system mappings.
        assert role_b_after == role_b_before, "Role-A-scoped run must not alter role B's mappings"


class TestApplyRulesIdempotency:
    """HAPPY_PATH -- re-running ApplyRules is idempotent and reflects data changes."""

    def test_04_apply_rules_rerun_without_changes_is_idempotent(self, admin_session, env):
        mappings_before = c.get_mappings_for_role(env["role_a_id"])
        before_history = c.latest_history_id(env["task_id"])

        resp = c.apply_rules(admin_session, env["role_a_id"])
        history_row = c.wait_for_task_completion(env["task_id"], before_history)

        mappings_after = c.get_mappings_for_role(env["role_a_id"])

        emit(
            "test_04_apply_rules_rerun_without_changes_is_idempotent",
            {
                "http_status": resp.status_code,
                "task_history_row": history_row,
                "mappings_before": sorted(mappings_before),
                "mappings_after": sorted(mappings_after),
            },
        )

        assert resp.status_code == 302
        assert history_row[4] is None
        assert mappings_after == mappings_before, "Re-running with no data change must not accumulate duplicates or drift"

    def test_05_apply_rules_rerun_reflects_data_changes(self, admin_session, env):
        # cA2 no longer matches role A's rule; cX now does.
        c.update_customer_number(env["c_a2"], f"{NS}_x_2_no_longer_a")
        c.update_customer_number(env["c_x"], f"{NS}_a_3_now_matches")

        role_b_before = c.get_mappings_for_role(env["role_b_id"])
        before_history = c.latest_history_id(env["task_id"])

        resp = c.apply_rules(admin_session, env["role_a_id"])
        history_row = c.wait_for_task_completion(env["task_id"], before_history)

        role_a_mappings = c.get_mappings_for_role(env["role_a_id"])
        role_b_after = c.get_mappings_for_role(env["role_b_id"])

        emit(
            "test_05_apply_rules_rerun_reflects_data_changes",
            {
                "http_status": resp.status_code,
                "task_history_row": history_row,
                "role_a_mappings": sorted(role_a_mappings),
                "role_b_mappings_before": sorted(role_b_before),
                "role_b_mappings_after": sorted(role_b_after),
                "expected_role_a_customers": [env["c_a1"], env["c_x"]],
                "removed_customer": env["c_a2"],
            },
        )

        assert resp.status_code == 302
        assert history_row[4] is None
        assert role_a_mappings == {(env["c_a1"], 1), (env["c_x"], 1)}, (
            "Mapping set must reflect the data change: cA2 dropped (no longer matches), "
            "cX added (now matches) -- not stale, not duplicated"
        )
        assert role_b_after == role_b_before, "Unrelated role-A rerun must still not touch role B"


class TestFullUnscopedRun:
    """HAPPY_PATH -- the full/unscoped nightly run re-evaluates ALL roles at once."""

    def test_06_full_task_run_reevaluates_all_roles_in_one_pass(self, admin_session, env):
        # Introduce a second change to role B's data at the same time, so that a
        # single full/unscoped run must correctly re-evaluate BOTH roles together
        # -- proving this exercises the real unscoped code path, not just two
        # separate scoped calls.
        c_b2 = c.seed_customer(f"{NS}_b_2")
        try:
            role_a_before = c.get_mappings_for_role(env["role_a_id"])
            before_history = c.latest_history_id(env["task_id"])

            resp = c.run_scheduled_job(admin_session, env["task_id"])
            history_row = c.wait_for_task_completion(env["task_id"], before_history)

            role_a_after = c.get_mappings_for_role(env["role_a_id"])
            role_b_after = c.get_mappings_for_role(env["role_b_id"])

            emit(
                "test_06_full_task_run_reevaluates_all_roles_in_one_pass",
                {
                    "http_status": resp.status_code,
                    "redirect_location": resp.headers.get("Location"),
                    "task_history_row": history_row,
                    "role_a_mappings_before": sorted(role_a_before),
                    "role_a_mappings_after": sorted(role_a_after),
                    "role_b_mappings_after": sorted(role_b_after),
                    "expected_role_a_customers": [env["c_a1"], env["c_x"]],
                    "expected_role_b_customers": [env["c_b1"], c_b2],
                },
            )

            assert resp.status_code == 302
            assert history_row[4] is None, f"Full task run reported an error: {history_row[4]}"
            # Role A's state from the previous (scoped) run must be preserved/reconfirmed...
            assert role_a_after == {(env["c_a1"], 1), (env["c_x"], 1)}
            # ...AND role B must have been independently re-evaluated in the SAME run,
            # picking up the brand-new matching customer -- proof this was the real
            # unscoped nightly code path touching multiple roles, not a single-role shortcut.
            assert role_b_after == {(env["c_b1"], 1), (c_b2, 1)}
        finally:
            c.delete_customer(c_b2)


class TestApplyRulesNegative:
    """AUTH / NOT_FOUND -- negative paths around the admin-triggered action."""

    def test_07_apply_rules_requires_authentication(self, env):
        anon_session = requests.Session()
        resp = anon_session.post(
            f"{c.BASE_URL}/Admin/CustomerRole/ApplyRules/{env['role_a_id']}",
            timeout=15,
            allow_redirects=False,
        )

        mappings_before = c.get_mappings_for_role(env["role_a_id"])

        emit(
            "test_07_apply_rules_requires_authentication",
            {
                "http_status": resp.status_code,
                "redirect_location": resp.headers.get("Location"),
                "mappings_unchanged": sorted(mappings_before),
            },
        )

        # Unauthenticated admin POSTs are redirected to login (302) rather than executed.
        assert resp.status_code in (302, 401, 403)
        if resp.status_code == 302:
            assert "login" in (resp.headers.get("Location") or "").lower()

    def test_08_apply_rules_nonexistent_role_returns_404(self, admin_session, env):
        nonexistent_id = 999_999_999
        token, _ = c.get_token(admin_session, f"{c.BASE_URL}/Admin/CustomerRole/Edit/{env['role_a_id']}")
        resp = admin_session.post(
            f"{c.BASE_URL}/Admin/CustomerRole/ApplyRules/{nonexistent_id}",
            data={"__RequestVerificationToken": token},
            timeout=15,
            allow_redirects=False,
        )

        emit(
            "test_08_apply_rules_nonexistent_role_returns_404",
            {
                "http_status": resp.status_code,
                "role_id_requested": nonexistent_id,
            },
        )

        assert resp.status_code == 404
