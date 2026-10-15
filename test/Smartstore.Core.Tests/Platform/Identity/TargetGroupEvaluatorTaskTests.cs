using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Moq;
using NUnit.Framework;
using Smartstore.Caching;
using Smartstore.Core.Common;
using Smartstore.Core.Common.Services;
using Smartstore.Core.Identity;
using Smartstore.Core.Identity.Rules;
using Smartstore.Core.Localization;
using Smartstore.Core.Rules;
using Smartstore.Core.Rules.Rendering;
using Smartstore.Core.Stores;
using Smartstore.Scheduling;
using Smartstore.Threading;

namespace Smartstore.Core.Tests.Platform.Identity;

/// <summary>
/// Full parity test matrix for <see cref="TargetGroupEvaluatorTask.Run(TaskExecutionContext, CancellationToken)"/>.
/// Uses the SQLite-backed <see cref="TargetGroupEvaluatorTaskTestsBase"/> harness (not the EF Core InMemory
/// provider) because the task relies on <c>ExecuteDeleteAsync</c>. Collaborators that are peripheral to the
/// behavior under test (<see cref="ICacheManager"/>, <see cref="ITaskStore"/>, etc.) are mocked; the rule
/// engine itself (<see cref="IRuleService"/>/<see cref="ITargetGroupService"/>) is real, so that rule
/// evaluation against seeded <see cref="RuleSetEntity"/>/<see cref="RuleEntity"/> rows is genuinely exercised.
/// </summary>
[TestFixture]
public class TargetGroupEvaluatorTaskTests : TargetGroupEvaluatorTaskTestsBase
{
    [TearDown]
    public async Task TearDown()
    {
        // The base fixture shares a single SmartDbContext across all tests in this fixture
        // ([OneTimeSetUp]/[OneTimeTearDown]), so each test must leave the database clean to stay independent.
        await DbContext.CustomerRoleMappings.ExecuteDeleteAsync();
        await DbContext.Rules.ExecuteDeleteAsync();
        await DbContext.RuleSets.ExecuteDeleteAsync();
        await DbContext.CustomerRoles.ExecuteDeleteAsync();
        await DbContext.Customers.ExecuteDeleteAsync();
        DbContext.ChangeTracker.Clear();
    }

    #region Seeding helpers

    private async Task<CustomerRole> CreateRoleAsync(string systemName, bool active = true)
    {
        var role = new CustomerRole
        {
            Name = systemName,
            SystemName = systemName,
            Active = active
        };

        DbContext.CustomerRoles.Add(role);
        await DbContext.SaveChangesAsync();

        return role;
    }

    private async Task<RuleSetEntity> CreateRuleSetAsync(CustomerRole role, bool isActive = true, bool isSubGroup = false)
    {
        var ruleSet = new RuleSetEntity
        {
            Name = $"RuleSet for {role.SystemName}",
            IsActive = isActive,
            IsSubGroup = isSubGroup,
            Scope = RuleScope.Customer,
            LogicalOperator = LogicalRuleOperator.And
        };

        DbContext.RuleSets.Add(ruleSet);
        await DbContext.SaveChangesAsync();

        if (!isSubGroup)
        {
            role.RuleSets.Add(ruleSet);
            await DbContext.SaveChangesAsync();
        }

        return ruleSet;
    }

    private async Task AddLeafRuleAsync(RuleSetEntity ruleSet, string ruleType, string op, string value)
    {
        DbContext.Rules.Add(new RuleEntity
        {
            RuleSetId = ruleSet.Id,
            RuleType = ruleType,
            Operator = op,
            Value = value
        });

        await DbContext.SaveChangesAsync();
    }

    private async Task AddGroupRuleAsync(RuleSetEntity parentRuleSet, RuleSetEntity nestedRuleSet)
    {
        DbContext.Rules.Add(new RuleEntity
        {
            RuleSetId = parentRuleSet.Id,
            RuleType = "Group",
            Operator = RuleOperator.IsEqualTo.Operator,
            Value = nestedRuleSet.Id.ToString()
        });

        await DbContext.SaveChangesAsync();
    }

