using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Fabrication;

namespace SpoolingSavantV3Exports.Workers.SpoolingManager.Services;

/// <summary>
/// Imports a PCF by creating fabrication parts from a user-selected service palette
/// and placing them at the original END-POINT coordinates.
/// </summary>
internal static class PcfImportService
{
	private const double MinSegmentLengthFeet = 1e-4;
	private const double DefaultBoreInches = 4.0;

	internal sealed class ImportOptions
	{
		internal FabricationService Service { get; set; }
		internal int PaletteIndex { get; set; }
		internal string PaletteName { get; set; } = string.Empty;
		/// <summary>Used when the PCF has no NOMINAL-SIZE / END-POINT bore (legacy files).</summary>
		internal double DefaultSizeInches { get; set; } = 4.0;
	}

	internal sealed class ImportResult
	{
		internal int ParsedComponents { get; set; }
		internal int FabPartsCreated { get; set; }
		internal int PipesCreated { get; set; }
		internal int ElbowsCreated { get; set; }
		internal int TeesCreated { get; set; }
		internal int FlangesCreated { get; set; }
		internal int OletsCreated { get; set; }
		internal int CapsCreated { get; set; }
		internal int WeldsCoupled { get; set; }
		internal int Skipped { get; set; }
		internal string ServiceName { get; set; } = string.Empty;
		internal string PaletteName { get; set; } = string.Empty;
		internal List<ElementId> CreatedFabIds { get; } = new List<ElementId>();
		/// <summary>PCF SPOOL-ID per created part — used to keep abut/connect inside one spool.</summary>
		internal Dictionary<ElementId, string> SpoolIdByPartId { get; } = new Dictionary<ElementId, string>();
		internal List<string> Warnings { get; } = new List<string>();

		internal IList<ElementId> ElementsToShow => CreatedFabIds;

		internal string BuildSummary(string fileName)
		{
			var sb = new StringBuilder();
			sb.AppendLine("File: " + (fileName ?? string.Empty));
			if (!string.IsNullOrWhiteSpace(ServiceName) || !string.IsNullOrWhiteSpace(PaletteName))
			{
				sb.AppendLine("Service: " + ServiceName);
				sb.AppendLine("Palette: " + PaletteName);
			}

			sb.AppendLine("Components parsed: " + ParsedComponents.ToString(CultureInfo.InvariantCulture));
			sb.AppendLine("Fabrication parts created: " + FabPartsCreated.ToString(CultureInfo.InvariantCulture)
				+ " (pipe " + PipesCreated.ToString(CultureInfo.InvariantCulture)
				+ ", elbow " + ElbowsCreated.ToString(CultureInfo.InvariantCulture)
				+ ", tee " + TeesCreated.ToString(CultureInfo.InvariantCulture)
				+ ", flange " + FlangesCreated.ToString(CultureInfo.InvariantCulture)
				+ ", olet " + OletsCreated.ToString(CultureInfo.InvariantCulture)
				+ ", cap " + CapsCreated.ToString(CultureInfo.InvariantCulture)
				+ ", shop-weld joints " + WeldsCoupled.ToString(CultureInfo.InvariantCulture) + ")");
			sb.AppendLine("Skipped: " + Skipped.ToString(CultureInfo.InvariantCulture));

			if (Warnings.Count > 0)
			{
				sb.AppendLine();
				sb.AppendLine("Notes:");
				foreach (string warning in Warnings.Take(16))
				{
					sb.AppendLine("- " + warning);
				}

				if (Warnings.Count > 16)
				{
					sb.AppendLine("- … +" + (Warnings.Count - 16).ToString(CultureInfo.InvariantCulture) + " more");
				}
			}

			return sb.ToString().TrimEnd();
		}
	}

	internal static IList<FabricationService> GetLoadedServices(Document doc)
	{
		try
		{
			FabricationConfiguration config = FabricationConfiguration.GetFabricationConfiguration(doc);
			return config?.GetAllLoadedServices() ?? Array.Empty<FabricationService>();
		}
		catch
		{
			return Array.Empty<FabricationService>();
		}
	}

	internal static ImportResult Import(Document doc, string pcfPath, ImportOptions options)
	{
		if (doc == null)
		{
			throw new ArgumentNullException(nameof(doc));
		}

		if (options?.Service == null || options.PaletteIndex < 0)
		{
			throw new ArgumentException("A fabrication service and palette are required.", nameof(options));
		}

		PcfDocument pcf = PcfParser.ParseFile(pcfPath);
		var result = new ImportResult
		{
			ParsedComponents = pcf.Components.Count,
			ServiceName = options.Service.Name ?? string.Empty,
			PaletteName = options.PaletteName ?? string.Empty
		};

		PaletteButtons buttons = PaletteButtons.Load(options.Service, options.PaletteIndex);
		if (buttons.Straight == null && buttons.Elbows.Count == 0)
		{
			result.Warnings.Add("Selected palette has no usable straight or elbow buttons.");
			return result;
		}

		using (Transaction tx = new Transaction(doc, "Spooling Savant V3 (Exports): Import PCF"))
		{
			FailureHandlingOptions failureOptions = tx.GetFailureHandlingOptions();
			failureOptions.SetFailuresPreprocessor(new PcfImportFailuresPreprocessor());
			failureOptions.SetClearAfterRollback(true);
			tx.SetFailureHandlingOptions(failureOptions);
			tx.Start();

			// Pass 1: pipes and elbows (main run geometry).
			var tees = new List<PcfComponent>();
			var flanges = new List<PcfComponent>();
			var olets = new List<PcfComponent>();
			var caps = new List<PcfComponent>();
			var inlineFittings = new List<PcfComponent>();
			var welds = new List<PcfComponent>();
			foreach (PcfComponent component in pcf.Components)
			{
				if (component.IsStraightPipe)
				{
					TryPlaceComponent(doc, pcf, component, buttons.Straight, isElbow: false, options, result);
				}
				else if (component.IsElbow)
				{
					FabricationServiceButton elbowButton = null;
					if (component.TryGetSegment(out XYZ elbowStart, out XYZ elbowEnd, out double elbowBore))
					{
						double elbowSize = elbowBore > 1e-6
							? elbowBore
							: (options.DefaultSizeInches > 1e-6 ? options.DefaultSizeInches : DefaultBoreInches);
						elbowButton = PickElbowButton(buttons.Elbows, elbowSize, elbowStart.DistanceTo(elbowEnd));
					}

					TryPlaceComponent(
						doc,
						pcf,
						component,
						elbowButton ?? buttons.Elbows.FirstOrDefault() ?? buttons.Straight,
						isElbow: true,
						options,
						result);
				}
				else if (component.IsTee)
				{
					tees.Add(component);
				}
				else if (component.IsFlange)
				{
					flanges.Add(component);
				}
				else if (component.IsOlet)
				{
					olets.Add(component);
				}
				else if (component.IsCap)
				{
					caps.Add(component);
				}
				else if (component.IsInlineFitting)
				{
					inlineFittings.Add(component);
				}
				else if (component.IsWeld)
				{
					// Welds are couple instructions — placed via ConnectAndCouple after hosts exist.
					welds.Add(component);
				}
				else
				{
					result.Skipped++;
					result.Warnings.Add(BuildSkippedMiscWarning(component));
				}
			}

			// Pass 1b: tees and flanges (use PCF neighbors to resolve tee branch faces).
			foreach (PcfComponent tee in tees)
			{
				TryPlaceTee(doc, pcf, tee, buttons, options, result);
			}

			foreach (PcfComponent flange in flanges)
			{
				TryPlaceFlange(doc, pcf, flange, buttons.Flanges, options, result);
			}

			foreach (PcfComponent cap in caps)
			{
				TryPlaceCap(doc, pcf, cap, buttons.Caps, options, result);
			}

			// Couplings / adapters / reducers — place at PCF END-POINTs (never invent from connect).
			foreach (PcfComponent fitting in inlineFittings)
			{
				TryPlaceInlineFitting(doc, pcf, fitting, buttons, options, result);
			}

			// Stretch host pipes so olet/misc/cap points land on a continuous run before placing branches.
			ExtendStraightsForBranchPoints(
				doc,
				result.CreatedFabIds,
				pcf.Components.Where(c =>
					!c.IsStraightPipe && !c.IsElbow && !c.IsTee && !c.IsFlange && !c.IsWeld).ToList());

			// Do NOT equalize pipes to parallel peers — that invents length (e.g. 4' stub → 22' riser)
			// and drives pipe through elbows. Place each PIPE at its PCF END-POINT span only.
			AbutStraightsToElbowConnectors(doc, result.CreatedFabIds, result.SpoolIdByPartId);
			AbutOpenStraightEndsTogether(doc, result.CreatedFabIds, result.SpoolIdByPartId);
			TrimCreatedStraightsAgainstElbows(doc, result.CreatedFabIds);

			// Pass 2: olets onto the placed pipe/fitting runs (PlaceAsTap — sized + connected).
			foreach (PcfComponent olet in olets)
			{
				TryPlaceOlet(doc, pcf, olet, buttons.Olets, options, result);
			}

			AbutStraightsToElbowConnectors(doc, result.CreatedFabIds, result.SpoolIdByPartId);
			AbutOpenStraightEndsTogether(doc, result.CreatedFabIds, result.SpoolIdByPartId);
			TrimCreatedStraightsAgainstElbows(doc, result.CreatedFabIds);

			// Back-to-back flanges: seat raised faces (gasket) BEFORE hub shop welds.
			// Catalog flange length often differs from the PCF hub→face span, so faces start
			// with a visible gap; ConnectTo / a small axial nudge closes that while hubs are
			// still free. Hub welds then snap pipes onto flanges (never move the flange).
			ConnectFacingFlangePairs(doc, result.CreatedFabIds, result.SpoolIdByPartId);
			if (welds.Count > 0)
			{
				CoupleAtWeldRecords(doc, result.CreatedFabIds, welds, result);
				// Second pass in case a hub weld nudged a face pair apart again.
				ConnectFacingFlangePairs(doc, result.CreatedFabIds, result.SpoolIdByPartId);
				ConnectPlacedPartsPlain(doc, result.CreatedFabIds, result.SpoolIdByPartId);
			}
			else
			{
				// No WELD rows (typical copper/soldered PCFs) — never ConnectAndCouple.
				// That invents shop-weld couplings on tees and stacks duplicates.
				ConnectFacingFlangePairs(doc, result.CreatedFabIds, result.SpoolIdByPartId);
				ConnectPlacedPartsPlain(doc, result.CreatedFabIds, result.SpoolIdByPartId);
			}

			tx.Commit();
		}

		return result;
	}

	private static void TryPlaceComponent(
		Document doc,
		PcfDocument pcf,
		PcfComponent component,
		FabricationServiceButton button,
		bool isElbow,
		ImportOptions options,
		ImportResult result)
	{
		if (button == null)
		{
			result.Skipped++;
			result.Warnings.Add(
				"No " + (isElbow ? "elbow" : "straight") + " button in palette for "
				+ component.Type + FormatId(component) + ".");
			return;
		}

		if (!component.TryGetSegment(out XYZ start, out XYZ end, out double boreInches))
		{
			result.Skipped++;
			result.Warnings.Add("Skipped " + component.Type + FormatId(component) + " (missing/zero-length END-POINTs).");
			return;
		}

		if (start.DistanceTo(end) < MinSegmentLengthFeet)
		{
			result.Skipped++;
			return;
		}

		ElementId levelId = FindNearestLevelId(doc, start, end);
		if (levelId == null || levelId == ElementId.InvalidElementId)
		{
			result.Skipped++;
			result.Warnings.Add("No level found for " + component.Type + FormatId(component) + ".");
			return;
		}

		double sizeInches = boreInches > 1e-6
			? boreInches
			: (options.DefaultSizeInches > 1e-6 ? options.DefaultSizeInches : DefaultBoreInches);
		if (boreInches <= 1e-6)
		{
			result.Warnings.Add(
				component.Type + FormatId(component)
				+ " had no size in PCF — used " + FormatSizeLabel(sizeInches) + " (default). Re-export PCF for true sizes.");
		}

		try
		{
			FabricationPart part = CreateSizedPart(doc, button, sizeInches, levelId);
			if (part == null)
			{
				result.Skipped++;
				result.Warnings.Add("Create failed for " + component.Type + FormatId(component) + ".");
				return;
			}

			if (!TryApplyProductListSize(doc, part, sizeInches))
			{
				result.Warnings.Add(
					component.Type + FormatId(component)
					+ " created but product-list size " + FormatSizeLabel(sizeInches)
					+ " could not be applied (part may be wrong diameter).");
			}

			TrySetSizeParameters(part, sizeInches, component.SizeText);

			bool placed = isElbow
				? PlaceElbow(doc, part, pcf, component, start, end)
				: PlaceStraight(doc, part, start, end);

			if (!placed)
			{
				try
				{
					doc.Delete(part.Id);
				}
				catch
				{
				}

				result.Skipped++;
				result.Warnings.Add("Could not place " + component.Type + FormatId(component) + " at END-POINTs.");
				return;
			}

			TrySetPartIdentity(part, pcf, component, FormatSizeLabel(sizeInches));
			RegisterCreatedPart(result, part, component);
			result.FabPartsCreated++;
			if (isElbow)
			{
				result.ElbowsCreated++;
			}
			else
			{
				result.PipesCreated++;
			}
		}
		catch (Exception ex)
		{
			result.Skipped++;
			result.Warnings.Add("Failed " + component.Type + FormatId(component) + ": " + ex.Message);
		}
	}

	private static void ConnectPlacedParts(
		Document doc,
		IList<ElementId> createdIds,
		IDictionary<ElementId, string> spoolIdByPartId = null)
	{
		ConnectPlacedPartsCore(doc, createdIds, allowCouple: true, spoolIdByPartId);
	}

	private static void ConnectPlacedPartsPlain(
		Document doc,
		IList<ElementId> createdIds,
		IDictionary<ElementId, string> spoolIdByPartId = null)
	{
		ConnectPlacedPartsCore(doc, createdIds, allowCouple: false, spoolIdByPartId);
	}

	/// <summary>
	/// Back-to-back flanges (gasket omitted from PCF) sit face-to-face with no WELD row.
	/// ConnectTo those open faces so the bolted pair is real — never Couple (that inserts
	/// extra flanges). Only pair faces within a gasket-scale gap; never hub-to-hub.
	/// </summary>
	private static void ConnectFacingFlangePairs(
		Document doc,
		IList<ElementId> createdIds,
		IDictionary<ElementId, string> spoolIdByPartId = null)
	{
		if (doc == null || createdIds == null || createdIds.Count < 2)
		{
			return;
		}

		var flanges = new List<FabricationPart>();
		foreach (ElementId id in createdIds)
		{
			if (doc.GetElement(id) is FabricationPart part
				&& FabricationPartClassification.IsFlangePart(part, doc))
			{
				flanges.Add(part);
			}
		}

		if (flanges.Count < 2)
		{
			return;
		}

		// PCF gasket gaps are ~1/16"; catalog length mismatch can leave ~1" face gaps.
		const double faceTol = 0.25;
		for (int i = 0; i < flanges.Count; i++)
		{
			List<Connector> aOpen = GetEndConnectors(flanges[i])
				.Where(c => c != null && !c.IsConnected)
				.ToList();
			if (aOpen.Count == 0)
			{
				continue;
			}

			for (int j = i + 1; j < flanges.Count; j++)
			{
				List<Connector> bOpen = GetEndConnectors(flanges[j])
					.Where(c => c != null && !c.IsConnected)
					.ToList();
				if (bOpen.Count == 0)
				{
					continue;
				}

				Connector bestA = null;
				Connector bestB = null;
				double bestDist = faceTol;
				foreach (Connector a in aOpen)
				{
					if (a.IsConnected)
					{
						continue;
					}

					foreach (Connector b in bOpen)
					{
						if (b.IsConnected || !ConnectorsSizeCompatible(a, b))
						{
							continue;
						}

						double dist = a.Origin.DistanceTo(b.Origin);
						if (dist >= bestDist)
						{
							continue;
						}

						// Raised faces must oppose (dot ~ -1). Parallel hubs are hub-to-hub — skip.
						XYZ axisA = GetConnectorOutwardAxis(a);
						XYZ axisB = GetConnectorOutwardAxis(b);
						if (axisA == null || axisB == null)
						{
							continue;
						}

						double oppose = axisA.Normalize().DotProduct(axisB.Normalize());
						if (oppose > -0.85)
						{
							continue;
						}

						// Vector between faces should run along the outward axes (collinear joint).
						XYZ delta = b.Origin - a.Origin;
						if (delta.GetLength() > 1e-9)
						{
							XYZ dir = delta.Normalize();
							if (Math.Abs(dir.DotProduct(axisA.Normalize())) < 0.85)
							{
								continue;
							}
						}

						bestDist = dist;
						bestA = a;
						bestB = b;
					}
				}

				if (bestA != null && bestB != null
					&& SameSpool(flanges[i].Id, flanges[j].Id, spoolIdByPartId))
				{
					// Allow a short axial nudge so ConnectTo can insert the gasket when the
					// catalog flange is shorter/longer than the PCF hub→face span. Hub welds
					// run after this pass and snap pipes to the (possibly nudged) hubs.
					TryConnectPair(doc, bestA, bestB, allowCouple: false, allowMoveFlange: true);
				}
			}
		}
	}

	private static void ConnectPlacedPartsCore(
		Document doc,
		IList<ElementId> createdIds,
		bool allowCouple,
		IDictionary<ElementId, string> spoolIdByPartId = null)
	{
		if (doc == null || createdIds == null || createdIds.Count < 2)
		{
			return;
		}

		var parts = new List<FabricationPart>();
		foreach (ElementId id in createdIds)
		{
			if (doc.GetElement(id) is FabricationPart part)
			{
				parts.Add(part);
			}
		}

		const double connectTol = 0.20; // snap first; couple/connect when within this
		for (int i = 0; i < parts.Count; i++)
		{
			List<Connector> aConnectors = GetEndConnectors(parts[i])
				.Where(c => c != null && !c.IsConnected)
				.ToList();
			if (aConnectors.Count == 0)
			{
				continue;
			}

			for (int j = i + 1; j < parts.Count; j++)
			{
				if (!SameSpool(parts[i].Id, parts[j].Id, spoolIdByPartId))
				{
					continue;
				}

				List<Connector> bConnectors = GetEndConnectors(parts[j])
					.Where(c => c != null && !c.IsConnected)
					.ToList();
				if (bConnectors.Count == 0)
				{
					continue;
				}

				foreach (Connector a in aConnectors)
				{
					if (a.IsConnected)
					{
						continue;
					}

					Connector bestB = null;
					double bestDist = connectTol;
					foreach (Connector b in bConnectors)
					{
						if (b.IsConnected || !ConnectorsSizeCompatible(a, b))
						{
							continue;
						}

						double dist = a.Origin.DistanceTo(b.Origin);
						if (dist < bestDist)
						{
							bestDist = dist;
							bestB = b;
						}
					}

					if (bestB == null)
					{
						continue;
					}

					TryConnectPair(doc, a, bestB, allowCouple);
				}
			}
		}
	}

	/// <summary>
	/// Each PCF WELD row is a shop-weld joint: find the two open connectors nearest its
	/// END-POINTs and ConnectAndCouple so the welded service inserts its Shop Weld part.
	/// </summary>
	private static void CoupleAtWeldRecords(
		Document doc,
		IList<ElementId> createdIds,
		IList<PcfComponent> welds,
		ImportResult result)
	{
		if (doc == null || createdIds == null || welds == null || welds.Count == 0)
		{
			return;
		}

		List<FabricationPart> parts = createdIds
			.Select(id => doc.GetElement(id) as FabricationPart)
			.Where(p => p != null)
			.ToList();
		if (parts.Count < 2)
		{
			return;
		}

		foreach (PcfComponent weld in welds)
		{
			if (weld == null)
			{
				continue;
			}

			XYZ aPt = null;
			XYZ bPt = null;
			foreach (PcfEndPoint ep in weld.EndPoints)
			{
				if (ep?.Point == null)
				{
					continue;
				}

				if (aPt == null)
				{
					aPt = ep.Point;
				}
				else
				{
					bPt = ep.Point;
					break;
				}
			}

			if (aPt == null)
			{
				result.Warnings.Add("Skipped WELD" + FormatId(weld) + " (missing END-POINT).");
				continue;
			}

			if (bPt == null || aPt.DistanceTo(bPt) < 1e-6)
			{
				bPt = aPt;
			}

			XYZ mid = (aPt + bPt) * 0.5;
			string weldSpool = (weld.SpoolId ?? string.Empty).Trim();
			if (!TryFindOpenConnectorPairForWeld(
				parts,
				aPt,
				bPt,
				mid,
				result.SpoolIdByPartId,
				weldSpool,
				out Connector cA,
				out Connector cB))
			{
				result.Warnings.Add(
					"WELD" + FormatId(weld) + " — no open connector pair near joint; left uncoupled.");
				continue;
			}

			// Nudge a straight onto the other face if needed, then couple.
			// Never move flanges here — faces were already seated; only snap pipe to hub.
			ElementId ownerIdA = (cA.Owner as FabricationPart)?.Id ?? ElementId.InvalidElementId;
			ElementId ownerIdB = (cB.Owner as FabricationPart)?.Id ?? ElementId.InvalidElementId;
			TryConnectPair(doc, cA, cB, allowCouple: true, allowMoveFlange: false);

			// Local check only — never scan the whole model (that hangs large projects).
			bool coupled = HostsJoinedNearPoint(doc, ownerIdA, ownerIdB, mid);
			if (coupled)
			{
				result.WeldsCoupled++;
				TryTrackNewWeldNearPoint(doc, createdIds, parts, mid);
			}
			else
			{
				result.Warnings.Add("WELD" + FormatId(weld) + " — couple/connect did not stick.");
			}
		}
	}

	private static bool HostsJoinedNearPoint(
		Document doc,
		ElementId ownerIdA,
		ElementId ownerIdB,
		XYZ mid)
	{
		if (doc == null || mid == null
			|| ownerIdA == null || ownerIdA == ElementId.InvalidElementId
			|| ownerIdB == null || ownerIdB == ElementId.InvalidElementId)
		{
			return false;
		}

		FabricationPart a = doc.GetElement(ownerIdA) as FabricationPart;
		FabricationPart b = doc.GetElement(ownerIdB) as FabricationPart;
		if (a == null || b == null)
		{
			return false;
		}

		const double tol = 0.40;
		foreach (Connector ca in GetEndConnectors(a))
		{
			if (ca?.Origin == null || ca.Origin.DistanceTo(mid) > tol)
			{
				continue;
			}

			try
			{
				if (!ca.IsConnected)
				{
					continue;
				}

				foreach (Connector r in ca.AllRefs)
				{
					if (r?.Owner == null || r.Owner.Id == a.Id)
					{
						continue;
					}

					if (r.Owner.Id == b.Id)
					{
						return true;
					}

					// pipe — weld — flange
					if (r.Owner is FabricationPart midPart
						&& FabricationPartClassification.IsWeldPart(midPart))
					{
						foreach (Connector mc in GetEndConnectors(midPart))
						{
							if (mc == null || !mc.IsConnected)
							{
								continue;
							}

							foreach (Connector r2 in mc.AllRefs)
							{
								if (r2?.Owner != null && r2.Owner.Id == b.Id)
								{
									return true;
								}
							}
						}
					}
				}
			}
			catch
			{
			}
		}

		return false;
	}

	private static void TryTrackNewWeldNearPoint(
		Document doc,
		IList<ElementId> createdIds,
		List<FabricationPart> parts,
		XYZ mid)
	{
		if (doc == null || createdIds == null || mid == null)
		{
			return;
		}

		const double tol = 0.40;
		// Only inspect connectors of already-known hosts near the joint for a weld neighbor.
		foreach (FabricationPart host in parts.ToList())
		{
			FabricationPart part = doc.GetElement(host.Id) as FabricationPart;
			if (part == null)
			{
				continue;
			}

			foreach (Connector c in GetEndConnectors(part))
			{
				if (c?.Origin == null || c.Origin.DistanceTo(mid) > tol || !c.IsConnected)
				{
					continue;
				}

				try
				{
					foreach (Connector r in c.AllRefs)
					{
						if (r?.Owner is FabricationPart wp
							&& FabricationPartClassification.IsWeldPart(wp)
							&& !createdIds.Contains(wp.Id))
						{
							createdIds.Add(wp.Id);
							parts.Add(wp);
						}
					}
				}
				catch
				{
				}
			}
		}
	}

	private static bool WeldJointLooksConnected(
		Document doc,
		IList<FabricationPart> parts,
		XYZ aPt,
		XYZ bPt,
		XYZ mid)
	{
		if (doc == null || parts == null || aPt == null)
		{
			return false;
		}

		XYZ checkMid = mid ?? aPt;
		XYZ checkB = bPt ?? aPt;
		const double tol = 0.35;

		foreach (FabricationPart host in parts)
		{
			FabricationPart part = doc.GetElement(host.Id) as FabricationPart;
			if (part == null || !FabricationPartClassification.IsWeldPart(part))
			{
				continue;
			}

			List<Connector> ends = GetEndConnectors(part);
			if (ends.Count < 2)
			{
				continue;
			}

			XYZ wMid = (ends[0].Origin + ends[1].Origin) * 0.5;
			if (wMid.DistanceTo(checkMid) > tol
				&& wMid.DistanceTo(aPt) > tol
				&& wMid.DistanceTo(checkB) > tol)
			{
				continue;
			}

			try
			{
				if (ends[0].IsConnected && ends[1].IsConnected)
				{
					return true;
				}
			}
			catch
			{
			}
		}

		return false;
	}

