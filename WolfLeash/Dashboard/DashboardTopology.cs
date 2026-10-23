namespace WolfLeash.Dashboard;

public static class DashboardTopology
{
    public static MemoryUsageTotals? CpuMemory(CpuStatus cpu, IEnumerable<MemoryNodeStatus> nodes)
    {
        var ids = cpu.NumaNodes.Distinct().ToArray();
        if (ids.Length == 0) return null;
        var memory = nodes.ToArray();
        ulong total = 0, used = 0;
        var totalKnown = true;
        var usedKnown = true;
        foreach (var id in ids)
        {
            var matches = memory.Where(node => node.Id >= 0 && (uint)node.Id == id).ToArray();
            if (matches.Length != 1 || !matches[0].CpuIds.Distinct().SequenceEqual([cpu.Id])) return null;
            var node = matches[0];
            if (node.TotalBytes is not { } capacity || capacity > ulong.MaxValue - total)
            {
                totalKnown = false;
                usedKnown = false;
                continue;
            }
            total += capacity;
            if (node.UsedBytes is not { } occupied || occupied > capacity || occupied > ulong.MaxValue - used)
                usedKnown = false;
            else used += occupied;
        }
        return new(totalKnown ? total : null, totalKnown && usedKnown ? used : null);
    }

    public static IReadOnlyList<CpuHardwareGroup> GroupHardware(IEnumerable<CpuStatus> cpus, IEnumerable<GpuStatus> gpus)
    {
        var sockets = cpus.OrderBy(cpu => cpu.Id).ToArray();
        var gpuGroups = GroupGpus(gpus);
        return sockets.Select(cpu => new CpuHardwareGroup(cpu, [cpu.Id],
                gpuGroups.FirstOrDefault(group => group.CpuIds.Length == 1 && group.CpuIds[0] == cpu.Id)?.Gpus ?? []))
            .Concat(gpuGroups.Where(group => group.CpuIds.Length != 1 || !sockets.Any(cpu => cpu.Id == group.CpuIds[0]))
                .Select(group => new CpuHardwareGroup(null, group.CpuIds, group.Gpus)))
            .ToArray();
    }

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
public sealed record MemoryUsageTotals(ulong? TotalBytes, ulong? UsedBytes)
{
    public double? UsedPercent => TotalBytes is > 0 && UsedBytes is { } used && used <= TotalBytes.Value
        ? 100d * used / TotalBytes.Value : null;
}
public sealed record CpuHardwareGroup(CpuStatus? Cpu, int[] CpuIds, GpuStatus[] Gpus)
{
    public string Key => Cpu is not null ? $"cpu:{Cpu.Id}" : "locality:" + string.Join(",", CpuIds);
}