    private async Task<Customer> CreateCustomerAsync(string customerNumber, bool active = true)
    {
        var customer = new Customer
        {
            CustomerNumber = customerNumber,
            Active = active,
            CreatedOnUtc = DateTime.UtcNow
        };

        DbContext.Customers.Add(customer);
        await DbContext.SaveChangesAsync();

        return customer;
    }

    /// <summary>
    /// Bulk-creates <paramref name="count"/> customers, all matching the "Active = true" filter, without the
    /// per-row round-trip of <see cref="CreateCustomerAsync"/> (needed for the chunking/cancellation tests,
    /// which seed hundreds/thousands of rows).
    /// </summary>
    private async Task<List<int>> CreateCustomersAsync(int count, string numberPrefix)
    {
        var customers = new List<Customer>(count);
        for (var i = 0; i < count; i++)
        {
            customers.Add(new Customer
            {
                CustomerNumber = $"{numberPrefix}-{i}",
                Active = true,
                CreatedOnUtc = DateTime.UtcNow
            });
        }

        DbContext.Customers.AddRange(customers);
        await DbContext.SaveChangesAsync();

        return customers.Select(x => x.Id).ToList();
    }

    #endregion

    #region Collaborator / context construction

    private sealed class TaskHarness
    {
        public TargetGroupEvaluatorTask Task;
        public TaskExecutionContext Context;
        public Mock<ICacheManager> CacheManagerMock;
        public Mock<ITaskStore> TaskStoreMock;
        public List<(string Message, int Value, int Max)> ProgressCalls;
    }

    /// <summary>
    /// Builds a real <see cref="TargetGroupEvaluatorTask"/> wired to the shared SQLite <see cref="DbContext"/>,
    /// a real <see cref="RuleService"/>/<see cref="TargetGroupService"/> rule-evaluation pipeline, and mocked
    /// peripheral collaborators (cache, task store, async state, component context, http context).
    /// </summary>
    private TaskHarness CreateHarness()
    {
        var workContextMock = new Mock<IWorkContext>();

        var ruleService = new RuleService(
            DbContext,
            workContextMock.Object,
            new Lazy<IEnumerable<IRuleOptionsProvider>>(() => Array.Empty<IRuleOptionsProvider>()));

        var storeContextMock = new Mock<IStoreContext>();
        storeContextMock.Setup(x => x.GetCachedStores()).Returns(new StoreEntityCache(new List<Store>()));

        var localizationServiceMock = new Mock<ILocalizationService>();

        var currencyServiceMock = new Mock<ICurrencyService>();
        currencyServiceMock.Setup(x => x.PrimaryCurrency).Returns(new Currency { CurrencyCode = "USD" });

        var targetGroupService = new TargetGroupService(
            DbContext,
            ruleService,
            storeContextMock.Object,
            localizationServiceMock.Object,
            currencyServiceMock.Object);

        var ruleProviderFactoryMock = new Mock<IRuleProviderFactory>();
        ruleProviderFactoryMock
            .Setup(x => x.GetProvider(RuleScope.Customer, It.IsAny<object>()))
            .Returns(targetGroupService);

        var cacheManagerMock = new Mock<ICacheManager>();
        cacheManagerMock
            .Setup(x => x.RemoveByPatternAsync(It.IsAny<string>()))
            .ReturnsAsync(0L);

        var task = new TargetGroupEvaluatorTask(
            DbContext,
            cacheManagerMock.Object,
            ruleService,
            ruleProviderFactoryMock.Object);

        var progressCalls = new List<(string Message, int Value, int Max)>();

        var taskStoreMock = new Mock<ITaskStore>();
        taskStoreMock
            .Setup(x => x.UpdateExecutionInfoAsync(It.IsAny<TaskExecutionInfo>()))
            .Callback<TaskExecutionInfo>(info => progressCalls.Add((info.ProgressMessage, info.ProgressPercent.GetValueOrDefault(), 100)))
            .Returns(Task.CompletedTask);

        var executionInfo = new TaskExecutionInfo
        {
            Task = new TaskDescriptor { Type = typeof(TargetGroupEvaluatorTask).AssemblyQualifiedName }
        };

        var context = new TaskExecutionContext(
            taskStoreMock.Object,
            new Mock<IAsyncState>().Object,
            new DefaultHttpContext(),
            new Mock<Autofac.IComponentContext>().Object,
            executionInfo);

        return new TaskHarness
        {
            Task = task,
            Context = context,
            CacheManagerMock = cacheManagerMock,
            TaskStoreMock = taskStoreMock,
            ProgressCalls = progressCalls
        };
    }

