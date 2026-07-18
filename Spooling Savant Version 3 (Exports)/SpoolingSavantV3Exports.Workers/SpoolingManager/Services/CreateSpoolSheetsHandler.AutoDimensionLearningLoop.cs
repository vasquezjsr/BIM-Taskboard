using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using SpoolingSavantV3Exports.Workers.SpoolingManager.Models;

namespace SpoolingSavantV3Exports.Workers.SpoolingManager.Services;

public partial class CreateSpoolSheetsHandler
{
	/// <summary>
	/// Hard laws (material-agnostic):
	/// - Elbow / Tee â†’ center C
	/// - Flange â†’ open face F
	/// - Olet â†’ short end-of-pipe E â†’ olet C
	/// - Branch â†’ main C (tee/pipe) â†’ branch C or E
	/// - NEVER ship a tilted Linear dim
	/// Learning loop: place â†’ regen â†’ straighten â†’ delete remaining tilt â†’ audit â†’ retry missing roles.
	/// </summary>
	private const int AutoDimLearningMaxPasses = 4;
	private const int AutoDimLearningMaxPassesRefresh = 4;
	private const int AutoDimLearningStuckPasses = 2;

	private sealed class AutoDimHardLawAudit
	{
		public int IntersectingSameSideCount;
		public int WrongSideOletCount;
		public int FittingFlangeDimCount;
		public int ExpectedVCount;
		public int ExpectedHCount;
		public int OffsetMeasuredCount;
		public int OffsetFailCount;
		public int TiltedCount;
		public int LinearCount;
		public int OletCount;
		public int OletDimCount;
		public int ElbowOrTeeCount;
		public int FlangeCount;
		public List<string> Failures = new List<string>();

				public bool Passed =>
			TiltedCount == 0
			&& Failures.Count == 0
			&& WrongSideOletCount == 0
			&& IntersectingSameSideCount == 0
			&& (OletCount == 0 || OletDimCount > 0);
	}

	/// <summary>
	/// Place auto-dims with up to <see cref="AutoDimLearningMaxPasses"/> verify/repair cycles
	/// until hard laws pass or passes are exhausted.
	/// </summary>
	private static int RunAutoDimLearningLoop(
		Document doc,
		View view,
		AssemblyInstance assembly,
		List<FabricationPart> parts,
		List<FabricationPart> allParts,
		XYZ unitAxis,
		SpoolingManagerSettings spoolSettings,
		List<string> failureNotes)
	{
		if (doc == null || view == null)
		{
			return 0;
		}

		List<FabricationPart> dimPool = (allParts != null && allParts.Count > 0) ? allParts : parts;
		DimensionType userType = TryResolveLinearDimensionType(doc, spoolSettings);
		SetAutoDimContentCentroid(view, dimPool);
		int lastPlaced = 0;
		AutoDimHardLawAudit lastAudit = null;

		try
		{
		for (int pass = 1; pass <= AutoDimLearningMaxPasses; pass++)
		{
			if (pass > 1)
			{
				RemoveViewLinearDimensions(doc, view);
				RemoveViewAutoDimensionDetailCurves(doc, view);
				try { DoRegenNow(doc); } catch { }
			}

			List<string> passNotes = new List<string>();
			lastPlaced = TryApplySpoolAutoDimensionRules(
				doc, view, assembly, parts, allParts, unitAxis, spoolSettings, passNotes);

			try
			{
				if (_batchSheetGeneration)
				{
					FlushPendingRegen(doc);
				}
				else
				{
					DoRegenNow(doc);
				}
			}
			catch
			{
			}

			TryStraightenAllTiltedViewLinearDimensions(doc, view, userType);
			int deletedTilt = DeleteAnyRemainingTiltedLinearDimensions(doc, view);
			int repairedFlange = RepairInboardFlangeSeatDimensions(doc, view, dimPool, spoolSettings);
			int repairedOlets = RepairMissingOletPickupDimensions(doc, view, dimPool, spoolSettings);
			int purgedDupes = PurgeDuplicateViewLinearDimensions(doc, view);
			UncrossViewLinearDimensions(doc, view);
			lastAudit = AuditAutoDimHardLaws(doc, view, dimPool);
			if (repairedFlange > 0 || repairedOlets > 0 || purgedDupes > 0)
			{
				lastPlaced += repairedFlange + repairedOlets;
				// Re-uncross after repairs can reintroduce interior stacks.
				UncrossViewLinearDimensions(doc, view);
				lastAudit = AuditAutoDimHardLaws(doc, view, dimPool);
			}

			string asmName = assembly != null ? AssemblyDisplayName.Get(assembly) : "?";
			TryAppendAutoDimDiagnosticLog(
				asmName,
				view.Name,
				"learning-pass-" + pass
					+ " placed=" + lastPlaced
					+ " deletedTilt=" + deletedTilt
					+ " tiltedLeft=" + lastAudit.TiltedCount
					+ " olets=" + lastAudit.OletCount + "/" + lastAudit.OletDimCount
					+ " linear=" + lastAudit.LinearCount
					+ (lastAudit.Failures.Count > 0 ? " fails=" + string.Join("; ", lastAudit.Failures) : " OK"),
				0,
				lastAudit.LinearCount);

			if (lastAudit.Passed)
			{
				failureNotes?.Clear();
				break;
			}

			if (pass < AutoDimLearningMaxPasses)
			{
				failureNotes?.Add(
					"Auto-dim learning pass " + pass + " failed hard-law check â€” retrying: "
					+ string.Join("; ", lastAudit.Failures.Count > 0 ? lastAudit.Failures : passNotes));
			}
			else if (passNotes.Count > 0)
			{
				failureNotes?.AddRange(passNotes);
			}
		}

		if (lastAudit != null && !lastAudit.Passed)
		{
			failureNotes?.Add(
				"Auto-dim learning loop exhausted (" + AutoDimLearningMaxPasses
				+ " passes). Remaining: tilted=" + lastAudit.TiltedCount
				+ (lastAudit.Failures.Count > 0 ? "; " + string.Join("; ", lastAudit.Failures) : string.Empty));
			// Fail closed â€” never report success while hard laws are still broken.
			return 0;
		}

		return lastPlaced;
		}
		finally
		{
			_autoDimContentCentroidWorld = null;
		}
	}