	private static bool TryFindOpenConnectorPairForWeld(
		IList<FabricationPart> parts,
		XYZ aPt,
		XYZ bPt,
		XYZ mid,
		IDictionary<ElementId, string> spoolIdByPartId,
		string preferredSpoolId,
		out Connector cA,
		out Connector cB)
	{
		cA = null;
		cB = null;
		const double searchTol = 0.50;

		var open = new List<(Connector Conn, FabricationPart Owner, XYZ Origin)>();
		foreach (FabricationPart part in parts)
		{
			if (!string.IsNullOrWhiteSpace(preferredSpoolId)
				&& spoolIdByPartId != null
				&& spoolIdByPartId.TryGetValue(part.Id, out string partSpool)
				&& !string.IsNullOrWhiteSpace(partSpool)
				&& !string.Equals(partSpool.Trim(), preferredSpoolId.Trim(), StringComparison.OrdinalIgnoreCase))
			{
				continue;
			}

			foreach (Connector c in GetEndConnectors(part))
			{
				try
				{
					if (c == null || c.IsConnected || c.Origin == null)
					{
						continue;
					}
				}
				catch
				{
					continue;
				}

				open.Add((c, part, c.Origin));
			}
		}

		if (open.Count < 2
			&& !string.IsNullOrWhiteSpace(preferredSpoolId)
			&& spoolIdByPartId != null)
		{
			// Fall back without preferred-spool filter; still keep same-spool pairing.
			return TryFindOpenConnectorPairForWeld(
				parts, aPt, bPt, mid, spoolIdByPartId, null, out cA, out cB);
		}

		if (open.Count < 2)
		{
			return false;
		}

		// Prefer one connector near each weld face; fall back to two nearest distinct owners at mid.
		(Connector Conn, FabricationPart Owner, XYZ Origin)? nearA = null;
		(Connector Conn, FabricationPart Owner, XYZ Origin)? nearB = null;
		double bestA = searchTol;
		double bestB = searchTol;
		foreach (var entry in open)
		{
			double dA = entry.Origin.DistanceTo(aPt);
			double dB = entry.Origin.DistanceTo(bPt);
			if (dA < bestA)
			{
				bestA = dA;
				nearA = entry;
			}

			if (dB < bestB)
			{
				bestB = dB;
				nearB = entry;
			}
		}

		if (nearA != null && nearB != null
			&& !ReferenceEquals(nearA.Value.Owner, nearB.Value.Owner)
			&& !ReferenceEquals(nearA.Value.Conn, nearB.Value.Conn)
			&& ConnectorsSizeCompatible(nearA.Value.Conn, nearB.Value.Conn)
			&& SameSpool(nearA.Value.Owner.Id, nearB.Value.Owner.Id, spoolIdByPartId))
		{
			cA = nearA.Value.Conn;
			cB = nearB.Value.Conn;
			return true;
		}

		// Same-point weld or overlapping faces: two nearest open ends from different owners at mid.
		var ranked = open
			.OrderBy(e => e.Origin.DistanceTo(mid))
			.Where(e => e.Origin.DistanceTo(mid) <= searchTol)
			.ToList();
		for (int i = 0; i < ranked.Count; i++)
		{
			for (int j = i + 1; j < ranked.Count; j++)
			{
				if (ReferenceEquals(ranked[i].Owner, ranked[j].Owner))
				{
					continue;
				}

				if (!SameSpool(ranked[i].Owner.Id, ranked[j].Owner.Id, spoolIdByPartId))
				{
					continue;
				}

				if (!ConnectorsSizeCompatible(ranked[i].Conn, ranked[j].Conn))
				{
					continue;
				}

				cA = ranked[i].Conn;
				cB = ranked[j].Conn;
				return true;
			}
		}

		return false;
	}

	private static bool ConnectorsSizeCompatible(Connector a, Connector b)
	{
		try
		{
			double ra = a.Radius;
			double rb = b.Radius;
			if (ra <= 1e-9 || rb <= 1e-9)
			{
				return true;
			}

			double larger = Math.Max(ra, rb);
			double smaller = Math.Min(ra, rb);
			return (larger - smaller) / larger <= 0.15;
		}
		catch
		{
			return true;
		}
	}

	/// <summary>
	/// Snap open ends to exact coincidence, then ConnectAndCouple so the welded palette
	/// inserts its Shop Weld joint. Falls back to plain ConnectTo if couple fails.
	/// </summary>
	private static void TryConnectPair(Document doc, Connector a, Connector b, bool allowCouple)
	{
		TryConnectPair(doc, a, b, allowCouple, allowMoveFlange: true);
	}

	private static void TryConnectPair(
		Document doc,
		Connector a,
		Connector b,
		bool allowCouple,
		bool allowMoveFlange)
	{
		if (doc == null || a == null || b == null)
		{
			return;
		}

		try
		{
			if (a.IsConnected || b.IsConnected)
			{
				return;
			}
		}
		catch
		{
			return;
		}

		FabricationPart ownerA = a.Owner as FabricationPart;
		FabricationPart ownerB = b.Owner as FabricationPart;
		ElementId ownerIdA = ownerA?.Id ?? ElementId.InvalidElementId;
		ElementId ownerIdB = ownerB?.Id ?? ElementId.InvalidElementId;
		XYZ nearA = null;
		XYZ nearB = null;
		try { nearA = a.Origin; nearB = b.Origin; } catch { }

		bool flangePair = ownerA != null && ownerB != null
			&& FabricationPartClassification.IsFlangePart(ownerA, doc)
			&& FabricationPartClassification.IsFlangePart(ownerB, doc);

		double dist;
		try
		{
			dist = a.Origin.DistanceTo(b.Origin);
		}
		catch
		{
			return;
		}

		// Stretch a straight so its open end lands on the other connector before connecting.
		if (dist > 1e-6 && dist <= 0.15)
		{
			if (ownerA != null && ownerA.IsAStraight())
			{
				SnapStraightEndToPoint(doc, ownerA, a, b.Origin);
				a = RefreshConnector(doc, ownerA, b.Origin) ?? a;
			}
			else if (ownerB != null && ownerB.IsAStraight())
			{
				SnapStraightEndToPoint(doc, ownerB, b, a.Origin);
				b = RefreshConnector(doc, ownerB, a.Origin) ?? b;
			}

			try
			{
				dist = a.Origin.DistanceTo(b.Origin);
			}
			catch
			{
				return;
			}
		}

		// Final micro-nudge: move a straight (or flange) so connectors are coincident (couple needs this).
		if (dist > 1e-6 && dist <= 0.08)
		{
			if (ownerA != null && ownerA.IsAStraight() && !a.IsConnected)
			{
				XYZ move = b.Origin - a.Origin;
				if (move.GetLength() > 1e-9 && move.GetLength() <= 0.08)
				{
					ElementTransformUtils.MoveElement(doc, ownerA.Id, move);
					doc.Regenerate();
					a = RefreshConnector(doc, ownerA, b.Origin) ?? a;
				}
			}
			else if (ownerB != null && ownerB.IsAStraight() && !b.IsConnected)
			{
				XYZ move = a.Origin - b.Origin;
				if (move.GetLength() > 1e-9 && move.GetLength() <= 0.08)
				{
					ElementTransformUtils.MoveElement(doc, ownerB.Id, move);
					doc.Regenerate();
					b = RefreshConnector(doc, ownerB, a.Origin) ?? b;
				}
			}
			else if (allowMoveFlange
				&& ownerA != null && FabricationPartClassification.IsFlangePart(ownerA, doc) && !a.IsConnected)
			{
				XYZ move = b.Origin - a.Origin;
				if (move.GetLength() > 1e-9 && move.GetLength() <= 0.08)
				{
					ElementTransformUtils.MoveElement(doc, ownerA.Id, move);
					doc.Regenerate();
					a = RefreshConnector(doc, ownerA, b.Origin) ?? a;
				}
			}
			else if (allowMoveFlange
				&& ownerB != null && FabricationPartClassification.IsFlangePart(ownerB, doc) && !b.IsConnected)
			{
				XYZ move = a.Origin - b.Origin;
				if (move.GetLength() > 1e-9 && move.GetLength() <= 0.08)
				{
					ElementTransformUtils.MoveElement(doc, ownerB.Id, move);
					doc.Regenerate();
					b = RefreshConnector(doc, ownerB, a.Origin) ?? b;
				}
			}

			try
			{
				dist = a.Origin.DistanceTo(b.Origin);
			}
			catch
			{
				return;
			}
		}

		if (dist > 0.04)
		{
			// Flange face pairs often sit apart when catalog length ≠ PCF hub→face span.
			// Nudge along the joint (when allowed) then ConnectTo so the gasket seats.
			if (flangePair && dist <= 0.25)
			{
				if (allowMoveFlange)
				{
					XYZ move = b.Origin - a.Origin;
					if (move.GetLength() > 1e-9 && ownerA != null)
					{
						try
						{
							ElementTransformUtils.MoveElement(doc, ownerA.Id, move);
							doc.Regenerate();
							a = RefreshConnector(doc, ownerA, b.Origin) ?? a;
							dist = a.Origin.DistanceTo(b.Origin);
						}
						catch
						{
							return;
						}
					}

					if (dist > 0.04)
					{
						return;
					}
				}
				// !allowMoveFlange: fall through to ConnectTo with the gasket-scale gap.
			}
			else if (dist <= 0.12
				&& ((ownerA != null && ownerA.IsAStraight()
						&& ownerB != null && FabricationPartClassification.IsFlangePart(ownerB, doc))
					|| (ownerB != null && ownerB.IsAStraight()
						&& ownerA != null && FabricationPartClassification.IsFlangePart(ownerA, doc))))
			{
				// Pipe ↔ flange hub: stretch/snap the pipe only (never move the flange).
				if (ownerA != null && ownerA.IsAStraight())
				{
					SnapStraightEndToPoint(doc, ownerA, a, b.Origin);
					a = RefreshConnector(doc, ownerA, b.Origin) ?? a;
				}
				else if (ownerB != null && ownerB.IsAStraight())
				{
					SnapStraightEndToPoint(doc, ownerB, b, a.Origin);
					b = RefreshConnector(doc, ownerB, a.Origin) ?? b;
				}

				try
				{
					dist = a.Origin.DistanceTo(b.Origin);
				}
				catch
				{
					return;
				}

				if (dist > 0.04)
				{
					return;
				}
			}
			else
			{
				return;
			}
		}

		// Welded systems: ConnectAndCouple inserts the service Shop Weld between parts.
		// Flange pairs already represent the bolted joint — do not couple (adds gasket/extra flanges).
		if (allowCouple && !flangePair)
		{
			XYZ coupleMid = null;
			try { coupleMid = (a.Origin + b.Origin) * 0.5; } catch { coupleMid = nearA; }
			HashSet<ElementId> beforeIds = SnapshotFabricationPartIdsNear(doc, coupleMid, 2.0);
			using (SubTransaction st = new SubTransaction(doc))
			{
				try
				{
					st.Start();
					doc.Regenerate();
					if (FabricationPart.ConnectAndCouple(doc, a, b))
					{
						doc.Regenerate();
						// If the service injected an extra flange/gasket, undo the whole couple.
						// Do NOT delete insides the open subtransaction — that invalidates connectors.
						if (!CouplingIntroducedExtraFlangeOrGasket(doc, beforeIds, coupleMid))
						{
							st.Commit();
							return;
						}
					}

					st.RollBack();
				}
				catch
				{
					try { st.RollBack(); } catch { }
				}
			}

			// RollBack / Regenerate invalidate Connector refs — refresh before ConnectTo fallback.
			ownerA = doc.GetElement(ownerIdA) as FabricationPart;
			ownerB = doc.GetElement(ownerIdB) as FabricationPart;
			a = RefreshConnector(doc, ownerA, nearB ?? nearA);
			b = RefreshConnector(doc, ownerB, nearA ?? nearB);
			if (a == null || b == null)
			{
				return;
			}

			try
			{
				if (a.IsConnected || b.IsConnected)
				{
					return;
				}

				dist = a.Origin.DistanceTo(b.Origin);
			}
			catch
			{
				return;
			}

			if (dist > 0.04)
			{
				return;
			}
		}

		using (SubTransaction st = new SubTransaction(doc))
		{
			try
			{
				st.Start();
				a.ConnectTo(b);
				st.Commit();
				return;
			}
			catch
			{
				try { st.RollBack(); } catch { }
			}
		}

		using (SubTransaction st = new SubTransaction(doc))
		{
			try
			{
				st.Start();
				b.ConnectTo(a);
				st.Commit();
			}
			catch
			{
				try { st.RollBack(); } catch { }
			}
		}
	}

	private static HashSet<ElementId> SnapshotFabricationPartIdsNear(Document doc, XYZ center, double radiusFeet)
	{
		var ids = new HashSet<ElementId>();
		if (doc == null)
		{
			return ids;
		}

		if (center == null || radiusFeet <= 1e-9)
		{
			return ids;
		}

		try
		{
			XYZ offset = new XYZ(radiusFeet, radiusFeet, radiusFeet);
			var outline = new Outline(center - offset, center + offset);
			var filter = new BoundingBoxIntersectsFilter(outline);
			foreach (FabricationPart part in new FilteredElementCollector(doc)
				.OfClass(typeof(FabricationPart))
				.WhereElementIsNotElementType()
				.WherePasses(filter)
				.Cast<FabricationPart>())
			{
				if (part != null)
				{
					ids.Add(part.Id);
				}
			}
		}
		catch
		{
			// Fallback: empty snapshot — skip extra-flange stripping rather than hang.
		}

		return ids;
	}

	/// <summary>
	/// True when ConnectAndCouple inserted a flange/gasket/bolt in addition to (or instead of)
	/// a shop weld — that hardware stacks on the hub and reads as a misaligned step.
	/// </summary>
	private static bool CouplingIntroducedExtraFlangeOrGasket(
		Document doc,
		HashSet<ElementId> beforeIds,
		XYZ center)
	{
		if (doc == null || beforeIds == null)
		{
			return false;
		}

		foreach (ElementId id in SnapshotFabricationPartIdsNear(doc, center, 2.0))
		{
			if (beforeIds.Contains(id))
			{
				continue;
			}

			FabricationPart part = doc.GetElement(id) as FabricationPart;
			if (part == null)
			{
				continue;
			}

			if (FabricationPartClassification.IsFlangePart(part, doc)
				|| FabricationPartClassification.IsGasketPart(part)
				|| FabricationPartClassification.IsBoltKitPart(part))
			{
				return true;
			}
		}

		return false;
	}

	private static void TryConnectCouple(Document doc, Connector a, Connector b)
	{
		TryConnectPair(doc, a, b, allowCouple: true);
	}

	private static Connector RefreshConnector(Document doc, FabricationPart part, XYZ near)
	{
		if (doc == null || part == null || near == null)
		{
			return null;
		}

		part = doc.GetElement(part.Id) as FabricationPart;
		if (part == null)
		{
			return null;
		}

		return GetEndConnectors(part)
			.Where(c => c != null && !c.IsConnected)
			.OrderBy(c => c.Origin.DistanceTo(near))
			.FirstOrDefault();
	}

	private static void SnapStraightEndToPoint(Document doc, FabricationPart straight, Connector end, XYZ target)
	{
		if (doc == null || straight == null || end == null || target == null)
		{
			return;
		}

		if (!TryGetStraightAxis(straight, out XYZ origin, out XYZ dir, out double tMin, out double tMax))
		{
			return;
		}

		double tEnd = (end.Origin - origin).DotProduct(dir);
		double tTarget = (target - origin).DotProduct(dir);
		XYZ onAxis = origin + dir.Multiply(tTarget);
		double radial = target.DistanceTo(onAxis);
		if (radial > 0.35)
		{
			return;
		}

		// Never grow/shrink a pipe by more than 1' in a snap — stops stubs from being
		// dragged onto distant parallel-run connectors.
		if (Math.Abs(tTarget - tEnd) > 1.0)
		{
			return;
		}

		double newMin = tMin;
		double newMax = tMax;
		if (Math.Abs(tEnd - tMin) <= Math.Abs(tEnd - tMax))
		{
			newMin = tTarget;
		}
		else
		{
			newMax = tTarget;
		}

		if ((newMax - newMin) < MinSegmentLengthFeet)
		{
			return;
		}

		XYZ newStart = origin + dir.Multiply(newMin);
		XYZ newEnd = origin + dir.Multiply(newMax);
		if (!PlaceStraight(doc, straight, newStart, newEnd))
		{
			ForceStraightSpan(doc, straight, newStart, newEnd);
		}
	}

	/// <summary>
	/// Close small gaps between two open straight ends (shop-weld seams in the PCF).
	/// Never bridge into a neighboring spool — stacked header stubs sit ~1–1.5' apart.
	/// </summary>
	private static void AbutOpenStraightEndsTogether(
		Document doc,
		IList<ElementId> createdIds,
		IDictionary<ElementId, string> spoolIdByPartId = null)
	{
		if (doc == null || createdIds == null)
		{
			return;
		}

		List<FabricationPart> straights = createdIds
			.Select(id => doc.GetElement(id) as FabricationPart)
			.Where(p => p != null && p.IsAStraight())
			.ToList();

		const double maxGap = 0.25;
		for (int i = 0; i < straights.Count; i++)
		{
			FabricationPart a = straights[i];
			List<Connector> aEnds = GetEndConnectors(a).Where(c => c != null && !c.IsConnected).ToList();
			if (aEnds.Count == 0)
			{
				continue;
			}

			for (int j = i + 1; j < straights.Count; j++)
			{
				FabricationPart b = straights[j];
				if (!SameSpool(a.Id, b.Id, spoolIdByPartId))
				{
					continue;
				}

				List<Connector> bEnds = GetEndConnectors(b).Where(c => c != null && !c.IsConnected).ToList();
				if (bEnds.Count == 0)
				{
					continue;
				}

				Connector bestA = null;
				Connector bestB = null;
				double bestDist = maxGap;
				foreach (Connector ca in aEnds)
				{
					foreach (Connector cb in bEnds)
					{
						if (!ConnectorsSizeCompatible(ca, cb))
						{
							continue;
						}

						double d = ca.Origin.DistanceTo(cb.Origin);
						if (d < bestDist && d > 0.005)
						{
							bestDist = d;
							bestA = ca;
							bestB = cb;
						}
					}
				}

				if (bestA == null || bestB == null)
				{
					continue;
				}

				if (!TryGetStraightAxis(a, out XYZ originA, out XYZ dirA, out _, out _)
					|| !TryGetStraightAxis(b, out XYZ originB, out XYZ dirB, out _, out _))
				{
					continue;
				}

				if (Math.Abs(dirA.DotProduct(dirB)) < 0.95)
				{
					continue;
				}

				// Meet at the midpoint so both pipes share the joint.
				XYZ meet = (bestA.Origin + bestB.Origin) * 0.5;
				SnapStraightEndToPoint(doc, a, bestA, meet);
				SnapStraightEndToPoint(doc, b, bestB, meet);
			}
		}
	}

	private static FabricationPart CreateSizedPart(
		Document doc,
		FabricationServiceButton button,
		double sizeInches,
		ElementId levelId)
	{
		int condition = FindBestConditionIndex(button, sizeInches);
		FabricationPart part = null;
		try
		{
			part = FabricationPart.Create(doc, button, condition, levelId);
		}
		catch
		{
			part = null;
		}

		if (part != null)
		{
			return part;
		}

		try
		{
			double diameterFeet = sizeInches / 12.0;
			return FabricationPart.Create(doc, button, diameterFeet, diameterFeet, levelId);
		}
		catch
		{
			return null;
		}
	}

	private static int FindBestConditionIndex(FabricationServiceButton button, double sizeInches)
	{
		if (button == null || button.ConditionCount <= 0)
		{
			return 0;
		}

		double sizeFeet = sizeInches / 12.0;
		int best = 0;
		double bestScore = double.MaxValue;

		for (int i = 0; i < button.ConditionCount; i++)
		{
			if (!FabricationServiceButton.IsValidConditionIndex(button, i))
			{
				continue;
			}

			string name = (button.GetConditionName(i) ?? string.Empty) + " " + (button.GetConditionDescription(i) ?? string.Empty);
			double named = PcfParser.ParseSizeInches(name);
			if (named > 1e-6 && Math.Abs(named - sizeInches) < 0.051)
			{
				return i;
			}

			double lo = button.GetConditionLowerValue(i);
			double hi = button.GetConditionUpperValue(i);
			bool unrestricted = false;
			try
			{
				unrestricted = button.IsUnrestrictedCondition(i);
			}
			catch
			{
			}

			// Fabrication often uses -1 as "no limit" on one side — do not swap that into a tiny range.
			bool loOpen = lo < 0;
			bool hiOpen = hi < 0;
			if (!loOpen && !hiOpen && hi < lo)
			{
				double swap = lo;
				lo = hi;
				hi = swap;
			}

			bool matchFeet = unrestricted
				|| ((!loOpen && !hiOpen && sizeFeet >= (lo - 1e-6) && sizeFeet <= (hi + 1e-6))
					|| (loOpen && !hiOpen && sizeFeet <= (hi + 1e-6))
					|| (!loOpen && hiOpen && sizeFeet >= (lo - 1e-6))
					|| (loOpen && hiOpen));
			bool matchInches = unrestricted
				|| ((!loOpen && !hiOpen && sizeInches >= (lo - 1e-6) && sizeInches <= (hi + 1e-6))
					|| (loOpen && !hiOpen && sizeInches <= (hi + 1e-6))
					|| (!loOpen && hiOpen && sizeInches >= (lo - 1e-6))
					|| (loOpen && hiOpen));

			if (!matchFeet && !matchInches)
			{
				continue;
			}

			double midFeet = (!loOpen && !hiOpen) ? (lo + hi) * 0.5 : (loOpen ? hi : lo);
			double midInches = midFeet;
			double scoreFeet = Math.Abs(midFeet - sizeFeet);
			double scoreIn = Math.Abs(midInches - sizeInches);
			// Prefer the unit system that produced a match; otherwise take the closer score.
			double score = matchFeet && matchInches
				? Math.Min(scoreFeet, scoreIn)
				: (matchFeet ? scoreFeet : scoreIn);
			if (unrestricted)
			{
				score = Math.Min(scoreFeet, scoreIn);
			}

			if (score < bestScore)
			{
				bestScore = score;
				best = i;
			}
		}

		return best;
	}

	/// <summary>
	/// Palette Create defaults to the first product-list row (often 1/8"). Must set ProductListEntry to the NPS.
	/// </summary>
	private static bool TryApplyProductListSize(Document doc, FabricationPart part, double sizeInches)
	{
		try
		{
			if (part == null || sizeInches <= 1e-6)
			{
				return false;
			}

			if (GetConnectorDiameterInches(part) > 1e-6
				&& Math.Abs(GetConnectorDiameterInches(part) - sizeInches) <= 0.051)
			{
				return true;
			}

			if (!part.IsProductList())
			{
				return Math.Abs(GetConnectorDiameterInches(part) - sizeInches) <= 0.051;
			}

			int count = part.GetProductListEntryCount();
			int best = -1;
			double bestDelta = double.MaxValue;
			for (int i = 0; i < count; i++)
			{
				string name = part.GetProductListEntryName(i) ?? string.Empty;
				double parsed = PcfParser.ParseSizeInches(name);
				if (parsed <= 1e-6)
				{
					continue;
				}

				double delta = Math.Abs(parsed - sizeInches);
				if (delta < bestDelta)
				{
					bestDelta = delta;
					best = i;
				}
			}

			if (best < 0 || bestDelta > 0.051)
			{
				return false;
			}

			try
			{
				part.ProductListEntry = best;
			}
			catch
			{
				return false;
			}

			try
			{
				doc?.Regenerate();
			}
			catch
			{
			}

			part = doc?.GetElement(part.Id) as FabricationPart ?? part;
			double actual = GetConnectorDiameterInches(part);
			return actual > 1e-6 && Math.Abs(actual - sizeInches) <= 0.051;
		}
		catch
		{
			return false;
		}
	}

	private static double GetConnectorDiameterInches(FabricationPart part)
	{
		try
		{
			ConnectorManager manager = part?.ConnectorManager;
			if (manager?.Connectors == null)
			{
				return 0.0;
			}

			double best = 0.0;
			foreach (Connector connector in manager.Connectors)
			{
				if (connector == null)
				{
					continue;
				}

				try
				{
					double diameterInches = connector.Radius * 2.0 * 12.0;
					if (diameterInches > best)
					{
						best = diameterInches;
					}
				}
				catch
				{
				}
			}

			return best;
		}
		catch
		{
			return 0.0;
		}
	}

