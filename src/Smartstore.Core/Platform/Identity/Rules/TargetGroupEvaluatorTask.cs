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

        // Delete existing system mappings.
        // Parity: legacy deletes (raw SQL) BEFORE and OUTSIDE the DbContextScope. We do the same.
        // EXCEPTION (technical): raw SQL "Delete From ..." is replaced by ExecuteDeleteAsync, which bypasses change tracking and hooks.
        var deleteQuery = _db.CustomerRoleMappings.Where(x => x.IsSystemMapping);
        if (ctx.Parameters.ContainsKey("CustomerRoleIds"))
        {
            roleIds = ctx.Parameters["CustomerRoleIds"].ToIntArray();
            deleteQuery = deleteQuery.Where(x => roleIds.Contains(x.CustomerRoleId));
        }

        numDeleted = await deleteQuery.ExecuteDeleteAsync(cancelToken);

        // Load roles incl. rule sets.
        // Parity: legacy used TableUntracked.Expand(RuleSets) (no tracking, eager load). AsNoTracking + Include/ThenInclude is the EF Core equivalent;
        // AsSplitQuery avoids a cartesian explosion (EXCEPTION: no EF6 equivalent, result set is identical).
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

        var roles = await rolesQuery.ToListAsync(cancelToken);
        rolesCount = roles.Count;

        // EXCEPTION (technical): legacy disabled hooks entirely (hooksEnabled: false). EF Core DbContextScope has no on/off switch, only a minimum
        // HookImportance. Audited: no Important/Essential-tier IDbSaveHook implementation (literal CustomerRoleMapping binding, or
        // generic BaseEntity/shared-interface binding) actually acts on CustomerRoleMapping inserts, so suppressing
        // only Normal-tier hooks here is safe. See TargetGroupEvaluatorTaskHookAuditTests for the regression guard.
        using (var scope = new DbContextScope(_db, autoDetectChanges: false, minHookImportance: HookImportance.Important, deferCommit: true))
        {
            foreach (var role in roles)
            {
                var ruleSetCustomerIds = new HashSet<int>();

                // Parity: same message text and timing as legacy (once per role, before rule evaluation). Legacy used sync SetProgress.
                await ctx.SetProgressAsync(++count, roles.Count, $"Add customer assignments for role \"{role.SystemName.NaIfEmpty()}\".");

                // Execute active rule sets and collect customer ids.
                foreach (var ruleSet in role.RuleSets.Where(x => x.IsActive))
                {
                    // Parity: legacy RETURNS (does not throw) on cancellation. Already deleted/committed mappings are kept and,
                    // like legacy, the ACL cache invalidation below is skipped on this early exit.
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

                    // Parity: legacy swallows detach exceptions (best-effort memory cleanup); kept intentionally.
                    try
                    {
                        scope.DbContext.DetachEntities<CustomerRoleMapping>();
                    }
                    catch
                    {
                    }
                }
            }
        }

        if (numAdded > 0 || numDeleted > 0)
        {
            await _cache.RemoveByPatternAsync(AclService.ACL_SEGMENT_PATTERN);
        }

        Debug.WriteLineIf(numDeleted > 0 || numAdded > 0, $"Deleted {numDeleted} and added {numAdded} customer assignments for {rolesCount} roles.");
    }
}