	/// <summary>Hard law: never leave a tilted Linear dim on a spool view.</summary>
	private static int DeleteAnyRemainingTiltedLinearDimensions(Document doc, View view)
	{
		if (doc == null || view == null || view is View3D || view.IsTemplate)
		{
			return 0;
		}
		List<ElementId> kill = new List<ElementId>();
		try
		{
			foreach (Dimension dim in new FilteredElementCollector(doc, ((Element)view).Id)
				.OfClass(typeof(Dimension))
				.Cast<Dimension>())
			{
				if (dim == null || !dim.IsValidObject)
				{
					continue;
				}
				if (dim.DimensionType == null || !IsLinearDimensionType(dim.DimensionType))
				{
					continue;
				}
				Curve curve = dim.Curve;
				if ((GeometryObject)(object)curve == (GeometryObject)null)
				{
					continue;
				}
				if (!TryIsCurveAxisAlignedInView(view, curve))
				{
					kill.Add(((Element)dim).Id);
				}
			}
		}
		catch
		{
			return 0;
		}
		int n = 0;
		foreach (ElementId id in kill)
		{
			try
			{
				doc.Delete(id);
				n++;
			}
			catch
			{
			}
		}
		if (n > 0)
		{
			try { DoRegenNow(doc); } catch { }
		}
		return n;
	}

