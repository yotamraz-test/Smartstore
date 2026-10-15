using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using Smartstore.Core.Bootstrapping;
using Smartstore.Core.Common.Hooks;
using Smartstore.Core.Catalog.Search;
using Smartstore.Core.Content.Media;
using Smartstore.Core.Data;
using Smartstore.Core.Identity;
using Smartstore.Core.Stores;
using Smartstore.Data.Hooks;

namespace Smartstore.Core.Tests.Platform.Identity;

/// <summary>
/// Regression guard for <see cref="Rules.TargetGroupEvaluatorTask.Run"/>, which bulk-inserts
/// <see cref="CustomerRoleMapping"/> rows inside a <c>DbContextScope</c> that only suppresses
/// Normal-tier hooks (<c>minHookImportance: HookImportance.Important</c>). Legacy SmartStoreNET
/// suppressed ALL hooks for this operation. The modern port is only behaviorally equivalent as
/// long as no Important/Essential-tier hook actually acts on CustomerRoleMapping inserts.
/// </summary>
[TestFixture]
public class TargetGroupEvaluatorTaskHookAuditTests
{
    /// <summary>
    /// Important/Essential-tier hooks that are bound to a type matching <see cref="CustomerRoleMapping"/>
    /// (directly, or via a shared base/interface such as <see cref="Smartstore.Domain.BaseEntity"/>), but
    /// that have been manually audited and verified to never actually act on CustomerRoleMapping inserts,
    /// because they explicitly filter on entity type/state and return <see cref="HookResult.Void"/> for
    /// CustomerRoleMapping:
    /// - <see cref="AssignedEntitiesHook"/>: only reacts to a hard-coded candidate-type set that does not
    ///   include CustomerRoleMapping.
    /// - <see cref="MediaTrackerHook"/>: only reacts to entity types with tracked media properties;
    ///   CustomerRoleMapping has none.
    /// - <see cref="SearchQueryAliasHook"/>: switches on a fixed set of catalog search/alias entity types
    ///   and falls through to Void for everything else, including CustomerRoleMapping.
    /// - <see cref="StoreContext"/>: only reacts to Store/Currency in its PostSave handler.
    ///
    /// If this test fails because a NEW hook type shows up here, re-audit it: either it is genuinely safe
    /// (then add it below with a short justification, mirroring the ones above), or it is a real behavioral
    /// parity gap against TargetGroupEvaluatorTask's hook suppression (then demote the hook's importance, or
    /// widen the DbContextScope suppression in TargetGroupEvaluatorTask.Run).
    /// </summary>
    private static readonly HashSet<Type> _knownSafeHooksForCustomerRoleMapping =
    [
        typeof(AssignedEntitiesHook),
        typeof(MediaTrackerHook),
        typeof(SearchQueryAliasHook),
        typeof(StoreContext)
    ];

    [Test]
    public void No_unvetted_Important_or_Essential_hook_targets_CustomerRoleMapping()
    {
        var entityType = typeof(CustomerRoleMapping);

        var hookTypes = typeof(SmartDbContext).Assembly
            .GetTypes()
            .Where(t => t is { IsClass: true, IsAbstract: false } && typeof(IDbSaveHook).IsAssignableFrom(t));

        var offenders = new List<string>();

        foreach (var hookType in hookTypes)
        {
            var importance = hookType.GetAttribute<ImportantAttribute>(false)?.Importance ?? HookImportance.Normal;
            if (importance < HookImportance.Important)
            {
                // Normal-tier hooks are suppressed by TargetGroupEvaluatorTask's DbContextScope anyway.
                continue;
            }

            var (_, hookedEntityType) = DbHooksModule.DiscoverHookTypes(hookType);
            if (!hookedEntityType.IsAssignableFrom(entityType))
            {
                // This hook is not even dispatched for CustomerRoleMapping entries.
                continue;
            }

            if (!_knownSafeHooksForCustomerRoleMapping.Contains(hookType))
            {
                offenders.Add($"{hookType.FullName} (Importance: {importance}, HookedType: {hookedEntityType.Name})");
            }
        }

        Assert.That(offenders, Is.Empty,
            "The following hook(s) are tagged Important/Essential and are bound to a type that matches " +
            $"{nameof(CustomerRoleMapping)}. TargetGroupEvaluatorTask.Run relies on the invariant that no such hook " +
            "actually acts on CustomerRoleMapping inserts, because its DbContextScope only suppresses Normal-tier " +
            $"hooks (legacy SmartStoreNET suppressed ALL hooks there). Audit the new hook: if it is safe (e.g. it " +
            $"filters out CustomerRoleMapping explicitly), add it to {nameof(_knownSafeHooksForCustomerRoleMapping)} " +
            "with a justification comment. Otherwise demote its importance or widen the suppression in " +
            $"TargetGroupEvaluatorTask.Run. Offenders: {string.Join(", ", offenders)}");
    }
}
