import os, re, sqlite3, time, uuid
import pytest, requests

BASE = os.environ.get("BASE_URL", "http://localhost:5000")
DB = os.path.join(os.environ.get("WORKSPACE_DIR", "/l2l/workspace"), "Smartstore/src/Smartstore.Web/App_Data/Tenants/Default/ftdb.db")
ADMIN = ("admin@mystore.com", "Admin#12345")
NOW = "2026-01-01 00:00:00"


def db():
    c = sqlite3.connect(DB, timeout=30)
    c.execute("pragma busy_timeout=30000")
    return c


def token(html):
    return re.search(r'name="__RequestVerificationToken"[^>]*value="([^"]+)"|__RequestVerificationToken" type="hidden" value="([^"]+)"', html).group(1) or re.search(r'__RequestVerificationToken" type="hidden" value="([^"]+)"', html).group(1)


@pytest.fixture(scope="module")
def admin():
    s = requests.Session()
    r = s.get(f"{BASE}/login", timeout=60)
    r = s.post(f"{BASE}/login/", data={"UsernameOrEmail": ADMIN[0], "Password": ADMIN[1],
               "CustomerLoginType": "UsernameOrEmail", "RememberMe": "false",
               "__RequestVerificationToken": token(r.text)}, timeout=60)
    assert r.status_code == 200
    assert s.get(f"{BASE}/admin", timeout=120).url.rstrip("/").endswith("/admin")
    return s


@pytest.fixture(scope="module")
def task_id():
    with db() as c:
        row = c.execute("select Id from ScheduleTask where Type like '%TargetGroupEvaluatorTask%'").fetchone()
    assert row, "seeded TargetGroupEvaluatorTask not found"
    return row[0]


@pytest.fixture()
def data():
    """Two active roles w/ active rule set (Company == tag), one active role w/ inactive rule set, one inactive role w/ active rule set."""
    tag = "ftrun_" + uuid.uuid4().hex[:8]
    ids = {"tag": tag, "customers": [], "roles": {}, "rulesets": []}
    c = db()
    cur = c.cursor()

    def role(key, active):
        cur.execute("insert into CustomerRole (Name,SystemName,FreeShipping,TaxExempt,Active,IsSystemRole) values (?,?,0,0,?,0)",
                    (f"{tag}_{key}", f"{tag}_{key}", active))
        ids["roles"][key] = cur.lastrowid

    def ruleset(rid, active, value):
        cur.execute("insert into RuleSet (Name,IsActive,Scope,IsSubGroup,LogicalOperator,CreatedOnUtc,UpdatedOnUtc) values (?,?,2,0,0,?,?)",
                    (f"{tag}_rs", active, NOW, NOW))
        rs = cur.lastrowid
        ids["rulesets"].append(rs)
        cur.execute("insert into Rule (RuleSetId,RuleType,Operator,Value,DisplayOrder) values (?,?,?,?,0)", (rs, "Company", "StartsWith", value))
        cur.execute("insert into RuleSet_CustomerRole_Mapping (CustomerRole_Id,RuleSetEntity_Id) values (?,?)", (rid, rs))

    role("a", 1); role("b", 1); role("inactive_rs", 1); role("inactive_role", 0)
    ruleset(ids["roles"]["a"], 1, tag)
    ruleset(ids["roles"]["b"], 1, tag)
    ruleset(ids["roles"]["inactive_rs"], 0, tag)
    ruleset(ids["roles"]["inactive_role"], 1, tag)
    for i, comp in enumerate([tag, tag, tag, "other_" + tag]):
        cur.execute("insert into Customer (CustomerGuid,PasswordFormatId,IsTaxExempt,AffiliateId,Active,Deleted,IsSystemAccount,CreatedOnUtc,LastActivityDateUtc,VatNumberStatusId,TaxDisplayTypeId,LimitedToStores,Company,Email,Username) values (?,0,0,0,1,0,0,?,?,0,0,0,?,?,?)",
                    (str(uuid.uuid4()), NOW, NOW, comp, f"{tag}_{i}@ft.test", f"{tag}_{i}"))
        ids["customers"].append(cur.lastrowid)
    c.commit(); c.close()
    yield ids
    with db() as c:
        rids = tuple(ids["roles"].values())
        q = ",".join("?" * len(rids))
        c.execute(f"delete from CustomerRoleMapping where CustomerRoleId in ({q})", rids)
        c.execute(f"delete from RuleSet_CustomerRole_Mapping where CustomerRole_Id in ({q})", rids)
        for rs in ids["rulesets"]:
            c.execute("delete from Rule where RuleSetId=?", (rs,))
            c.execute("delete from RuleSet where Id=?", (rs,))
        c.execute(f"delete from CustomerRole where Id in ({q})", rids)
        cq = ",".join("?" * len(ids["customers"]))
        c.execute(f"delete from CustomerRoleMapping where CustomerId in ({cq})", ids["customers"])
        c.execute(f"delete from Customer where Id in ({cq})", ids["customers"])