	private static AutoDimHardLawAudit AuditAutoDimHardLaws(
		Document doc,
		View view,
		IList<FabricationPart> parts)
	{
		var audit = new AutoDimHardLawAudit();
		if (doc == null || view == null || parts == null)
		{
			audit.Failures.Add("missing doc/view/parts");
			return audit;
		}

		Document assemblyDoc = parts.Count > 0 ? ((Element)parts[0]).Document : doc;
		HashSet<long> oletIds = new HashSet<long>();
		foreach (FabricationPart p in parts)
		{
			if (p == null)
			{
				continue;
			}
			if (IsOletPart(p))
			{
				audit.OletCount++;
				oletIds.Add(((Element)p).Id.Value);
			}
			if (FabricationPartClassification.IsElbowPart(p, assemblyDoc)
				|| FabricationPartClassification.IsTeePart(p, assemblyDoc))
			{
				audit.ElbowOrTeeCount++;
			}
			if (FabricationPartClassification.IsFlangePart(p, assemblyDoc))
			{
				audit.FlangeCount++;
			}
		}

		HashSet<long> oletDimmed = new HashSet<long>();
		try
		{
			foreach (Dimension dim in new FilteredElementCollector(doc, ((Element)view).Id)
				.OfClass(typeof(Dimension))
				.Cast<Dimension>())
			{
				if (dim == null || !dim.IsValidObject)
				{
					continue;
				}
				if (dim.DimensionType == null || !IsLinearDimensionType(dim.DimensionType))
				{
					continue;
				}
				audit.LinearCount++;
				Curve curve = dim.Curve;
				if ((GeometryObject)(object)curve != (GeometryObject)null
					&& !TryIsCurveAxisAlignedInView(view, curve))
				{
					audit.TiltedCount++;
				}
				try
				{
					ReferenceArray refs = dim.References;
					if (refs == null)
					{
						continue;
					}
					for (int i = 0; i < refs.Size; i++)
					{
						long id = refs.get_Item(i).ElementId.Value;
						if (oletIds.Contains(id))
						{
							oletDimmed.Add(id);
						}
					}
				}
				catch
				{
				}
			}
		}
		catch
		{
			audit.Failures.Add("could not enumerate dims");
			return audit;
		}

		audit.OletDimCount = oletDimmed.Count;
		if (audit.TiltedCount > 0)
		{
			audit.Failures.Add(audit.TiltedCount + " tilted Linear dim(s) remain");
		}
		if (audit.OletCount > 0 && audit.OletDimCount == 0)
		{
			audit.Failures.Add(audit.OletCount + " olet(s) present but none dimensioned (need short-E â†’ olet C)");
		}
		if (audit.ElbowOrTeeCount > 0 && audit.FlangeCount > 0 && audit.LinearCount == 0)
		{
			audit.Failures.Add("elbow/tee + flange present but zero Linear dims placed");
		}
		AuditRejectInboardFlangeSeatDimensions(doc, view, parts, audit);
		return audit;
	}

	/// <summary>Hard law: every olet needs short-E â†’ olet C. Place any still missing.</summary>
	private static int RepairMissingOletPickupDimensions(
		Document doc,
		View view,
		IList<FabricationPart> parts,
		SpoolingManagerSettings spoolSettings)
	{
		if (doc == null || view == null || parts == null)
		{
			return 0;
		}
		HashSet<long> oletIds = new HashSet<long>();
		foreach (FabricationPart p in parts)
		{
			if (p != null && IsOletPart(p))
			{
				oletIds.Add(((Element)p).Id.Value);
			}
		}
		if (oletIds.Count == 0)
		{
			return 0;
		}
		HashSet<long> already = new HashSet<long>();
		try
		{
			foreach (Dimension dim in new FilteredElementCollector(doc, ((Element)view).Id)
				.OfClass(typeof(Dimension))
				.Cast<Dimension>())
			{
				if (dim?.References == null)
				{
					continue;
				}
				for (int i = 0; i < dim.References.Size; i++)
				{
					long id = dim.References.get_Item(i).ElementId.Value;
					if (oletIds.Contains(id))
					{
						already.Add(id);
					}
				}
			}
		}
		catch
		{
		}
		if (already.Count >= oletIds.Count)
		{
			return 0;
		}
		int stackIndex = CountViewLinearDimensions(doc, view);
		List<string> notes = new List<string>();
		int before = already.Count;
		int placed = TryPlaceOletRunStackDimensions(doc, view, parts as List<FabricationPart> ?? parts.ToList(), spoolSettings, ref stackIndex, notes);
		if (placed > 0)
		{
			TryAppendAutoDimDiagnosticLog("learning-repair", view.Name, "repaired missing olet pickups=" + placed, before, before + placed);
		}
		return placed;
	}

