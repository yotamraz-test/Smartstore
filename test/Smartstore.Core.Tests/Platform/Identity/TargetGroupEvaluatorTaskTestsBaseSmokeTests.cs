using System.Linq;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using NUnit.Framework;
using Smartstore.Core.Common;
using Smartstore.Core.Identity;
using Smartstore.Test.Common;

namespace Smartstore.Core.Tests.Platform.Identity;

/// <summary>
/// Smoke test proving that <see cref="TargetGroupEvaluatorTaskTestsBase"/> provides a working SQLite
/// in-memory <see cref="Smartstore.Core.Data.SmartDbContext"/>. In particular it exercises
/// <c>ExecuteDeleteAsync</c>, which is the capability the EF Core InMemory provider (used by the shared
/// <see cref="Smartstore.Test.Common.TestDbFactory"/> fixture) does not support and throws on. This is
/// deliberately just a smoke test; the full parity test matrix for
/// <see cref="Rules.TargetGroupEvaluatorTask"/> is a separate, later task.
/// </summary>
[TestFixture]
public class TargetGroupEvaluatorTaskTestsBaseSmokeTests : TargetGroupEvaluatorTaskTestsBase
{
    [Test]
    public async Task Can_insert_query_and_bulk_delete_GenericAttribute_via_ExecuteDeleteAsync()
    {
        const string keyGroup = nameof(Customer);
        const string key = "TargetGroupEvaluatorTaskTestsBaseSmokeTests.Key";

        DbContext.GenericAttributes.Add(new GenericAttribute
        {
            EntityId = 1,
            KeyGroup = keyGroup,
            Key = key,
            Value = "SmokeTestValue"
        });

        await DbContext.SaveChangesAsync();

        var inserted = await DbContext.GenericAttributes
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.KeyGroup == keyGroup && x.Key == key);

        inserted.ShouldNotBeNull();
        inserted.Value.ShouldEqual("SmokeTestValue");

        // This is exactly the capability the EF Core InMemory provider lacks (it throws on
        // ExecuteDeleteAsync/ExecuteUpdateAsync). Proving it works here is the whole point of this harness.
        var numDeleted = await DbContext.GenericAttributes
            .Where(x => x.KeyGroup == keyGroup && x.Key == key)
            .ExecuteDeleteAsync();

        numDeleted.ShouldEqual(1);

        var stillExists = await DbContext.GenericAttributes
            .AnyAsync(x => x.KeyGroup == keyGroup && x.Key == key);

        stillExists.ShouldBeFalse();
    }
}
