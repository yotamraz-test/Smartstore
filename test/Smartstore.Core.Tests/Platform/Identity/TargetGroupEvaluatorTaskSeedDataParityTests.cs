using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using Smartstore.Core.Identity.Rules;
using Smartstore.Core.Installation;
using Smartstore.Scheduling;

namespace Smartstore.Core.Tests.Platform.Identity;

[TestFixture]
public class TargetGroupEvaluatorTaskSeedDataParityTests
{
    private const string LegacyName = "Update assignments of customers to customer roles";
    private const string LegacyCronExpression = "15 2 * * *";
    private const bool LegacyEnabled = true;
    private const bool LegacyStopOnError = false;
    private const string LegacyGermanName = "Zuordnungen von Kunden zu Kundengruppen aktualisieren";
    private const string LegacyAssemblyQualifiedTypeName = "SmartStore.Services.Customers.TargetGroupEvaluatorTask, SmartStore.Services";

    [Test]
    public void Invariant_seed_data_TaskDescriptor_matches_legacy_fields()
    {
        var descriptor = new EnUSSeedData().TaskDescriptors()
            .Single(x => x.Type == nameof(TargetGroupEvaluatorTask));

        Assert.That(descriptor.Name, Is.EqualTo(LegacyName));
        Assert.That(descriptor.CronExpression, Is.EqualTo(LegacyCronExpression));
        Assert.That(descriptor.Enabled, Is.EqualTo(LegacyEnabled));
        Assert.That(descriptor.StopOnError, Is.EqualTo(LegacyStopOnError));
    }

    [Test]
    public void DeDE_seed_data_overrides_only_Name_and_keeps_cron_Enabled_StopOnError()
    {
        var descriptor = new DeDESeedData().TaskDescriptors()
            .Single(x => x.Type == nameof(TargetGroupEvaluatorTask));

        Assert.That(descriptor.Name, Is.EqualTo(LegacyGermanName));
        Assert.That(descriptor.CronExpression, Is.EqualTo(LegacyCronExpression));
        Assert.That(descriptor.Enabled, Is.EqualTo(LegacyEnabled));
        Assert.That(descriptor.StopOnError, Is.EqualTo(LegacyStopOnError));
    }

    [Test]
    public void DbTaskStore_legacy_type_alias_matches_legacy_assembly_qualified_name()
    {
        var field = typeof(DbTaskStore).GetField("_legacyTypeNamesMap", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.That(field, Is.Not.Null,
            "DbTaskStore._legacyTypeNamesMap not found. If it was renamed, update this test to match.");

        var map = (Dictionary<string, string>)field.GetValue(null);

        Assert.That(map.TryGetValue(nameof(TargetGroupEvaluatorTask), out var legacyTypeName), Is.True,
            $"DbTaskStore._legacyTypeNamesMap is missing an entry for {nameof(TargetGroupEvaluatorTask)}.");
        Assert.That(legacyTypeName, Is.EqualTo(LegacyAssemblyQualifiedTypeName));
    }
}