	/// <summary>
	/// Delete inboard-seat flange dims and re-place stubs with open-F geometry.
	/// Learning loop calls this after each place pass so retries are not identical.
	/// </summary>
	private static int RepairInboardFlangeSeatDimensions(
		Document doc,
		View view,
		IList<FabricationPart> parts,
		SpoolingManagerSettings spoolSettings)
	{
		if (doc == null || view == null || parts == null)
		{
			return 0;
		}
		const double tolFeet = 1.0 / 16.0 / 12.0;
		Document assemblyDoc = parts.Count > 0 ? ((Element)parts[0]).Document : doc;
		Dictionary<long, FabricationPart> byId = parts
			.Where(p => p != null)
			.GroupBy(p => ((Element)p).Id.Value)
			.ToDictionary(g => g.Key, g => g.First());
		List<(Dimension dim, FabricationPart flange, FabricationPart fitting)> bad = new List<(Dimension, FabricationPart, FabricationPart)>();
		try
		{
			foreach (Dimension dim in new FilteredElementCollector(doc, ((Element)view).Id)
				.OfClass(typeof(Dimension))
				.Cast<Dimension>())
			{
				if (dim == null || !dim.IsValidObject || dim.DimensionType == null || !IsLinearDimensionType(dim.DimensionType))
				{
					continue;
				}
				ReferenceArray refs = dim.References;
				if (refs == null || refs.Size < 2)
				{
					continue;
				}
				FabricationPart flange = null;
				FabricationPart fitting = null;
				for (int i = 0; i < refs.Size; i++)
				{
					long id = refs.get_Item(i).ElementId.Value;
					if (!byId.TryGetValue(id, out FabricationPart part) || part == null)
					{
						continue;
					}
					if (FabricationPartClassification.IsFlangePart(part, assemblyDoc))
					{
						flange = part;
					}
					else if (FabricationPartClassification.IsElbowPart(part, assemblyDoc)
						|| FabricationPartClassification.IsTeePart(part, assemblyDoc))
					{
						fitting = part;
					}
				}
				if (flange == null || fitting == null)
				{
					continue;
				}
				XYZ openF = ResolveFlangeFaceAnchorForDimension(flange, fitting, parts);
				XYZ center = ResolveUniversalCenterlineIntersectionAnchor(fitting, null, null)
					?? GetFabricationFittingDimensionAnchor(fitting, null, null, parts);
				if (openF == null || center == null)
				{
					continue;
				}
				XYZ inboard = null;
				foreach (Connector connector in ListConnectors(flange))
				{
					if (connector?.Origin == null)
					{
						continue;
					}
					if (inboard == null || connector.Origin.DistanceTo(center) < inboard.DistanceTo(center))
					{
						inboard = connector.Origin;
					}
				}
				if (inboard == null || openF.DistanceTo(inboard) < tolFeet)
				{
					continue;
				}
				double? value = null;
				try { value = dim.Value; } catch { }
				if (!value.HasValue)
				{
					continue;
				}
				double openSpan = openF.DistanceTo(center);
				double inboardSpan = inboard.DistanceTo(center);
				if (Math.Abs(openSpan - inboardSpan) < tolFeet)
				{
					continue;
				}
				bool nearInboard = Math.Abs(value.Value - inboardSpan) <= Math.Max(tolFeet, Math.Abs(openSpan - inboardSpan) * 0.25);
				bool nearOpen = Math.Abs(value.Value - openSpan) <= Math.Max(tolFeet, Math.Abs(openSpan - inboardSpan) * 0.25);
				if (nearInboard && !nearOpen)
				{
					bad.Add((dim, flange, fitting));
				}
			}
		}
		catch
		{
			return 0;
		}
		if (bad.Count == 0)
		{
			return 0;
		}
		int repaired = 0;
		int stackIndex = CountViewLinearDimensions(doc, view);
		foreach ((Dimension dim, FabricationPart flange, FabricationPart fitting) item in bad)
		{
			try { doc.Delete(((Element)item.dim).Id); } catch { }
			XYZ fittingPt = ResolveUniversalCenterlineIntersectionAnchor(item.fitting, null, null)
				?? GetFabricationFittingDimensionAnchor(item.fitting, null, null, parts);
			XYZ flangePt = ResolveFlangeFaceAnchorForDimension(item.flange, item.fitting, parts);
			if (fittingPt == null || flangePt == null)
			{
				continue;
			}
			if (TryPlaceFittingFlangeStubDimension(doc, view, item.fitting, fittingPt, item.flange, flangePt, spoolSettings, ref stackIndex, out _))
			{
				repaired++;
			}
		}
		if (repaired > 0)
		{
			try { DoRegenNow(doc); } catch { }
			TryAppendAutoDimDiagnosticLog("learning-repair", view.Name, "repaired inboard flange dims=" + repaired, 0, repaired);
		}
		return repaired;
	}