	private static void TrySetSizeParameters(FabricationPart part, double sizeInches, string sizeText)
	{
		string label = !string.IsNullOrWhiteSpace(sizeText) ? sizeText.Trim() : FormatSizeLabel(sizeInches);
		foreach (string name in new[] { "Product Entry", "S-Size", "E-Size", "Size" })
		{
			try
			{
				Parameter parameter = ((Element)part).LookupParameter(name);
				if (parameter != null && !parameter.IsReadOnly && parameter.StorageType == StorageType.String)
				{
					parameter.Set(label);
				}
			}
			catch
			{
			}
		}

		try
		{
			Parameter diameter = ((Element)part).LookupParameter("Main Primary Diameter");
			if (diameter != null && !diameter.IsReadOnly && diameter.StorageType == StorageType.Double)
			{
				diameter.Set(sizeInches / 12.0);
			}
		}
		catch
		{
		}
	}

	private static string FormatSizeLabel(double sizeInches)
	{
		if (Math.Abs(sizeInches - Math.Round(sizeInches)) < 1e-6)
		{
			return Math.Round(sizeInches).ToString("0", CultureInfo.InvariantCulture) + "\"";
		}

		return sizeInches.ToString("0.###", CultureInfo.InvariantCulture) + "\"";
	}

	private static bool PlaceStraight(Document doc, FabricationPart part, XYZ start, XYZ end)
	{
		List<Connector> connectors = GetEndConnectors(part);
		if (connectors.Count < 2)
		{
			return false;
		}

		XYZ targetDir = end - start;
		double length = targetDir.GetLength();
		if (length < MinSegmentLengthFeet)
		{
			return false;
		}

		targetDir = targetDir.Normalize();

		// Stock straights often create at 20' and CanAdjustEndLength is false — set Length dim instead.
		if (!TrySetStraightLength(doc, part, length))
		{
			return false;
		}

		part = doc.GetElement(part.Id) as FabricationPart;
		connectors = GetEndConnectors(part);
		if (part == null || connectors.Count < 2)
		{
			return false;
		}

		OrientPartAlongDirection(doc, part, connectors[0].Origin, GetConnectorAxis(connectors[0], connectors[1]), targetDir);
		part = doc.GetElement(part.Id) as FabricationPart;
		connectors = GetEndConnectors(part);
		if (part == null || connectors.Count < 2)
		{
			return false;
		}

		// Anchor the connector nearest the start point, then verify the far end.
		Connector cStart = connectors.OrderBy(c => c.Origin.DistanceTo(start)).First();
		XYZ move = start - cStart.Origin;
		if (move.GetLength() > 1e-9)
		{
			ElementTransformUtils.MoveElement(doc, part.Id, move);
		}

		part = doc.GetElement(part.Id) as FabricationPart;
		connectors = GetEndConnectors(part);
		if (part == null || connectors.Count < 2)
		{
			return false;
		}

		cStart = connectors.OrderBy(c => c.Origin.DistanceTo(start)).First();
		Connector cEnd = connectors.OrderBy(c => c.Origin.DistanceTo(end)).First();
		if (ReferenceEquals(cStart, cEnd))
		{
			cEnd = connectors.First(c => !ReferenceEquals(c, cStart));
		}

		// If oriented 180° wrong, flip about the mid-point.
		if (cEnd.Origin.DistanceTo(end) > 0.05 && cStart.Origin.DistanceTo(start) <= 0.05)
		{
			XYZ axis = GetConnectorAxis(cStart, cEnd);
			XYZ mid = (start + end) * 0.5;
			XYZ flipAxis = Math.Abs(axis.DotProduct(XYZ.BasisZ)) < 0.9
				? axis.CrossProduct(XYZ.BasisZ)
				: axis.CrossProduct(XYZ.BasisX);
			if (flipAxis.GetLength() > 1e-9)
			{
				Line rotationAxis = Line.CreateBound(mid, mid + flipAxis.Normalize());
				ElementTransformUtils.RotateElement(doc, part.Id, rotationAxis, Math.PI);
				part = doc.GetElement(part.Id) as FabricationPart;
				connectors = GetEndConnectors(part);
				if (part == null || connectors.Count < 2)
				{
					return false;
				}

				cStart = connectors.OrderBy(c => c.Origin.DistanceTo(start)).First();
				move = start - cStart.Origin;
				if (move.GetLength() > 1e-8)
				{
					ElementTransformUtils.MoveElement(doc, part.Id, move);
				}

				part = doc.GetElement(part.Id) as FabricationPart;
				connectors = GetEndConnectors(part);
			}
		}

		if (part == null || connectors == null || connectors.Count < 2)
		{
			return false;
		}

		cStart = connectors.OrderBy(c => c.Origin.DistanceTo(start)).First();
		cEnd = connectors.OrderBy(c => c.Origin.DistanceTo(end)).First();
		if (ReferenceEquals(cStart, cEnd) && connectors.Count > 1)
		{
			cEnd = connectors.First(c => !ReferenceEquals(c, cStart));
		}

		double err = cStart.Origin.DistanceTo(start) + cEnd.Origin.DistanceTo(end);
		double lengthErr = Math.Abs(part.CenterlineLength - length);
		return err < 0.25 || lengthErr <= 0.05;
	}

	private static bool TrySetStraightLength(Document doc, FabricationPart part, double lengthFeet)
	{
		if (part == null || lengthFeet < MinSegmentLengthFeet)
		{
			return false;
		}

		try
		{
			IList<FabricationDimensionDefinition> dims = part.GetDimensions();
			if (dims != null)
			{
				foreach (FabricationDimensionDefinition dim in dims)
				{
					if (dim == null)
					{
						continue;
					}

					bool isLength = false;
					try
					{
						isLength = dim.Type == FabricationDimensionType.Length;
					}
					catch
					{
					}

					string name = dim.Name ?? string.Empty;
					if (!isLength && name.IndexOf("Length", StringComparison.OrdinalIgnoreCase) < 0)
					{
						continue;
					}

					try
					{
						part.SetDimensionValue(dim, lengthFeet);
						doc?.Regenerate();
						part = doc?.GetElement(part.Id) as FabricationPart ?? part;
						if (Math.Abs(part.CenterlineLength - lengthFeet) <= 0.02)
						{
							return true;
						}

						// Some product-list rows clamp Length (often ~20'). Keep pushing via AdjustEndLength.
						if (TryAdjustStraightToLength(doc, ref part, lengthFeet))
						{
							return true;
						}
					}
					catch
					{
					}
				}
			}
		}
		catch
		{
		}

		// Fallback when Length dim is unavailable or clamped.
		if (TryAdjustStraightToLength(doc, ref part, lengthFeet))
		{
			return true;
		}

		return Math.Abs(part.CenterlineLength - lengthFeet) <= 0.02;
	}

	/// <summary>
	/// Grow/shrink a straight to an exact centerline length. No 20' stock limit — PCF length wins
	/// whether that is 15' or 100'.
	/// </summary>
	private static bool TryAdjustStraightToLength(Document doc, ref FabricationPart part, double lengthFeet)
	{
		if (part == null || lengthFeet < MinSegmentLengthFeet)
		{
			return false;
		}

		try
		{
			for (int attempt = 0; attempt < 8; attempt++)
			{
				double current = part.CenterlineLength;
				double delta = lengthFeet - current;
				if (Math.Abs(delta) <= 0.02)
				{
					return true;
				}

				List<Connector> connectors = GetEndConnectors(part);
				Connector adjustable = null;
				foreach (Connector connector in connectors)
				{
					if (connector != null && part.CanAdjustEndLength(connector))
					{
						adjustable = connector;
						break;
					}
				}

				if (adjustable == null)
				{
					return Math.Abs(part.CenterlineLength - lengthFeet) <= 0.02;
				}

				part.AdjustEndLength(adjustable, delta, true);
				doc?.Regenerate();
				part = doc?.GetElement(part.Id) as FabricationPart ?? part;
			}
		}
		catch
		{
		}

		return Math.Abs(part.CenterlineLength - lengthFeet) <= 0.02;
	}

	/// <summary>
	/// Pick SR vs LR (etc.) by comparing expected face-to-face chord to the PCF END-POINT distance.
	/// This content pack's "90° Ell" is short-radius; "90° LR Ell" is long-radius.
	/// </summary>
	private static FabricationServiceButton PickElbowButton(
		IList<FabricationServiceButton> elbows,
		double sizeInches,
		double targetChordFeet)
	{
		if (elbows == null || elbows.Count == 0 || sizeInches <= 1e-6 || targetChordFeet <= 1e-9)
		{
			return elbows?.FirstOrDefault();
		}

		// PCF chord / (NPS/12) / √2 ≈ 1.5 for LR, ≈ 1.0 for SR.
		double impliedRadiusFactor = targetChordFeet / (sizeInches / 12.0) / Math.Sqrt(2.0);
		bool wantLongRadius = impliedRadiusFactor >= 1.25;

		FabricationServiceButton best = elbows[0];
		double bestScore = double.MaxValue;
		foreach (FabricationServiceButton button in elbows)
		{
			if (button == null)
			{
				continue;
			}

			string corpus = ((button.Name ?? string.Empty) + " " + (button.Code ?? string.Empty)).ToUpperInvariant();
			double factor = GuessElbowRadiusFactor(button);
			// Unlabeled "90° Ell" defaults to SR in GuessElbowRadiusFactor — override from PCF chord.
			if (!corpus.Contains("LONG") && !corpus.Contains("SHORT")
				&& !corpus.Contains(" LR") && !corpus.Contains("LR ")
				&& !corpus.Contains(" SR") && !corpus.Contains("SR ")
				&& !corpus.Contains("LRELL") && !corpus.Contains("SRELL"))
			{
				factor = wantLongRadius ? 1.5 : 1.0;
			}

			double radiusFeet = factor * sizeInches / 12.0;
			double expectedChord = radiusFeet * Math.Sqrt(2.0);
			double score = Math.Abs(expectedChord - targetChordFeet);
			if (corpus.Contains("45"))
			{
				score += 1.0; // PCF elbows here are 90°
			}

			if (corpus.Contains("WELDBEND"))
			{
				score += 0.02;
			}

			if (wantLongRadius && (corpus.Contains("LONG RADIUS") || corpus.Contains(" LR") || corpus.Contains("LRELL")))
			{
				score -= 0.05;
			}

			if (!wantLongRadius && (corpus.Contains("SHORT RADIUS") || corpus.Contains(" SR") || corpus.Contains("SRELL")))
			{
				score -= 0.05;
			}

			if (wantLongRadius && (corpus.Contains("SHORT RADIUS") || corpus.Contains(" SR") || corpus.Contains("SRELL")))
			{
				score += 0.2;
			}

			if (score < bestScore)
			{
				bestScore = score;
				best = button;
			}
		}

		return best;
	}

	private static double GuessElbowRadiusFactor(FabricationServiceButton button)
	{
		string corpus = (((button?.Name ?? string.Empty) + " " + (button?.Code ?? string.Empty))).ToUpperInvariant();
		if (corpus.Contains("LONG RADIUS")
			|| corpus.Contains(" LR")
			|| corpus.Contains("LR ")
			|| corpus.Contains("LRELL")
			|| corpus.Contains("LR ELL")
			|| corpus.Contains("LR-ELL")
			|| corpus.Contains("90 LR")
			|| corpus.Contains("90° LR")
			|| corpus.Contains("90 DEG LR"))
		{
			return 1.5;
		}

		if (corpus.Contains("SHORT RADIUS")
			|| corpus.Contains(" SR")
			|| corpus.Contains("SR ")
			|| corpus.Contains("SRELL")
			|| corpus.Contains("SR ELL")
			|| corpus.Contains("90 SR")
			|| corpus.Contains("90° SR"))
		{
			return 1.0;
		}

		// Bare "90° Ell" / Weldbend in this fabrication database tracks short-radius takeout.
		return 1.0;
	}

	private static bool PlaceElbow(
		Document doc,
		FabricationPart part,
		PcfDocument pcf,
		PcfComponent component,
		XYZ start,
		XYZ end)
	{
		List<Connector> byId = FabricationConnectorEnds.GetEndConnectorsById(part);
		if (byId.Count < 2)
		{
			return false;
		}

		// ISOGEN CENTRE-POINT + C1/C2 order: EP0→C1, EP1→C2 (never remap by axis guess).
		if (component?.CentrePoint != null)
		{
			XYZ centre = component.CentrePoint;
			XYZ outwardStart = start - centre;
			XYZ outwardEnd = end - centre;
			if (outwardStart.GetLength() > 1e-9 && outwardEnd.GetLength() > 1e-9)
			{
				if (PlaceElbowByAxes(
					doc,
					part,
					start,
					end,
					outwardStart.Normalize(),
					outwardEnd.Normalize(),
					byId[0],
					byId[1]))
				{
					return true;
				}
			}
		}

		TryResolveElbowOutwardDirections(pcf, component, start, end, out XYZ outwardStart2, out XYZ outwardEnd2);

		if (outwardStart2 != null && outwardEnd2 != null)
		{
			if (PlaceElbowByAxes(doc, part, start, end, outwardStart2, outwardEnd2, byId[0], byId[1]))
			{
				return true;
			}
		}

		// One adjoining run (common when PCF omits the other leg): pin that face/axis, then swing.
		if (outwardStart2 != null || outwardEnd2 != null)
		{
			XYZ knownOut = outwardStart2 ?? outwardEnd2;
			XYZ knownFace = outwardStart2 != null ? start : end;
			XYZ otherFace = outwardStart2 != null ? end : start;
			Connector knownConn = outwardStart2 != null ? byId[0] : byId[1];
			Connector otherConn = outwardStart2 != null ? byId[1] : byId[0];
			if (PlaceElbowByAxes(
				doc,
				part,
				knownFace,
				otherFace,
				knownOut,
				InferMissingElbowOutward(knownFace, otherFace, knownOut),
				knownConn,
				otherConn))
			{
				return true;
			}
		}

		return PlaceElbowByChord(doc, part, start, end);
	}

	/// <summary>
	/// For a 90° elbow with one known leg direction, the other leg is perpendicular in the plane
	/// of (knownOut × chord).
	/// </summary>
	private static XYZ InferMissingElbowOutward(XYZ knownFace, XYZ otherFace, XYZ knownOut)
	{
		if (knownFace == null || otherFace == null || knownOut == null)
		{
			return null;
		}

		XYZ chord = otherFace - knownFace;
		if (chord.GetLength() < 1e-9)
		{
			return null;
		}

		XYZ planeNormal = knownOut.Normalize().CrossProduct(chord.Normalize());
		if (planeNormal.GetLength() < 1e-9)
		{
			planeNormal = Math.Abs(knownOut.DotProduct(XYZ.BasisZ)) < 0.9
				? knownOut.CrossProduct(XYZ.BasisZ)
				: knownOut.CrossProduct(XYZ.BasisX);
		}

		if (planeNormal.GetLength() < 1e-9)
		{
			return null;
		}

		XYZ inferred = planeNormal.Normalize().CrossProduct(knownOut.Normalize());
		if (inferred.GetLength() < 1e-9)
		{
			return null;
		}

		inferred = inferred.Normalize();
		// Pick the sense that points roughly from known face toward the other face.
		if (inferred.DotProduct(chord) < 0)
		{
			inferred = inferred.Negate();
		}

		return inferred;
	}

	private static void TryResolveElbowOutwardDirections(
		PcfDocument pcf,
		PcfComponent elbow,
		XYZ start,
		XYZ end,
		out XYZ outwardStart,
		out XYZ outwardEnd)
	{
		outwardStart = FindRunOutwardFromElbow(pcf, elbow, start);
		outwardEnd = FindRunOutwardFromElbow(pcf, elbow, end);
	}

	/// <summary>
	/// Outward connector axis at an elbow face = direction from that face along the adjoining run
	/// (toward the run's far END-POINT). Prefer pipe / weld / flange neighbors — never another
	/// elbow's far port (that diagonal flips back-to-back elbows into the wrong plane).
	/// </summary>
	private static XYZ FindRunOutwardFromElbow(PcfDocument pcf, PcfComponent elbow, XYZ face)
	{
		if (pcf == null || face == null)
		{
			return null;
		}

		const double tol = 0.08;
		XYZ best = null;
		double bestScore = double.MinValue;
		foreach (PcfComponent other in pcf.Components)
		{
			if (other == null || ReferenceEquals(other, elbow) || other.EndPoints.Count < 2)
			{
				continue;
			}

			// Sister fittings at a shared face: their opposite port is not this connector's axis.
			if (other.IsElbow || other.IsTee)
			{
				continue;
			}

			XYZ a = other.EndPoints[0].Point;
			XYZ b = other.EndPoints[1].Point;
			if (a == null || b == null)
			{
				continue;
			}

			XYZ outward = null;
			if (a.DistanceTo(face) <= tol)
			{
				outward = b - a;
			}
			else if (b.DistanceTo(face) <= tol)
			{
				outward = a - b;
			}

			if (outward == null || outward.GetLength() < 1e-6)
			{
				continue;
			}

			double len = outward.GetLength();
			// Prefer real pipe runs; welds are short but give the correct local joint axis.
			double typeBonus = other.IsStraightPipe ? 100.0
				: (other.IsWeld ? 50.0
				: (other.IsFlange || other.IsCap ? 25.0
				: (other.IsInlineFitting ? 40.0 : 0.0)));
			double score = typeBonus + Math.Min(len, 20.0);
			if (score > bestScore)
			{
				bestScore = score;
				best = outward.Normalize();
			}
		}

		// Last resort: adjacent elbow/tee — use the vector between the shared faces only
		// (not through the sister fitting to its far port).
		if (best == null)
		{
			best = FindOutwardFromAdjacentFittingFace(pcf, elbow, face, tol);
		}

		return best;
	}

	private static XYZ FindOutwardFromAdjacentFittingFace(
		PcfDocument pcf,
		PcfComponent elbow,
		XYZ face,
		double tol)
	{
		XYZ best = null;
		double bestDist = tol;
		foreach (PcfComponent other in pcf.Components)
		{
			if (other == null || ReferenceEquals(other, elbow) || (!other.IsElbow && !other.IsTee))
			{
				continue;
			}

			foreach (PcfEndPoint ep in other.EndPoints)
			{
				if (ep?.Point == null)
				{
					continue;
				}

				double d = ep.Point.DistanceTo(face);
				if (d < 1e-6 || d > bestDist)
				{
					continue;
				}

				XYZ dir = ep.Point - face;
				if (dir.GetLength() < 1e-9)
				{
					continue;
				}

				bestDist = d;
				best = dir.Normalize();
			}
		}

		return best;
	}

	private static bool PlaceElbowByAxes(
		Document doc,
		FabricationPart part,
		XYZ start,
		XYZ end,
		XYZ outwardStart,
		XYZ outwardEnd,
		Connector forcedStart = null,
		Connector forcedEnd = null)
	{
		int idStart = forcedStart != null ? forcedStart.Id : -1;
		int idEnd = forcedEnd != null ? forcedEnd.Id : -1;

		if (!TryResolveElbowConnectorPair(
			part,
			outwardStart,
			outwardEnd,
			idStart,
			idEnd,
			out Connector cStart,
			out Connector cEnd))
		{
			return false;
		}

		XYZ move = start - cStart.Origin;
		if (move.GetLength() > 1e-9)
		{
			ElementTransformUtils.MoveElement(doc, part.Id, move);
		}

		part = doc.GetElement(part.Id) as FabricationPart;
		if (!TryResolveElbowConnectorPair(part, outwardStart, outwardEnd, idStart, idEnd, out cStart, out cEnd))
		{
			return false;
		}

		XYZ axis0 = GetConnectorOutwardAxis(cStart);
		if (axis0 != null)
		{
			OrientPartAlongDirection(doc, part, start, axis0, outwardStart);
		}

		part = doc.GetElement(part.Id) as FabricationPart;
		if (!TryResolveElbowConnectorPair(part, outwardStart, outwardEnd, idStart, idEnd, out cStart, out cEnd))
		{
			return false;
		}

		// Swing the second leg about the first outward axis so cEnd lands on `end`.
		XYZ swingAxis = outwardStart;
		XYZ fromVec = ProjectPerpendicularToAxis(cEnd.Origin - start, swingAxis);
		XYZ toVec = ProjectPerpendicularToAxis(end - start, swingAxis);
		if (fromVec.GetLength() > 1e-8 && toVec.GetLength() > 1e-8)
		{
			double angle = SignedAngleAroundAxis(fromVec.Normalize(), toVec.Normalize(), swingAxis);
			if (Math.Abs(angle) > 1e-6)
			{
				Line rotationAxis = Line.CreateBound(start, start + swingAxis);
				ElementTransformUtils.RotateElement(doc, part.Id, rotationAxis, angle);
			}
		}

		part = doc.GetElement(part.Id) as FabricationPart;
		if (!TryResolveElbowConnectorPair(part, outwardStart, outwardEnd, idStart, idEnd, out cStart, out cEnd))
		{
			return false;
		}

		XYZ nudge = start - cStart.Origin;
		if (nudge.GetLength() > 1e-8)
		{
			ElementTransformUtils.MoveElement(doc, part.Id, nudge);
		}

		part = doc.GetElement(part.Id) as FabricationPart;
		if (!TryResolveElbowConnectorPair(part, outwardStart, outwardEnd, idStart, idEnd, out cStart, out cEnd))
		{
			return false;
		}

		double posErr = cStart.Origin.DistanceTo(start) + cEnd.Origin.DistanceTo(end);
		if (posErr > 0.15)
		{
			return false;
		}

		XYZ axisStart = GetConnectorOutwardAxis(cStart);
		XYZ axisEnd = GetConnectorOutwardAxis(cEnd);
		if (axisStart == null || axisEnd == null)
		{
			return posErr < 0.12;
		}

		double alignStart = axisStart.Normalize().DotProduct(outwardStart.Normalize());
		double alignEnd = axisEnd.Normalize().DotProduct(outwardEnd.Normalize());
		if (alignStart >= 0.75 && alignEnd >= 0.75)
		{
			return true;
		}

		// Do not mirror-flip when connectors are locked to C1/C2 — that swaps ends.
		if (idStart >= 0 && idEnd >= 0)
		{
			return posErr < 0.12;
		}

		// Mirrored bend (legacy axis-picked path only): rotate 180° about the chord mid-axis.
		XYZ chord = end - start;
		if (chord.GetLength() < 1e-9)
		{
			return false;
		}

		XYZ mid = (start + end) * 0.5;
		Line flipAxis = Line.CreateBound(mid, mid + chord.Normalize());
		try
		{
			ElementTransformUtils.RotateElement(doc, part.Id, flipAxis, Math.PI);
		}
		catch
		{
			return false;
		}

		part = doc.GetElement(part.Id) as FabricationPart;
		if (!TryResolveElbowConnectorPair(part, outwardStart, outwardEnd, idStart, idEnd, out cStart, out cEnd))
		{
			return false;
		}

		nudge = start - cStart.Origin;
		if (nudge.GetLength() > 1e-8)
		{
			ElementTransformUtils.MoveElement(doc, part.Id, nudge);
		}

		part = doc.GetElement(part.Id) as FabricationPart;
		if (!TryResolveElbowConnectorPair(part, outwardStart, outwardEnd, idStart, idEnd, out cStart, out cEnd))
		{
			return false;
		}

		posErr = cStart.Origin.DistanceTo(start) + cEnd.Origin.DistanceTo(end);
		axisStart = GetConnectorOutwardAxis(cStart);
		axisEnd = GetConnectorOutwardAxis(cEnd);
		if (posErr > 0.15 || axisStart == null || axisEnd == null)
		{
			return false;
		}

		alignStart = axisStart.Normalize().DotProduct(outwardStart.Normalize());
		alignEnd = axisEnd.Normalize().DotProduct(outwardEnd.Normalize());
		return alignStart >= 0.75 && alignEnd >= 0.75;
	}

	private static bool TryResolveElbowConnectorPair(
		FabricationPart part,
		XYZ outwardStart,
		XYZ outwardEnd,
		int idStart,
		int idEnd,
		out Connector cStart,
		out Connector cEnd)
	{
		cStart = null;
		cEnd = null;
		part = part == null ? null : part.Document?.GetElement(part.Id) as FabricationPart ?? part;
		List<Connector> connectors = FabricationConnectorEnds.GetEndConnectorsById(part);
		if (part == null || connectors.Count < 2)
		{
			return false;
		}

		if (idStart >= 0 && idEnd >= 0)
		{
			cStart = connectors.FirstOrDefault(c => c.Id == idStart);
			cEnd = connectors.FirstOrDefault(c => c.Id == idEnd);
			if (cStart != null && cEnd != null && !ReferenceEquals(cStart, cEnd))
			{
				return true;
			}

			cStart = connectors[0];
			cEnd = connectors[1];
			return true;
		}

		if (TryPickElbowConnectors(connectors, outwardStart, outwardEnd, out cStart, out cEnd))
		{
			return true;
		}

		cStart = connectors[0];
		cEnd = connectors[1];
		return true;
	}

	private static bool PlaceElbowByChord(Document doc, FabricationPart part, XYZ start, XYZ end)
	{
		return PlaceByConnectorEndPoints(doc, part, start, end);
	}

