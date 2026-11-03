using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
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

    private static DirectoryInfo[] _moduleDirs;
    private static ResolveEventHandler _moduleDependencyResolver;

    [OneTimeSetUp]
    public static void RegisterModuleDependencyResolver()
    {
        var modulesRoot = FindBuiltModulesRoot();
        Assert.That(modulesRoot, Is.Not.Null,
            "Could not locate the built 'src/Smartstore.Web/Modules' directory by walking up from " +
            $"'{AppContext.BaseDirectory}'. Build Smartstore.sln (which also builds every Smartstore.Modules.* " +
            "project) before running this test; otherwise this audit cannot see any module-defined hook and " +
            "would silently regress to only covering Smartstore.Core.");

        _moduleDirs = modulesRoot.GetDirectories();

        // Keep this resolver registered for the fixture's whole lifetime, not just around Assembly.LoadFrom:
        // the dependency resolution a module's private references actually need is most likely triggered
        // lazily by assembly.GetTypes() inside GetLoadableTypes(), which runs in the test methods AFTER
        // GetCoreAndModuleAssemblies() has already returned. A try/finally scoped only around the LoadFrom
        // loop unregisters the handler before that point, leaving private-dependency resolution unavailable
        // for the actual type scan.
        _moduleDependencyResolver = (_, args) => ResolveModuleDependency(args.Name, _moduleDirs);
        AppDomain.CurrentDomain.AssemblyResolve += _moduleDependencyResolver;
    }

    [OneTimeTearDown]
    public static void UnregisterModuleDependencyResolver()
    {
        if (_moduleDependencyResolver != null)
        {
            AppDomain.CurrentDomain.AssemblyResolve -= _moduleDependencyResolver;
            _moduleDependencyResolver = null;
        }
    }

    [Test]
    public void No_unvetted_Important_or_Essential_hook_targets_CustomerRoleMapping()
    {
        var entityType = typeof(CustomerRoleMapping);

        var assemblies = GetCoreAndModuleAssemblies();

        var hookTypes = assemblies
            .SelectMany(GetLoadableTypes)
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

    /// <summary>
    /// Verifies that the scan below actually reaches module assemblies and isn't silently reduced to just
    /// <see cref="SmartDbContext"/>'s own assembly (which would make the test above vacuously blind to every
    /// hook defined under <c>src/Smartstore.Modules/</c>).
    /// </summary>
    [Test]
    public void Scan_covers_every_built_module_assembly()
    {
        var assemblies = GetCoreAndModuleAssemblies();
        var moduleAssemblyNames = assemblies
            .Select(a => a.GetName().Name)
            .Where(n => n != null && n.StartsWith("Smartstore.", StringComparison.Ordinal) && n != typeof(SmartDbContext).Assembly.GetName().Name)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToArray();

        // This list only needs to prove the scan is non-trivial; it intentionally does not enumerate every
        // module so this test doesn't have to be touched whenever a module is added/removed/renamed.
        Assert.That(moduleAssemblyNames.Length, Is.GreaterThanOrEqualTo(10),
            "Expected the hook audit to scan a double-digit number of built Smartstore.Modules.* assemblies " +
            "(found: " + string.Join(", ", moduleAssemblyNames) + "). If this drops to 0, the audit above is " +
            "silently back to only covering Smartstore.Core, which is exactly the gap this test guards against. " +
            "Make sure 'dotnet build Smartstore.sln' (or at least the module projects) ran before this test.");
    }

    /// <summary>
    /// Returns <see cref="SmartDbContext"/>'s own assembly (<c>Smartstore.Core</c>) plus every module assembly
    /// built under <c>src/Smartstore.Web/Modules/</c> — the directory <see cref="Engine.Modularity.ModuleExplorer"/>
    /// and <see cref="Engine.Modularity.ModuleLoader"/> enumerate at runtime to discover modules (see
    /// <c>SmartApplicationContext.ModulesRoot</c>, which is <c>{ContentRoot}/Modules</c>, and
    /// <c>ModuleDescriptor.AssemblyName</c>, which is <c>{directory name}.dll</c>).
    ///
    /// <c>Smartstore.Core.Tests.csproj</c> deliberately does NOT carry a <c>ProjectReference</c> to every
    /// <c>Smartstore.Modules.*</c> project: every module project references the full <c>Smartstore.Web</c> app
    /// project (ASP.NET Core host, Razor views, the works) at compile time, so adding ~17 module
    /// ProjectReferences here would drag the entire web app build (and its Razor compilation) into the Core unit
    /// test project's build graph: a layering violation and a likely build-time hit, for a test that only needs
    /// reflection over already-compiled DLLs. Instead this mirrors the app's own discovery
    /// mechanism: it locates the solution's already-built module output folder and loads each module's main
    /// assembly via <see cref="Assembly.LoadFrom(string)"/>, exactly like the production host loads module
    /// assemblies from that same folder. Loading by path still lets the CLR bind the module's references to the
    /// test process's already-loaded (same-version, same build) <c>Smartstore</c>/<c>Smartstore.Core</c>
    /// assemblies, so the <see cref="IDbSaveHook"/> and generic hook-base-type identity checks below remain
    /// valid across assemblies.
    /// </summary>
    private static List<Assembly> GetCoreAndModuleAssemblies()
    {
        var assemblies = new List<Assembly> { typeof(SmartDbContext).Assembly };

        foreach (var moduleDir in _moduleDirs)
        {
            var moduleManifest = Path.Combine(moduleDir.FullName, "module.json");
            var moduleAssemblyPath = Path.Combine(moduleDir.FullName, moduleDir.Name + ".dll");

            if (File.Exists(moduleManifest) && File.Exists(moduleAssemblyPath))
            {
                assemblies.Add(Assembly.LoadFrom(moduleAssemblyPath));
            }
        }

        return assemblies;
    }

    private static Assembly ResolveModuleDependency(string assemblyName, DirectoryInfo[] moduleDirs)
    {
        var simpleName = new AssemblyName(assemblyName).Name;

        foreach (var moduleDir in moduleDirs)
        {
            var candidate = Path.Combine(moduleDir.FullName, simpleName + ".dll");
            if (File.Exists(candidate))
            {
                try
                {
                    return Assembly.LoadFrom(candidate);
                }
                catch
                {
                    return null;
                }
            }
        }

        return null;
    }

    /// <summary>
    /// Walks up from the test assembly's own output directory to the repo root (identified by
    /// <c>Smartstore.sln</c>) and returns <c>src/Smartstore.Web/Modules</c>, the directory module projects build
    /// into and the same directory the production host scans for modules (see remarks on
    /// <see cref="GetCoreAndModuleAssemblies"/>).
    /// </summary>
    private static DirectoryInfo FindBuiltModulesRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);

        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "Smartstore.sln")))
        {
            dir = dir.Parent;
        }

        if (dir == null)
        {
            return null;
        }

        var modulesDir = new DirectoryInfo(Path.Combine(dir.FullName, "src", "Smartstore.Web", "Modules"));
        return modulesDir.Exists ? modulesDir : null;
    }

    private static IEnumerable<Type> GetLoadableTypes(Assembly assembly)
    {
        try
        {
            return assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException ex)
        {
            // Some types in a module assembly may fail to load because they reference a dependency that this
            // test process cannot resolve (e.g. a type that is only usable inside the full Smartstore.Web host).
            // That's fine as long as the IDbSaveHook implementations themselves (which only depend on
            // Smartstore/Smartstore.Core/Smartstore.Data, all already loaded here) still load successfully.
            return ex.Types.Where(t => t != null);
        }
    }
}
