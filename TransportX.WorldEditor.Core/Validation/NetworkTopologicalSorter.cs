using System;
using System.Collections.Generic;
using TransportX.WorldEditor.Core.Models.Placement;

namespace TransportX.WorldEditor.Core.Validation;

/// <summary>
/// Identifies a spline or junction placement node in declaration/topological order.
/// </summary>
public readonly record struct NetworkNodeRef(bool IsJunction, string Id);

/// <summary>
/// Result of topologically sorting world splines and junctions.
/// </summary>
public sealed record NetworkTopologicalSortResult(
    IReadOnlyList<NetworkNodeRef> OrderedNodes,
    IReadOnlyList<string> CycleNodeIds);

/// <summary>
/// Orders <see cref="SplinePlacementNode"/> and <see cref="JunctionPlacementNode"/> items
/// so every upstream <c>Origin</c> and <c>ConnectBezier</c> target is declared before its dependents.
/// </summary>
public static class NetworkTopologicalSorter
{
    public static NetworkTopologicalSortResult Sort(WorldPlacementDocument placement)
    {
        ArgumentNullException.ThrowIfNull(placement);

        List<NetworkNodeRef> declarationOrder = new List<NetworkNodeRef>();
        Dictionary<string, int> idToIndex = new Dictionary<string, int>(StringComparer.Ordinal);

        for (int i = 0; i < placement.Splines.Count; i++)
        {
            SplinePlacementNode spline = placement.Splines[i];
            if (!idToIndex.ContainsKey(spline.Id))
            {
                idToIndex.Add(spline.Id, declarationOrder.Count);
                declarationOrder.Add(new NetworkNodeRef(false, spline.Id));
            }
        }

        for (int i = 0; i < placement.Junctions.Count; i++)
        {
            JunctionPlacementNode junction = placement.Junctions[i];
            if (!idToIndex.ContainsKey(junction.Id))
            {
                idToIndex.Add(junction.Id, declarationOrder.Count);
                declarationOrder.Add(new NetworkNodeRef(true, junction.Id));
            }
        }

        int count = declarationOrder.Count;
        List<int>[] adjacency = new List<int>[count];
        int[] inDegree = new int[count];
        for (int i = 0; i < count; i++)
        {
            adjacency[i] = new List<int>();
        }

        foreach (SplinePlacementNode spline in placement.Splines)
        {
            if (!idToIndex.TryGetValue(spline.Id, out int destIdx))
            {
                continue;
            }

            if (spline.Origin.Kind != SplineOriginKind.ChunkPose && !string.IsNullOrEmpty(spline.Origin.SourceNodeId))
            {
                AddEdge(spline.Origin.SourceNodeId, destIdx, idToIndex, adjacency, inDegree);
            }

            if (spline.ConnectBezier is not null && !string.IsNullOrEmpty(spline.ConnectBezier.TargetPort.ElementId))
            {
                AddEdge(spline.ConnectBezier.TargetPort.ElementId, destIdx, idToIndex, adjacency, inDegree);
            }
        }

        foreach (JunctionPlacementNode junction in placement.Junctions)
        {
            if (!idToIndex.TryGetValue(junction.Id, out int destIdx))
            {
                continue;
            }

            if (junction.Origin.Kind != JunctionOriginKind.ChunkPose && !string.IsNullOrEmpty(junction.Origin.SourceNodeId))
            {
                AddEdge(junction.Origin.SourceNodeId, destIdx, idToIndex, adjacency, inDegree);
            }
        }

        Stack<int> readyStack = new();
        for (int i = count - 1; 0 <= i; i--)
        {
            if (inDegree[i] == 0)
            {
                readyStack.Push(i);
            }
        }

        List<NetworkNodeRef> ordered = new(count);
        bool[] visited = new bool[count];
        List<int> unlocked = new();

        while (0 < readyStack.Count)
        {
            int current = readyStack.Pop();
            if (visited[current])
            {
                continue;
            }

            visited[current] = true;
            ordered.Add(declarationOrder[current]);

            unlocked.Clear();
            foreach (int next in adjacency[current])
            {
                inDegree[next]--;
                if (inDegree[next] == 0)
                {
                    unlocked.Add(next);
                }
            }

            if (1 < unlocked.Count)
            {
                unlocked.Sort();
            }

            for (int i = unlocked.Count - 1; 0 <= i; i--)
            {
                readyStack.Push(unlocked[i]);
            }
        }

        List<string> cycleNodeIds = new List<string>();
        for (int i = 0; i < count; i++)
        {
            if (!visited[i])
            {
                cycleNodeIds.Add(declarationOrder[i].Id);
            }
        }

        return new NetworkTopologicalSortResult(ordered, cycleNodeIds);
    }

    private static void AddEdge(
        string sourceNodeId,
        int destIdx,
        IReadOnlyDictionary<string, int> idToIndex,
        IReadOnlyList<List<int>> adjacency,
        int[] inDegree)
    {
        if (!idToIndex.TryGetValue(sourceNodeId, out int srcIdx))
        {
            return;
        }

        if (srcIdx == destIdx)
        {
            inDegree[destIdx]++;
            return;
        }

        if (!adjacency[srcIdx].Contains(destIdx))
        {
            adjacency[srcIdx].Add(destIdx);
            inDegree[destIdx]++;
        }
    }
}