	/// <summary>
	/// Pin Edit Part C1 → END-POINT[0], C2 → END-POINT[1]. Never flip 180° (that swaps solder/FPT).
	/// </summary>
	private static bool PlaceByConnectorEndPoints(Document doc, FabricationPart part, XYZ ep0, XYZ ep1)
	{
		if (doc == null || part == null || ep0 == null || ep1 == null)
		{
			return false;
		}

		List<Connector> connectors = FabricationConnectorEnds.GetEndConnectorsById(part);
		if (connectors.Count < 2)
		{
			return false;
		}

		int id0 = connectors[0].Id;
		int id1 = connectors[1].Id;
		XYZ targetDir = ep1 - ep0;
		if (targetDir.GetLength() < 1e-9)
		{
			return false;
		}

		targetDir = targetDir.Normalize();

		XYZ move = ep0 - connectors[0].Origin;
		if (move.GetLength() > 1e-9)
		{
			ElementTransformUtils.MoveElement(doc, part.Id, move);
		}

		part = doc.GetElement(part.Id) as FabricationPart;
		connectors = FabricationConnectorEnds.GetEndConnectorsById(part);
		Connector c0 = connectors.FirstOrDefault(c => c.Id == id0) ?? connectors[0];
		Connector c1 = connectors.FirstOrDefault(c => c.Id == id1) ?? connectors[1];
		if (part == null || c0 == null || c1 == null)
		{
			return false;
		}

		XYZ from = c1.Origin - c0.Origin;
		if (from.GetLength() > 1e-9)
		{
			OrientPartAlongDirection(doc, part, ep0, from.Normalize(), targetDir);
		}

		part = doc.GetElement(part.Id) as FabricationPart;
		connectors = FabricationConnectorEnds.GetEndConnectorsById(part);
		c0 = connectors.FirstOrDefault(c => c.Id == id0) ?? connectors[0];
		c1 = connectors.FirstOrDefault(c => c.Id == id1) ?? connectors[1];
		if (part == null || c0 == null || c1 == null)
		{
			return false;
		}

		// Center on the PCF span while keeping C1→C2 along EP0→EP1 (never 180-flip — that swaps solder/FPT).
		XYZ midTarget = (ep0 + ep1) * 0.5;
		XYZ midConn = (c0.Origin + c1.Origin) * 0.5;
		XYZ centerMove = midTarget - midConn;
		if (centerMove.GetLength() > 1e-9)
		{
			ElementTransformUtils.MoveElement(doc, part.Id, centerMove);
		}

		part = doc.GetElement(part.Id) as FabricationPart;
		connectors = FabricationConnectorEnds.GetEndConnectorsById(part);
		c0 = connectors.FirstOrDefault(c => c.Id == id0) ?? connectors[0];
		c1 = connectors.FirstOrDefault(c => c.Id == id1) ?? connectors[1];
		if (part == null || c0 == null || c1 == null)
		{
			return false;
		}

		// Pin C1 exactly to EP0 (axial).
		XYZ finalNudge = ep0 - c0.Origin;
		if (finalNudge.GetLength() > 1e-8)
		{
			ElementTransformUtils.MoveElement(doc, part.Id, finalNudge);
			part = doc.GetElement(part.Id) as FabricationPart;
			connectors = FabricationConnectorEnds.GetEndConnectorsById(part);
			c0 = connectors.FirstOrDefault(c => c.Id == id0) ?? connectors[0];
			c1 = connectors.FirstOrDefault(c => c.Id == id1) ?? connectors[1];
		}

		if (part == null || c0 == null || c1 == null)
		{
			return false;
		}

		// C1 must remain the EP0-side connector.
		XYZ along = c1.Origin - c0.Origin;
		if (along.GetLength() > 1e-9 && along.Normalize().DotProduct(targetDir) < 0)
		{
			return false;
		}

		return c0.Origin.DistanceTo(ep0) <= 0.20
			&& RadialDistanceToSegment(c0.Origin, ep0, ep1) <= 0.05
			&& RadialDistanceToSegment(c1.Origin, ep0, ep1) <= 0.05;
	}

	private static bool TryPickElbowConnectors(
		List<Connector> connectors,
		XYZ outwardStart,
		XYZ outwardEnd,
		out Connector cStart,
		out Connector cEnd)
	{
		cStart = null;
		cEnd = null;
		if (connectors == null || connectors.Count < 2 || outwardStart == null || outwardEnd == null)
		{
			return false;
		}

		Connector bestA = null;
		Connector bestB = null;
		double bestScore = double.MaxValue;
		foreach (Connector a in connectors)
		{
			XYZ axisA = GetConnectorOutwardAxis(a);
			if (axisA == null)
			{
				continue;
			}

			foreach (Connector b in connectors)
			{
				if (ReferenceEquals(a, b))
				{
					continue;
				}

				XYZ axisB = GetConnectorOutwardAxis(b);
				if (axisB == null)
				{
					continue;
				}

				double score = (1.0 - axisA.DotProduct(outwardStart)) + (1.0 - axisB.DotProduct(outwardEnd));
				if (score < bestScore)
				{
					bestScore = score;
					bestA = a;
					bestB = b;
				}
			}
		}

		if (bestA == null || bestB == null)
		{
			return false;
		}

		cStart = bestA;
		cEnd = bestB;
		return true;
	}

	private static XYZ GetConnectorOutwardAxis(Connector connector)
	{
		try
		{
			XYZ axis = connector?.CoordinateSystem?.BasisZ;
			if (axis != null && axis.GetLength() > 1e-9)
			{
				return axis.Normalize();
			}
		}
		catch
		{
		}

		return null;
	}

	private static XYZ ProjectPerpendicularToAxis(XYZ vector, XYZ axis)
	{
		if (vector == null || axis == null)
		{
			return XYZ.Zero;
		}

		XYZ n = axis.Normalize();
		return vector - n.Multiply(vector.DotProduct(n));
	}

	private static double SignedAngleAroundAxis(XYZ from, XYZ to, XYZ axis)
	{
		XYZ n = axis.Normalize();
		double dot = Math.Max(-1.0, Math.Min(1.0, from.DotProduct(to)));
		double angle = Math.Acos(dot);
		XYZ cross = from.CrossProduct(to);
		if (cross.DotProduct(n) < 0)
		{
			angle = -angle;
		}

		return angle;
	}

	private static void OrientPartAlongDirection(Document doc, FabricationPart part, XYZ pivot, XYZ fromDir, XYZ toDir)
	{
		if (fromDir == null || toDir == null || pivot == null)
		{
			return;
		}

		XYZ a = fromDir.Normalize();
		XYZ b = toDir.Normalize();
		double dot = Math.Max(-1.0, Math.Min(1.0, a.DotProduct(b)));
		if (dot > 0.999999)
		{
			return;
		}

		XYZ axis;
		double angle;
		if (dot < -0.999999)
		{
			axis = Math.Abs(a.X) < 0.9 ? XYZ.BasisX.CrossProduct(a) : XYZ.BasisY.CrossProduct(a);
			if (axis.GetLength() < 1e-9)
			{
				return;
			}

			axis = axis.Normalize();
			angle = Math.PI;
		}
		else
		{
			axis = a.CrossProduct(b);
			if (axis.GetLength() < 1e-9)
			{
				return;
			}

			axis = axis.Normalize();
			angle = Math.Acos(dot);
		}

		Line rotationAxis = Line.CreateBound(pivot, pivot + axis);
		ElementTransformUtils.RotateElement(doc, part.Id, rotationAxis, angle);
	}

	private static XYZ GetConnectorAxis(Connector a, Connector b)
	{
		XYZ delta = b.Origin - a.Origin;
		if (delta.GetLength() > 1e-9)
		{
			return delta.Normalize();
		}

		try
		{
			XYZ basis = a.CoordinateSystem?.BasisZ;
			if (basis != null && basis.GetLength() > 1e-9)
			{
				return basis.Normalize();
			}
		}
		catch
		{
		}

		return XYZ.BasisX;
	}

	private static List<Connector> GetEndConnectors(FabricationPart part)
	{
		var list = new List<Connector>();
		try
		{
			ConnectorManager manager = part?.ConnectorManager;
			if (manager?.Connectors == null)
			{
				return list;
			}

			foreach (Connector connector in manager.Connectors)
			{
				if (connector == null)
				{
					continue;
				}

				try
				{
					if (connector.ConnectorType == ConnectorType.End)
					{
						list.Add(connector);
					}
				}
				catch
				{
					list.Add(connector);
				}
			}

			if (list.Count < 2)
			{
				list.Clear();
				foreach (Connector connector in manager.Connectors)
				{
					if (connector != null)
					{
						list.Add(connector);
					}
				}
			}
		}
		catch
		{
		}

		return list
			.OrderBy(c => c.Origin.X)
			.ThenBy(c => c.Origin.Y)
			.ThenBy(c => c.Origin.Z)
			.ToList();
	}

	private static void TrySetComments(FabricationPart part, string comment)
	{
		try
		{
			Parameter comments = ((Element)part).get_Parameter(BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS);
			if (comments != null && !comments.IsReadOnly && !string.IsNullOrWhiteSpace(comment))
			{
				comments.Set(comment);
			}
		}
		catch
		{
		}
	}

	/// <summary>
	/// Stamp Comments / Mark / eV_SpoolId from PCF so Create Assembly and abut/connect can stay spool-scoped.
	/// </summary>
	private static void TrySetPartIdentity(
		FabricationPart part,
		PcfDocument pcf,
		PcfComponent component,
		string sizeSuffix)
	{
		if (part == null || component == null)
		{
			return;
		}

		string suffix = string.IsNullOrWhiteSpace(sizeSuffix) ? string.Empty : " | " + sizeSuffix.Trim();
		TrySetComments(part, BuildComment(pcf, component) + suffix);

		string spoolId = (component.SpoolId ?? string.Empty).Trim();
		if (string.IsNullOrEmpty(spoolId))
		{
			return;
		}

		try
		{
			Parameter mark = ((Element)part).get_Parameter(BuiltInParameter.ALL_MODEL_MARK);
			if (mark != null && !mark.IsReadOnly)
			{
				mark.Set(spoolId);
			}
		}
		catch
		{
		}

		foreach (string name in new[] { "eV_SpoolId", "Spool ID", "SpoolId", "SPOOL-ID" })
		{
			try
			{
				Parameter p = ((Element)part).LookupParameter(name);
				if (p != null && !p.IsReadOnly && p.StorageType == StorageType.String)
				{
					p.Set(spoolId);
					break;
				}
			}
			catch
			{
			}
		}
	}

	private static void RegisterCreatedPart(ImportResult result, FabricationPart part, PcfComponent component)
	{
		if (result == null || part == null)
		{
			return;
		}

		result.CreatedFabIds.Add(part.Id);
		string spoolId = (component?.SpoolId ?? string.Empty).Trim();
		if (!string.IsNullOrEmpty(spoolId))
		{
			result.SpoolIdByPartId[part.Id] = spoolId;
		}
	}

	/// <summary>
	/// When both parts have a SPOOL-ID and they differ, treat as different assemblies.
	/// Missing IDs stay compatible so older imports / untagged parts still connect.
	/// </summary>
	private static bool SameSpool(
		ElementId idA,
		ElementId idB,
		IDictionary<ElementId, string> spoolIdByPartId)
	{
		if (spoolIdByPartId == null
			|| idA == null || idA == ElementId.InvalidElementId
			|| idB == null || idB == ElementId.InvalidElementId)
		{
			return true;
		}

		if (!spoolIdByPartId.TryGetValue(idA, out string a) || string.IsNullOrWhiteSpace(a))
		{
			return true;
		}

		if (!spoolIdByPartId.TryGetValue(idB, out string b) || string.IsNullOrWhiteSpace(b))
		{
			return true;
		}

		return string.Equals(a.Trim(), b.Trim(), StringComparison.OrdinalIgnoreCase);
	}

	private static string BuildComment(PcfDocument pcf, PcfComponent component)
	{
		var parts = new List<string> { "PCF Import" };
		if (!string.IsNullOrWhiteSpace(pcf.PipelineReference))
		{
			parts.Add(pcf.PipelineReference);
		}

		if (!string.IsNullOrWhiteSpace(component.SpoolId))
		{
			parts.Add(component.SpoolId);
		}

		if (!string.IsNullOrWhiteSpace(component.Type))
		{
			parts.Add(component.Type);
		}

		if (!string.IsNullOrWhiteSpace(component.ItemCode))
		{
			parts.Add("ITEM " + component.ItemCode);
		}

		return string.Join(" | ", parts);
	}

	private static string FormatId(PcfComponent component)
	{
		return string.IsNullOrWhiteSpace(component.ComponentIdentifier)
			? string.Empty
			: " #" + component.ComponentIdentifier;
	}

	private static string BuildSkippedMiscWarning(PcfComponent component)
	{
		var bits = new List<string>
		{
			"Skipped " + (component?.Type ?? "MISC-COMPONENT") + FormatId(component)
		};

		if (!string.IsNullOrWhiteSpace(component?.ItemCode))
		{
			bits.Add("ITEM-CODE " + component.ItemCode);
		}

		if (!string.IsNullOrWhiteSpace(component?.SizeText))
		{
			bits.Add(component.SizeText.Trim());
		}

		if (!string.IsNullOrWhiteSpace(component?.SpoolId))
		{
			bits.Add("spool " + component.SpoolId);
		}

		bits.Add(
			"unclassified in PCF (not pipe/elbow/tee/flange/olet/cap) — usually a weld-adjacent coupling "
			+ "or other fitting with no palette detail in this pass; endpoints still used to size the host run");

		return string.Join(" — ", bits) + ".";
	}

	private static ElementId FindNearestLevelId(Document doc, XYZ a, XYZ b)
	{
		double z = (a.Z + b.Z) * 0.5;
		Level best = null;
		double bestDist = double.MaxValue;
		foreach (Level level in new FilteredElementCollector(doc).OfClass(typeof(Level)).Cast<Level>())
		{
			double dist = Math.Abs(level.Elevation - z);
			if (dist < bestDist)
			{
				bestDist = dist;
				best = level;
			}
		}

		return best?.Id ?? ElementId.InvalidElementId;
	}

	private static void ExtendStraightsForBranchPoints(Document doc, IList<ElementId> createdIds, IList<PcfComponent> ancillaries)
	{
		if (doc == null || createdIds == null || ancillaries == null || ancillaries.Count == 0)
		{
			return;
		}

		List<FabricationPart> straights = createdIds
			.Select(id => doc.GetElement(id) as FabricationPart)
			.Where(p => p != null && p.IsAStraight())
			.ToList();
		List<FabricationPart> fittings = createdIds
			.Select(id => doc.GetElement(id) as FabricationPart)
			.Where(p => p != null && !p.IsAStraight())
			.ToList();
		if (straights.Count == 0)
		{
			return;
		}

		// Never grow a stub more than this past its current end — olet headers far outside
		// belong on a different host (parallel riser), not a short elbow stub.
		const double maxExtendFeet = 1.0;
		const double elbowLockTolFeet = 0.75;

		var extents = new Dictionary<ElementId, (FabricationPart Host, XYZ Origin, XYZ Dir, double Min, double Max)>();

		void AddPoint(XYZ point)
		{
			if (point == null)
			{
				return;
			}

			FabricationPart host = FindBestHostStraight(
				straights,
				point,
				out XYZ axisOrigin,
				out XYZ axisDir,
				out double tMin,
				out double tMax,
				out double tPoint);
			if (host == null || axisDir == null || axisOrigin == null)
			{
				return;
			}

			// Point already on the run — nothing to stretch.
			if (tPoint >= tMin - 0.05 && tPoint <= tMax + 0.05)
			{
				return;
			}

			// Only nudge a nearby free end; do not drag a 4' stub across the whole riser.
			if (tPoint < tMin - maxExtendFeet || tPoint > tMax + maxExtendFeet)
			{
				return;
			}

			bool lockMin = IsNearElbowFace(axisOrigin + axisDir.Multiply(tMin), fittings, elbowLockTolFeet);
			bool lockMax = IsNearElbowFace(axisOrigin + axisDir.Multiply(tMax), fittings, elbowLockTolFeet);

			double wantMin = tMin;
			double wantMax = tMax;
			if (tPoint < tMin && !lockMin)
			{
				wantMin = Math.Max(tPoint - 0.05, tMin - maxExtendFeet);
			}
			else if (tPoint > tMax && !lockMax)
			{
				wantMax = Math.Min(tPoint + 0.05, tMax + maxExtendFeet);
			}
			else
			{
				return;
			}

			ElementId id = host.Id;
			if (extents.TryGetValue(id, out var existing))
			{
				extents[id] = (
					existing.Host,
					existing.Origin,
					existing.Dir,
					Math.Min(existing.Min, wantMin),
					Math.Max(existing.Max, wantMax));
			}
			else
			{
				extents[id] = (host, axisOrigin, axisDir, wantMin, wantMax);
			}
		}

		foreach (PcfComponent component in ancillaries)
		{
			if (component == null)
			{
				continue;
			}

			if (component.IsOlet)
			{
				if (TryGetOletPoints(component, out XYZ header, out _))
				{
					AddPoint(header);
				}

				continue;
			}

			// Caps / misc: only use endpoints to nudge a nearby free pipe end, never invent length.
			foreach (PcfEndPoint endPoint in component.EndPoints)
			{
				AddPoint(endPoint?.Point);
			}
		}

		foreach (var entry in extents.Values)
		{
			double span = entry.Max - entry.Min;
			if (span < MinSegmentLengthFeet)
			{
				continue;
			}

			XYZ newStart = entry.Origin + entry.Dir.Multiply(entry.Min);
			XYZ newEnd = entry.Origin + entry.Dir.Multiply(entry.Max);
			if (!PlaceStraight(doc, entry.Host, newStart, newEnd))
			{
				ForceStraightSpan(doc, entry.Host, newStart, newEnd);
			}
		}
	}

	/// <summary>
	/// When a host was truncated at an olet in the PCF, stretch only the free (non-elbow)
	/// end to match longer parallel same-size peers. Never move an end that already abuts
	/// an elbow — that overshoots into the fitting.
	/// </summary>
	private static void EqualizeTruncatedParallelStraights(
		Document doc,
		IList<ElementId> createdIds,
		IList<PcfComponent> olets)
	{
		if (doc == null || createdIds == null || olets == null || olets.Count == 0)
		{
			return;
		}

		List<FabricationPart> straights = createdIds
			.Select(id => doc.GetElement(id) as FabricationPart)
			.Where(p => p != null && p.IsAStraight())
			.ToList();
		List<FabricationPart> elbows = createdIds
			.Select(id => doc.GetElement(id) as FabricationPart)
			.Where(p => p != null && !p.IsAStraight())
			.Where(p => GetEndConnectors(p).Count >= 2)
			.ToList();
		if (straights.Count < 2)
		{
			return;
		}

		var hostIds = new HashSet<ElementId>();
		foreach (PcfComponent olet in olets)
		{
			if (olet == null || !TryGetOletPoints(olet, out XYZ header, out _))
			{
				continue;
			}

			FabricationPart host = FindBestHostStraight(
				straights,
				header,
				out _,
				out _,
				out _,
				out _,
				out _);
			if (host != null)
			{
				hostIds.Add(host.Id);
			}
		}

		const double shareStartTolFeet = 2.0;
		const double minGainFeet = 1.0;
		const double maxAxisSeparationFeet = 20.0;
		const double elbowLockTolFeet = 0.75;

		foreach (ElementId hostId in hostIds)
		{
			FabricationPart host = doc.GetElement(hostId) as FabricationPart;
			if (host == null || !TryGetStraightAxis(host, out XYZ origin, out XYZ dir, out double tMin, out double tMax))
			{
				continue;
			}

			bool lockMin = IsNearElbowFace(origin + dir.Multiply(tMin), elbows, elbowLockTolFeet);
			bool lockMax = IsNearElbowFace(origin + dir.Multiply(tMax), elbows, elbowLockTolFeet);
			if (lockMin && lockMax)
			{
				continue;
			}

			// No elbow lock: do not guess — equalization without a pinned fitting end overshoots.
			if (!lockMin && !lockMax)
			{
				continue;
			}

			double hostRadius = TryGetStraightRadiusFeet(host);
			double wantMin = tMin;
			double wantMax = tMax;

			foreach (FabricationPart peer in straights)
			{
				if (peer == null || peer.Id == hostId)
				{
					continue;
				}

				if (!TryGetStraightAxis(peer, out XYZ peerOrigin, out XYZ peerDir, out double peerMin, out double peerMax))
				{
					continue;
				}

				if (Math.Abs(dir.DotProduct(peerDir)) < 0.95)
				{
					continue;
				}

				double peerRadius = TryGetStraightRadiusFeet(peer);
				if (hostRadius > 1e-6 && peerRadius > 1e-6
					&& Math.Abs(hostRadius - peerRadius) > Math.Max(0.02, hostRadius * 0.15))
				{
					continue;
				}

				XYZ peerMid = peerOrigin + peerDir.Multiply(0.5 * (peerMin + peerMax));
				double radial = (peerMid - origin).CrossProduct(dir).GetLength();
				if (radial > maxAxisSeparationFeet)
				{
					continue;
				}

				double p0 = (peerOrigin + peerDir.Multiply(peerMin) - origin).DotProduct(dir);
				double p1 = (peerOrigin + peerDir.Multiply(peerMax) - origin).DotProduct(dir);
				double pMin = Math.Min(p0, p1);
				double pMax = Math.Max(p0, p1);

				bool sharesPinnedEnd = lockMin
					? (Math.Abs(pMin - tMin) <= shareStartTolFeet || Math.Abs(pMax - tMin) <= shareStartTolFeet)
					: (Math.Abs(pMin - tMax) <= shareStartTolFeet || Math.Abs(pMax - tMax) <= shareStartTolFeet);
				if (!sharesPinnedEnd)
				{
					continue;
				}

				if (lockMin)
				{
					wantMax = Math.Max(wantMax, pMax);
				}
				else
				{
					wantMin = Math.Min(wantMin, pMin);
				}
			}

			if (lockMin)
			{
				wantMin = tMin;
			}

			if (lockMax)
			{
				wantMax = tMax;
			}

			if ((wantMax - wantMin) < (tMax - tMin) + minGainFeet)
			{
				continue;
			}

			XYZ newStart = origin + dir.Multiply(wantMin);
			XYZ newEnd = origin + dir.Multiply(wantMax);
			if (!PlaceStraight(doc, host, newStart, newEnd))
			{
				ForceStraightSpan(doc, host, newStart, newEnd);
			}
		}
	}

	private static void TrimCreatedStraightsAgainstElbows(Document doc, IList<ElementId> createdIds)
	{
		if (doc == null || createdIds == null)
		{
			return;
		}

		List<FabricationPart> straights = createdIds
			.Select(id => doc.GetElement(id) as FabricationPart)
			.Where(p => p != null && p.IsAStraight())
			.ToList();
		List<FabricationPart> elbows = createdIds
			.Select(id => doc.GetElement(id) as FabricationPart)
			.Where(p => p != null && !p.IsAStraight())
			.Where(p => GetEndConnectors(p).Count >= 2)
			.ToList();
		TrimStraightsThatEnterElbows(doc, straights, elbows);
	}

	/// <summary>
	/// Stretch or trim open straight ends so they land on nearby elbow/tee faces (closes PCF undershoot gaps
	/// and stops pipe from stopping short of an elbow). Never grow into a different PCF spool — stacked
	/// header branch stubs are ~1–1.5' apart and must stay separate.
	/// </summary>
	private static void AbutStraightsToElbowConnectors(
		Document doc,
		IList<ElementId> createdIds,
		IDictionary<ElementId, string> spoolIdByPartId = null)
	{
		if (doc == null || createdIds == null)
		{
			return;
		}

		List<FabricationPart> straights = createdIds
			.Select(id => doc.GetElement(id) as FabricationPart)
			.Where(p => p != null && p.IsAStraight())
			.ToList();
		List<FabricationPart> fittings = createdIds
			.Select(id => doc.GetElement(id) as FabricationPart)
			.Where(p => p != null && !p.IsAStraight())
			.ToList();
		if (straights.Count == 0 || fittings.Count == 0)
		{
			return;
		}

		// Shop-weld undershoot only — larger gaps are neighboring spools on the same axis.
		const double maxGapFeet = 0.35;

		foreach (FabricationPart straight in straights)
		{
			if (straight == null || !TryGetStraightAxis(straight, out XYZ origin, out XYZ dir, out double tMin, out double tMax))
			{
				continue;
			}

			double newMin = tMin;
			double newMax = tMax;
			bool changed = false;
			double pipeRadius = TryGetStraightRadiusFeet(straight);
			double radialTol = Math.Max(0.06, pipeRadius * 1.35);

			foreach (FabricationPart fitting in fittings)
			{
				if (!SameSpool(straight.Id, fitting.Id, spoolIdByPartId))
				{
					continue;
				}

				foreach (Connector face in GetEndConnectors(fitting))
				{
					XYZ facePt;
					XYZ faceAxis = null;
					try
					{
						facePt = face?.Origin;
						faceAxis = face?.CoordinateSystem?.BasisZ;
					}
					catch
					{
						continue;
					}

					if (facePt == null)
					{
						continue;
					}

					try
					{
						if (face.IsConnected)
						{
							continue;
						}
					}
					catch
					{
					}

					// Only abut to faces that point along this pipe — never the elbow's other leg.
					if (faceAxis != null && faceAxis.GetLength() > 1e-9)
					{
						if (Math.Abs(faceAxis.Normalize().DotProduct(dir)) < 0.85)
						{
							continue;
						}
					}

					if (pipeRadius > 1e-6)
					{
						try
						{
							if (face.Radius > 1e-6
								&& Math.Abs(face.Radius - pipeRadius) > Math.Max(0.02, pipeRadius * 0.25))
							{
								continue;
							}
						}
						catch
						{
						}
					}

					double tFace = (facePt - origin).DotProduct(dir);
					XYZ onAxis = origin + dir.Multiply(tFace);
					double radial = facePt.DistanceTo(onAxis);
					if (radial > radialTol)
					{
						continue;
					}

					double gapMin = Math.Abs(tFace - newMin);
					double gapMax = Math.Abs(tFace - newMax);

					// Prefer snapping the nearer open end when the face is just beyond / short of the pipe.
					if (gapMin <= maxGapFeet && gapMin <= gapMax + 1e-9 && gapMin > 0.01)
					{
						newMin = tFace;
						changed = true;
					}
					else if (gapMax <= maxGapFeet && gapMax > 0.01)
					{
						newMax = tFace;
						changed = true;
					}
				}
			}

			if (!changed || (newMax - newMin) < MinSegmentLengthFeet)
			{
				continue;
			}

			XYZ newStart = origin + dir.Multiply(newMin);
			XYZ newEnd = origin + dir.Multiply(newMax);
			if (!PlaceStraight(doc, straight, newStart, newEnd))
			{
				ForceStraightSpan(doc, straight, newStart, newEnd);
			}
		}
	}

