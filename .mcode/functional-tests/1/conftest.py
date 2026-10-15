"""Shared helpers for TargetGroupEvaluatorTask functional tests.

These tests drive the real, running Smartstore admin app over HTTP
(requests) to exercise TargetGroupEvaluatorTask end to end:

  * CustomerRoleController.ApplyRules(id)  -> admin-triggered, single-role run
  * SchedulingController.RunJob(id)        -> full/unscoped nightly run

Fixture data that cannot practically be created through the app's own UI/API
(RuleSet/Rule rows -- the rule-builder is a complex multi-step AJAX flow --
and Customer.CustomerNumber, which the admin Create form only persists when
CustomerNumberMethod == Enabled, not the default Disabled) is seeded directly
into the app's own writable SQLite database. The behavior under test itself
(role creation, ApplyRules, the scheduled task run) is always driven through
the real HTTP API, never faked.
"""
from __future__ import annotations

import datetime
import os
import re
import sqlite3
import time
import uuid

import requests

BASE_URL = os.environ.get("APP_BASE_URL", "http://localhost:5000")
ADMIN_EMAIL = os.environ.get("ADMIN_EMAIL", "admin@example.com")
ADMIN_PASSWORD = os.environ.get("ADMIN_PASSWORD", "Passw0rd!")

# DB path relative to the repo root (tests are run with cwd == repo root,
# per the sandbox-app-runner skill's pytest execution instructions).
DB_PATH = os.path.abspath(
    os.path.join("src", "Smartstore.Web", "App_Data", "Tenants", "Default", "smartstore.db")
)

TASK_TYPE = "TargetGroupEvaluatorTask"
RULE_SCOPE_CUSTOMER = 2  # RuleScope.Customer


def db_conn() -> sqlite3.Connection:
    conn = sqlite3.connect(DB_PATH, timeout=30)
    conn.execute("PRAGMA busy_timeout = 30000")
    return conn


def utcnow_iso() -> str:
    return datetime.datetime.now(datetime.timezone.utc).strftime("%Y-%m-%d %H:%M:%S.%f")


def extract_antiforgery_token(html: str) -> str:
    m = re.search(r'name="__RequestVerificationToken"[^>]*value="([^"]+)"', html)
    assert m, "Could not find __RequestVerificationToken in page HTML"
    return m.group(1)


def get_token(session: requests.Session, url: str) -> tuple[str, requests.Response]:
    r = session.get(url, timeout=15)
    assert r.status_code == 200, f"GET {url} failed: {r.status_code}"
    return extract_antiforgery_token(r.text), r


def login(session: requests.Session) -> requests.Response:
    r = session.get(f"{BASE_URL}/login", timeout=15)
    assert r.status_code == 200, "Login page not reachable"
    token = extract_antiforgery_token(r.text)
    payload = {
        "UsernameOrEmail": ADMIN_EMAIL,
        "Password": ADMIN_PASSWORD,
        "RememberMe": "false",
        "__RequestVerificationToken": token,
    }
    return session.post(f"{BASE_URL}/login", data=payload, timeout=15)


# --- Customer seeding (direct DB: CustomerNumber cannot be set through the
#     admin Create form unless CustomerNumberMethod == Enabled, which is not
#     the default) -------------------------------------------------------

def seed_customer(customer_number: str) -> int:
    conn = db_conn()
    try:
        cur = conn.cursor()
        guid = str(uuid.uuid4())
        now = utcnow_iso()
        cur.execute(
            """
            INSERT INTO Customer (
                CustomerGuid, PasswordFormatId, IsTaxExempt, AffiliateId, Active, Deleted,
                IsSystemAccount, CreatedOnUtc, LastActivityDateUtc, CustomerNumber,
                VatNumberStatusId, TaxDisplayTypeId, LimitedToStores
            ) VALUES (?, 0, 0, 0, 1, 0, 0, ?, ?, ?, 0, 0, 0)
            """,
            (guid, now, now, customer_number),
        )
        conn.commit()
        return cur.lastrowid
    finally:
        conn.close()


def update_customer_number(customer_id: int, new_number: str) -> None:
    conn = db_conn()
    try:
        conn.execute("UPDATE Customer SET CustomerNumber = ? WHERE Id = ?", (new_number, customer_id))
        conn.commit()
    finally:
        conn.close()


def delete_customer(customer_id: int) -> None:
    conn = db_conn()
    try:
        conn.execute("DELETE FROM Customer WHERE Id = ?", (customer_id,))
        conn.commit()
    finally:
        conn.close()


# --- RuleSet/Rule seeding (direct DB: the rule-builder admin UI is a complex
#     multi-step AJAX flow not practical to drive for fixture setup) -------

def seed_ruleset_with_rule(name: str, customer_number_prefix: str, role_id: int) -> int:
    conn = db_conn()
    try:
        cur = conn.cursor()
        now = utcnow_iso()
        cur.execute(
            """
            INSERT INTO RuleSet (Name, Description, IsActive, Scope, IsSubGroup, LogicalOperator, CreatedOnUtc, UpdatedOnUtc)
            VALUES (?, '', 1, ?, 0, 0, ?, ?)
            """,
            (name, RULE_SCOPE_CUSTOMER, now, now),
        )
        ruleset_id = cur.lastrowid
        cur.execute(
            """
            INSERT INTO Rule (RuleSetId, RuleType, Operator, Value, DisplayOrder)
            VALUES (?, 'CustomerNumber', 'StartsWith', ?, 0)
            """,
            (ruleset_id, customer_number_prefix),
        )
        cur.execute(
            """
            INSERT INTO RuleSet_CustomerRole_Mapping (CustomerRole_Id, RuleSetEntity_Id)
            VALUES (?, ?)
            """,
            (role_id, ruleset_id),
        )
        conn.commit()
        return ruleset_id
    finally:
        conn.close()


