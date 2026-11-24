"""Functional tests for TargetGroupEvaluatorTask against the running Smartstore web app (SQLite).

Trigger: POST /admin/scheduling/runjob/{id} (full) and POST /admin/customerrole/applyrules/{id} (filtered).
Effect is read back from the SQLite DB (CustomerRoleMapping, ScheduleTaskHistory).
Env: SS_URL (default http://127.0.0.1:5199), SS_DB, SS_ADMIN_EMAIL, SS_ADMIN_PASSWORD.
"""
import os, re, sqlite3, threading, time, uuid
import pytest, requests

BASE = os.environ.get("SS_URL", "http://127.0.0.1:5199")
DB = os.environ.get("SS_DB", os.path.expandvars("$WORKSPACE_DIR/Smartstore/src/Smartstore.Web/App_Data/Tenants/Default/smartstore.db"))
EMAIL = os.environ.get("SS_ADMIN_EMAIL", "admin@example.com")
PASSWORD = os.environ.get("SS_ADMIN_PASSWORD", "Admin123!x")
RUN = "ftrun_" + uuid.uuid4().hex[:6]
NOW = "2026-01-01 00:00:00"


def q(sql, args=(), many=False):
    c = sqlite3.connect(DB, timeout=30)
    try:
        cur = c.executemany(sql, args) if many else c.execute(sql, args)
        c.commit()
        return cur.fetchall(), cur.lastrowid
    finally:
        c.close()


def mk_role(name, active=1):
    _, rid = q("insert into CustomerRole(Name,FreeShipping,TaxExempt,Active,IsSystemRole,SystemName) values(?,0,0,?,0,?)", (name, active, name))
    return rid


def mk_ruleset(role_id, company, active=1):
    _, sid = q("insert into RuleSet(Name,IsActive,Scope,IsSubGroup,LogicalOperator,CreatedOnUtc,UpdatedOnUtc) values(?,?,2,0,0,?,?)", (RUN + company, active, NOW, NOW))
    q("insert into Rule(RuleSetId,RuleType,Operator,Value,DisplayOrder) values(?,?,?,?,0)", (sid, "Company", "=", company))
    q("insert into RuleSet_CustomerRole_Mapping(CustomerRole_Id,RuleSetEntity_Id) values(?,?)", (role_id, sid))
    return sid


def mk_customers(company, n):
    rows = [(str(uuid.uuid4()), company, NOW, NOW) for _ in range(n)]
    q("insert into Customer(CustomerGuid,PasswordFormatId,IsTaxExempt,AffiliateId,Active,Deleted,IsSystemAccount,CreatedOnUtc,LastActivityDateUtc,VatNumberStatusId,TaxDisplayTypeId,LimitedToStores,Company) values(?,0,0,0,1,0,0,?,?,0,0,0,?)",
      [(g, a, b, c) for g, c, a, b in rows], many=True)
    ids, _ = q("select Id from Customer where Company=?", (company,))
    return [i[0] for i in ids]


def mappings(role_id, system=None):
    sql = "select CustomerId,IsSystemMapping from CustomerRoleMapping where CustomerRoleId=?"
    if system is not None:
        sql += " and IsSystemMapping=%d" % system
    return q(sql, (role_id,))[0]


@pytest.fixture(scope="session")
def admin():
    s = requests.Session()
    r = s.get(BASE + "/login", timeout=180)
    tok = re.search(r'name="__RequestVerificationToken"[^>]*value="([^"]+)"', r.text).group(1)
    r = s.post(BASE + "/login", data={"UsernameOrEmail": EMAIL, "Email": EMAIL, "Password": PASSWORD, "__RequestVerificationToken": tok},
               allow_redirects=False, timeout=180)
    assert r.status_code == 302, "admin login failed: %s" % r.status_code
    return s


@pytest.fixture(scope="session")
def task_id():
    rows, _ = q("select Id from ScheduleTask where Type like '%TargetGroupEvaluatorTask%'")
    assert rows
    return rows[0][0]


@pytest.fixture(scope="session")
def data():
    d = {}
    d["a"] = mk_role(RUN + "_A")
    d["b"] = mk_role(RUN + "_B")
    d["inactive_role"] = mk_role(RUN + "_C", active=0)
    d["inactive_rs"] = mk_role(RUN + "_D")
    d["big"] = mk_role(RUN + "_E")
    d["ca"] = mk_customers(RUN + "_A", 3)
    d["cb"] = mk_customers(RUN + "_B", 2)
    d["cc"] = mk_customers(RUN + "_C", 2)
    d["cd"] = mk_customers(RUN + "_D", 2)
    d["cbig"] = mk_customers(RUN + "_E", 1100)
    mk_ruleset(d["a"], RUN + "_A")
    mk_ruleset(d["b"], RUN + "_B")
    mk_ruleset(d["inactive_role"], RUN + "_C")
    mk_ruleset(d["inactive_rs"], RUN + "_D", active=0)
    mk_ruleset(d["big"], RUN + "_E")
    yield d
    roles = [d[k] for k in ("a", "b", "inactive_role", "inactive_rs", "big")]
    ph = ",".join("?" * len(roles))
    q("delete from CustomerRoleMapping where CustomerRoleId in (%s)" % ph, roles)
    q("delete from RuleSet_CustomerRole_Mapping where CustomerRole_Id in (%s)" % ph, roles)
    q("delete from Rule where RuleSetId in (select Id from RuleSet where Name like ?)", (RUN + "%",))
    q("delete from RuleSet where Name like ?", (RUN + "%",))
    q("delete from CustomerRole where Id in (%s)" % ph, roles)
    q("delete from Customer where Company like ?", (RUN + "%",))