	private static bool IsNearElbowFace(XYZ point, IList<FabricationPart> elbows, double tolFeet)
	{
		if (point == null || elbows == null)
		{
			return false;
		}

		foreach (FabricationPart elbow in elbows)
		{
			foreach (Connector c in GetEndConnectors(elbow))
			{
				try
				{
					if (c?.Origin != null && c.Origin.DistanceTo(point) <= tolFeet)
					{
						return true;
					}
				}
				catch
				{
				}
			}
		}

		return false;
	}

	/// <summary>
	/// Pull a straight end back to the elbow connector if it landed inside the elbow body.
	/// </summary>
	private static void TrimStraightsThatEnterElbows(
		Document doc,
		IList<FabricationPart> straights,
		IList<FabricationPart> elbows)
	{
		if (doc == null || straights == null || elbows == null)
		{
			return;
		}

		foreach (FabricationPart straight in straights)
		{
			if (straight == null || !TryGetStraightAxis(straight, out XYZ origin, out XYZ dir, out double tMin, out double tMax))
			{
				continue;
			}

			double newMin = tMin;
			double newMax = tMax;
			bool changed = false;

			foreach (FabricationPart elbow in elbows)
			{
				BoundingBoxXYZ bb = null;
				try
				{
					bb = elbow.get_BoundingBox(null);
				}
				catch
				{
				}

				if (bb == null)
				{
					continue;
				}

				foreach (Connector face in GetEndConnectors(elbow))
				{
					XYZ facePt;
					XYZ faceAxis = null;
					try
					{
						facePt = face?.Origin;
						faceAxis = face?.CoordinateSystem?.BasisZ;
					}
					catch
					{
						continue;
					}

					if (facePt == null)
					{
						continue;
					}

					// Only trim against faces on this pipe's axis (not the elbow's other leg).
					if (faceAxis != null && faceAxis.GetLength() > 1e-9)
					{
						if (Math.Abs(faceAxis.Normalize().DotProduct(dir)) < 0.85)
						{
							continue;
						}
					}

					double tFace = (facePt - origin).DotProduct(dir);
					XYZ onAxis = origin + dir.Multiply(tFace);
					double radial = facePt.DistanceTo(onAxis);
					if (radial > 1.0)
					{
						continue;
					}

					// If the straight extends past the face into the elbow bbox, trim to the face.
					XYZ pastMin = origin + dir.Multiply(Math.Min(newMin, tFace - 0.05));
					XYZ pastMax = origin + dir.Multiply(Math.Max(newMax, tFace + 0.05));

					if (newMin < tFace - 0.02 && PointInExpandedBbox(pastMin, bb, 0.05)
						&& PointInExpandedBbox(facePt, bb, 0.35))
					{
						newMin = tFace;
						changed = true;
					}

					if (newMax > tFace + 0.02 && PointInExpandedBbox(pastMax, bb, 0.05)
						&& PointInExpandedBbox(facePt, bb, 0.35))
					{
						newMax = tFace;
						changed = true;
					}

					// Also: if an end point sits inside the fitting bbox away from this face, pull it back.
					XYZ endMin = origin + dir.Multiply(newMin);
					XYZ endMax = origin + dir.Multiply(newMax);
					if (PointInExpandedBbox(endMin, bb, -0.01) && endMin.DistanceTo(facePt) > 0.05
						&& Math.Abs(tFace - newMin) < 1.0 && newMin < tFace)
					{
						newMin = tFace;
						changed = true;
					}

					if (PointInExpandedBbox(endMax, bb, -0.01) && endMax.DistanceTo(facePt) > 0.05
						&& Math.Abs(tFace - newMax) < 1.0 && newMax > tFace)
					{
						newMax = tFace;
						changed = true;
					}
				}
			}

			if (!changed || (newMax - newMin) < MinSegmentLengthFeet)
			{
				continue;
			}

			XYZ newStart = origin + dir.Multiply(newMin);
			XYZ newEnd = origin + dir.Multiply(newMax);
			if (!PlaceStraight(doc, straight, newStart, newEnd))
			{
				ForceStraightSpan(doc, straight, newStart, newEnd);
			}
		}
	}

	private static bool PointInExpandedBbox(XYZ point, BoundingBoxXYZ bb, double padFeet)
	{
		if (point == null || bb == null)
		{
			return false;
		}

		return point.X >= bb.Min.X - padFeet && point.X <= bb.Max.X + padFeet
			&& point.Y >= bb.Min.Y - padFeet && point.Y <= bb.Max.Y + padFeet
			&& point.Z >= bb.Min.Z - padFeet && point.Z <= bb.Max.Z + padFeet;
	}

	private static bool TryGetStraightAxis(
		FabricationPart part,
		out XYZ origin,
		out XYZ dir,
		out double tMin,
		out double tMax)
	{
		origin = null;
		dir = null;
		tMin = tMax = 0;
		List<Connector> ends = GetEndConnectors(part);
		if (ends.Count < 2)
		{
			return false;
		}

		XYZ a = ends[0].Origin;
		XYZ b = ends[1].Origin;
		double len = a.DistanceTo(b);
		if (len < MinSegmentLengthFeet)
		{
			return false;
		}

		origin = a;
		dir = (b - a).Normalize();
		tMin = 0;
		tMax = len;
		return true;
	}

	private static double TryGetStraightRadiusFeet(FabricationPart part)
	{
		foreach (Connector c in GetEndConnectors(part))
		{
			try
			{
				if (c.Radius > 1e-6)
				{
					return c.Radius;
				}
			}
			catch
			{
			}
		}

		return 0;
	}

	private static void ForceStraightSpan(Document doc, FabricationPart part, XYZ start, XYZ end)
	{
		if (part == null || start == null || end == null)
		{
			return;
		}

		double length = start.DistanceTo(end);
		if (length < MinSegmentLengthFeet)
		{
			return;
		}

		TrySetStraightLength(doc, part, length);
		part = doc.GetElement(part.Id) as FabricationPart;
		if (part == null)
		{
			return;
		}

		List<Connector> connectors = GetEndConnectors(part);
		if (connectors.Count < 2)
		{
			return;
		}

		XYZ dir = (end - start).Normalize();
		OrientPartAlongDirection(doc, part, connectors[0].Origin, GetConnectorAxis(connectors[0], connectors[1]), dir);
		part = doc.GetElement(part.Id) as FabricationPart;
		connectors = GetEndConnectors(part);
		if (part == null || connectors.Count < 2)
		{
			return;
		}

		Connector cStart = connectors.OrderBy(c => c.Origin.DistanceTo(start)).First();
		XYZ move = start - cStart.Origin;
		if (move.GetLength() > 1e-9)
		{
			ElementTransformUtils.MoveElement(doc, part.Id, move);
		}
	}

	private static void TryPlaceFlange(
		Document doc,
		PcfDocument pcf,
		PcfComponent component,
		IList<FabricationServiceButton> flangeButtons,
		ImportOptions options,
		ImportResult result)
	{
		if (flangeButtons == null || flangeButtons.Count == 0)
		{
			result.Skipped++;
			result.Warnings.Add("No flange button in palette for " + component.Type + FormatId(component) + ".");
			return;
		}

		if (!component.TryGetSegment(out XYZ start, out XYZ end, out double boreInches))
		{
			result.Skipped++;
			result.Warnings.Add("Skipped " + component.Type + FormatId(component) + " (missing END-POINTs).");
			return;
		}

		// Hub first, raised face second — matches export and keeps WN orientation stable.
		ReorderFlangeEndsHubThenFace(pcf, component, ref start, ref end);

		ElementId levelId = FindNearestLevelId(doc, start, end);
		if (levelId == null || levelId == ElementId.InvalidElementId)
		{
			result.Skipped++;
			result.Warnings.Add("No level found for " + component.Type + FormatId(component) + ".");
			return;
		}

		double sizeInches = boreInches > 1e-6
			? boreInches
			: (options.DefaultSizeInches > 1e-6 ? options.DefaultSizeInches : DefaultBoreInches);
		FabricationServiceButton button = PickFlangeButton(flangeButtons, component) ?? flangeButtons[0];

		try
		{
			FabricationPart part = CreateSizedPart(doc, button, sizeInches, levelId);
			if (part == null)
			{
				result.Skipped++;
				result.Warnings.Add("Create failed for " + component.Type + FormatId(component) + ".");
				return;
			}

			if (!TryApplyProductListSize(doc, part, sizeInches))
			{
				result.Warnings.Add(
					component.Type + FormatId(component)
					+ " created but product-list size " + FormatSizeLabel(sizeInches)
					+ " could not be applied.");
			}

			TrySetSizeParameters(part, sizeInches, component.SizeText);

			if (!PlaceFlange(doc, part, start, end))
			{
				try { doc.Delete(part.Id); } catch { }
				result.Skipped++;
				result.Warnings.Add("Could not place " + component.Type + FormatId(component) + ".");
				return;
			}

			TrySetPartIdentity(part, pcf, component, FormatSizeLabel(sizeInches));
			RegisterCreatedPart(result, part, component);
			result.FabPartsCreated++;
			result.FlangesCreated++;
		}
		catch (Exception ex)
		{
			result.Skipped++;
			result.Warnings.Add("Failed " + component.Type + FormatId(component) + ": " + ex.Message);
		}
	}

	private static FabricationServiceButton PickFlangeButton(
		IList<FabricationServiceButton> buttons,
		PcfComponent component)
	{
		if (buttons == null || buttons.Count == 0)
		{
			return null;
		}

		string hint = ((component?.SizeText ?? string.Empty)
			+ " " + (component?.ItemCode ?? string.Empty)
			+ " " + (component?.Skey ?? string.Empty)
			+ " " + (component?.Type ?? string.Empty)).ToUpperInvariant();
		bool wantsAdapter = hint.Contains("ADAPTER") || hint.Contains("ADAPTOR");
		bool wantsWn = hint.Contains("WN") || hint.Contains("WELD NECK") || hint.Contains("WELD-NECK");
		bool wantsSlip = hint.Contains("SO") || hint.Contains("SLIP");
		bool wantsBlind = hint.Contains("BLIND");

		FabricationServiceButton best = null;
		int bestScore = int.MinValue;
		foreach (FabricationServiceButton button in buttons)
		{
			if (button == null)
			{
				continue;
			}

			string corpus = ((button.Name ?? string.Empty) + " " + (button.Code ?? string.Empty)).ToUpperInvariant();
			int score = 0;

			if (corpus.Contains("ADAPTER") || corpus.Contains("ADAPTOR"))
			{
				score += wantsAdapter ? 60 : 25;
			}

			if (corpus.Contains("WELD NECK") || corpus.Contains("WELD-NECK") || corpus.Contains(" WN"))
			{
				score += wantsWn ? 40 : -15;
			}

			if (corpus.Contains("SLIP") || corpus.Contains(" SO"))
			{
				score += wantsSlip ? 35 : 5;
			}

			if (corpus.Contains("BLIND"))
			{
				score += wantsBlind ? 50 : -40;
			}

			if (corpus.Contains("DIELECTRIC"))
			{
				score -= 20;
			}

			if (!string.IsNullOrWhiteSpace(hint))
			{
				foreach (string token in hint.Split(new[] { ' ', '-', '/', '(', ')' }, StringSplitOptions.RemoveEmptyEntries))
				{
					if (token.Length >= 3 && corpus.Contains(token))
					{
						score += token.Length;
					}
				}
			}

			if (corpus.Contains("150"))
			{
				score += 5;
			}

			if (score > bestScore)
			{
				bestScore = score;
				best = button;
			}
		}

		return best ?? buttons[0];
	}

	private static bool PlaceFlange(Document doc, FabricationPart part, XYZ start, XYZ end)
	{
		List<Connector> connectors = GetEndConnectors(part);
		if (connectors.Count < 2 || start == null || end == null)
		{
			return false;
		}

		// start = hub, end = raised face (after ReorderFlangeEndsHubThenFace).
		XYZ targetDir = end - start;
		if (targetDir.GetLength() < 1e-9)
		{
			return false;
		}

		targetDir = targetDir.Normalize();
		OrientPartAlongDirection(doc, part, connectors[0].Origin, GetConnectorAxis(connectors[0], connectors[1]), targetDir);
		if (!TryCenterFlangeOnSpan(doc, ref part, start, end))
		{
			return false;
		}

		// Evaluate both 0° and 180° — a single hubOut heuristic previously fixed one pipe end
		// and flipped the opposite end the wrong way.
		double score0 = ScoreFlangeOrientation(part, start, end, targetDir);
		if (!TryFlipFlange180(doc, ref part, start, end, targetDir))
		{
			return AcceptFlangePlacement(part, start, end);
		}

		double score1 = ScoreFlangeOrientation(part, start, end, targetDir);
		if (score0 >= score1)
		{
			// Restore the better (unflipped) orientation.
			TryFlipFlange180(doc, ref part, start, end, targetDir);
		}

		return AcceptFlangePlacement(part, start, end);
	}

	private static bool TryCenterFlangeOnSpan(Document doc, ref FabricationPart part, XYZ start, XYZ end)
	{
		part = doc.GetElement(part.Id) as FabricationPart;
		List<Connector> connectors = GetEndConnectors(part);
		if (part == null || connectors.Count < 2)
		{
			return false;
		}

		Connector cStart = connectors.OrderBy(c => c.Origin.DistanceTo(start)).First();
		Connector cEnd = connectors.First(c => !ReferenceEquals(c, cStart));
		XYZ midTarget = (start + end) * 0.5;
		XYZ midConn = (cStart.Origin + cEnd.Origin) * 0.5;
		XYZ move = midTarget - midConn;
		if (move.GetLength() > 1e-9)
		{
			ElementTransformUtils.MoveElement(doc, part.Id, move);
		}

		part = doc.GetElement(part.Id) as FabricationPart;
		return part != null && GetEndConnectors(part).Count >= 2;
	}

	private static bool TryFlipFlange180(
		Document doc,
		ref FabricationPart part,
		XYZ start,
		XYZ end,
		XYZ targetDir)
	{
		XYZ mid = (start + end) * 0.5;
		XYZ flipAxis = targetDir.CrossProduct(
			Math.Abs(targetDir.DotProduct(XYZ.BasisZ)) < 0.9 ? XYZ.BasisZ : XYZ.BasisX);
		if (flipAxis.GetLength() < 1e-9)
		{
			return false;
		}

		try
		{
			ElementTransformUtils.RotateElement(
				doc,
				part.Id,
				Line.CreateBound(mid, mid + flipAxis.Normalize()),
				Math.PI);
		}
		catch
		{
			return false;
		}

		return TryCenterFlangeOnSpan(doc, ref part, start, end);
	}

	/// <summary>
	/// Higher is better. Prefers hub connector at <paramref name="start"/> with outward
	/// into the pipe (−hub→face), and face connector at <paramref name="end"/>.
	/// </summary>
	private static double ScoreFlangeOrientation(
		FabricationPart part,
		XYZ start,
		XYZ end,
		XYZ targetDir)
	{
		List<Connector> connectors = GetEndConnectors(part);
		if (part == null || connectors.Count < 2 || start == null || end == null || targetDir == null)
		{
			return double.NegativeInfinity;
		}

		Connector cStart = connectors.OrderBy(c => c.Origin.DistanceTo(start)).First();
		Connector cEnd = connectors.First(c => !ReferenceEquals(c, cStart));
		double posErr = cStart.Origin.DistanceTo(start) + cEnd.Origin.DistanceTo(end);

		double align = 0;
		XYZ hubOut = GetConnectorOutwardAxis(cStart);
		if (hubOut != null && hubOut.GetLength() > 1e-9)
		{
			// Hub must point into the pipe = opposite of hub→face.
			align = hubOut.Normalize().DotProduct(targetDir.Negate());
		}

		return (align * 10.0) - (posErr * 100.0);
	}

	private static bool AcceptFlangePlacement(FabricationPart part, XYZ start, XYZ end)
	{
		List<Connector> connectors = GetEndConnectors(part);
		if (part == null || connectors.Count < 2)
		{
			return false;
		}

		Connector cStart = connectors.OrderBy(c => c.Origin.DistanceTo(start)).First();
		Connector cEnd = connectors.First(c => !ReferenceEquals(c, cStart));
		if (RadialDistanceToSegment(cStart.Origin, start, end) > 0.02
			|| RadialDistanceToSegment(cEnd.Origin, start, end) > 0.02
			|| cStart.Origin.DistanceTo(start) > 0.20
			|| cEnd.Origin.DistanceTo(end) > 0.20)
		{
			return false;
		}

		return true;
	}

	/// <summary>
	/// Ensure start=hub (toward pipe/elbow/weld), end=raised face (toward mate flange or free).
	/// </summary>
	private static void ReorderFlangeEndsHubThenFace(
		PcfDocument pcf,
		PcfComponent flange,
		ref XYZ start,
		ref XYZ end)
	{
		if (pcf == null || start == null || end == null)
		{
			return;
		}

		double startHubScore = ScoreFlangeEndAsHub(pcf, flange, start);
		double endHubScore = ScoreFlangeEndAsHub(pcf, flange, end);
		if (endHubScore > startHubScore + 1e-6)
		{
			XYZ swap = start;
			start = end;
			end = swap;
		}
	}

	private static double ScoreFlangeEndAsHub(PcfDocument pcf, PcfComponent flange, XYZ point)
	{
		// Face is often only ~0.04' past the pipe end — a flat ≤0.12' test scores both ends
		// equally and leaves export order (face→hub vs hub→face) intact on one side only.
		const double tol = 0.25;
		double best = 0;
		foreach (PcfComponent other in pcf.Components)
		{
			if (other == null || ReferenceEquals(other, flange) || other.EndPoints.Count == 0)
			{
				continue;
			}

			foreach (PcfEndPoint ep in other.EndPoints)
			{
				if (ep?.Point == null)
				{
					continue;
				}

				double distance = ep.Point.DistanceTo(point);
				if (distance > tol)
				{
					continue;
				}

				double proximity = 1.0 / (distance + 1e-4);
				if (other.IsStraightPipe || other.IsWeld || other.IsElbow || other.IsTee || other.IsCap
					|| other.IsInlineFitting)
				{
					best = Math.Max(best, 1000.0 * proximity);
				}
				else if (other.IsFlange)
				{
					best = Math.Max(best, 10.0 * proximity); // mate face — not hub
				}
			}
		}

		return best;
	}

	private static double RadialDistanceToSegment(XYZ point, XYZ segA, XYZ segB)
	{
		if (point == null || segA == null || segB == null)
		{
			return double.MaxValue;
		}

		XYZ axis = segB - segA;
		double len = axis.GetLength();
		if (len < 1e-9)
		{
			return point.DistanceTo(segA);
		}

		axis = axis.Normalize();
		XYZ delta = point - segA;
		XYZ along = axis.Multiply(delta.DotProduct(axis));
		return (delta - along).GetLength();
	}

	/// <summary>
	/// Place coupling / adapter / reducer / misc at PCF END-POINTs using a matching palette button.
	/// </summary>
	private static void TryPlaceInlineFitting(
		Document doc,
		PcfDocument pcf,
		PcfComponent component,
		PaletteButtons buttons,
		ImportOptions options,
		ImportResult result)
	{
		try
		{
			if (!component.TryGetSegment(out XYZ start, out XYZ end, out double boreInches))
			{
				// Very short couplings may have near-coincident ends — still place at the midpoint.
				if (component.EndPoints.Count >= 1 && component.EndPoints[0]?.Point != null)
				{
					start = component.EndPoints[0].Point;
					end = component.EndPoints.Count > 1 && component.EndPoints[1]?.Point != null
						? component.EndPoints[1].Point
						: start + XYZ.BasisX.Multiply(0.02);
					boreInches = Math.Max(
						component.EndPoints[0].BoreInches,
						component.NominalSizeInches);
				}
				else
				{
					result.Skipped++;
					result.Warnings.Add(
						"Skipped " + component.Type + FormatId(component) + " (missing END-POINTs).");
					return;
				}
			}

			if (start.DistanceTo(end) < 1e-6)
			{
				end = start + XYZ.BasisX.Multiply(0.02);
			}

			double sizeInches = boreInches > 1e-6
				? boreInches
				: (options.DefaultSizeInches > 1e-6 ? options.DefaultSizeInches : DefaultBoreInches);

			double sizeA = component.EndPoints.Count > 0 ? component.EndPoints[0].BoreInches : 0;
			double sizeB = component.EndPoints.Count > 1 ? component.EndPoints[1].BoreInches : 0;
			if (sizeA <= 1e-6)
			{
				sizeA = sizeInches;
			}

			if (sizeB <= 1e-6)
			{
				sizeB = sizeInches;
			}

			bool reducing = component.IsReducer
				|| Math.Abs(sizeA - sizeB) > 0.12
				|| TryParseReducingSize(component.SizeText, out _, out _);

			FabricationServiceButton button = PickInlineFittingButton(buttons, component, sizeInches);
			if (button == null)
			{
				result.Skipped++;
				result.Warnings.Add(
					"No palette button for " + component.Type + FormatId(component)
					+ " — add a matching fitting to the selected palette.");
				return;
			}

			ElementId levelId = FindNearestLevelId(doc, start, end);
			if (levelId == null || levelId == ElementId.InvalidElementId)
			{
				result.Skipped++;
				result.Warnings.Add("No level found for " + component.Type + FormatId(component) + ".");
				return;
			}

			FabricationPart part = CreateSizedPart(doc, button, Math.Max(sizeA, sizeB), levelId);
			if (part == null)
			{
				result.Skipped++;
				result.Warnings.Add("Create failed for " + component.Type + FormatId(component) + ".");
				return;
			}

			if (reducing)
			{
				if (!TryParseReducingSize(component.SizeText, out double large, out double small))
				{
					large = Math.Max(sizeA, sizeB);
					small = Math.Min(sizeA, sizeB);
				}

				if (!TryApplyReducerProductListSize(doc, part, large, small))
				{
					TryApplyProductListSize(doc, part, large);
					result.Warnings.Add(
						component.Type + FormatId(component)
						+ " placed but reducing size " + FormatSizeLabel(large) + "x"
						+ FormatSizeLabel(small) + " could not be applied from product list.");
				}
			}
			else if (!string.IsNullOrWhiteSpace(component.SizeText))
			{
				TryApplyProductListSize(doc, part, sizeInches);
				TrySetSizeParameters(part, sizeInches, component.SizeText);
			}
			else
			{
				TrySetSizeParameters(part, sizeInches, component.SizeText);
			}

			// C1 → END-POINT[0], C2 → END-POINT[1] (Edit Part connector order — never flange flip).
			if (!PlaceByConnectorEndPoints(doc, part, start, end))
			{
				try { doc.Delete(part.Id); } catch { }
				result.Skipped++;
				result.Warnings.Add(
					"Could not place " + component.Type + FormatId(component) + " at END-POINTs.");
				return;
			}

			TrySetPartIdentity(part, pcf, component, FormatSizeLabel(Math.Max(sizeA, sizeB)));
			RegisterCreatedPart(result, part, component);
			result.FabPartsCreated++;
		}
		catch (Exception ex)
		{
			result.Skipped++;
			result.Warnings.Add("Failed " + component.Type + FormatId(component) + ": " + ex.Message);
		}
	}

