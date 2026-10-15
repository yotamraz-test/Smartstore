using System;
using System.Data.Common;
using System.Threading.Tasks;
using Autofac;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Moq;
using NUnit.Framework;
using Smartstore.Core.Data;
using Smartstore.Data;
using Smartstore.Data.Providers;
using Smartstore.Engine;

namespace Smartstore.Core.Tests.Platform.Identity;

/// <summary>
/// Minimal <see cref="DbFactory"/> implementation that configures <see cref="SmartDbContext"/> to use
/// a SQLite database over an already-open <see cref="DbConnection"/>. Pattern-matched after
/// <see cref="Smartstore.Test.Common.TestDbFactory"/>, but targets the real SQLite relational provider
/// instead of the EF Core InMemory provider, because InMemory does not support
/// <c>ExecuteDeleteAsync</c>/<c>ExecuteUpdateAsync</c>, which <see cref="Rules.TargetGroupEvaluatorTask"/>
/// relies on.
/// </summary>
public class TargetGroupEvaluatorTaskTestSqliteDbFactory : DbFactory
{
    private readonly DbConnection _connection;

    public TargetGroupEvaluatorTaskTestSqliteDbFactory(DbConnection connection)
    {
        Guard.NotNull(connection, nameof(connection));
        _connection = connection;
    }

    public override DbSystemType DbSystem
        => DbSystemType.SQLite;

    public override DbConnectionStringBuilder CreateConnectionStringBuilder(string connectionString)
        => throw new NotImplementedException();

    public override DbConnectionStringBuilder CreateConnectionStringBuilder(
        string server,
        string database,
        string userName,
        string password)
        => throw new NotImplementedException();

    public override DataProvider CreateDataProvider(DatabaseFacade database)
        => new TargetGroupEvaluatorTaskTestSqliteDataProvider(database);

    public override TContext CreateDbContext<TContext>(string connectionString, int? commandTimeout = null)
    {
        var optionsBuilder = new DbContextOptionsBuilder<TContext>();
        ConfigureDbContext(optionsBuilder, connectionString);

        return (TContext)Activator.CreateInstance(typeof(TContext), new object[] { optionsBuilder.Options });
    }

    public override DbContextOptionsBuilder ConfigureDbContext(DbContextOptionsBuilder builder, string connectionString)
        => builder.UseSqlite(_connection);
}

/// <summary>
/// Minimal <see cref="DataProvider"/> implementation for the SQLite in-memory test harness.
/// Pattern-matched after <see cref="Smartstore.Test.Common.TestDataProvider"/>.
/// </summary>
public class TargetGroupEvaluatorTaskTestSqliteDataProvider : DataProvider
{
    public TargetGroupEvaluatorTaskTestSqliteDataProvider(DatabaseFacade database)
        : base(database)
    {
    }

    public override DbSystemType ProviderType
        => DbSystemType.SQLite;

    public override bool MARSEnabled
        => false;

    public override string ApplyPaging(string sql, int skip, int take)
        => sql;

    public override DbParameter CreateParameter()
        => throw new NotImplementedException();

    public override string EncloseIdentifier(string identifier)
        => identifier;

    public override bool IsTransientException(Exception ex)
        => false;

    public override bool IsUniquenessViolationException(DbUpdateException ex)
        => false;

    protected override ValueTask<string[]> GetTableNamesCore(bool async)
        => ValueTask.FromResult(Array.Empty<string>());

    protected override ValueTask<bool> HasColumnCore(string tableName, string columnName, bool async)
        => ValueTask.FromResult(false);

    protected override ValueTask<bool> HasDatabaseCore(string databaseName, bool async)
        => ValueTask.FromResult(false);

    protected override ValueTask<bool> HasTableCore(string tableName, bool async)
        => ValueTask.FromResult(false);

    protected override Task<int> InsertIntoCore(string sql, bool async, params object[] parameters)
        => Task.FromResult(0);

    protected override Task<int> TruncateTableCore(string tableName, bool async)
        => Task.FromResult(0);
}