    #endregion

    // ------------------------------------------------------------------------------------------------------
    // 1. Delete scoping.
    // ------------------------------------------------------------------------------------------------------

    [Test]
    public async Task Run_deletes_all_system_mappings_for_full_run_but_only_scoped_role_for_CustomerRoleIds_param()
    {
        var roleA = await CreateRoleAsync("RoleA", active: false); // Inactive: left out of the evaluation, mappings seeded directly.
        var roleB = await CreateRoleAsync("RoleB", active: false);

        var customer1 = await CreateCustomerAsync("DEL-1");
        var customer2 = await CreateCustomerAsync("DEL-2");

        DbContext.CustomerRoleMappings.Add(new CustomerRoleMapping { CustomerId = customer1.Id, CustomerRoleId = roleA.Id, IsSystemMapping = true });
        DbContext.CustomerRoleMappings.Add(new CustomerRoleMapping { CustomerId = customer2.Id, CustomerRoleId = roleB.Id, IsSystemMapping = true });
        // A non-system mapping must never be touched by the delete step, regardless of scoping.
        DbContext.CustomerRoleMappings.Add(new CustomerRoleMapping { CustomerId = customer1.Id, CustomerRoleId = roleB.Id, IsSystemMapping = false });
        await DbContext.SaveChangesAsync();

        // Scoped run: only RoleA's system mappings should be deleted.
        var harnessScoped = CreateHarness();
        harnessScoped.Context.Parameters["CustomerRoleIds"] = roleA.Id.ToString();

        await harnessScoped.Task.Run(harnessScoped.Context);

        var remainingAfterScopedRun = await DbContext.CustomerRoleMappings.AsNoTracking().ToListAsync();
        Assert.That(remainingAfterScopedRun.Any(x => x.CustomerRoleId == roleA.Id && x.IsSystemMapping), Is.False);
        Assert.That(remainingAfterScopedRun.Any(x => x.CustomerRoleId == roleB.Id && x.IsSystemMapping), Is.True);
        Assert.That(remainingAfterScopedRun.Any(x => x.CustomerRoleId == roleB.Id && !x.IsSystemMapping), Is.True);

        // Full run: all remaining system mappings (RoleB's) should now be deleted too; the non-system mapping survives.
        var harnessFull = CreateHarness();
        await harnessFull.Task.Run(harnessFull.Context);

        var remainingAfterFullRun = await DbContext.CustomerRoleMappings.AsNoTracking().ToListAsync();
        Assert.That(remainingAfterFullRun.Any(x => x.IsSystemMapping), Is.False);
        Assert.That(remainingAfterFullRun.Any(x => x.CustomerRoleId == roleB.Id && !x.IsSystemMapping), Is.True);
    }

    // ------------------------------------------------------------------------------------------------------
    // 2. Active/inactive filtering.
    // ------------------------------------------------------------------------------------------------------