	private static bool TryApplyReducerProductListSize(
		Document doc,
		FabricationPart part,
		double largeInches,
		double smallInches)
	{
		try
		{
			if (part == null || largeInches <= 1e-6 || smallInches <= 1e-6)
			{
				return false;
			}

			if (!part.IsProductList())
			{
				return Math.Abs(GetConnectorDiameterInches(part) - largeInches) <= 0.12;
			}

			string target = (FormatNpsToken(largeInches) + "x" + FormatNpsToken(smallInches)).ToUpperInvariant();
			string targetRev = (FormatNpsToken(smallInches) + "x" + FormatNpsToken(largeInches)).ToUpperInvariant();
			int count = part.GetProductListEntryCount();
			int best = -1;
			double bestDelta = double.MaxValue;
			for (int i = 0; i < count; i++)
			{
				string name = part.GetProductListEntryName(i) ?? string.Empty;
				string normalized = System.Text.RegularExpressions.Regex.Replace(
					name,
					@"[""'\u2032\u2033\u00F8\u2300\s]+",
					string.Empty).ToUpperInvariant();

				if (normalized.Contains(target) || normalized.Contains(targetRev))
				{
					best = i;
					bestDelta = 0;
					break;
				}

				if (!TryParseReducingSize(name, out double a, out double b))
				{
					continue;
				}

				double delta = Math.Abs(Math.Max(a, b) - largeInches) + Math.Abs(Math.Min(a, b) - smallInches);
				if (delta < bestDelta)
				{
					bestDelta = delta;
					best = i;
				}
			}

			if (best < 0 || bestDelta > 0.15)
			{
				return false;
			}

			part.ProductListEntry = best;
			doc?.Regenerate();
			return true;
		}
		catch
		{
			return false;
		}
	}

	private static FabricationServiceButton PickInlineFittingButton(
		PaletteButtons buttons,
		PcfComponent component,
		double sizeInches)
	{
		if (buttons == null || component == null)
		{
			return null;
		}

		var pool = new List<FabricationServiceButton>();
		void AddPool(IList<FabricationServiceButton> source)
		{
			if (source == null)
			{
				return;
			}

			foreach (FabricationServiceButton button in source)
			{
				if (button != null && !pool.Contains(button))
				{
					pool.Add(button);
				}
			}
		}

		if (component.IsReducer)
		{
			AddPool(buttons.Reducers);
			AddPool(buttons.MiscFittings);
		}
		else if (component.IsCoupling)
		{
			AddPool(buttons.Couplings);
			AddPool(buttons.MiscFittings);
			AddPool(buttons.Adapters);
		}
		else if (component.IsAdapter)
		{
			AddPool(buttons.Adapters);
			AddPool(buttons.MiscFittings);
			AddPool(buttons.Couplings);
		}
		else
		{
			AddPool(buttons.MiscFittings);
			AddPool(buttons.Adapters);
			AddPool(buttons.Couplings);
			AddPool(buttons.Reducers);
		}

		if (pool.Count == 0)
		{
			return null;
		}

		string itemCode = component.ItemCode ?? string.Empty;
		string itemKey = NormalizeCatalogKey(itemCode + " " + (component.Description ?? string.Empty));
		string hint = ((component.Type ?? string.Empty)
			+ " " + (component.Skey ?? string.Empty)
			+ " " + itemCode
			+ " " + (component.Description ?? string.Empty)
			+ " " + (component.SizeText ?? string.Empty)).ToUpperInvariant();

		FabricationServiceButton best = null;
		int bestScore = int.MinValue;
		foreach (FabricationServiceButton button in pool)
		{
			string corpus = ((button.Name ?? string.Empty) + " " + (button.Code ?? string.Empty)).ToUpperInvariant();
			string corpusKey = NormalizeCatalogKey(corpus);
			int score = 0;

			// Strong match: Fig.604-2 / No604-2 / 604-2 must beat a generic "ADAPTER" hit on Fig.603.
			if (!string.IsNullOrWhiteSpace(itemKey))
			{
				if (corpusKey.Contains(itemKey) || (itemKey.Contains(corpusKey) && corpusKey.Length >= 3))
				{
					score += 5000;
				}

				string itemFigure = ExtractFigureNumber(itemKey);
				string corpusFigure = ExtractFigureNumber(corpusKey);
				if (!string.IsNullOrWhiteSpace(itemFigure) && !string.IsNullOrWhiteSpace(corpusFigure))
				{
					if (string.Equals(itemFigure, corpusFigure, StringComparison.Ordinal))
					{
						score += 4000;
					}
					else
					{
						score -= 8000; // different figure → never pick (603 vs 604-2)
					}
				}
			}

			if (component.IsAdapter)
			{
				if (corpus.Contains("FTGXM") || corpus.Contains("FTG X M") || corpus.Contains("(FTGXM)")
					|| (corpus.Contains("FTG") && corpus.Contains("MPT")))
				{
					score += hint.Contains("604") || hint.Contains("FTG") ? 200 : 50;
				}

				if (corpus.Contains("CXF") || corpus.Contains("C X F") || corpus.Contains("(CXF)"))
				{
					score += hint.Contains("603") || hint.Contains("CXF") ? 200 : 0;
				}
			}

			foreach (string token in hint.Split(new[] { ' ', '-', '/', 'x', 'X', '.', '(', ')' }, StringSplitOptions.RemoveEmptyEntries))
			{
				if (token.Length >= 3 && corpus.Contains(token))
				{
					score += token.Length;
				}
			}

			if (score > bestScore)
			{
				bestScore = score;
				best = button;
			}
		}

		return best ?? pool[0];
	}

	/// <summary>Normalize ITEM-CODE / button names: "Fig.604-2", "No604-2" → "604-2".</summary>
	private static string NormalizeCatalogKey(string text)
	{
		if (string.IsNullOrWhiteSpace(text))
		{
			return string.Empty;
		}

		string up = text.ToUpperInvariant();
		up = System.Text.RegularExpressions.Regex.Replace(up, @"\b(FIG\.?|NO\.?|ITEM\.?|#)\s*", string.Empty);
		up = System.Text.RegularExpressions.Regex.Replace(up, @"[^A-Z0-9\-]+", string.Empty);
		return up.Trim('-');
	}

	/// <summary>Pull the leading figure token, e.g. "604-2" from "604-2FITTINGADAPTER".</summary>
	private static string ExtractFigureNumber(string normalizedKey)
	{
		if (string.IsNullOrWhiteSpace(normalizedKey))
		{
			return string.Empty;
		}

		System.Text.RegularExpressions.Match match = System.Text.RegularExpressions.Regex.Match(
			normalizedKey,
			@"^(\d+(?:-\d+)*)");
		return match.Success ? match.Groups[1].Value : string.Empty;
	}

	private static void TryPlaceCap(
		Document doc,
		PcfDocument pcf,
		PcfComponent component,
		IList<FabricationServiceButton> capButtons,
		ImportOptions options,
		ImportResult result)
	{
		if (capButtons == null || capButtons.Count == 0)
		{
			result.Skipped++;
			result.Warnings.Add("No cap button in palette for " + component.Type + FormatId(component) + ".");
			return;
		}

		if (!TryGetCapPoint(component, out XYZ facePoint, out double boreInches))
		{
			result.Skipped++;
			result.Warnings.Add("Skipped " + component.Type + FormatId(component) + " (missing END-POINT).");
			return;
		}

		ElementId levelId = FindNearestLevelId(doc, facePoint, facePoint);
		if (levelId == null || levelId == ElementId.InvalidElementId)
		{
			result.Skipped++;
			result.Warnings.Add("No level found for " + component.Type + FormatId(component) + ".");
			return;
		}

		double sizeInches = boreInches > 1e-6
			? boreInches
			: (component.NominalSizeInches > 1e-6
				? component.NominalSizeInches
				: (options.DefaultSizeInches > 1e-6 ? options.DefaultSizeInches : DefaultBoreInches));

		XYZ outward = FindNeighborOutwardFromPoint(pcf, component, facePoint, facePoint)
			?? InferCapOutwardFromPlacedParts(doc, result.CreatedFabIds, facePoint)
			?? XYZ.BasisX;

		try
		{
			FabricationPart part = CreateSizedPart(doc, capButtons[0], sizeInches, levelId);
			if (part == null)
			{
				result.Skipped++;
				result.Warnings.Add("Create failed for " + component.Type + FormatId(component) + ".");
				return;
			}

			if (!TryApplyProductListSize(doc, part, sizeInches))
			{
				result.Warnings.Add(
					component.Type + FormatId(component)
					+ " created but product-list size " + FormatSizeLabel(sizeInches)
					+ " could not be applied.");
			}

			TrySetSizeParameters(part, sizeInches, component.SizeText);

			if (!PlaceCap(doc, part, facePoint, outward))
			{
				try { doc.Delete(part.Id); } catch { }
				result.Skipped++;
				result.Warnings.Add("Could not place " + component.Type + FormatId(component) + ".");
				return;
			}

			TrySetPartIdentity(part, pcf, component, FormatSizeLabel(sizeInches));
			RegisterCreatedPart(result, part, component);
			result.FabPartsCreated++;
			result.CapsCreated++;
		}
		catch (Exception ex)
		{
			result.Skipped++;
			result.Warnings.Add("Failed " + component.Type + FormatId(component) + ": " + ex.Message);
		}
	}

	private static bool TryGetCapPoint(PcfComponent component, out XYZ point, out double boreInches)
	{
		point = null;
		boreInches = 0;
		if (component?.EndPoints == null || component.EndPoints.Count == 0)
		{
			return false;
		}

		foreach (PcfEndPoint ep in component.EndPoints)
		{
			if (ep?.Point == null)
			{
				continue;
			}

			point = ep.Point;
			boreInches = ep.BoreInches > 1e-6 ? ep.BoreInches : component.NominalSizeInches;
			return true;
		}

		return false;
	}

	private static XYZ InferCapOutwardFromPlacedParts(Document doc, IList<ElementId> createdIds, XYZ facePoint)
	{
		if (doc == null || createdIds == null || facePoint == null)
		{
			return null;
		}

		const double tol = 0.15;
		foreach (ElementId id in createdIds)
		{
			FabricationPart part = doc.GetElement(id) as FabricationPart;
			if (part == null || !part.IsAStraight())
			{
				continue;
			}

			List<Connector> ends = GetEndConnectors(part);
			if (ends.Count < 2)
			{
				continue;
			}

			Connector near = ends.OrderBy(c => c.Origin.DistanceTo(facePoint)).First();
			if (near.Origin.DistanceTo(facePoint) > tol)
			{
				continue;
			}

			Connector far = ends.OrderByDescending(c => c.Origin.DistanceTo(facePoint)).First();
			XYZ dir = near.Origin - far.Origin;
			if (dir.GetLength() > 1e-9)
			{
				return dir.Normalize();
			}
		}

		return null;
	}

	private static bool PlaceCap(Document doc, FabricationPart part, XYZ facePoint, XYZ outward)
	{
		List<Connector> connectors = GetEndConnectors(part);
		if (connectors.Count < 1 || facePoint == null)
		{
			return false;
		}

		XYZ dir = outward != null && outward.GetLength() > 1e-9 ? outward.Normalize() : XYZ.BasisX;
		Connector face = connectors[0];
		XYZ axis = GetConnectorAxis(face, connectors.Count > 1 ? connectors[1] : face);
		OrientPartAlongDirection(doc, part, face.Origin, axis, dir);
		part = doc.GetElement(part.Id) as FabricationPart;
		connectors = GetEndConnectors(part);
		if (part == null || connectors.Count < 1)
		{
			return false;
		}

		face = connectors.OrderBy(c => c.Origin.DistanceTo(facePoint)).First();
		XYZ move = facePoint - face.Origin;
		if (move.GetLength() > 1e-9)
		{
			ElementTransformUtils.MoveElement(doc, part.Id, move);
		}

		return true;
	}

	private static void TryPlaceTee(
		Document doc,
		PcfDocument pcf,
		PcfComponent component,
		PaletteButtons buttons,
		ImportOptions options,
		ImportResult result)
	{
		if (!TryResolveTeeFaces(pcf, component, out List<TeeFace> faces) || faces.Count < 3)
		{
			result.Skipped++;
			result.Warnings.Add(
				"Skipped " + component.Type + FormatId(component)
				+ " (need 3 junction faces; PCF/neighbors only resolved "
				+ (faces?.Count ?? 0).ToString(CultureInfo.InvariantCulture) + ").");
			return;
		}

		ClassifyTeeFaces(faces, out TeeFace runA, out TeeFace runB, out TeeFace branch);
		if (runA == null || runB == null || branch == null)
		{
			result.Skipped++;
			result.Warnings.Add("Skipped " + component.Type + FormatId(component) + " (could not classify run/branch faces).");
			return;
		}

		NormalizeTeeFaceBores(component, runA, runB, branch);

		double runInches = Math.Max(runA.BoreInches, runB.BoreInches);
		double branchInches = branch.BoreInches;
		if (TryParseTeeProductSize(component.SizeText, out double peRun1, out double peRun2, out double peBranch))
		{
			runInches = Math.Max(peRun1, peRun2);
			branchInches = peBranch;
		}
		else if (component.NominalSizeInches > 1e-6)
		{
			// Prefer header NOMINAL-SIZE when END-POINT bores look like radius-inches mistakes.
			if (runInches <= 1e-6
				|| (runInches < component.NominalSizeInches * 0.75
					&& Math.Abs(runInches * 2.0 - component.NominalSizeInches) < 0.35))
			{
				runInches = component.NominalSizeInches;
			}
		}

		if (runInches <= 1e-6)
		{
			runInches = options.DefaultSizeInches > 1e-6 ? options.DefaultSizeInches : DefaultBoreInches;
		}

		if (branchInches <= 1e-6)
		{
			branchInches = runInches;
		}

		bool reducing = Math.Abs(runInches - branchInches) > 0.12;
		FabricationServiceButton button = PickTeeButton(buttons, reducing);
		if (button == null)
		{
			result.Skipped++;
			result.Warnings.Add("No tee button in palette for " + component.Type + FormatId(component) + ".");
			return;
		}

		XYZ center = (runA.Point + runB.Point + branch.Point) * (1.0 / 3.0);
		ElementId levelId = FindNearestLevelId(doc, center, runA.Point);
		if (levelId == null || levelId == ElementId.InvalidElementId)
		{
			result.Skipped++;
			result.Warnings.Add("No level found for " + component.Type + FormatId(component) + ".");
			return;
		}

		try
		{
			FabricationPart part = CreateSizedPart(doc, button, runInches, levelId);
			if (part == null)
			{
				result.Skipped++;
				result.Warnings.Add("Create failed for " + component.Type + FormatId(component) + ".");
				return;
			}

			bool sized = reducing
				? TryApplyReducingTeeProductListSize(doc, part, runInches, branchInches)
				: TryApplyProductListSize(doc, part, runInches);
			if (!sized)
			{
				result.Warnings.Add(
					component.Type + FormatId(component)
					+ " created but product-list size "
					+ (reducing
						? FormatNpsToken(runInches) + "x" + FormatNpsToken(runInches) + "x" + FormatNpsToken(branchInches)
						: FormatSizeLabel(runInches))
					+ " could not be applied.");
			}

			TrySetSizeParameters(
				part,
				runInches,
				reducing
					? FormatNpsToken(runInches) + "x" + FormatNpsToken(runInches) + "x" + FormatNpsToken(branchInches)
					: component.SizeText);

			if (!PlaceTee(doc, part, runA, runB, branch))
			{
				try { doc.Delete(part.Id); } catch { }
				result.Skipped++;
				result.Warnings.Add("Could not place " + component.Type + FormatId(component) + ".");
				return;
			}

			TrySetPartIdentity(part, pcf, component, "tee");
			RegisterCreatedPart(result, part, component);
			result.FabPartsCreated++;
			result.TeesCreated++;
		}
		catch (Exception ex)
		{
			result.Skipped++;
			result.Warnings.Add("Failed " + component.Type + FormatId(component) + ": " + ex.Message);
		}
	}

	private sealed class TeeFace
	{
		internal XYZ Point;
		internal double BoreInches;
		internal XYZ Outward;
	}

	/// <summary>
	/// Older exports wrote connector radius-inches as END-POINT bore (4" NPS → 2). Double those
	/// when they clearly match half of NOMINAL-SIZE / product-entry run size.
	/// </summary>
	private static void NormalizeTeeFaceBores(PcfComponent component, TeeFace runA, TeeFace runB, TeeFace branch)
	{
		double nominal = component?.NominalSizeInches ?? 0;
		if (TryParseTeeProductSize(component?.SizeText, out double peRun1, out double peRun2, out double peBranch))
		{
			nominal = Math.Max(nominal, Math.Max(peRun1, peRun2));
			FixHalfBore(branch, peBranch > 1e-6 ? peBranch : nominal);
		}

		FixHalfBore(runA, nominal);
		FixHalfBore(runB, nominal);
		FixHalfBore(branch, nominal);
	}

	private static void FixHalfBore(TeeFace face, double expectedDiameterInches)
	{
		if (face == null || expectedDiameterInches <= 1e-6 || face.BoreInches <= 1e-6)
		{
			return;
		}

		if (Math.Abs(face.BoreInches * 2.0 - expectedDiameterInches) < 0.35)
		{
			face.BoreInches *= 2.0;
		}
	}

	private static bool TryResolveTeeFaces(PcfDocument pcf, PcfComponent tee, out List<TeeFace> faces)
	{
		faces = new List<TeeFace>();
		if (pcf == null || tee == null || tee.EndPoints.Count < 2)
		{
			return false;
		}

		XYZ center = XYZ.Zero;
		int n = 0;
		foreach (PcfEndPoint ep in tee.EndPoints)
		{
			if (ep?.Point == null)
			{
				continue;
			}

			center += ep.Point;
			n++;
		}

		if (n == 0)
		{
			return false;
		}

		center *= 1.0 / n;
		List<TeeFace> resolved = new List<TeeFace>();

		// 1) Seed from the tee's own END-POINTs only.
		foreach (PcfEndPoint ep in tee.EndPoints)
		{
			if (ep?.Point == null)
			{
				continue;
			}

			XYZ outward = FindNeighborOutwardFromPoint(pcf, tee, ep.Point, center);
			AddOrMergeTeeFace(
				resolved,
				center,
				10.0,
				ep.Point,
				ep.BoreInches > 1e-6 ? ep.BoreInches : tee.NominalSizeInches,
				outward);
		}

		// 2) Add only true junction neighbors (endpoint coincident with a seed face),
		//    not every endpoint near the tee center (that pulls adjacent header branches).
		const double joinTol = 0.08;
		foreach (PcfComponent other in pcf.Components)
		{
			if (other == null || ReferenceEquals(other, tee) || other.IsOlet)
			{
				continue;
			}

			if (other.EndPoints.Count < 1)
			{
				continue;
			}

			for (int i = 0; i < other.EndPoints.Count; i++)
			{
				PcfEndPoint ep = other.EndPoints[i];
				if (ep?.Point == null)
				{
					continue;
				}

				bool joinsSeed = resolved.Any(f => f.Point.DistanceTo(ep.Point) <= joinTol);
				if (!joinsSeed)
				{
					continue;
				}

				XYZ outward = null;
				if (other.EndPoints.Count >= 2)
				{
					XYZ otherPt = other.EndPoints[i == 0 ? other.EndPoints.Count - 1 : 0].Point;
					// Prefer the far end of a 2-point segment.
					if (other.EndPoints.Count == 2)
					{
						otherPt = other.EndPoints[i == 0 ? 1 : 0].Point;
					}

					if (otherPt != null && otherPt.DistanceTo(ep.Point) > MinSegmentLengthFeet)
					{
						outward = (otherPt - ep.Point).Normalize();
					}
				}

				double bore = ep.BoreInches > 1e-6
					? ep.BoreInches
					: (other.NominalSizeInches > 1e-6 ? other.NominalSizeInches : tee.NominalSizeInches);
				AddOrMergeTeeFace(resolved, center, 10.0, ep.Point, bore, outward);
			}
		}

		// 3) If still only 2 faces, look for one more pipe end near center that is NOT
		//    collinear with the known run and has a clear outward away from center.
		if (resolved.Count == 2)
		{
			TryAddMissingTeeFaceFromNearPipes(pcf, tee, resolved, center);
		}

		if (resolved.Count == 2)
		{
			TrySynthesizeThirdTeeFace(resolved, center);
		}

		if (resolved.Count > 3)
		{
			resolved = PickBestTeeTrio(resolved);
		}

		faces = resolved;
		return faces.Count >= 3;
	}

	private static void TryAddMissingTeeFaceFromNearPipes(
		PcfDocument pcf,
		PcfComponent tee,
		List<TeeFace> faces,
		XYZ center)
	{
		if (faces == null || faces.Count != 2 || center == null)
		{
			return;
		}

		XYZ runDir = faces[1].Point - faces[0].Point;
		if (runDir.GetLength() < 1e-9)
		{
			runDir = (faces[0].Outward ?? (faces[0].Point - center));
		}

		if (runDir.GetLength() < 1e-9)
		{
			return;
		}

		runDir = runDir.Normalize();
		double searchRadius = Math.Max(1.0, EstimateTeeSearchRadius(tee));
		TeeFace best = null;
		double bestScore = double.MinValue;

		foreach (PcfComponent other in pcf.Components)
		{
			if (other == null || ReferenceEquals(other, tee) || other.IsOlet || other.EndPoints.Count < 2)
			{
				continue;
			}

			for (int i = 0; i < other.EndPoints.Count; i++)
			{
				PcfEndPoint ep = other.EndPoints[i];
				if (ep?.Point == null || ep.Point.DistanceTo(center) > searchRadius)
				{
					continue;
				}

				if (faces.Any(f => f.Point.DistanceTo(ep.Point) <= 0.08))
				{
					continue;
				}

				XYZ otherPt = other.EndPoints[i == 0 ? 1 : 0].Point;
				if (otherPt == null)
				{
					continue;
				}

				XYZ outward = otherPt - ep.Point;
				if (outward.GetLength() < MinSegmentLengthFeet)
				{
					continue;
				}

				outward = outward.Normalize();
				XYZ fromCenter = ep.Point - center;
				if (fromCenter.GetLength() < 1e-6)
				{
					continue;
				}

				double alongRun = Math.Abs(fromCenter.Normalize().DotProduct(runDir));
				double perp = Math.Abs(outward.DotProduct(runDir));
				// Want a face off the run axis with pipe leaving roughly away from center.
				double away = outward.DotProduct(fromCenter.Normalize());
				double score = (1.0 - alongRun) * 2.0 + (1.0 - perp) + away - ep.Point.DistanceTo(center);
				if (score > bestScore)
				{
					bestScore = score;
					best = new TeeFace
					{
						Point = ep.Point,
						BoreInches = ep.BoreInches > 1e-6
							? ep.BoreInches
							: (other.NominalSizeInches > 1e-6 ? other.NominalSizeInches : tee.NominalSizeInches),
						Outward = outward
					};
				}
			}
		}

		if (best != null && bestScore > -0.5)
		{
			faces.Add(best);
		}
	}

	private static List<TeeFace> PickBestTeeTrio(List<TeeFace> faces)
	{
		if (faces == null || faces.Count <= 3)
		{
			return faces ?? new List<TeeFace>();
		}

		double bestScore = double.MinValue;
		List<TeeFace> best = faces.Take(3).ToList();
		for (int i = 0; i < faces.Count; i++)
		{
			for (int j = i + 1; j < faces.Count; j++)
			{
				for (int k = j + 1; k < faces.Count; k++)
				{
					var trio = new List<TeeFace> { faces[i], faces[j], faces[k] };
					ClassifyTeeFaces(trio, out TeeFace runA, out TeeFace runB, out TeeFace branch);
					if (runA == null || runB == null || branch == null)
					{
						continue;
					}

					XYZ center = (runA.Point + runB.Point + branch.Point) * (1.0 / 3.0);
					XYZ run = runB.Point - runA.Point;
					XYZ br = branch.Point - center;
					if (run.GetLength() < 1e-9 || br.GetLength() < 1e-9)
					{
						continue;
					}

					double runOpp = (runA.Point - center).Normalize().DotProduct((runB.Point - center).Normalize());
					double branchPerp = 1.0 - Math.Abs(br.Normalize().DotProduct(run.Normalize()));
					double neighborBonus =
						(runA.Outward != null ? 0.3 : 0) +
						(runB.Outward != null ? 0.3 : 0) +
						(branch.Outward != null ? 0.5 : 0);
					double score = (-runOpp) + branchPerp * 2.0 + neighborBonus;
					if (score > bestScore)
					{
						bestScore = score;
						best = trio;
					}
				}
			}
		}

		return best;
	}

	private static void AddOrMergeTeeFace(
		List<TeeFace> faces,
		XYZ center,
		double searchRadius,
		XYZ point,
		double bore,
		XYZ outwardHint)
	{
		if (faces == null || point == null || center == null || point.DistanceTo(center) > searchRadius)
		{
			return;
		}

		for (int i = 0; i < faces.Count; i++)
		{
			if (faces[i].Point.DistanceTo(point) <= 0.06)
			{
				if (bore > faces[i].BoreInches)
				{
					faces[i].BoreInches = bore;
				}

				if (faces[i].Outward == null && outwardHint != null)
				{
					faces[i].Outward = outwardHint;
				}

				return;
			}
		}

		faces.Add(new TeeFace
		{
			Point = point,
			BoreInches = bore,
			Outward = outwardHint
		});
	}