	/// <summary>
	/// Fail when a flange Linear dim lands on the inboard weld seat (~37.375") instead of open F (~40.125").
	/// </summary>
	private static void AuditRejectInboardFlangeSeatDimensions(
		Document doc,
		View view,
		IList<FabricationPart> parts,
		AutoDimHardLawAudit audit)
	{
		if (doc == null || view == null || parts == null || audit == null)
		{
			return;
		}
		const double tolFeet = 1.0 / 16.0 / 12.0; // 1/16"
		Document assemblyDoc = parts.Count > 0 ? ((Element)parts[0]).Document : doc;
		Dictionary<long, FabricationPart> byId = parts
			.Where(p => p != null)
			.GroupBy(p => ((Element)p).Id.Value)
			.ToDictionary(g => g.Key, g => g.First());

		try
		{
			foreach (Dimension dim in new FilteredElementCollector(doc, ((Element)view).Id)
				.OfClass(typeof(Dimension))
				.Cast<Dimension>())
			{
				if (dim == null || !dim.IsValidObject || dim.DimensionType == null || !IsLinearDimensionType(dim.DimensionType))
				{
					continue;
				}
				ReferenceArray refs = dim.References;
				if (refs == null || refs.Size < 2)
				{
					continue;
				}
				FabricationPart flange = null;
				FabricationPart fitting = null;
				for (int i = 0; i < refs.Size; i++)
				{
					long id = refs.get_Item(i).ElementId.Value;
					if (!byId.TryGetValue(id, out FabricationPart part) || part == null)
					{
						continue;
					}
					if (FabricationPartClassification.IsFlangePart(part, assemblyDoc))
					{
						flange = part;
					}
					else if (FabricationPartClassification.IsElbowPart(part, assemblyDoc)
						|| FabricationPartClassification.IsTeePart(part, assemblyDoc))
					{
						fitting = part;
					}
				}
				if (flange == null || fitting == null)
				{
					continue;
				}
				XYZ openF = ResolveFlangeFaceAnchorForDimension(flange, fitting, parts);
				XYZ center = ResolveUniversalCenterlineIntersectionAnchor(fitting, null, null)
					?? GetFabricationFittingDimensionAnchor(fitting, null, null, parts);
				if (openF == null || center == null)
				{
					continue;
				}
				XYZ inboard = null;
				foreach (Connector connector in ListConnectors(flange))
				{
					if (connector?.Origin == null)
					{
						continue;
					}
					if (inboard == null || connector.Origin.DistanceTo(center) < inboard.DistanceTo(center))
					{
						inboard = connector.Origin;
					}
				}
				if (inboard == null || openF.DistanceTo(inboard) < tolFeet)
				{
					continue;
				}
				double measured;
				try
				{
					double? value = dim.Value;
					if (!value.HasValue)
					{
						continue;
					}
					measured = value.Value;
				}
				catch
				{
					continue;
				}
				double openSpan = openF.DistanceTo(center);
				double inboardSpan = inboard.DistanceTo(center);
				if (Math.Abs(openSpan - inboardSpan) < tolFeet)
				{
					continue;
				}
				bool nearInboard = Math.Abs(measured - inboardSpan) <= Math.Max(tolFeet, Math.Abs(openSpan - inboardSpan) * 0.25);
				bool nearOpen = Math.Abs(measured - openSpan) <= Math.Max(tolFeet, Math.Abs(openSpan - inboardSpan) * 0.25);
				if (nearInboard && !nearOpen)
				{
					audit.Failures.Add(
						"flange dim uses inboard seat ("
						+ (measured * 12.0).ToString("0.###")
						+ "\") not open F ("
						+ (openSpan * 12.0).ToString("0.###")
						+ "\")");
				}
			}
		}
		catch
		{
		}
	


	}
	private static void RunAutoDimPostCropHardLawStabilize(
		Document doc,
		View view,
		AssemblyInstance assembly,
		SpoolingManagerSettings spoolSettings)
	{
		if (doc == null || view == null || view is View3D || view.IsTemplate)
		{
			return;
		}
		List<FabricationPart> parts = null;
		try
		{
			if (assembly != null)
			{
				parts = GetAssemblyFabricationParts(doc, assembly);
				parts = FilterLocalFabricationPartsForView(doc, view, parts);
				parts = ExpandFilteredPartsWithFlangeConnections(GetAssemblyFabricationParts(doc, assembly), parts, doc);
				parts = EnsureOletPartsForAutoDim(GetAssemblyFabricationParts(doc, assembly), parts);
			}
		}
		catch
		{
			parts = null;
		}

		DimensionType userType = TryResolveLinearDimensionType(doc, spoolSettings);
		int placedIgnore = CountViewLinearDimensions(doc, view);
		try { DoRegenNow(doc); } catch { }

		TryStraightenAllTiltedViewLinearDimensions(doc, view, userType);
		int stillTilted = DeleteAnyRemainingTiltedLinearDimensions(doc, view);
		int killedIllegal = DeleteIllegalSpoolLinearDimensions(doc, view);
		if (parts != null && parts.Count > 0)
		{
			PurgeOletPickupDimsWhenOletLandsOnWeld(doc, view, parts);
		}
		SnapUnstackedLinearDimsToThreeEighthsSheet(doc, view);
		TryStraightenAllTiltedViewLinearDimensions(doc, view, userType);
		stillTilted += DeleteAnyRemainingTiltedLinearDimensions(doc, view);
		killedIllegal += DeleteIllegalSpoolLinearDimensions(doc, view);

		AutoDimHardLawAudit audit = parts != null && parts.Count > 0
			? AuditAutoDimView(doc, view, parts)
			: null;
		TryAppendAutoDimLearningLog(
			assembly != null ? AssemblyDisplayName.Get(assembly) : "post-crop",
			view.Name,
			"post-crop stabilize"
				+ " stillTilted=" + stillTilted
				+ " killIllegal=" + killedIllegal
				+ " tiltedLeft=" + (audit?.TiltedCount ?? -1)
				+ " CF=" + (audit?.FittingFlangeDimCount ?? 0)
				+ " HV=" + (audit?.ExpectedHCount ?? 0) + "/" + (audit?.ExpectedVCount ?? 0));

		RunAutoDimAbsoluteShipGate(doc, view, parts, spoolSettings, userType, ref placedIgnore);
	}

