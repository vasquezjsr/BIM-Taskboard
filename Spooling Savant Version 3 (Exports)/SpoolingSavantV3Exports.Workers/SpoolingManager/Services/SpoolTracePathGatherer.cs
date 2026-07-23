using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Fabrication;

namespace SpoolingSavantV3Exports.Workers.SpoolingManager.Services;

/// <summary>
/// Builds a spool member set from endpoint picks: union of shortest mate-graph paths
/// between every pair of selected fabrication parts (flanges, olets, fittings, etc.).
/// </summary>
public static class SpoolTracePathGatherer
{
	private const double ConnectorMatchToleranceFeet = 0.08;

	public static IReadOnlyList<ElementId> GatherMembersBetweenEndpoints(
		Document doc,
		IEnumerable<ElementId> endpointIds)
	{
		if (doc == null || endpointIds == null)
		{
			return Array.Empty<ElementId>();
		}

		List<FabricationPart> seeds = new List<FabricationPart>();
		HashSet<long> seedKeys = new HashSet<long>();
		foreach (ElementId id in endpointIds)
		{
			if (id == null || id == ElementId.InvalidElementId)
			{
				continue;
			}

			if (!(doc.GetElement(id) is FabricationPart part))
			{
				continue;
			}

			if (!seedKeys.Add(part.Id.Value))
			{
				continue;
			}

			if (IsAlreadyInAssembly(part))
			{
				continue;
			}

			seeds.Add(part);
		}

		if (seeds.Count == 0)
		{
			return Array.Empty<ElementId>();
		}

		List<FabricationPart> pool = new FilteredElementCollector(doc)
			.OfClass(typeof(FabricationPart))
			.Cast<FabricationPart>()
			.Where((FabricationPart p) => p != null && !IsAlreadyInAssembly(p))
			.ToList();

		Dictionary<long, List<long>> adjacency = BuildMateAdjacency(pool);
		HashSet<long> gathered = new HashSet<long>(seeds.Select((FabricationPart s) => s.Id.Value));

		if (seeds.Count == 1)
		{
			return gathered.Select((long v) => new ElementId(v)).ToList();
		}

		List<string> disconnected = new List<string>();
		for (int i = 0; i < seeds.Count; i++)
		{
			for (int j = i + 1; j < seeds.Count; j++)
			{
				List<long> path = FindShortestPath(
					seeds[i].Id.Value,
					seeds[j].Id.Value,
					adjacency);
				if (path == null || path.Count == 0)
				{
					disconnected.Add(DescribePart(seeds[i]) + " ↔ " + DescribePart(seeds[j]));
					continue;
				}

				foreach (long node in path)
				{
					gathered.Add(node);
				}
			}
		}

		if (disconnected.Count > 0 && gathered.Count <= seeds.Count)
		{
			throw new InvalidOperationException(
				"Could not trace a connected path between some endpoints:\n"
				+ string.Join("\n", disconnected.Take(6))
				+ (disconnected.Count > 6 ? "\n…" : string.Empty)
				+ "\n\nPick endpoints that sit on the same continuous fabrication run.");
		}

		return gathered
			.Select((long v) => new ElementId(v))
			.OrderBy((ElementId id) => id.Value)
			.ToList();
	}