	private static double EstimateTeeSearchRadius(PcfComponent tee)
	{
		double size = tee.NominalSizeInches > 1e-6 ? tee.NominalSizeInches : DefaultBoreInches;
		if (tee.EndPoints.Count >= 2 && tee.EndPoints[0].Point != null && tee.EndPoints[1].Point != null)
		{
			size = Math.Max(size, tee.EndPoints[0].Point.DistanceTo(tee.EndPoints[1].Point) * 12.0);
		}

		return Math.Max(1.0, (size / 12.0) * 2.5);
	}

	private static XYZ FindNeighborOutwardFromPoint(PcfDocument pcf, PcfComponent self, XYZ face, XYZ center)
	{
		const double tol = 0.06;
		XYZ best = null;
		double bestLen = 0;
		foreach (PcfComponent other in pcf.Components)
		{
			if (other == null || ReferenceEquals(other, self) || other.EndPoints.Count < 2)
			{
				continue;
			}

			XYZ a = other.EndPoints[0].Point;
			XYZ b = other.EndPoints[1].Point;
			if (a == null || b == null)
			{
				continue;
			}

			XYZ outward = null;
			if (a.DistanceTo(face) <= tol)
			{
				outward = b - a;
			}
			else if (b.DistanceTo(face) <= tol)
			{
				outward = a - b;
			}

			if (outward == null || outward.GetLength() < MinSegmentLengthFeet)
			{
				continue;
			}

			double len = outward.GetLength();
			if (len > bestLen)
			{
				bestLen = len;
				best = outward.Normalize();
			}
		}

		if (best == null && face != null && center != null && face.DistanceTo(center) > 1e-6)
		{
			best = (face - center).Normalize();
		}

		return best;
	}

	private static void TrySynthesizeThirdTeeFace(List<TeeFace> faces, XYZ center)
	{
		if (faces == null || faces.Count != 2 || center == null)
		{
			return;
		}

		TeeFace a = faces[0];
		TeeFace b = faces[1];
		XYZ va = a.Point - center;
		XYZ vb = b.Point - center;
		if (va.GetLength() < 1e-6 || vb.GetLength() < 1e-6)
		{
			return;
		}

		va = va.Normalize();
		vb = vb.Normalize();
		double dot = va.DotProduct(vb);

		// Two faces already look like a run (opposite): invent branch perpendicular.
		if (dot < -0.5)
		{
			XYZ run = (a.Point - b.Point);
			if (run.GetLength() < 1e-6)
			{
				return;
			}

			run = run.Normalize();
			XYZ branchDir = Math.Abs(run.DotProduct(XYZ.BasisZ)) < 0.9
				? run.CrossProduct(XYZ.BasisZ).Normalize()
				: run.CrossProduct(XYZ.BasisX).Normalize();
			double takeout = 0.5 * a.Point.DistanceTo(b.Point);
			faces.Add(new TeeFace
			{
				Point = center + branchDir.Multiply(takeout),
				BoreInches = Math.Max(a.BoreInches, b.BoreInches),
				Outward = branchDir
			});
			return;
		}

		// One run face + branch: invent opposite run face.
		XYZ knownRun = Math.Abs(dot) < 0.5 ? (a.Outward ?? va) : va;
		if (knownRun.GetLength() < 1e-6)
		{
			return;
		}

		knownRun = knownRun.Normalize();
		double dist = Math.Max(a.Point.DistanceTo(center), b.Point.DistanceTo(center));
		XYZ third = center - knownRun.Multiply(dist);
		if (third.DistanceTo(a.Point) > 0.1 && third.DistanceTo(b.Point) > 0.1)
		{
			faces.Add(new TeeFace
			{
				Point = third,
				BoreInches = Math.Max(a.BoreInches, b.BoreInches),
				Outward = knownRun.Negate()
			});
		}
	}

	private static void ClassifyTeeFaces(List<TeeFace> faces, out TeeFace runA, out TeeFace runB, out TeeFace branch)
	{
		runA = runB = branch = null;
		if (faces == null || faces.Count < 3)
		{
			return;
		}

		XYZ center = (faces[0].Point + faces[1].Point + faces[2].Point) * (1.0 / 3.0);

		// Prefer branch = face whose neighbor outward is most perpendicular to the run.
		int bestBranch = -1;
		double bestBranchScore = double.MinValue;
		for (int b = 0; b < faces.Count; b++)
		{
			int i = (b + 1) % faces.Count;
			int j = (b + 2) % faces.Count;
			XYZ run = faces[j].Point - faces[i].Point;
			if (run.GetLength() < 1e-9)
			{
				continue;
			}

			run = run.Normalize();
			XYZ outward = faces[b].Outward;
			if (outward == null || outward.GetLength() < 1e-9)
			{
				outward = faces[b].Point - center;
			}

			if (outward.GetLength() < 1e-9)
			{
				continue;
			}

			outward = outward.Normalize();
			double perp = 1.0 - Math.Abs(outward.DotProduct(run));
			double away = outward.DotProduct((faces[b].Point - center).Normalize());
			double runOpp = (faces[i].Point - center).Normalize().DotProduct((faces[j].Point - center).Normalize());
			double score = perp * 2.0 + away - runOpp;
			if (faces[b].Outward != null)
			{
				score += 0.75;
			}

			if (score > bestBranchScore)
			{
				bestBranchScore = score;
				bestBranch = b;
			}
		}

		if (bestBranch >= 0)
		{
			branch = faces[bestBranch];
			var runs = new List<TeeFace>();
			for (int k = 0; k < faces.Count; k++)
			{
				if (k != bestBranch)
				{
					runs.Add(faces[k]);
				}
			}

			runA = runs[0];
			runB = runs[1];
			return;
		}

		double bestOpp = double.MaxValue;
		int bestI = 0, bestJ = 1;
		for (int i = 0; i < faces.Count; i++)
		{
			for (int j = i + 1; j < faces.Count; j++)
			{
				XYZ vi = faces[i].Point - center;
				XYZ vj = faces[j].Point - center;
				if (vi.GetLength() < 1e-9 || vj.GetLength() < 1e-9)
				{
					continue;
				}

				double opp = vi.Normalize().DotProduct(vj.Normalize());
				if (opp < bestOpp)
				{
					bestOpp = opp;
					bestI = i;
					bestJ = j;
				}
			}
		}

		runA = faces[bestI];
		runB = faces[bestJ];
		for (int k = 0; k < faces.Count; k++)
		{
			if (k != bestI && k != bestJ)
			{
				branch = faces[k];
				break;
			}
		}
	}

	private static FabricationServiceButton PickTeeButton(PaletteButtons buttons, bool reducing)
	{
		if (buttons == null)
		{
			return null;
		}

		if (reducing && buttons.ReducingTees.Count > 0)
		{
			return buttons.ReducingTees[0];
		}

		if (buttons.Tees.Count > 0)
		{
			return buttons.Tees[0];
		}

		if (buttons.ReducingTees.Count > 0)
		{
			return buttons.ReducingTees[0];
		}

		return null;
	}

	private static bool TryApplyReducingTeeProductListSize(
		Document doc,
		FabricationPart part,
		double runInches,
		double branchInches)
	{
		try
		{
			if (part == null || !part.IsProductList())
			{
				return false;
			}

			string target = (FormatNpsToken(runInches) + "x" + FormatNpsToken(runInches) + "x" + FormatNpsToken(branchInches))
				.ToUpperInvariant();
			int count = part.GetProductListEntryCount();
			int best = -1;
			double bestDelta = double.MaxValue;
			for (int i = 0; i < count; i++)
			{
				string name = part.GetProductListEntryName(i) ?? string.Empty;
				string normalized = System.Text.RegularExpressions.Regex.Replace(
					name,
					@"[""'\u2032\u2033\u00F8\u2300\s]+",
					string.Empty).ToUpperInvariant();

				if (normalized.Contains(target) || normalized.StartsWith(target, StringComparison.Ordinal))
				{
					best = i;
					bestDelta = 0;
					break;
				}

				if (!TryParseTeeProductSize(name, out double r1, out double r2, out double b))
				{
					continue;
				}

				double run = Math.Max(r1, r2);
				double delta = Math.Abs(run - runInches) + Math.Abs(b - branchInches);
				if (delta < bestDelta)
				{
					bestDelta = delta;
					best = i;
				}
			}

			if (best < 0 || bestDelta > 0.15)
			{
				return false;
			}

			part.ProductListEntry = best;
			doc?.Regenerate();
			return true;
		}
		catch
		{
			return false;
		}
	}

	private static bool TryParseTeeProductSize(string text, out double run1, out double run2, out double branch)
	{
		run1 = run2 = branch = 0;
		if (string.IsNullOrWhiteSpace(text))
		{
			return false;
		}

		string[] parts = text.Split(new[] { 'x', 'X', '×' }, StringSplitOptions.RemoveEmptyEntries);
		if (parts.Length < 3)
		{
			return false;
		}

		run1 = PcfParser.ParseSizeInches(parts[0]);
		run2 = PcfParser.ParseSizeInches(parts[1]);
		branch = PcfParser.ParseSizeInches(parts[2]);
		return run1 > 1e-6 && run2 > 1e-6 && branch > 1e-6;
	}

	private static bool PlaceTee(Document doc, FabricationPart part, TeeFace runA, TeeFace runB, TeeFace branch)
	{
		if (doc == null || part == null || runA?.Point == null || runB?.Point == null || branch?.Point == null)
		{
			return false;
		}

		List<Connector> connectors = GetEndConnectors(part);
		if (connectors.Count < 3)
		{
			return false;
		}

		if (!TryIdentifyTeeConnectors(connectors, runA, runB, branch, out Connector cRunA, out Connector cRunB, out Connector cBranch))
		{
			return false;
		}

		XYZ wantRun = runB.Point - runA.Point;
		if (wantRun.GetLength() < 1e-9)
		{
			return false;
		}

		wantRun = wantRun.Normalize();
		XYZ haveRun = cRunB.Origin - cRunA.Origin;
		if (haveRun.GetLength() < 1e-9)
		{
			return false;
		}

		haveRun = haveRun.Normalize();
		XYZ runMid = (runA.Point + runB.Point) * 0.5;

		OrientPartAlongDirection(doc, part, cRunA.Origin, haveRun, wantRun);
		part = doc.GetElement(part.Id) as FabricationPart;
		connectors = GetEndConnectors(part);
		if (part == null || !TryIdentifyTeeConnectors(connectors, runA, runB, branch, out cRunA, out cRunB, out cBranch))
		{
			return false;
		}

		XYZ partRunMid = (cRunA.Origin + cRunB.Origin) * 0.5;
		XYZ move = runMid - partRunMid;
		if (move.GetLength() > 1e-9)
		{
			ElementTransformUtils.MoveElement(doc, part.Id, move);
		}

		part = doc.GetElement(part.Id) as FabricationPart;
		connectors = GetEndConnectors(part);
		if (part == null || !TryIdentifyTeeConnectors(connectors, runA, runB, branch, out cRunA, out cRunB, out cBranch))
		{
			return false;
		}

		XYZ swingAxis = wantRun;
		XYZ fromVec = ProjectPerpendicularToAxis(cBranch.Origin - runMid, swingAxis);
		XYZ toVec = ProjectPerpendicularToAxis(branch.Point - runMid, swingAxis);
		if (fromVec.GetLength() > 1e-8 && toVec.GetLength() > 1e-8)
		{
			double angle = SignedAngleAroundAxis(fromVec.Normalize(), toVec.Normalize(), swingAxis);
			if (Math.Abs(angle) > 1e-6)
			{
				Line rotationAxis = Line.CreateBound(runMid, runMid + swingAxis);
				ElementTransformUtils.RotateElement(doc, part.Id, rotationAxis, angle);
			}
		}

		part = doc.GetElement(part.Id) as FabricationPart;
		connectors = GetEndConnectors(part);
		if (part == null || !TryIdentifyTeeConnectors(connectors, runA, runB, branch, out cRunA, out cRunB, out cBranch))
		{
			return false;
		}

		// If the branch outlet still faces the opposite way, spin 180 about the run.
		XYZ wantBranch = branch.Point - runMid;
		if (wantBranch.GetLength() > 1e-9)
		{
			wantBranch = wantBranch.Normalize();
			XYZ haveBranch = GetConnectorOutwardAxis(cBranch);
			if (haveBranch == null || haveBranch.GetLength() < 1e-9)
			{
				haveBranch = cBranch.Origin - runMid;
			}

			if (haveBranch.GetLength() > 1e-9)
			{
				haveBranch = haveBranch.Normalize();
				XYZ haveSide = ProjectPerpendicularToAxis(cBranch.Origin - runMid, swingAxis);
				XYZ wantSide = ProjectPerpendicularToAxis(branch.Point - runMid, swingAxis);
				bool axisWrong = haveBranch.DotProduct(wantBranch) < 0.15;
				bool sideWrong = haveSide.GetLength() > 1e-8 && wantSide.GetLength() > 1e-8
					&& haveSide.Normalize().DotProduct(wantSide.Normalize()) < 0;
				if (axisWrong || sideWrong)
				{
					Line rotationAxis = Line.CreateBound(runMid, runMid + swingAxis);
					ElementTransformUtils.RotateElement(doc, part.Id, rotationAxis, Math.PI);
					part = doc.GetElement(part.Id) as FabricationPart;
					connectors = GetEndConnectors(part);
					if (part != null)
					{
						TryIdentifyTeeConnectors(connectors, runA, runB, branch, out cRunA, out cRunB, out cBranch);
					}
				}
			}
		}

		if (part != null && cRunA != null && cRunB != null && cBranch != null)
		{
			XYZ nudge =
				((runA.Point - cRunA.Origin) + (runB.Point - cRunB.Origin) + (branch.Point - cBranch.Origin))
				* (1.0 / 3.0);
			if (nudge.GetLength() > 1e-9 && nudge.GetLength() < 0.75)
			{
				ElementTransformUtils.MoveElement(doc, part.Id, nudge);
			}
		}

		return true;
	}

	/// <summary>
	/// Pick run/branch connectors by part geometry first (branch = non-collinear outlet),
	/// then assign run ends to the nearer target faces.
	/// </summary>
	private static bool TryIdentifyTeeConnectors(
		IList<Connector> connectors,
		TeeFace runA,
		TeeFace runB,
		TeeFace branch,
		out Connector cRunA,
		out Connector cRunB,
		out Connector cBranch)
	{
		cRunA = cRunB = cBranch = null;
		if (connectors == null || connectors.Count < 3)
		{
			return false;
		}

		Connector geoBranch = null;
		Connector geoRun0 = null;
		Connector geoRun1 = null;
		double bestPerp = double.MinValue;
		for (int b = 0; b < connectors.Count; b++)
		{
			var others = new List<Connector>();
			for (int i = 0; i < connectors.Count; i++)
			{
				if (i != b)
				{
					others.Add(connectors[i]);
				}
			}

			if (others.Count < 2)
			{
				continue;
			}

			XYZ run = others[1].Origin - others[0].Origin;
			if (run.GetLength() < 1e-9)
			{
				continue;
			}

			run = run.Normalize();
			XYZ mid = (others[0].Origin + others[1].Origin) * 0.5;
			XYZ branchVec = connectors[b].Origin - mid;
			if (branchVec.GetLength() < 1e-9)
			{
				XYZ axis = GetConnectorOutwardAxis(connectors[b]);
				if (axis != null)
				{
					branchVec = axis;
				}
			}

			if (branchVec.GetLength() < 1e-9)
			{
				continue;
			}

			double perp = 1.0 - Math.Abs(branchVec.Normalize().DotProduct(run));
			double radiusBias = 0;
			try
			{
				if (branch.BoreInches > 1e-6)
				{
					radiusBias = -Math.Abs(connectors[b].Radius * 24.0 - branch.BoreInches) * 0.05;
				}
			}
			catch
			{
			}

			if (perp + radiusBias > bestPerp)
			{
				bestPerp = perp + radiusBias;
				geoBranch = connectors[b];
				geoRun0 = others[0];
				geoRun1 = others[1];
			}
		}

		if (geoBranch == null)
		{
			return TryPickTeeConnectors(connectors, runA, runB, branch, out cRunA, out cRunB, out cBranch);
		}

		cBranch = geoBranch;
		if (geoRun0.Origin.DistanceTo(runA.Point) + geoRun1.Origin.DistanceTo(runB.Point)
			<= geoRun0.Origin.DistanceTo(runB.Point) + geoRun1.Origin.DistanceTo(runA.Point))
		{
			cRunA = geoRun0;
			cRunB = geoRun1;
		}
		else
		{
			cRunA = geoRun1;
			cRunB = geoRun0;
		}

		return true;
	}

	private static bool TryPickTeeConnectors(
		IList<Connector> connectors,
		TeeFace runA,
		TeeFace runB,
		TeeFace branch,
		out Connector cRunA,
		out Connector cRunB,
		out Connector cBranch)
	{
		cRunA = cRunB = cBranch = null;
		if (connectors == null || connectors.Count < 3)
		{
			return false;
		}

		Connector bestBranch = null;
		double bestBranchScore = double.MaxValue;
		double branchRadius = branch.BoreInches > 1e-6 ? (branch.BoreInches / 24.0) : -1;
		foreach (Connector c in connectors)
		{
			double score = c.Origin.DistanceTo(branch.Point);
			try
			{
				if (branchRadius > 0)
				{
					score += Math.Abs(c.Radius - branchRadius) * 20.0;
				}
			}
			catch
			{
			}

			if (score < bestBranchScore)
			{
				bestBranchScore = score;
				bestBranch = c;
			}
		}

		cBranch = bestBranch;
		Connector branchConn = bestBranch;
		var runs = new List<Connector>();
		foreach (Connector c in connectors)
		{
			if (!ReferenceEquals(c, branchConn))
			{
				runs.Add(c);
			}
		}

		if (runs.Count < 2)
		{
			return false;
		}

		Connector r0 = runs[0];
		Connector r1 = runs[1];
		double dDirect = r0.Origin.DistanceTo(runA.Point) + r1.Origin.DistanceTo(runB.Point);
		double dSwap = r0.Origin.DistanceTo(runB.Point) + r1.Origin.DistanceTo(runA.Point);
		if (dDirect <= dSwap)
		{
			cRunA = r0;
			cRunB = r1;
		}
		else
		{
			cRunA = r1;
			cRunB = r0;
		}

		return cRunA != null && cRunB != null && cBranch != null;
	}

	private static void TryPlaceOlet(
		Document doc,
		PcfDocument pcf,
		PcfComponent component,
		IList<FabricationServiceButton> oletButtons,
		ImportOptions options,
		ImportResult result)
	{
		if (oletButtons == null || oletButtons.Count == 0)
		{
			result.Skipped++;
			result.Warnings.Add("No olet button in palette for " + component.Type + FormatId(component) + ".");
			return;
		}

		if (!TryGetOletPoints(component, out XYZ header, out XYZ branch))
		{
			result.Skipped++;
			result.Warnings.Add("Skipped " + component.Type + FormatId(component) + " (missing END-POINTs).");
			return;
		}

		ParseOletSizes(component, options, out double headerInches, out double branchInches);
		ElementId levelId = FindNearestLevelId(doc, header, branch);
		if (levelId == null || levelId == ElementId.InvalidElementId)
		{
			result.Skipped++;
			result.Warnings.Add("No level found for " + component.Type + FormatId(component) + ".");
			return;
		}

		FabricationPart host = FindHostStraightNearPoint(doc, result.CreatedFabIds, header);
		if (host == null)
		{
			result.Skipped++;
			result.Warnings.Add(
				"Skipped " + component.Type + FormatId(component)
				+ " (no host pipe at header — extend/place main run first).");
			return;
		}

		try
		{
			FabricationPart part = CreateSizedOletPart(
				doc,
				oletButtons,
				headerInches,
				branchInches,
				levelId,
				out bool sizeApplied);
			if (part == null)
			{
				result.Skipped++;
				result.Warnings.Add("Create failed for " + component.Type + FormatId(component) + ".");
				return;
			}

			if (!sizeApplied)
			{
				result.Warnings.Add(
					component.Type + FormatId(component)
					+ " created but product-list size "
					+ FormatOletSizeLabel(headerInches, branchInches)
					+ " could not be applied.");
			}

			TrySetSizeParameters(part, headerInches, FormatOletSizeLabel(headerInches, branchInches));

			if (!PlaceOletAsTap(doc, part, host, header, branch))
			{
				try
				{
					doc.Delete(part.Id);
				}
				catch
				{
				}

				result.Skipped++;
				result.Warnings.Add(
					"Could not PlaceAsTap " + component.Type + FormatId(component)
					+ " onto host pipe.");
				return;
			}

			TrySetPartIdentity(part, pcf, component, FormatOletSizeLabel(headerInches, branchInches));
			RegisterCreatedPart(result, part, component);
			result.FabPartsCreated++;
			result.OletsCreated++;
		}
		catch (Exception ex)
		{
			result.Skipped++;
			result.Warnings.Add("Failed " + component.Type + FormatId(component) + ": " + ex.Message);
		}
	}

	/// <summary>
	/// Create an olet from the first palette button whose product list contains the reducing size
	/// (e.g. Weld-O-Let for 6x2-1/2 when Thread-O-Let only has 6x2).
	/// </summary>
	private static FabricationPart CreateSizedOletPart(
		Document doc,
		IList<FabricationServiceButton> oletButtons,
		double headerInches,
		double branchInches,
		ElementId levelId,
		out bool sizeApplied)
	{
		sizeApplied = false;
		if (doc == null || oletButtons == null || oletButtons.Count == 0)
		{
			return null;
		}

		List<FabricationServiceButton> ranked = RankOletButtons(oletButtons);
		FabricationPart fallback = null;

		foreach (FabricationServiceButton button in ranked)
		{
			if (button == null)
			{
				continue;
			}

			FabricationPart part = CreateSizedPart(doc, button, headerInches, levelId);
			if (part == null)
			{
				continue;
			}

			if (TryApplyOletProductListSize(doc, part, headerInches, branchInches))
			{
				if (fallback != null)
				{
					try
					{
						doc.Delete(fallback.Id);
					}
					catch
					{
					}
				}

				sizeApplied = true;
				return part;
			}

			if (fallback == null)
			{
				fallback = part;
			}
			else
			{
				try
				{
					doc.Delete(part.Id);
				}
				catch
				{
				}
			}
		}

		return fallback;
	}

	private static FabricationPart FindHostStraightNearPoint(Document doc, IList<ElementId> createdIds, XYZ point)
	{
		List<FabricationPart> straights = createdIds
			.Select(id => doc.GetElement(id) as FabricationPart)
			.Where(p => p != null && p.IsAStraight())
			.ToList();

		return FindBestHostStraight(
			straights,
			point,
			out _,
			out _,
			out _,
			out _,
			out _);
	}

	/// <summary>
	/// Place olet on an already-placed straight using PlaceAsTap (cuts in / couples to the run).
	/// </summary>
	private static bool PlaceOletAsTap(
		Document doc,
		FabricationPart olet,
		FabricationPart host,
		XYZ header,
		XYZ branch)
	{
		if (doc == null || olet == null || host == null || header == null || branch == null)
		{
			return false;
		}

		List<Connector> hostEnds = GetEndConnectors(host);
		List<Connector> oletEnds = GetEndConnectors(olet);
		if (hostEnds.Count < 2 || oletEnds.Count < 2)
		{
			return false;
		}

		Connector headerConn = oletEnds.OrderByDescending(c =>
		{
			try { return c.Radius; }
			catch { return 0; }
		}).First();

		// Use the host end that yields a positive distance along the run to the header.
		Connector hostRef = null;
		double distance = 0;
		XYZ pipeAxis = null;
		double best = double.MaxValue;
		foreach (Connector end in hostEnds)
		{
			Connector other = hostEnds.First(c => !ReferenceEquals(c, end));
			XYZ axis = other.Origin - end.Origin;
			double len = axis.GetLength();
			if (len < MinSegmentLengthFeet)
			{
				continue;
			}

			axis = axis.Normalize();
			double t = (header - end.Origin).DotProduct(axis);
			if (t < -0.02 || t > len + 0.02)
			{
				continue;
			}

			double radial = (header - (end.Origin + axis.Multiply(t))).GetLength();
			if (radial < best)
			{
				best = radial;
				hostRef = end;
				distance = Math.Max(0, Math.Min(len, t));
				pipeAxis = axis;
			}
		}

		if (hostRef == null || pipeAxis == null)
		{
			return false;
		}

		XYZ wantBranch = branch - header;
		if (wantBranch.GetLength() < 1e-9)
		{
			return false;
		}

		wantBranch = wantBranch.Normalize();
		double axisRotation = ComputeTapAxisRotation(pipeAxis, wantBranch);

		try
		{
			FabricationPart.PlaceAsTap(doc, headerConn, hostRef, distance, axisRotation, 0.0);
			doc.Regenerate();
		}
		catch
		{
			try
			{
				FabricationPart.PlaceAsTap(doc, headerConn, hostRef, distance, 0.0, 0.0);
				doc.Regenerate();
				olet = doc.GetElement(olet.Id) as FabricationPart;
				if (olet != null)
				{
					AimConnectedTap(doc, olet, host, header, wantBranch);
				}
			}
			catch
			{
				return false;
			}
		}

		olet = doc.GetElement(olet.Id) as FabricationPart;
		if (olet == null)
		{
			return false;
		}

		AimConnectedTap(doc, olet, host, header, wantBranch);

		List<Connector> after = GetEndConnectors(olet);
		Connector large = after.OrderByDescending(c =>
		{
			try { return c.Radius; }
			catch { return 0; }
		}).FirstOrDefault();
		return large != null && large.IsConnected;
	}

