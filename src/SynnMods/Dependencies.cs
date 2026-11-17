namespace SynnMods;

/// <summary>Works out install order and who depends on whom. No I/O, so it's easy to test.</summary>
public static class DependencyPlanner
{
    /// <summary>
    /// Returns the mods to install, each mod's dependencies before it. Every requested mod is
    /// included; a dependency is included only when <paramref name="needsInstall"/> says it is
    /// missing or out of date. Dependencies of up-to-date dependencies are still checked.
    /// </summary>
    /// <param name="needsInstall">For a mod id: true if it isn't installed, or is installed at the
    /// wrong or an unknown version.</param>
    /// <exception cref="InvalidOperationException">A needed dependency can't be installed: it isn't
    /// in the mod list or hasn't been published yet.</exception>
    public static List<ModEntry> Plan(IEnumerable<ModEntry> requested,
        IReadOnlyDictionary<string, ModEntry> catalog, Func<string, bool> needsInstall)
    {
        var requestedList = requested.ToList();
        var requestedIds = new HashSet<string>(requestedList.Select(r => r.Id), StringComparer.OrdinalIgnoreCase);
        var order = new List<ModEntry>();
        var done = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var visiting = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void Visit(ModEntry mod, bool include)
        {
            if (done.Contains(mod.Id) || !visiting.Add(mod.Id)) return; // done, or a dependency loop
            foreach (var depId in mod.Dependencies)
            {
                if (!catalog.TryGetValue(depId, out var dep))
                {
                    if (needsInstall(depId))
                        throw new InvalidOperationException(
                            $"{mod.Name} needs {depId}, which isn't in the Synn Mods list. Install {depId} yourself first.");
                    continue; // installed by hand: fine
                }
                var includeDep = requestedIds.Contains(dep.Id) || needsInstall(dep.Id);
                if (includeDep && !dep.IsPublished)
                    throw new InvalidOperationException($"{mod.Name} needs {dep.Name}, which hasn't been published yet.");
                Visit(dep, includeDep);
            }
            visiting.Remove(mod.Id);
            done.Add(mod.Id);
            if (include) order.Add(mod);
        }

        foreach (var mod in requestedList) Visit(mod, include: true);
        return order;
    }

    /// <summary>Installed mods (other than <paramref name="modId"/>) that need <paramref name="modId"/>.</summary>
    public static List<ModInstaller.LocalMod> Dependents(IEnumerable<ModInstaller.LocalMod> installed, string modId) =>
        installed
            .Where(m => !string.Equals(m.Id, modId, StringComparison.OrdinalIgnoreCase) &&
                        m.Dependencies.Contains(modId, StringComparer.OrdinalIgnoreCase))
            .ToList();
}