def delete_ruleset(ruleset_id: int) -> None:
    conn = db_conn()
    try:
        conn.execute("DELETE FROM RuleSet_CustomerRole_Mapping WHERE RuleSetEntity_Id = ?", (ruleset_id,))
        conn.execute("DELETE FROM Rule WHERE RuleSetId = ?", (ruleset_id,))
        conn.execute("DELETE FROM RuleSet WHERE Id = ?", (ruleset_id,))
        conn.commit()
    finally:
        conn.close()


# --- CustomerRole (created via the real admin HTTP API) -------------------

def create_customer_role(session: requests.Session, name: str) -> int:
    token, _ = get_token(session, f"{BASE_URL}/Admin/CustomerRole/Create")
    payload = {
        "__RequestVerificationToken": token,
        "Name": name,
        "Active": "true",
        "FreeShipping": "false",
        "TaxExempt": "false",
        "IsSystemRole": "false",
        "continueEditing": "false",
    }
    r = session.post(f"{BASE_URL}/Admin/CustomerRole/Create", data=payload, timeout=15, allow_redirects=False)
    assert r.status_code == 302, f"Create role failed: {r.status_code} body={r.text[:300]}"

    conn = db_conn()
    try:
        cur = conn.cursor()
        cur.execute("SELECT Id FROM CustomerRole WHERE Name = ?", (name,))
        row = cur.fetchone()
        assert row, f"Role '{name}' not found in DB after creation"
        return row[0]
    finally:
        conn.close()


def delete_customer_role(role_id: int) -> None:
    conn = db_conn()
    try:
        # Clean up any CustomerRoleMapping rows (including IsSystemMapping=1 rows
        # created by the task under test) before removing the role itself, so no
        # orphaned mapping rows are left behind.
        conn.execute("DELETE FROM CustomerRoleMapping WHERE CustomerRoleId = ?", (role_id,))
        conn.execute("DELETE FROM CustomerRole WHERE Id = ?", (role_id,))
        conn.commit()
    finally:
        conn.close()


# --- Mapping / task-history inspection ------------------------------------

def get_mappings_for_role(role_id: int) -> set[tuple[int, int]]:
    """Returns {(CustomerId, IsSystemMapping), ...} for the given role."""
    conn = db_conn()
    try:
        cur = conn.cursor()
        cur.execute("SELECT CustomerId, IsSystemMapping FROM CustomerRoleMapping WHERE CustomerRoleId = ?", (role_id,))
        return set(cur.fetchall())
    finally:
        conn.close()


def get_task_id(task_type: str = TASK_TYPE) -> int:
    conn = db_conn()
    try:
        cur = conn.cursor()
        cur.execute("SELECT Id FROM ScheduleTask WHERE Type = ?", (task_type,))
        row = cur.fetchone()
        assert row, f"Task of type {task_type} not found in ScheduleTask table"
        return row[0]
    finally:
        conn.close()


def latest_history_id(task_id: int) -> int:
    conn = db_conn()
    try:
        cur = conn.cursor()
        cur.execute("SELECT COALESCE(MAX(Id), 0) FROM ScheduleTaskHistory WHERE ScheduleTaskId = ?", (task_id,))
        return cur.fetchone()[0]
    finally:
        conn.close()


def wait_for_task_completion(task_id: int, after_history_id: int, timeout: float = 40.0):
    """Poll ScheduleTaskHistory until a new, finished row appears.

    TargetGroupEvaluatorTask is triggered via DefaultTaskScheduler.RunSingleTaskAsync,
    which is a fire-and-forget internal HTTP self-call relative to the triggering
    request -- so completion must be polled for, not assumed synchronous.
    """
    deadline = time.time() + timeout
    last_row = None
    while time.time() < deadline:
        conn = db_conn()
        try:
            cur = conn.cursor()
            cur.execute(
                """
                SELECT Id, IsRunning, FinishedOnUtc, SucceededOnUtc, Error
                FROM ScheduleTaskHistory
                WHERE ScheduleTaskId = ? AND Id > ?
                ORDER BY Id DESC LIMIT 1
                """,
                (task_id, after_history_id),
            )
            row = cur.fetchone()
        finally:
            conn.close()

        if row:
            last_row = row
            is_running, finished_on = row[1], row[2]
            if not is_running and finished_on:
                return row
        time.sleep(0.5)

    raise TimeoutError(
        f"Task {task_id} did not complete within {timeout}s after history id {after_history_id}; "
        f"last seen row: {last_row}"
    )


def apply_rules(session: requests.Session, role_id: int) -> requests.Response:
    """POST /Admin/CustomerRole/ApplyRules/{id} -- the real admin-triggered action."""
    token, _ = get_token(session, f"{BASE_URL}/Admin/CustomerRole/Edit/{role_id}")
    return session.post(
        f"{BASE_URL}/Admin/CustomerRole/ApplyRules/{role_id}",
        data={"__RequestVerificationToken": token},
        timeout=15,
        allow_redirects=False,
    )


def run_scheduled_job(session: requests.Session, task_id: int) -> requests.Response:
    """GET /Admin/Scheduling/RunJob/{id} -- the full/unscoped nightly-task trigger."""
    return session.get(
        f"{BASE_URL}/Admin/Scheduling/RunJob/{task_id}",
        params={"returnUrl": "/admin/scheduling/list"},
        timeout=15,
        allow_redirects=False,
    )