/// <summary>
/// Base fixture for <see cref="Rules.TargetGroupEvaluatorTask"/> tests that need a real relational
/// database rather than the EF Core InMemory provider used by the shared <c>ServiceTestBase</c>/
/// <see cref="Smartstore.Test.Common.TestDbFactory"/> fixture. InMemory throws on
/// <c>ExecuteDeleteAsync</c>/<c>ExecuteUpdateAsync</c>, which the task's delete step uses, so parity
/// tests for that task need a provider that actually supports it.
/// </summary>
/// <remarks>
/// This harness is completely independent of <see cref="Smartstore.Test.Common.TestDbFactory"/> and the
/// shared <c>ServiceTestBase</c> host/Autofac bootstrap. It builds its own, minimal
/// <see cref="DbContextOptions{TContext}"/> for <see cref="SmartDbContext"/> backed by a SQLite
/// in-memory database (<c>DataSource=:memory:</c>) over a single <see cref="SqliteConnection"/> that is
/// opened once in <see cref="BaseOneTimeSetUp"/> and kept open for the lifetime of the fixture, because a
/// SQLite in-memory database is destroyed as soon as the connection that created it is closed. The schema
/// is created via <see cref="DatabaseFacade.EnsureCreated"/>.
///
/// <see cref="SmartDbContext.OnModelCreating(ModelBuilder)"/> unconditionally dereferences the
/// <see cref="DbFactoryOptionsExtension"/> that <c>UseDbFactory</c> attaches to the context options, and
/// that extension's constructor reads <see cref="EngineContext.Current"/>. To satisfy that dependency
/// without pulling in the full Autofac/host bootstrap that <c>ServiceTestBase</c> uses, this fixture
/// installs a lightweight <see cref="TestEngine"/> backed by an empty Autofac container and Moq stubs for
/// <see cref="IApplicationContext"/>, <see cref="ILifetimeScopeAccessor"/> and
/// <see cref="IHttpContextAccessor"/>, and restores the previous <see cref="EngineContext.Current"/> in
/// <see cref="BaseOneTimeTearDown"/>.
/// </remarks>
[TestFixture]
public abstract class TargetGroupEvaluatorTaskTestsBase
{
    private SqliteConnection _connection;
    private SmartDbContext _dbContext;
    private IEngine _previousEngine;

    protected SmartDbContext DbContext
        => _dbContext;

    [OneTimeSetUp]
    public void BaseOneTimeSetUp()
    {
        _previousEngine = EngineContext.Current;

        var rootContainer = new ContainerBuilder().Build();

        var lifetimeScopeAccessorMock = new Mock<ILifetimeScopeAccessor>();
        lifetimeScopeAccessorMock.Setup(x => x.LifetimeScope).Returns((ILifetimeScope)null);

        var httpContextAccessorMock = new Mock<IHttpContextAccessor>();
        httpContextAccessorMock.Setup(x => x.HttpContext).Returns((HttpContext)null);

        var applicationContextMock = new Mock<IApplicationContext>();
        applicationContextMock.Setup(x => x.Services).Returns(rootContainer);

        var engine = new TestEngine
        {
            Application = applicationContextMock.Object
        };
        engine.Scope = new ScopedServiceContainer(
            lifetimeScopeAccessorMock.Object,
            httpContextAccessorMock.Object,
            rootContainer);

        EngineContext.Replace(engine);

        // A SQLite in-memory database only lives as long as the connection that created it stays open,
        // so it must be opened once here and held for the entire fixture lifetime.
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();

        DataSettings.Instance = new DataSettings
        {
            AppVersion = SmartstoreVersion.Version,
            ConnectionString = "DataSource=:memory:",
            TenantName = "Default",
            TenantRoot = null,
            DbFactory = new TargetGroupEvaluatorTaskTestSqliteDbFactory(_connection)
        };
        DataSettings.SetTestMode(true);

        var optionsBuilder = new DbContextOptionsBuilder<SmartDbContext>()
            .UseDbFactory(factoryBuilder =>
            {
                factoryBuilder.AddModelAssemblies(new[]
                {
                    // Add all core models from the Smartstore.Core assembly.
                    typeof(SmartDbContext).Assembly
                });
            });

        _dbContext = new SmartDbContext((DbContextOptions<SmartDbContext>)optionsBuilder.Options);
        _dbContext.Database.EnsureCreated();
    }

    [OneTimeTearDown]
    public void BaseOneTimeTearDown()
    {
        _dbContext?.Dispose();
        _connection?.Dispose();

        DataSettings.Reload();
        EngineContext.Replace(_previousEngine);
    }
}
