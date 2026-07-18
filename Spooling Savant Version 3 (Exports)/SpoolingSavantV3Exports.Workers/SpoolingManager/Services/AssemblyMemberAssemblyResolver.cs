using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Fabrication;

namespace SpoolingSavantV3Exports.Workers.SpoolingManager.Services;

public static class AssemblyMemberAssemblyResolver
{
	private const double ConnectorMatchToleranceFeet = 0.08;

	public static HashSet<ElementId> ResolveAssemblyIds(Document doc, IEnumerable<ElementId> elementIds)
	{
		HashSet<ElementId> assemblyIds = new HashSet<ElementId>();
		if (doc == null || elementIds == null)
			return assemblyIds;

		List<FabricationPart> fabricationParts = new FilteredElementCollector(doc)
			.OfClass(typeof(FabricationPart))
			.Cast<FabricationPart>()
			.ToList();

		foreach (ElementId elementId in elementIds)
		{
			if (elementId == null || elementId == ElementId.InvalidElementId)
				continue;

			ElementId resolved = ResolveAssemblyId(doc, elementId, fabricationParts);
			if (resolved != null && resolved != ElementId.InvalidElementId)
				assemblyIds.Add(resolved);
		}

		return assemblyIds;
	}

	public static ElementId ResolveAssemblyId(Document doc, ElementId elementId, IList<FabricationPart> fabricationParts = null)
	{
		Element element = doc?.GetElement(elementId);
		if (element == null)
			return ElementId.InvalidElementId;

		ElementId direct = element.AssemblyInstanceId;
		if (direct != null && direct != ElementId.InvalidElementId)
			return direct;

		FabricationPart part = element as FabricationPart;
		if (part == null)
			return ElementId.InvalidElementId;

		IList<FabricationPart> pool = fabricationParts ??
			new FilteredElementCollector(doc)
				.OfClass(typeof(FabricationPart))
				.Cast<FabricationPart>()
				.ToList();

		return FindAssemblyIdThroughConnections(part, pool);
	}

	private static ElementId FindAssemblyIdThroughConnections(FabricationPart start, IList<FabricationPart> pool)
	{
		if (start == null || pool == null || pool.Count == 0)
			return ElementId.InvalidElementId;

		HashSet<ElementId> visited = new HashSet<ElementId> { start.Id };
		Queue<FabricationPart> queue = new Queue<FabricationPart>();
		queue.Enqueue(start);

		while (queue.Count > 0)
		{
			FabricationPart current = queue.Dequeue();
			ElementId assemblyId = current.AssemblyInstanceId;
			if (assemblyId != null && assemblyId != ElementId.InvalidElementId)
				return assemblyId;

			foreach (Connector connector in ListConnectors(current))
			{
				FabricationPart mate = FindMateAtConnector(current, connector, pool);
				if (mate == null || !visited.Add(mate.Id))
					continue;

				queue.Enqueue(mate);
			}
		}

		return ElementId.InvalidElementId;
	}

	private static FabricationPart FindMateAtConnector(FabricationPart self, Connector connector, IList<FabricationPart> pool)
	{
		if (self == null || connector?.Origin == null || pool == null)
			return null;

		XYZ origin = connector.Origin;
		foreach (FabricationPart candidate in pool)
		{
			if (candidate == null || candidate.Id == self.Id)
				continue;

			foreach (Connector mateConnector in ListConnectors(candidate))
			{
				if (mateConnector?.Origin != null &&
				    origin.DistanceTo(mateConnector.Origin) < ConnectorMatchToleranceFeet)
				{
					return candidate;
				}
			}
		}

		return null;
	}

	private static IEnumerable<Connector> ListConnectors(FabricationPart part)
	{
		if (part?.ConnectorManager == null)
			yield break;

		foreach (Connector connector in part.ConnectorManager.Connectors)
			yield return connector;
	}
}