    [Test]
    public async Task Run_skips_inactive_roles_and_roles_with_only_inactive_rule_sets()
    {
        var customer = await CreateCustomerAsync("FLT-1");

        var inactiveRole = await CreateRoleAsync("InactiveRole", active: false);
        var inactiveRoleRuleSet = await CreateRuleSetAsync(inactiveRole, isActive: true);
        await AddLeafRuleAsync(inactiveRoleRuleSet, "CustomerNumber", RuleOperator.IsEqualTo.Operator, customer.CustomerNumber);

        var roleWithOnlyInactiveRuleSets = await CreateRoleAsync("OnlyInactiveRuleSets", active: true);
        var onlyInactiveRuleSet = await CreateRuleSetAsync(roleWithOnlyInactiveRuleSets, isActive: false);
        await AddLeafRuleAsync(onlyInactiveRuleSet, "CustomerNumber", RuleOperator.IsEqualTo.Operator, customer.CustomerNumber);

        var eligibleRole = await CreateRoleAsync("EligibleRole", active: true);
        var eligibleRuleSet = await CreateRuleSetAsync(eligibleRole, isActive: true);
        await AddLeafRuleAsync(eligibleRuleSet, "CustomerNumber", RuleOperator.IsEqualTo.Operator, customer.CustomerNumber);

        var harness = CreateHarness();
        await harness.Task.Run(harness.Context);

        // Only the eligible role should ever be reported as processed.
        Assert.That(harness.ProgressCalls, Has.Count.EqualTo(1));
        Assert.That(harness.ProgressCalls[0].Message, Is.EqualTo($"Add customer assignments for role \"{eligibleRole.SystemName}\"."));

        var mappings = await DbContext.CustomerRoleMappings.AsNoTracking().ToListAsync();
        Assert.That(mappings, Has.Count.EqualTo(1));
        Assert.That(mappings[0].CustomerRoleId, Is.EqualTo(eligibleRole.Id));
        Assert.That(mappings[0].CustomerId, Is.EqualTo(customer.Id));
    }

    // ------------------------------------------------------------------------------------------------------
    // 3. Eager-loading correctness (zero rule sets / one rule set / multiple + nested group rule sets).
    // ------------------------------------------------------------------------------------------------------

    [Test]
    public async Task Run_produces_correct_mappings_for_roles_with_zero_one_and_nested_rule_sets()
    {
        // Role A: active, but zero rule sets -> excluded entirely by the task's own roles query, no churn.
        var roleA = await CreateRoleAsync("RoleA_NoRuleSets", active: true);

        // Role B: one active rule set with one leaf rule, matches exactly one customer.
        var roleB = await CreateRoleAsync("RoleB_OneRuleSet", active: true);
        var customerB = await CreateCustomerAsync("B1");
        var ruleSetB = await CreateRuleSetAsync(roleB);
        await AddLeafRuleAsync(ruleSetB, "CustomerNumber", RuleOperator.IsEqualTo.Operator, customerB.CustomerNumber);

        // Role C: multiple active rule sets - one simple leaf rule set, and one containing a nested "Group" rule
        // that references a sub rule set. Exercises RuleService's nested DB round-trip for group rules.
        var roleC = await CreateRoleAsync("RoleC_NestedRuleSets", active: true);
        var customerCDirect = await CreateCustomerAsync("C-direct");
        var customerCNested = await CreateCustomerAsync("C-nested");

        var ruleSetCSimple = await CreateRuleSetAsync(roleC);
        await AddLeafRuleAsync(ruleSetCSimple, "CustomerNumber", RuleOperator.IsEqualTo.Operator, customerCDirect.CustomerNumber);

        var ruleSetCGroupParent = await CreateRuleSetAsync(roleC);
        var nestedSubRuleSet = await CreateRuleSetAsync(roleC, isActive: true, isSubGroup: true);
        await AddLeafRuleAsync(nestedSubRuleSet, "CustomerNumber", RuleOperator.IsEqualTo.Operator, customerCNested.CustomerNumber);
        await AddGroupRuleAsync(ruleSetCGroupParent, nestedSubRuleSet);

        var harness = CreateHarness();
        await harness.Task.Run(harness.Context);

        Assert.That(harness.ProgressCalls.Any(x => x.Message.Contains(roleA.SystemName)), Is.False);

        var mappings = await DbContext.CustomerRoleMappings.AsNoTracking().ToListAsync();

        Assert.That(mappings.Any(x => x.CustomerRoleId == roleA.Id), Is.False);

        var roleBMappings = mappings.Where(x => x.CustomerRoleId == roleB.Id).ToList();
        Assert.That(roleBMappings, Has.Count.EqualTo(1));
        Assert.That(roleBMappings[0].CustomerId, Is.EqualTo(customerB.Id));

        var roleCMappings = mappings.Where(x => x.CustomerRoleId == roleC.Id).Select(x => x.CustomerId).OrderBy(x => x).ToList();
        Assert.That(roleCMappings, Is.EqualTo(new[] { customerCDirect.Id, customerCNested.Id }.OrderBy(x => x)));
    }