	/// <summary>
	/// Chain Spooling: shortest mate path from a free pick back to the previous assembly.
	/// Returns free parts on that path (not members of the previous assembly).
	/// </summary>
	public static IReadOnlyList<ElementId> GatherMembersBetweenPickAndAssembly(
		Document doc,
		ElementId pickId,
		ElementId previousAssemblyId)
	{
		if (doc == null
			|| pickId == null
			|| pickId == ElementId.InvalidElementId
			|| previousAssemblyId == null
			|| previousAssemblyId == ElementId.InvalidElementId)
		{
			return Array.Empty<ElementId>();
		}

		if (!(doc.GetElement(pickId) is FabricationPart pick) || IsAlreadyInAssembly(pick))
		{
			throw new InvalidOperationException(
				"Pick a fabrication part that is not already in an assembly.");
		}

		if (!(doc.GetElement(previousAssemblyId) is AssemblyInstance previousAssembly))
		{
			throw new InvalidOperationException("The previous spool assembly could not be found.");
		}

		HashSet<long> anchorIds = new HashSet<long>();
		foreach (ElementId memberId in previousAssembly.GetMemberIds())
		{
			if (doc.GetElement(memberId) is FabricationPart member)
			{
				anchorIds.Add(member.Id.Value);
			}
		}

		if (anchorIds.Count == 0)
		{
			throw new InvalidOperationException(
				"The previous spool has no fabrication parts to connect from.");
		}

		// Free parts for the new spool + previous assembly members so the graph can reach it.
		List<FabricationPart> pool = new FilteredElementCollector(doc)
			.OfClass(typeof(FabricationPart))
			.Cast<FabricationPart>()
			.Where((FabricationPart p) =>
				p != null
				&& (!IsAlreadyInAssembly(p) || anchorIds.Contains(p.Id.Value)))
			.ToList();

		Dictionary<long, List<long>> adjacency = BuildMateAdjacency(pool);
		List<long> path = FindShortestPathToAny(
			pick.Id.Value,
			anchorIds,
			adjacency);

		if (path == null || path.Count == 0)
		{
			throw new InvalidOperationException(
				"Could not trace a path from that pick back to the last spool.\n"
				+ "Pick a part on the same continuous fabrication run.");
		}

		HashSet<long> gathered = new HashSet<long>();
		foreach (long node in path)
		{
			if (anchorIds.Contains(node))
			{
				continue;
			}

			gathered.Add(node);
		}

		if (gathered.Count == 0)
		{
			throw new InvalidOperationException(
				"That pick is already on the last spool — nothing new to assemble.");
		}

		return gathered
			.Select((long v) => new ElementId(v))
			.OrderBy((ElementId id) => id.Value)
			.ToList();
	}

	private static List<long> FindShortestPathToAny(
		long startId,
		HashSet<long> goalIds,
		Dictionary<long, List<long>> adjacency)
	{
		if (goalIds == null || goalIds.Count == 0)
		{
			return null;
		}

		if (goalIds.Contains(startId))
		{
			return new List<long> { startId };
		}

		if (!adjacency.ContainsKey(startId))
		{
			return null;
		}

		Queue<long> queue = new Queue<long>();
		Dictionary<long, long> cameFrom = new Dictionary<long, long>();
		HashSet<long> visited = new HashSet<long> { startId };
		queue.Enqueue(startId);

		long foundGoal = -1;
		while (queue.Count > 0)
		{
			long current = queue.Dequeue();
			if (goalIds.Contains(current))
			{
				foundGoal = current;
				break;
			}

			if (!adjacency.TryGetValue(current, out List<long> neighbors))
			{
				continue;
			}

			foreach (long next in neighbors)
			{
				if (!visited.Add(next))
				{
					continue;
				}

				cameFrom[next] = current;
				queue.Enqueue(next);
			}
		}

		if (foundGoal < 0)
		{
			return null;
		}

		List<long> path = new List<long>();
		long node = foundGoal;
		path.Add(node);
		while (cameFrom.TryGetValue(node, out long prev))
		{
			path.Add(prev);
			node = prev;
			if (node == startId)
			{
				break;
			}
		}

		path.Reverse();
		return path;
	}

	private static bool IsAlreadyInAssembly(Element element)
	{
		if (element == null)
		{
			return true;
		}

		ElementId assemblyId = element.AssemblyInstanceId;
		return assemblyId != null && assemblyId != ElementId.InvalidElementId;
	}

	private static string DescribePart(FabricationPart part)
	{
		if (part == null)
		{
			return "?";
		}

		try
		{
			string item = part.get_Parameter(BuiltInParameter.FABRICATION_PRODUCT_CODE)?.AsString();
			if (string.IsNullOrWhiteSpace(item))
			{
				Parameter p = part.LookupParameter("Item Number")
					?? part.LookupParameter("Fabrication Item Number");
				item = p?.AsString();
			}

			if (!string.IsNullOrWhiteSpace(item))
			{
				return item.Trim();
			}
		}
		catch
		{
		}

		return "Id " + part.Id.Value;
	}