def mappings(role_id):
    with db() as c:
        return c.execute("select CustomerId,IsSystemMapping from CustomerRoleMapping where CustomerRoleId=?", (role_id,)).fetchall()


def wait_for(pred, timeout=90):
    end = time.time() + timeout
    while time.time() < end:
        if pred():
            return True
        time.sleep(1)
    return False


def run_task(admin, task_id):
    return admin.get(f"{BASE}/admin/scheduling/runjob/{task_id}", timeout=60, allow_redirects=False)


def add_stale(role_id, cust_id, system):
    with db() as c:
        c.execute("insert into CustomerRoleMapping (CustomerId,CustomerRoleId,IsSystemMapping) values (?,?,?)", (cust_id, role_id, system))


def apply_rules(admin, role_id):
    page = admin.get(f"{BASE}/admin/customerrole/edit/{role_id}", timeout=120)
    assert page.status_code == 200
    return admin.post(f"{BASE}/admin/customerrole/applyrules/{role_id}", data={"__RequestVerificationToken": token(page.text)},
                      timeout=60, allow_redirects=False)


class TestTaskRegistration:
    def test_legacy_type_name_resolves(self, task_id):
        with db() as c:
            t = c.execute("select Type from ScheduleTask where Id=?", (task_id,)).fetchone()[0]
        assert "TargetGroupEvaluatorTask" in t


class TestFullRun:
    def test_recreates_system_mappings_for_active_roles(self, admin, task_id, data):
        c0, c1, c2, other = data["customers"]
        a, b = data["roles"]["a"], data["roles"]["b"]
        add_stale(a, other, 1)   # stale system mapping must be deleted
        add_stale(a, c0, 0)      # manual mapping must remain
        r = run_task(admin, task_id)
        assert r.status_code in (200, 302)
        assert wait_for(lambda: len([m for m in mappings(a) if m[1] == 1]) == 3 and len(mappings(b)) == 3)
        sa = {m[0] for m in mappings(a) if m[1] == 1}
        assert sa == {c0, c1, c2}
        assert other not in {m[0] for m in mappings(a)}
        assert (c0, 0) in mappings(a)  # non-system mapping preserved
        assert {m[0] for m in mappings(b)} == {c0, c1, c2}
        assert all(m[1] == 1 for m in mappings(b))

    def test_inactive_ruleset_and_inactive_role_get_no_mappings(self, admin, task_id, data):
        a = data["roles"]["a"]
        run_task(admin, task_id)
        assert wait_for(lambda: len(mappings(a)) == 3)
        time.sleep(2)
        assert mappings(data["roles"]["inactive_rs"]) == []
        assert mappings(data["roles"]["inactive_role"]) == []


class TestFilteredRun:
    def test_apply_rules_only_touches_requested_role(self, admin, data):
        a, b = data["roles"]["a"], data["roles"]["b"]
        c0, c1, c2, other = data["customers"]
        add_stale(a, other, 1)
        add_stale(b, other, 1)
        r = apply_rules(admin, a)
        assert r.status_code in (200, 302)
        assert wait_for(lambda: {m[0] for m in mappings(a)} == {c0, c1, c2})
        time.sleep(2)
        # role b untouched: stale mapping still present, no new ones
        assert mappings(b) == [(other, 1)]

    def test_apply_rules_unknown_role_404(self, admin):
        page = admin.get(f"{BASE}/admin/customerrole/edit/1", timeout=120)
        r = admin.post(f"{BASE}/admin/customerrole/applyrules/99999999", data={"__RequestVerificationToken": token(page.text)},
                       timeout=60, allow_redirects=False)
        assert r.status_code == 404


class TestAuth:
    def test_runjob_anonymous_denied(self, task_id):
        r = requests.get(f"{BASE}/admin/scheduling/runjob/{task_id}", timeout=60, allow_redirects=False)
        assert r.status_code in (302, 401, 403)
