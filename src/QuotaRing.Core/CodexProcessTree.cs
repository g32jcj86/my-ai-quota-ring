namespace QuotaRing.Core;

public static class CodexProcessTree
{
    public static IReadOnlyList<int> DesktopProcesses(IReadOnlyList<(int Id, int ParentId)> processes, IEnumerable<int> desktopRoots, int monitorId)
    {
        var desktop = desktopRoots.ToHashSet();
        var protectedIds = new HashSet<int> { monitorId };
        bool changed;
        do
        {
            changed = false;
            foreach (var process in processes)
            {
                if (desktop.Contains(process.ParentId)) changed |= desktop.Add(process.Id);
                if (protectedIds.Contains(process.ParentId)) changed |= protectedIds.Add(process.Id);
            }
        } while (changed);
        desktop.ExceptWith(protectedIds);
        return desktop.ToArray();
    }

    // Walks parent links (bounded, since Windows reuses PIDs and can form cycles) looking for a process named ancestorName.
    public static bool HasAncestor(IReadOnlyDictionary<int, (int ParentId, string Name)> processes, int id, string ancestorName)
    {
        for (var depth = 0; depth < 32 && processes.TryGetValue(id, out var process); depth++)
        {
            if (!processes.TryGetValue(process.ParentId, out var parent)) return false;
            if (parent.Name.Equals(ancestorName, StringComparison.OrdinalIgnoreCase)) return true;
            id = process.ParentId;
        }
        return false;
    }
}