    // ------------------------------------------------------------------------------------------------------
    // 4. Chunking boundary (exactly 500 / exactly 501 matching customers).
    // ------------------------------------------------------------------------------------------------------

    [TestCase(500)]
    [TestCase(501)]
    public async Task Run_creates_correct_mapping_count_at_chunk_boundary(int customerCount)
    {
        var role = await CreateRoleAsync($"ChunkRole_{customerCount}", active: true);
        var ruleSet = await CreateRuleSetAsync(role);
        await AddLeafRuleAsync(ruleSet, "Active", RuleOperator.IsEqualTo.Operator, "true");

        var customerIds = await CreateCustomersAsync(customerCount, $"CHUNK-{customerCount}");

        var harness = CreateHarness();
        await harness.Task.Run(harness.Context);

        var mappedCustomerIds = await DbContext.CustomerRoleMappings
            .AsNoTracking()
            .Where(x => x.CustomerRoleId == role.Id)
            .Select(x => x.CustomerId)
            .ToListAsync();

        Assert.That(mappedCustomerIds, Has.Count.EqualTo(customerCount));
        Assert.That(mappedCustomerIds.OrderBy(x => x), Is.EqualTo(customerIds.OrderBy(x => x)));
    }

    // ------------------------------------------------------------------------------------------------------
    // 5. Mid-loop cancellation.
    // ------------------------------------------------------------------------------------------------------

    [Test]
    public async Task Run_stops_early_on_cancellation_and_does_not_persist_uncommitted_chunks()
    {
        const int customerCount = 1001; // Spans three chunks of 500/500/1.

        var role = await CreateRoleAsync("CancelRole", active: true);
        var ruleSet = await CreateRuleSetAsync(role);
        await AddLeafRuleAsync(ruleSet, "Active", RuleOperator.IsEqualTo.Operator, "true");

        await CreateCustomersAsync(customerCount, "CANCEL");

        var harness = CreateHarness();

        using var cts = new CancellationTokenSource();

        void OnSavedChanges(object sender, EventArgs e)
        {
            // Cancel right after the first chunk's commit lands, to force a genuine mid-loop early return
            // on the SECOND chunk's cancellation check, rather than relying only on a pre-cancelled token.
            cts.Cancel();
        }

        DbContext.SavedChanges += OnSavedChanges;
        try
        {
            await harness.Task.Run(harness.Context, cts.Token);
        }
        finally
        {
            DbContext.SavedChanges -= OnSavedChanges;
        }

        var mappedCount = await DbContext.CustomerRoleMappings
            .AsNoTracking()
            .CountAsync(x => x.CustomerRoleId == role.Id);

        // Only the first chunk (500 rows) was committed before cancellation was observed; no partial/uncommitted
        // second chunk leaked through, and the loop did not continue to a third chunk.
        Assert.That(mappedCount, Is.EqualTo(500));
    }

    [Test]
    public async Task Run_does_not_persist_anything_when_token_is_already_cancelled()
    {
        // EF Core's own cancellation-token handling kicks in on the very first awaited database call
        // (the bulk delete), before TargetGroupEvaluatorTask.Run reaches any of its own manual
        // "cancelToken.IsCancellationRequested" checkpoints, so an already-cancelled token surfaces as a
        // thrown OperationCanceledException/TaskCanceledException rather than a silent early return. This
        // is the standard, framework-supported cancellation signal for ITask.Run: TaskExecutor.ExecuteAsync
        // explicitly catches "ex is OperationCanceledException" around the "await job.Run(ctx, cts.Token)"
        // call and treats it as a normal cancellation outcome, not a failure (see
        // Smartstore/src/Smartstore/Scheduling/Services/TaskExecutor.cs). The important invariant for this
        // test is therefore not "no exception", but "no partial/uncommitted state was persisted".
        var role = await CreateRoleAsync("PreCancelledRole", active: true);
        var ruleSet = await CreateRuleSetAsync(role);
        await AddLeafRuleAsync(ruleSet, "Active", RuleOperator.IsEqualTo.Operator, "true");

        await CreateCustomersAsync(10, "PRECANCEL");

        var harness = CreateHarness();

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        Assert.ThrowsAsync<TaskCanceledException>(async () => await harness.Task.Run(harness.Context, cts.Token));

        var mappedCount = await DbContext.CustomerRoleMappings
            .AsNoTracking()
            .CountAsync(x => x.CustomerRoleId == role.Id);

        Assert.That(mappedCount, Is.EqualTo(0));
    }

