using System.Diagnostics;
using Smartstore.Caching;
using Smartstore.Core.Data;
using Smartstore.Core.Rules;
using Smartstore.Core.Rules.Filters;
using Smartstore.Core.Security;
using Smartstore.Data;
using Smartstore.Data.Hooks;
using Smartstore.Scheduling;

namespace Smartstore.Core.Identity.Rules;

public partial class TargetGroupEvaluatorTask(
    SmartDbContext db,
    ICacheManager cache,
    IRuleService ruleService,
    IRuleProviderFactory ruleProviderFactory) : ITask
{
    protected readonly SmartDbContext _db = db;
    protected readonly ICacheManager _cache = cache;
    protected readonly IRuleService _ruleService = ruleService;
    protected readonly ITargetGroupService _targetGroupService = ruleProviderFactory.GetProvider<ITargetGroupService>(RuleScope.Customer);

    public async Task Run(TaskExecutionContext ctx, CancellationToken cancelToken = default)
    {
        var count = 0;
        var numDeleted = 0;
        var numAdded = 0;
        var rolesCount = 0;

        int[] roleIds = null;

        // Parity with legacy: system mappings are deleted OUTSIDE (before) the DbContextScope.
        // ExecuteDeleteAsync bypasses change tracking and hooks (legacy used raw SQL as well).
        // Like legacy, no cancellation token is passed to the delete and role queries: cancellation
        // is only honored inside the loops below (return, not throw).
        var deleteQuery = _db.CustomerRoleMappings.Where(x => x.IsSystemMapping);
        if (ctx.Parameters.ContainsKey("CustomerRoleIds"))
        {
            roleIds = ctx.Parameters["CustomerRoleIds"].ToIntArray();
            deleteQuery = deleteQuery.Where(x => roleIds.Contains(x.CustomerRoleId));
        }

        numDeleted = await deleteQuery.ExecuteDeleteAsync(CancellationToken.None);

        // Legacy loaded roles (untracked, rule sets expanded) before opening the scope.
        // EF Core: AsNoTracking + AsSplitQuery with Include/ThenInclude replaces EF6 Expand().
        var rolesQuery = _db.CustomerRoles
            .Include(x => x.RuleSets)
            .ThenInclude(x => x.Rules)
            .AsNoTracking()
            .AsSplitQuery()
            .Where(x => x.Active && x.RuleSets.Any(y => y.IsActive));

        if (roleIds != null)
        {
            rolesQuery = rolesQuery.Where(x => roleIds.Contains(x.Id));
        }

        var roles = await rolesQuery.ToListAsync(CancellationToken.None);
        rolesCount = roles.Count;

        // EXCEPTION to legacy (hooksEnabled: false, validateOnSave: false, autoCommit: false):
        // EF Core has no switch to disable hooks entirely; HookImportance.Important suppresses only
        // Normal-tier hooks. Audited: no Important/Essential-tier IDbSaveHook acts on CustomerRoleMapping
        // inserts (see TargetGroupEvaluatorTaskHookAuditTests). deferCommit replaces autoCommit: false.
        using (var scope = new DbContextScope(_db, autoDetectChanges: false, minHookImportance: HookImportance.Important, deferCommit: true))
        {
            foreach (var role in roles)
            {
                var ruleSetCustomerIds = new HashSet<int>();

                await ctx.SetProgressAsync(++count, roles.Count, $"Add customer assignments for role \"{role.SystemName.NaIfEmpty()}\".");

                // Execute active rule sets and collect customer ids.
                foreach (var ruleSet in role.RuleSets.Where(x => x.IsActive))
                {
                    if (cancelToken.IsCancellationRequested)
                        return;

                    var expressionGroup = await _ruleService.CreateExpressionGroupAsync(ruleSet, _targetGroupService);
                    if (expressionGroup is FilterExpression expression)
                    {
                        var filterResult = _targetGroupService.ProcessFilter(expression, 0, 500);
                        var resultPager = new FastPager<Customer>(filterResult.SourceQuery, 500);

                        while ((await resultPager.ReadNextPageAsync(x => x.Id, x => x, cancelToken)).Out(out var customerIds))
                        {
                            ruleSetCustomerIds.AddRange(customerIds);
                        }
                    }
                }

                // Add mappings.
                if (ruleSetCustomerIds.Any())
                {
                    foreach (var chunk in ruleSetCustomerIds.Chunk(500))
                    {
                        if (cancelToken.IsCancellationRequested)
                            return;

                        foreach (var customerId in chunk)
                        {
                            _db.CustomerRoleMappings.Add(new CustomerRoleMapping
                            {
                                CustomerId = customerId,
                                CustomerRoleId = role.Id,
                                IsSystemMapping = true
                            });

                            ++numAdded;
                        }

                        await scope.CommitAsync(cancelToken);
                    }

                    try
                    {
                        scope.DbContext.DetachEntities<CustomerRoleMapping>();
                    }
                    catch
                    {
                        // Legacy parity: detaching is best-effort; failures are intentionally swallowed.
                    }
                }
            }
        }

        // Cancellation returns from the loops above (as in legacy) and therefore skips this
        // invalidation, even if rows were already deleted/committed. Legacy behaves identically.
        if (numAdded > 0 || numDeleted > 0)
        {
            await _cache.RemoveByPatternAsync(AclService.ACL_SEGMENT_PATTERN);
        }

        Debug.WriteLineIf(numDeleted > 0 || numAdded > 0, $"Deleted {numDeleted} and added {numAdded} customer assignments for {rolesCount} roles.");
    }
}