	private static void RunAutoDimHardLawCloseout(
		Document doc,
		View view,
		IList<FabricationPart> dimPool,
		SpoolingManagerSettings spoolSettings,
		DimensionType userType,
		ref int lastPlaced)
	{
		TryStraightenAllTiltedViewLinearDimensions(doc, view, userType);
		DeleteAnyRemainingTiltedLinearDimensions(doc, view);
		DeleteIllegalSpoolLinearDimensions(doc, view);
		if (dimPool != null && dimPool.Count > 0)
		{
			PurgeOletPickupDimsWhenOletLandsOnWeld(doc, view, dimPool);
		}
		SnapUnstackedLinearDimsToThreeEighthsSheet(doc, view);
		TryStraightenAllTiltedViewLinearDimensions(doc, view, userType);
		DeleteAnyRemainingTiltedLinearDimensions(doc, view);
		DeleteIllegalSpoolLinearDimensions(doc, view);
	}

	private static void RunAutoDimAbsoluteShipGate(
		Document doc,
		View view,
		IList<FabricationPart> dimPool,
		SpoolingManagerSettings spoolSettings,
		DimensionType userType,
		ref int lastPlaced)
	{
		if (doc == null || view == null)
		{
			return;
		}
		TryStraightenAllTiltedViewLinearDimensions(doc, view, userType);
		DeleteAnyRemainingTiltedLinearDimensions(doc, view);
		DeleteIllegalSpoolLinearDimensions(doc, view);
		if (dimPool != null && dimPool.Count > 0)
		{
			PurgeOletPickupDimsWhenOletLandsOnWeld(doc, view, dimPool);
		}
		SnapUnstackedLinearDimsToThreeEighthsSheet(doc, view);
		TryStraightenAllTiltedViewLinearDimensions(doc, view, userType);
		DeleteAnyRemainingTiltedLinearDimensions(doc, view);
		DeleteIllegalSpoolLinearDimensions(doc, view);
		TryAppendAutoDimLearningLog(
			"ship-gate",
			view != null ? view.Name : "?",
			"absolute ship gate: tilt/illegal reject + olet-on-weld");
	}

}
