namespace WolfLeash.Dashboard;

public static class DashboardTopology
{
    // Each GPU appears once, even if its local NUMA node spans several CPU sockets.
    public static IReadOnlyList<GpuCpuGroup> GroupGpus(IEnumerable<GpuStatus> gpus) => gpus
        .GroupBy(gpu => string.Join(",", gpu.CpuIds.Distinct().Order()))
        .Select(group => new GpuCpuGroup(
            group.First().CpuIds.Distinct().Order().ToArray(),
            group.OrderBy(gpu => gpu.Id, StringComparer.Ordinal).ToArray()))
        .OrderBy(group => group.CpuIds.FirstOrDefault(int.MaxValue))
        .ThenBy(group => group.CpuIds.Length)
        .ThenBy(group => string.Join(",", group.CpuIds), StringComparer.Ordinal)
        .ToArray();

    public static string CpuNames(IEnumerable<int> ids)
    {
        var sorted = ids.Distinct().Order().ToArray();
        return sorted.Length == 0 ? "CPU locality unavailable"
            : $"{(sorted.Length == 1 ? "CPU" : "CPUs")} {string.Join(", ", sorted)}";
    }

    public static string CpuList(IEnumerable<uint> cpus)
    {
        var sorted = cpus.Distinct().Order().ToArray();
        var ranges = new List<string>();
        for (var i = 0; i < sorted.Length; i++)
        {
            var first = sorted[i];
            var last = first;
            while (i + 1 < sorted.Length && (ulong)sorted[i + 1] == (ulong)last + 1)
                last = sorted[++i];
            ranges.Add(first == last ? first.ToString() : $"{first}–{last}");
        }
        return string.Join(", ", ranges);
    }
}

public sealed record GpuCpuGroup(int[] CpuIds, GpuStatus[] Gpus);