def wait_task(tid, before_id, timeout=300):
    end = time.time() + timeout
    while time.time() < end:
        rows, _ = q("select Id,IsRunning,FinishedOnUtc,SucceededOnUtc,Error,ProgressMessage from ScheduleTaskHistory where ScheduleTaskId=? and Id>? order by Id desc limit 1", (tid, before_id))
        if rows and rows[0][2] is not None and not rows[0][1]:
            return rows[0]
        time.sleep(1)
    pytest.fail("task did not finish")


def last_hist(tid):
    rows, _ = q("select coalesce(max(Id),0) from ScheduleTaskHistory where ScheduleTaskId=?", (tid,))
    return rows[0][0]


def seed_stale(d):
    q("delete from CustomerRoleMapping where CustomerRoleId in (?,?,?,?,?)", (d["a"], d["b"], d["inactive_role"], d["inactive_rs"], d["big"]))
    # stale system mappings (customers who do NOT match), for every role
    for k in ("a", "b", "inactive_role", "inactive_rs"):
        q("insert into CustomerRoleMapping(CustomerId,CustomerRoleId,IsSystemMapping) values(?,?,1)", (d["cd"][0] if k != "inactive_rs" else d["ca"][0], d[k]))
    # manual (non-system) mapping must survive
    q("insert into CustomerRoleMapping(CustomerId,CustomerRoleId,IsSystemMapping) values(?,?,0)", (d["cb"][0], d["a"]))


def run_full(admin, tid, progress=None):
    before = last_hist(tid)
    stop = threading.Event()

    def poll():
        while not stop.is_set():
            try:
                rows, _ = q("select ProgressMessage from ScheduleTaskHistory where ScheduleTaskId=? and Id>? and ProgressMessage is not null", (tid, before))
                for r in rows:
                    progress.add(r[0])
            except Exception:
                pass
            time.sleep(0.02)

    if progress is not None:
        threading.Thread(target=poll, daemon=True).start()
    r = admin.get(BASE + "/admin/scheduling/runjob/%d" % tid, timeout=120, allow_redirects=False)
    assert r.status_code in (200, 302), r.status_code
    try:
        return wait_task(tid, before)
    finally:
        stop.set()


def post_apply(admin, role_id):
    page = admin.get(BASE + "/admin/customerrole/edit/%d" % (role_id if role_id < 90000000 else 1), timeout=120)
    tok = re.search(r'name="__RequestVerificationToken"[^>]*value="([^"]+)"', page.text).group(1)
    return admin.post(BASE + "/admin/customerrole/applyrules/%d" % role_id, data={"__RequestVerificationToken": tok},
                      headers={"RequestVerificationToken": tok}, timeout=120, allow_redirects=False)


def run_filtered(admin, tid, role_id):
    before = last_hist(tid)
    r = post_apply(admin, role_id)
    assert r.status_code in (200, 302), r.status_code
    return wait_task(tid, before)


class TestFullRun:
    def test_full_run_replaces_system_mappings(self, admin, task_id, data):
        seed_stale(data)
        h = run_full(admin, task_id)
        assert h[3] is not None and not h[4], h
        a = mappings(data["a"], 1)
        assert sorted(c for c, _ in a) == sorted(data["ca"])
        assert sorted(c for c, _ in mappings(data["b"], 1)) == sorted(data["cb"])
        assert [m for m in mappings(data["a"], 0)] == [(data["cb"][0], 0)], "manual mapping must be preserved"

    def test_inactive_role_and_ruleset_get_no_mappings_and_stale_deleted(self, admin, task_id, data):
        seed_stale(data)
        run_full(admin, task_id)
        assert mappings(data["inactive_role"]) == []
        assert mappings(data["inactive_rs"]) == []

    def test_chunking_over_500_customers(self, admin, task_id, data):
        seed_stale(data)
        run_full(admin, task_id)
        got = mappings(data["big"], 1)
        assert len(got) == 1100 and len({c for c, _ in got}) == 1100

    def test_progress_message(self, admin, task_id, data):
        seed_stale(data)
        seen = set()
        run_full(admin, task_id, progress=seen)
        assert any('Add customer assignments for role "' in m for m in seen), seen

    def test_rerun_is_idempotent(self, admin, task_id, data):
        run_full(admin, task_id)
        n1 = len(mappings(data["big"], 1))
        run_full(admin, task_id)
        assert len(mappings(data["big"], 1)) == n1 == 1100


class TestFilteredRun:
    def test_filtered_run_only_touches_given_role(self, admin, task_id, data):
        seed_stale(data)
        stale_b = mappings(data["b"], 1)
        stale_inactive = mappings(data["inactive_role"], 1)
        h = run_filtered(admin, task_id, data["a"])
        assert h[3] is not None and not h[4], h
        assert sorted(c for c, _ in mappings(data["a"], 1)) == sorted(data["ca"])
        assert mappings(data["b"], 1) == stale_b, "other roles' system mappings must be untouched"
        assert mappings(data["inactive_role"], 1) == stale_inactive
        assert mappings(data["big"], 1) == []

    def test_filtered_run_on_inactive_role_deletes_but_adds_nothing(self, admin, task_id, data):
        seed_stale(data)
        run_filtered(admin, task_id, data["inactive_role"])
        assert mappings(data["inactive_role"]) == []
        assert len(mappings(data["b"], 1)) == 1  # still stale

    def test_apply_rules_unknown_role_404(self, admin):
        r = post_apply(admin, 99999999)
        assert r.status_code == 404