	private static double ComputeTapAxisRotation(XYZ pipeAxis, XYZ wantBranch)
	{
		// Empirically PlaceAsTap(..., 0, 0) aims branch along a world basis perpendicular to the pipe.
		// Match the probe: for pipe ≈ ±X, 0→+Y and π/2→+Z.
		XYZ axis = pipeAxis.Normalize();
		XYZ want = ProjectPerpendicularToAxis(wantBranch, axis);
		if (want.GetLength() < 1e-9)
		{
			return 0;
		}

		want = want.Normalize();

		XYZ reference;
		if (Math.Abs(axis.DotProduct(XYZ.BasisY)) < 0.9)
		{
			reference = ProjectPerpendicularToAxis(XYZ.BasisY, axis);
		}
		else
		{
			reference = ProjectPerpendicularToAxis(XYZ.BasisX, axis);
		}

		if (reference.GetLength() < 1e-9)
		{
			reference = ProjectPerpendicularToAxis(XYZ.BasisZ, axis);
		}

		if (reference.GetLength() < 1e-9)
		{
			return 0;
		}

		return SignedAngleAroundAxis(reference.Normalize(), want, axis);
	}

	private static void AimConnectedTap(
		Document doc,
		FabricationPart olet,
		FabricationPart host,
		XYZ header,
		XYZ wantBranch)
	{
		try
		{
			List<Connector> ends = GetEndConnectors(olet);
			if (ends.Count < 2)
			{
				return;
			}

			Connector large = ends.OrderByDescending(c =>
			{
				try { return c.Radius; }
				catch { return 0; }
			}).First();
			Connector small = ends.First(c => !ReferenceEquals(c, large));
			XYZ actual = small.Origin - large.Origin;
			if (actual.GetLength() < 1e-9)
			{
				return;
			}

			List<Connector> hostEnds = GetEndConnectors(host);
			if (hostEnds.Count < 2)
			{
				return;
			}

			XYZ pipeAxis = (hostEnds[1].Origin - hostEnds[0].Origin).Normalize();
			XYZ from = ProjectPerpendicularToAxis(actual.Normalize(), pipeAxis);
			XYZ to = ProjectPerpendicularToAxis(wantBranch, pipeAxis);
			if (from.GetLength() < 1e-9 || to.GetLength() < 1e-9)
			{
				return;
			}

			double angle = SignedAngleAroundAxis(from.Normalize(), to.Normalize(), pipeAxis);
			if (Math.Abs(angle) < 1e-4)
			{
				return;
			}

			FabricationPart.RotateConnectedTap(doc, olet, angle, 0.0);
			doc.Regenerate();
		}
		catch
		{
		}
	}

	private static bool TryApplyOletProductListSize(Document doc, FabricationPart part, double headerInches, double branchInches)
	{
		try
		{
			if (part == null || !part.IsProductList())
			{
				return false;
			}

			string target = (FormatNpsToken(headerInches) + "x" + FormatNpsToken(branchInches)).ToUpperInvariant();
			int count = part.GetProductListEntryCount();
			int best = -1;
			double bestDelta = double.MaxValue;
			for (int i = 0; i < count; i++)
			{
				string name = part.GetProductListEntryName(i) ?? string.Empty;
				string normalized = System.Text.RegularExpressions.Regex.Replace(
					name,
					@"[""'\u2032\u2033\u00F8\u2300\s]+",
					string.Empty).ToUpperInvariant();

				if (normalized.Contains(target) || normalized.StartsWith(target, StringComparison.Ordinal))
				{
					best = i;
					bestDelta = 0;
					break;
				}

				if (!TryParseReducingSize(name, out double h, out double b))
				{
					continue;
				}

				double delta = Math.Abs(h - headerInches) + Math.Abs(b - branchInches);
				if (delta < bestDelta)
				{
					bestDelta = delta;
					best = i;
				}
			}

			if (best < 0 || bestDelta > 0.12)
			{
				return false;
			}

			part.ProductListEntry = best;
			doc?.Regenerate();
			part = doc?.GetElement(part.Id) as FabricationPart ?? part;

			// Verify a connector near the branch NPS exists (header is large).
			double branchRadiusFeet = (branchInches / 12.0) * 0.5;
			foreach (Connector c in GetEndConnectors(part))
			{
				try
				{
					if (Math.Abs(c.Radius - branchRadiusFeet) <= 0.02 / 12.0 * 6.0
						|| Math.Abs(c.Radius * 24.0 - branchInches) <= 0.2)
					{
						return true;
					}
				}
				catch
				{
				}
			}

			return bestDelta <= 0.12;
		}
		catch
		{
			return false;
		}
	}

	private static bool PlaceOlet(Document doc, FabricationPart part, XYZ header, XYZ branch)
	{
		// Legacy free-place fallback (not connected). Prefer PlaceOletAsTap.
		List<Connector> connectors = GetEndConnectors(part);
		if (connectors.Count < 2)
		{
			return false;
		}

		Connector headerConn = connectors.OrderByDescending(c =>
		{
			try { return c.Radius; }
			catch { return 0; }
		}).First();
		Connector branchConn = connectors.First(c => !ReferenceEquals(c, headerConn));

		XYZ move = header - headerConn.Origin;
		if (move.GetLength() > 1e-9)
		{
			ElementTransformUtils.MoveElement(doc, part.Id, move);
		}

		part = doc.GetElement(part.Id) as FabricationPart;
		connectors = GetEndConnectors(part);
		if (part == null || connectors.Count < 2)
		{
			return false;
		}

		headerConn = connectors.OrderByDescending(c =>
		{
			try { return c.Radius; }
			catch { return 0; }
		}).First();
		branchConn = connectors.First(c => !ReferenceEquals(c, headerConn));

		XYZ from = branchConn.Origin - headerConn.Origin;
		XYZ to = branch - header;
		if (from.GetLength() > 1e-9 && to.GetLength() > 1e-9)
		{
			OrientPartAlongDirection(doc, part, headerConn.Origin, from.Normalize(), to.Normalize());
		}

		part = doc.GetElement(part.Id) as FabricationPart;
		connectors = GetEndConnectors(part);
		if (part == null || connectors.Count < 2)
		{
			return false;
		}

		headerConn = connectors.OrderByDescending(c =>
		{
			try { return c.Radius; }
			catch { return 0; }
		}).First();
		XYZ nudge = header - headerConn.Origin;
		if (nudge.GetLength() > 1e-8)
		{
			ElementTransformUtils.MoveElement(doc, part.Id, nudge);
		}

		return true;
	}

	private static bool TryGetOletPoints(PcfComponent component, out XYZ header, out XYZ branch)
	{
		header = null;
		branch = null;
		if (component?.EndPoints == null || component.EndPoints.Count < 2)
		{
			return false;
		}

		XYZ a = component.EndPoints[0].Point;
		XYZ b = component.EndPoints[1].Point;
		if (a == null || b == null || a.DistanceTo(b) < MinSegmentLengthFeet)
		{
			return false;
		}

		// Header sits on the main run; branch tip is the short stub end.
		// Prefer the endpoint with larger bore as header when they differ.
		double boreA = component.EndPoints[0].BoreInches;
		double boreB = component.EndPoints[1].BoreInches;
		if (boreA > boreB + 1e-6)
		{
			header = a;
			branch = b;
		}
		else if (boreB > boreA + 1e-6)
		{
			header = b;
			branch = a;
		}
		else
		{
			// Same bore in PCF — keep file order (export writes header/run point first).
			header = a;
			branch = b;
		}

		return true;
	}

	private static void ParseOletSizes(PcfComponent component, ImportOptions options, out double headerInches, out double branchInches)
	{
		headerInches = component?.NominalSizeInches ?? 0;
		branchInches = 0;
		string sizeText = component?.SizeText ?? string.Empty;
		if (TryParseReducingSize(sizeText, out double h, out double b))
		{
			headerInches = h;
			branchInches = b;
		}

		if (headerInches <= 1e-6)
		{
			headerInches = options?.DefaultSizeInches > 1e-6 ? options.DefaultSizeInches : DefaultBoreInches;
		}

		if (branchInches <= 1e-6)
		{
			// Common instrument/branch default when PCF only carries header NPS.
			branchInches = 0.75;
		}
	}

	internal static bool TryParseReducingSize(string sizeText, out double headerInches, out double branchInches)
	{
		headerInches = 0;
		branchInches = 0;
		if (string.IsNullOrWhiteSpace(sizeText))
		{
			return false;
		}

		// Product-list names are like 6''x3/4'' — strip inch marks so regex can see 6x3/4.
		string text = System.Text.RegularExpressions.Regex.Replace(
			sizeText.Trim(),
			@"[""'\u2032\u2033\u00F8\u2300]+",
			string.Empty);
		text = System.Text.RegularExpressions.Regex.Replace(text, @"\s+", string.Empty);

		// Prefer sanitized / packed forms first so "6x34" is not read as 6 x 34".
		var packed = System.Text.RegularExpressions.Regex.Match(
			text,
			@"^(\d+(?:\.\d+)?)[xX×]([0-9\-]+)$");
		if (packed.Success)
		{
			string branchToken = packed.Groups[2].Value;
			bool looksPacked = branchToken.IndexOf('/') < 0
				&& (branchToken.Contains("-") || branchToken.Length >= 2);
			if (looksPacked)
			{
				double decoded = DecodeSanitizedBranchSize(branchToken);
				if (decoded > 1e-6 && decoded <= 12.0)
				{
					headerInches = PcfParser.ParseSizeInches(packed.Groups[1].Value);
					branchInches = decoded;
					return headerInches > 1e-6;
				}
			}
		}

		// "6x3/4", "6x2-1/2"
		var match = System.Text.RegularExpressions.Regex.Match(
			text,
			@"^(\d+(?:\-\d+/\d+|/\d+|\.\d+)?)[xX×](\d+(?:\-\d+/\d+|/\d+|\.\d+)?)$");
		if (!match.Success)
		{
			return false;
		}

		headerInches = PcfParser.ParseSizeInches(match.Groups[1].Value);
		string branchRaw = match.Groups[2].Value.Trim();
		branchInches = PcfParser.ParseSizeInches(branchRaw);

		if (branchRaw.IndexOf('/') < 0
			&& branchRaw.IndexOf('-') < 0
			&& (branchRaw == "34" || branchRaw == "12" || branchRaw == "14" || branchRaw == "38"
				|| branchRaw == "112" || branchRaw == "114" || branchRaw == "212"
				|| branchInches > 12.0 - 1e-6))
		{
			double decoded = DecodePackedFraction(branchRaw);
			if (decoded > 1e-6)
			{
				branchInches = decoded;
			}
		}

		return headerInches > 1e-6 && branchInches > 1e-6;
	}

	private static string FormatOletSizeLabel(double headerInches, double branchInches)
	{
		return FormatNpsToken(headerInches) + "x" + FormatNpsToken(branchInches) + "\"";
	}

	private static string FormatNpsToken(double inches)
	{
		var known = new (double Value, string Label)[]
		{
			(0.25, "1/4"),
			(0.375, "3/8"),
			(0.5, "1/2"),
			(0.75, "3/4"),
			(1.0, "1"),
			(1.25, "1-1/4"),
			(1.5, "1-1/2"),
			(2.0, "2"),
			(2.5, "2-1/2"),
			(3.0, "3"),
			(3.5, "3-1/2"),
			(4.0, "4"),
			(5.0, "5"),
			(6.0, "6"),
			(8.0, "8"),
			(10.0, "10"),
			(12.0, "12")
		};

		foreach ((double value, string label) in known)
		{
			if (Math.Abs(value - inches) < 0.02)
			{
				return label;
			}
		}

		if (Math.Abs(inches - Math.Round(inches)) < 1e-6)
		{
			return Math.Round(inches).ToString("0", CultureInfo.InvariantCulture);
		}

		return inches.ToString("0.###", CultureInfo.InvariantCulture);
	}

	private static double DecodeSanitizedBranchSize(string token)
	{
		if (string.IsNullOrWhiteSpace(token))
		{
			return 0;
		}

		string t = token.Trim();
		if (t.Contains("-"))
		{
			string[] bits = t.Split('-');
			if (bits.Length == 2
				&& double.TryParse(bits[0], NumberStyles.Float, CultureInfo.InvariantCulture, out double whole))
			{
				double frac = DecodePackedFraction(bits[1]);
				if (frac > 1e-6)
				{
					return whole + frac;
				}
			}
		}

		double packed = DecodePackedFraction(t);
		if (packed > 1e-6)
		{
			return packed;
		}

		return PcfParser.ParseSizeInches(t);
	}

	private static double DecodePackedFraction(string digits)
	{
		switch (digits)
		{
			case "14": return 0.25;
			case "38": return 0.375;
			case "12": return 0.5;
			case "34": return 0.75;
			case "114": return 1.25;
			case "112": return 1.5;
			case "212": return 2.5;
			default:
				return 0;
		}
	}

	private static List<FabricationServiceButton> RankOletButtons(IList<FabricationServiceButton> buttons)
	{
		if (buttons == null || buttons.Count == 0)
		{
			return new List<FabricationServiceButton>();
		}

		// Name preference is only a tie-break — CreateSizedOletPart picks by product-list match.
		return buttons
			.Where(b => b != null)
			.OrderByDescending(ScoreOletButtonName)
			.ToList();
	}

	private static int ScoreOletButtonName(FabricationServiceButton button)
	{
		string corpus = ((button?.Name ?? string.Empty) + " " + (button?.Code ?? string.Empty)).ToUpperInvariant();
		int score = 0;
		if (corpus.Contains("THREAD") || corpus.Contains("TOL"))
		{
			score += 30;
		}

		if (corpus.Contains("WELD-O") || corpus.Contains("WELDO") || corpus.Contains("WOL"))
		{
			score += 20;
		}

		if (corpus.Contains("OLET"))
		{
			score += 10;
		}

		if (corpus.Contains("ELBOLET"))
		{
			score -= 20;
		}

		return score;
	}

	private static FabricationPart FindBestHostStraight(
		IList<FabricationPart> straights,
		XYZ point,
		out XYZ axisOrigin,
		out XYZ axisDir,
		out double tMin,
		out double tMax,
		out double tPoint)
	{
		axisOrigin = null;
		axisDir = null;
		tMin = tMax = tPoint = 0;
		FabricationPart best = null;
		double bestScore = double.MaxValue;
		const double maxRadial = 0.5; // 6"

		foreach (FabricationPart straight in straights)
		{
			List<Connector> ends = GetEndConnectors(straight);
			if (ends.Count < 2)
			{
				continue;
			}

			XYZ a = ends[0].Origin;
			XYZ b = ends[1].Origin;
			XYZ dir = b - a;
			double len = dir.GetLength();
			if (len < MinSegmentLengthFeet)
			{
				continue;
			}

			dir = dir.Normalize();
			double t = (point - a).DotProduct(dir);
			XYZ projected = a + dir.Multiply(t);
			double dist = projected.DistanceTo(point);
			if (dist > maxRadial)
			{
				continue;
			}

			// Prefer a host that already covers this point. Far-outside projections
			// (e.g. olet on a parallel riser matching a short elbow stub) score poorly.
			double outside = 0;
			if (t < 0)
			{
				outside = -t;
			}
			else if (t > len)
			{
				outside = t - len;
			}

			double score = dist + outside * 5.0;
			if (outside <= 0.1)
			{
				score -= 2.0; // strong bonus when already on-span
			}

			if (score < bestScore)
			{
				bestScore = score;
				best = straight;
				axisOrigin = a;
				axisDir = dir;
				tMin = 0;
				tMax = len;
				tPoint = t;
			}
		}

		return best;
	}

	private sealed class PaletteButtons
	{
		internal FabricationServiceButton Straight { get; private set; }
		internal List<FabricationServiceButton> Elbows { get; } = new List<FabricationServiceButton>();
		internal List<FabricationServiceButton> Tees { get; } = new List<FabricationServiceButton>();
		internal List<FabricationServiceButton> ReducingTees { get; } = new List<FabricationServiceButton>();
		internal List<FabricationServiceButton> Flanges { get; } = new List<FabricationServiceButton>();
		internal List<FabricationServiceButton> Caps { get; } = new List<FabricationServiceButton>();
		internal List<FabricationServiceButton> Olets { get; } = new List<FabricationServiceButton>();
		internal List<FabricationServiceButton> Couplings { get; } = new List<FabricationServiceButton>();
		internal List<FabricationServiceButton> Adapters { get; } = new List<FabricationServiceButton>();
		internal List<FabricationServiceButton> Reducers { get; } = new List<FabricationServiceButton>();
		internal List<FabricationServiceButton> MiscFittings { get; } = new List<FabricationServiceButton>();

		internal static PaletteButtons Load(FabricationService service, int paletteIndex)
		{
			var result = new PaletteButtons();
			if (service == null || !service.IsValidPaletteIndex(paletteIndex))
			{
				return result;
			}

			int count = service.GetButtonCount(paletteIndex);
			for (int i = 0; i < count; i++)
			{
				if (!service.IsValidButtonIndex(paletteIndex, i))
				{
					continue;
				}

				FabricationServiceButton button = service.GetButton(paletteIndex, i);
				if (button == null || !button.IsValid() || button.IsExcluded() || button.IsAHanger)
				{
					continue;
				}

				string corpus = ((button.Name ?? string.Empty) + " " + (button.Code ?? string.Empty)).ToUpperInvariant();
				if (button.IsStraight)
				{
					if (result.Straight == null)
					{
						result.Straight = button;
					}

					continue;
				}

				if (LooksLikeOlet(corpus))
				{
					result.Olets.Add(button);
					continue;
				}

				if (LooksLikeCap(corpus))
				{
					result.Caps.Add(button);
					continue;
				}

				if (LooksLikeReducer(corpus))
				{
					result.Reducers.Add(button);
					continue;
				}

				if (LooksLikeCoupling(corpus))
				{
					result.Couplings.Add(button);
					continue;
				}

				if (LooksLikeAdapter(corpus))
				{
					result.Adapters.Add(button);
					continue;
				}

				if (LooksLikeReducingTee(corpus))
				{
					result.ReducingTees.Add(button);
					continue;
				}

				if (LooksLikeTee(corpus))
				{
					result.Tees.Add(button);
					continue;
				}

				if (LooksLikeFlange(corpus))
				{
					result.Flanges.Add(button);
					continue;
				}

				if (LooksLikeElbow(corpus))
				{
					result.Elbows.Add(button);
					continue;
				}

				result.MiscFittings.Add(button);
			}

			if (result.Elbows.Count == 0)
			{
				for (int i = 0; i < count; i++)
				{
					if (!service.IsValidButtonIndex(paletteIndex, i))
					{
						continue;
					}

					FabricationServiceButton button = service.GetButton(paletteIndex, i);
					if (button == null || !button.IsValid() || button.IsExcluded() || button.IsAHanger || button.IsStraight)
					{
						continue;
					}

					string corpus = ((button.Name ?? string.Empty) + " " + (button.Code ?? string.Empty)).ToUpperInvariant();
					if (LooksLikeOlet(corpus) || LooksLikeCap(corpus) || LooksLikeTee(corpus) || LooksLikeReducingTee(corpus) || LooksLikeFlange(corpus))
					{
						continue;
					}

					result.Elbows.Add(button);
					break;
				}
			}

			return result;
		}

		private static bool LooksLikeCap(string corpus)
		{
			if (string.IsNullOrWhiteSpace(corpus) || LooksLikeOlet(corpus))
			{
				return false;
			}

			// Avoid "capacity" / "capture" false positives — palette codes are CAP / Cap.
			return corpus == "CAP"
				|| corpus.StartsWith("CAP ", StringComparison.Ordinal)
				|| corpus.EndsWith(" CAP", StringComparison.Ordinal)
				|| corpus.Contains(" CAP ")
				|| corpus.Contains("END CAP")
				|| corpus.Contains("PIPE CAP")
				|| corpus.Contains("-CAP");
		}

		private static bool LooksLikeOlet(string corpus)
		{
			if (string.IsNullOrWhiteSpace(corpus))
			{
				return false;
			}

			return corpus.Contains("OLET")
				|| corpus.Contains("WELDO")
				|| corpus.Contains("WELD-O")
				|| corpus.Contains("THREAD-O")
				|| corpus.Contains("THREADED-O")
				|| corpus.Contains("SOCKO")
				|| corpus.Contains("SOCK-O")
				|| corpus.Contains("NIPOLET")
				|| corpus.Contains(" LATRO")
				|| string.Equals(corpus.Trim(), "TOL", StringComparison.Ordinal)
				|| corpus.Contains(" TOL")
				|| corpus.StartsWith("TOL ")
				|| corpus.Contains("WOL");
		}

		private static bool LooksLikeReducingTee(string corpus)
		{
			if (string.IsNullOrWhiteSpace(corpus) || LooksLikeOlet(corpus))
			{
				return false;
			}

			return (corpus.Contains("TEE") || corpus.Contains(" RED-TEE") || corpus.Contains("RED-TEE"))
				&& (corpus.Contains("RED") || corpus.Contains("REDUC"));
		}

		private static bool LooksLikeTee(string corpus)
		{
			if (string.IsNullOrWhiteSpace(corpus) || LooksLikeOlet(corpus) || LooksLikeReducingTee(corpus))
			{
				return false;
			}

			return corpus.Contains("TEE") || string.Equals(corpus.Trim(), "TEE", StringComparison.Ordinal);
		}

		private static bool LooksLikeFlange(string corpus)
		{
			if (string.IsNullOrWhiteSpace(corpus) || LooksLikeOlet(corpus))
			{
				return false;
			}

			return corpus.Contains("FLANGE")
				|| corpus.Contains("FLNG")
				|| corpus.Contains("FLG");
		}

		private static bool LooksLikeReducer(string corpus)
		{
			if (string.IsNullOrWhiteSpace(corpus) || LooksLikeOlet(corpus) || LooksLikeTee(corpus))
			{
				return false;
			}

			return corpus.Contains("REDUCER") || corpus.Contains("REDUC");
		}

		private static bool LooksLikeCoupling(string corpus)
		{
			if (string.IsNullOrWhiteSpace(corpus) || LooksLikeOlet(corpus))
			{
				return false;
			}

			return corpus.Contains("COUPLING") || corpus.Contains("UNION") || corpus.Contains("NIPPLE");
		}

		private static bool LooksLikeAdapter(string corpus)
		{
			if (string.IsNullOrWhiteSpace(corpus) || LooksLikeOlet(corpus) || LooksLikeFlange(corpus))
			{
				return false;
			}

			return corpus.Contains("ADAPTER") || corpus.Contains("ADAPTOR");
		}

		private static bool LooksLikeElbow(string corpus)
		{
			if (string.IsNullOrWhiteSpace(corpus) || corpus.Contains("45") || LooksLikeOlet(corpus)
				|| LooksLikeCap(corpus) || LooksLikeTee(corpus) || LooksLikeReducingTee(corpus) || LooksLikeFlange(corpus))
			{
				return false;
			}

			return corpus.Contains("ELBOW")
				|| corpus.Contains("EL90")
				|| corpus.Contains("90 DEG")
				|| corpus.Contains("90DEG")
				|| corpus.Contains(" BEND")
				|| corpus.Contains(" ELL");
		}
	}

	/// <summary>
	/// Auto-resolve fabrication connectivity failures so ConnectAndCouple can insert shop welds
	/// without blocking the import dialog on "Disconnect the family from the network?".
	/// </summary>
	private sealed class PcfImportFailuresPreprocessor : IFailuresPreprocessor
	{
		public FailureProcessingResult PreprocessFailures(FailuresAccessor failuresAccessor)
		{
			if (failuresAccessor == null)
			{
				return FailureProcessingResult.Continue;
			}

			IList<FailureMessageAccessor> failures = failuresAccessor.GetFailureMessages();
			if (failures == null || failures.Count == 0)
			{
				return FailureProcessingResult.Continue;
			}

			bool resolvedAny = false;
			foreach (FailureMessageAccessor failure in failures)
			{
				try
				{
					if (failure.GetSeverity() == FailureSeverity.Warning)
					{
						failuresAccessor.DeleteWarning(failure);
						continue;
					}

					string description = (failure.GetDescriptionText() ?? string.Empty).ToUpperInvariant();
					bool connectivity = description.IndexOf("CONNECT", StringComparison.Ordinal) >= 0
						|| description.IndexOf("NETWORK", StringComparison.Ordinal) >= 0;

					if (connectivity && failure.HasResolutionOfType(FailureResolutionType.DetachElements))
					{
						failure.SetCurrentResolutionType(FailureResolutionType.DetachElements);
						failuresAccessor.ResolveFailure(failure);
						resolvedAny = true;
						continue;
					}

					if (failure.HasResolutions())
					{
						failuresAccessor.ResolveFailure(failure);
						resolvedAny = true;
					}
				}
				catch
				{
				}
			}

			// ProceedWithCommit tells Revit we handled the errors — avoids the modal Disconnect dialog.
			return resolvedAny
				? FailureProcessingResult.ProceedWithCommit
				: FailureProcessingResult.Continue;
		}
	}
}