    // ------------------------------------------------------------------------------------------------------
    // 6. Cache invalidation guard.
    // ------------------------------------------------------------------------------------------------------

    [Test]
    public async Task Run_invalidates_Acl_cache_only_when_something_was_added_or_deleted()
    {
        // Case 1: numAdded > 0 -> cache must be invalidated.
        var role = await CreateRoleAsync("CacheRole", active: true);
        var ruleSet = await CreateRuleSetAsync(role);
        var customer = await CreateCustomerAsync("CACHE-1");
        await AddLeafRuleAsync(ruleSet, "CustomerNumber", RuleOperator.IsEqualTo.Operator, customer.CustomerNumber);

        var harnessWithChanges = CreateHarness();
        await harnessWithChanges.Task.Run(harnessWithChanges.Context);

        harnessWithChanges.CacheManagerMock.Verify(
            x => x.RemoveByPatternAsync(Smartstore.Core.Security.AclService.ACL_SEGMENT_PATTERN),
            Times.Once);

        // Case 2: nothing to delete, nothing matches -> cache must NOT be invalidated. Scope this run to
        // "noOpRole" via the "CustomerRoleIds" parameter so it does not also re-process "CacheRole" from
        // Case 1 above (a full, unscoped run would re-evaluate every active role, including "CacheRole",
        // whose rule still matches the still-existing "CACHE-1" customer and would add a mapping again).
        var noOpRole = await CreateRoleAsync("NoOpRole", active: true);
        var noOpRuleSet = await CreateRuleSetAsync(noOpRole);
        await AddLeafRuleAsync(noOpRuleSet, "CustomerNumber", RuleOperator.IsEqualTo.Operator, "NO-SUCH-CUSTOMER");

        var harnessNoOp = CreateHarness();
        harnessNoOp.Context.Parameters["CustomerRoleIds"] = noOpRole.Id.ToString();
        await harnessNoOp.Task.Run(harnessNoOp.Context);

        harnessNoOp.CacheManagerMock.Verify(
            x => x.RemoveByPatternAsync(It.IsAny<string>()),
            Times.Never);
    }

    // ------------------------------------------------------------------------------------------------------
    // 7. Progress reporting shape.
    // ------------------------------------------------------------------------------------------------------

    [Test]
    public async Task Run_reports_progress_exactly_once_per_processed_role_with_expected_message()
    {
        var role1 = await CreateRoleAsync("ProgressRole1", active: true);
        var ruleSet1 = await CreateRuleSetAsync(role1);
        var customer1 = await CreateCustomerAsync("PROG-1");
        await AddLeafRuleAsync(ruleSet1, "CustomerNumber", RuleOperator.IsEqualTo.Operator, customer1.CustomerNumber);

        var role2 = await CreateRoleAsync("ProgressRole2", active: true);
        var ruleSet2 = await CreateRuleSetAsync(role2);
        var customer2 = await CreateCustomerAsync("PROG-2");
        await AddLeafRuleAsync(ruleSet2, "CustomerNumber", RuleOperator.IsEqualTo.Operator, customer2.CustomerNumber);

        var harness = CreateHarness();
        await harness.Task.Run(harness.Context);

        Assert.That(harness.ProgressCalls, Has.Count.EqualTo(2));

        Assert.That(harness.ProgressCalls.Any(x => x.Message == $"Add customer assignments for role \"{role1.SystemName}\"."), Is.True);
        Assert.That(harness.ProgressCalls.Any(x => x.Message == $"Add customer assignments for role \"{role2.SystemName}\"."), Is.True);

        // Each progress message must only be reported once for its role (no duplicate calls per role).
        Assert.That(harness.ProgressCalls.Count(x => x.Message.Contains(role1.SystemName)), Is.EqualTo(1));
        Assert.That(harness.ProgressCalls.Count(x => x.Message.Contains(role2.SystemName)), Is.EqualTo(1));
    }
}