	private static Dictionary<long, List<long>> BuildMateAdjacency(IList<FabricationPart> pool)
	{
		Dictionary<long, FabricationPart> byId = pool.ToDictionary((FabricationPart p) => p.Id.Value);
		Dictionary<long, List<long>> adjacency = byId.Keys.ToDictionary(
			(long id) => id,
			(long _) => new List<long>());

		foreach (FabricationPart part in pool)
		{
			long selfId = part.Id.Value;
			HashSet<long> mates = new HashSet<long>();

			foreach (Connector connector in ListConnectors(part))
			{
				FabricationPart mate = FindMateAtConnector(part, connector, pool);
				if (mate == null || mate.Id.Value == selfId)
				{
					continue;
				}

				if (!byId.ContainsKey(mate.Id.Value))
				{
					continue;
				}

				mates.Add(mate.Id.Value);
			}

			foreach (long mateId in mates)
			{
				adjacency[selfId].Add(mateId);
			}
		}

		return adjacency;
	}

	private static List<long> FindShortestPath(
		long startId,
		long endId,
		Dictionary<long, List<long>> adjacency)
	{
		if (startId == endId)
		{
			return new List<long> { startId };
		}

		if (!adjacency.ContainsKey(startId) || !adjacency.ContainsKey(endId))
		{
			return null;
		}

		Queue<long> queue = new Queue<long>();
		Dictionary<long, long> cameFrom = new Dictionary<long, long>();
		HashSet<long> visited = new HashSet<long> { startId };
		queue.Enqueue(startId);

		bool found = false;
		while (queue.Count > 0)
		{
			long current = queue.Dequeue();
			if (current == endId)
			{
				found = true;
				break;
			}

			if (!adjacency.TryGetValue(current, out List<long> neighbors))
			{
				continue;
			}

			foreach (long next in neighbors)
			{
				if (!visited.Add(next))
				{
					continue;
				}

				cameFrom[next] = current;
				queue.Enqueue(next);
			}
		}

		if (!found)
		{
			return null;
		}

		List<long> path = new List<long>();
		long node = endId;
		path.Add(node);
		while (cameFrom.TryGetValue(node, out long prev))
		{
			path.Add(prev);
			node = prev;
			if (node == startId)
			{
				break;
			}
		}

		path.Reverse();
		return path;
	}

	private static FabricationPart FindMateAtConnector(
		FabricationPart self,
		Connector connector,
		IList<FabricationPart> pool)
	{
		if (self == null || connector == null || pool == null)
		{
			return null;
		}

		// Prefer Revit's live mate refs — does not require Origin (logical connectors throw on Origin).
		try
		{
			if (connector.IsConnected)
			{
				foreach (Connector other in connector.AllRefs)
				{
					if (other?.Owner is FabricationPart mate
						&& mate.Id != self.Id)
					{
						return mate;
					}
				}
			}
		}
		catch
		{
		}

		if (!TryGetPhysicalOrigin(connector, out XYZ origin))
		{
			return null;
		}

		foreach (FabricationPart candidate in pool)
		{
			if (candidate == null || candidate.Id == self.Id)
			{
				continue;
			}

			foreach (Connector mateConnector in ListConnectors(candidate))
			{
				if (TryGetPhysicalOrigin(mateConnector, out XYZ mateOrigin)
					&& origin.DistanceTo(mateOrigin) < ConnectorMatchToleranceFeet)
				{
					return candidate;
				}
			}
		}

		return null;
	}

	/// <summary>
	/// Origin is only valid on physical connectors; logical/utility connectors throw
	/// "Origin is available only for connectors of PhysicalConn type."
	/// </summary>
	private static bool TryGetPhysicalOrigin(Connector connector, out XYZ origin)
	{
		origin = null;
		if (connector == null)
		{
			return false;
		}

		try
		{
			if (connector.ConnectorType == ConnectorType.Logical)
			{
				return false;
			}
		}
		catch
		{
		}

		try
		{
			origin = connector.Origin;
			return origin != null;
		}
		catch
		{
			origin = null;
			return false;
		}
	}

	private static IEnumerable<Connector> ListConnectors(FabricationPart part)
	{
		if (part?.ConnectorManager == null)
		{
			yield break;
		}

		foreach (Connector connector in part.ConnectorManager.Connectors)
		{
			if (connector == null)
			{
				continue;
			}

			try
			{
				if (connector.ConnectorType == ConnectorType.Logical)
				{
					continue;
				}
			}
			catch
			{
			}

			yield return connector;
		}
	}
}
