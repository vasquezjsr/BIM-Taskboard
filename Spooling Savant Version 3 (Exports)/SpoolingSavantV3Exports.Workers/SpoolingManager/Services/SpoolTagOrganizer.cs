using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Fabrication;

namespace SpoolingSavantV3Exports.Workers.SpoolingManager.Services;

/// <summary>
/// Places the tag BODY (item-number pill) in a tight clear ring around its host snap.
/// Prefers short leaders; rejects leaders that cross assembly silhouette or other leaders.
/// </summary>
public static class SpoolTagOrganizer
{
	private const double Tolerance = 1e-8;

	/// <summary>
	/// When true, draws visible model curves in the spool view:
	/// host snap cross, outer clearance circle, and final tag-body rectangle.
	/// </summary>
	public static bool DrawDebugHostRings = false;

	/// <summary>Typical fabrication item-number pill size on paper (inches).</summary>
	private const double DefaultBodyWidthInches = 0.60;

	private const double DefaultBodyHeightInches = 0.24;

	/// <summary>
	/// Prefer short leaders (caller min/max), but expand this far before giving up.
	/// Hard rule: never leave a tag body on the assembly silhouette.
	/// </summary>
	private const double AbsoluteMaxLeaderInches = 1.50;

	/// <summary>Face-mesh samples only (centerline samples are kept in full).</summary>
	private const int MaxFaceHitSamples = 280;

	/// <summary>Paper inches — pill must stay this far off the solid samples/edges.</summary>
	private const double SilhouetteClearanceInches = 0.06;

	private static readonly string LogPath = Path.Combine(
		Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
		"Autodesk",
		"Revit",
		"Addins",
		"2024",
		"Spooling-Savant-V3-Exports",
		"SpoolingManager",
		"SpoolTagOrganizer.log");

	public static IReadOnlyList<ElementId> Organize(
		Document doc,
		View spoolView,
		ICollection<ElementId> assemblyElementIds,
		ICollection<ElementId> createdTagIds,
		double minimumDistanceInches = 0.50,
		double maximumDistanceInches = 0.75,
		double tagClearanceInches = 0.0625,
		double radialStepInches = 0.035,
		double angleStepDegrees = 3.0,
		double minLeaderSeparationInches = 0.04)
	{
		if (doc == null)
		{
			throw new ArgumentNullException(nameof(doc));
		}

		if (spoolView == null)
		{
			throw new ArgumentNullException(nameof(spoolView));
		}

		if (assemblyElementIds == null)
		{
			throw new ArgumentNullException(nameof(assemblyElementIds));
		}

		if (createdTagIds == null)
		{
			throw new ArgumentNullException(nameof(createdTagIds));
		}

		if (minimumDistanceInches < 0.0
			|| maximumDistanceInches <= minimumDistanceInches)
		{
			throw new ArgumentException(
				"The maximum distance must be greater than the minimum distance.");
		}

		string viewName = ((Element)spoolView).Name ?? string.Empty;
		Log("BEGIN view=" + viewName
			+ " tags=" + createdTagIds.Count
			+ " members=" + assemblyElementIds.Count
			+ " scale=" + spoolView.Scale);

		if (createdTagIds.Count == 0 || assemblyElementIds.Count == 0)
		{
			Log("SKIP empty input");
			return Array.Empty<ElementId>();
		}

		doc.Regenerate();

		ViewPlane plane = new ViewPlane(spoolView);

		double tagClearance = PaperToModelDistance(tagClearanceInches, spoolView.Scale);
		double minLeaderSeparation = PaperToModelDistance(minLeaderSeparationInches, spoolView.Scale);

		double defaultBodyW = PaperToModelDistance(DefaultBodyWidthInches, spoolView.Scale);
		double defaultBodyH = PaperToModelDistance(DefaultBodyHeightInches, spoolView.Scale);

		AssemblyGeometry2D assemblyGeometry = AssemblyGeometry2D.Create(
			doc,
			spoolView,
			plane,
			assemblyElementIds);

		Log("geometry segments=" + assemblyGeometry.SegmentCount
			+ " samples=" + assemblyGeometry.SampleCount
			+ " partBoxes=" + assemblyGeometry.PartBoxCount);

		HashSet<long> memberIds = new HashSet<long>(
			assemblyElementIds.Select(id => id.Value));

		List<TagItem> tags = createdTagIds
			.Select(doc.GetElement)
			.OfType<IndependentTag>()
			.Where(tag => tag.OwnerViewId == spoolView.Id)
			.Where(tag => !tag.IsOrphaned)
			.Select(tag => CreateTagItem(
				doc, tag, spoolView, plane, memberIds, defaultBodyW, defaultBodyH))
			.Where(item => item != null)
			.ToList();

		Log("layoutItems=" + tags.Count);

		if (tags.Count == 0)
		{
			return Array.Empty<ElementId>();
		}

		// Each tag independently: push off its own host until the pill clears the solid,
		// then only nudge if it lands on another tag. No shared columns / perimeter groups.
		double minLeader = PaperToModelDistance(0.45, spoolView.Scale);
		double maxLeader = PaperToModelDistance(
			Math.Max(maximumDistanceInches, AbsoluteMaxLeaderInches), spoolView.Scale);
		double radialStep = PaperToModelDistance(0.04, spoolView.Scale);
		double silhouetteClear = PaperToModelDistance(SilhouetteClearanceInches, spoolView.Scale);
		double bodyGap = Math.Max(tagClearance, PaperToModelDistance(0.05, spoolView.Scale));

		tags = tags
			.OrderBy(t => assemblyGeometry.BodyClearsSilhouette(
				t.GetBodyAt(t.OriginalHeadPosition).Expand(tagClearance / 2.0),
				silhouetteClear)
				? 1
				: 0)
			.ThenBy(t => Distance(t.HostAnchor, t.OriginalHeadPosition))
			.ToList();

		List<ElementId> unresolvedTagIds = new List<ElementId>();
		List<Rectangle2D> occupiedBodies = new List<Rectangle2D>();
		List<LeaderRay> placedLeaders = new List<LeaderRay>();
		List<DebugRing> debugRings = new List<DebugRing>();
		int moved = 0;
		int unchanged = 0;

		foreach (TagItem tag in tags)
		{
			Rectangle2D originalBody =
				tag.GetBodyAt(tag.OriginalHeadPosition).Expand(tagClearance / 2.0);
			bool alreadyClear = assemblyGeometry.BodyClearsSilhouette(originalBody, silhouetteClear)
				&& !BodyHitsOccupied(originalBody, occupiedBodies)
				&& !LeaderHitsOccupiedBodies(tag.HostAnchor, tag.OriginalHeadPosition, occupiedBodies, Tolerance)
				&& !LeadersConflictAny(
					tag.HostAnchor, tag.OriginalHeadPosition, placedLeaders, minLeaderSeparation);

			Candidate seat = null;
			if (alreadyClear)
			{
				seat = new Candidate
				{
					HeadPosition = tag.OriginalHeadPosition,
					LeaderLength = Distance(tag.HostAnchor, tag.OriginalHeadPosition),
					Score = 0.0
				};
				unchanged++;
				Log("LEAVE id=" + tag.Tag.Id.Value + " host=" + tag.HostElementId);
			}
			else
			{
				seat = FindClearSeatNearHost(
					tag,
					assemblyGeometry,
					occupiedBodies,
					placedLeaders,
					minLeader,
					maxLeader,
					radialStep,
					tagClearance,
					silhouetteClear,
					minLeaderSeparation,
					spoolView.Scale);
			}

			if (seat == null)
			{
				Log("UNRESOLVED id=" + tag.Tag.Id.Value + " host=" + tag.HostElementId);
				unresolvedTagIds.Add(tag.Tag.Id);
				occupiedBodies.Add(originalBody);
				placedLeaders.Add(new LeaderRay(tag.HostAnchor, tag.OriginalHeadPosition));
				debugRings.Add(new DebugRing(
					tag, tag.OriginalHeadPosition, maxLeader, keptOriginal: false, unresolved: true));
				continue;
			}

			if (!alreadyClear)
			{
				tag.Tag.TagHeadPosition = plane.ToModel(seat.HeadPosition, tag.Tag.TagHeadPosition);
				moved++;
				Log("MOVED id=" + tag.Tag.Id.Value
					+ " host=" + tag.HostElementId
					+ " leaderIn=" + (seat.LeaderLength * 12.0 / Math.Max(1, spoolView.Scale)).ToString("0.##"));
			}

			occupiedBodies.Add(tag.GetBodyAt(seat.HeadPosition).Expand(bodyGap));
			placedLeaders.Add(new LeaderRay(tag.HostAnchor, seat.HeadPosition));
			debugRings.Add(new DebugRing(tag, seat.HeadPosition, maxLeader, keptOriginal: alreadyClear));
		}

		int repaired = NudgeOverlappingTagsNearHosts(
			tags,
			plane,
			assemblyGeometry,
			minLeader,
			maxLeader,
			radialStep,
			tagClearance,
			silhouetteClear,
			minLeaderSeparation,
			spoolView.Scale);
		moved += repaired;

		if (moved > 0)
		{
			doc.Regenerate();
		}

		if (DrawDebugHostRings)
		{
			int drawn = DrawDebugHostRingGraphics(
				doc,
				spoolView,
				plane,
				debugRings);
			Log("DEBUG_RINGS drawn=" + drawn + " (host cross + min/max circles + body rect)");
		}

		Log("END moved=" + moved
			+ " unchanged=" + unchanged
			+ " unresolved=" + unresolvedTagIds.Count
			+ " mode=per-host");
		return unresolvedTagIds;
	}

	/// <summary>
	/// Push this one tag off its host along short radial rays until the pill clears the solid
	/// and other placed pills. Independent — no shared alignment grid.
	/// </summary>
	private static Candidate FindClearSeatNearHost(
		TagItem tag,
		AssemblyGeometry2D assemblyGeometry,
		IReadOnlyCollection<Rectangle2D> occupiedBodies,
		IReadOnlyCollection<LeaderRay> placedLeaders,
		double minLeader,
		double maxLeader,
		double radialStep,
		double tagClearance,
		double silhouetteClear,
		double minLeaderSeparation,
		int viewScale)
	{
		UV host = tag.HostAnchor;
		double prefU = tag.OriginalHeadPosition.U - host.U;
		double prefV = tag.OriginalHeadPosition.V - host.V;
		double prefLen = Math.Sqrt(prefU * prefU + prefV * prefV);
		double baseAngle = prefLen > Tolerance
			? Math.Atan2(prefV, prefU)
			: 0.0;

		// Prefer original direction, then fan left/right — not a global side of the assembly.
		double[] angleOffsetsDeg =
		{
			0, 15, -15, 30, -30, 45, -45, 60, -60, 90, -90, 120, -120, 150, -150, 180
		};

		Candidate best = null;
		foreach (double offsetDeg in angleOffsetsDeg)
		{
			double angle = baseAngle + DegreesToRadians(offsetDeg);
			double cos = Math.Cos(angle);
			double sin = Math.Sin(angle);

			for (double radius = minLeader; radius <= maxLeader + Tolerance; radius += radialStep)
			{
				UV head = new UV(host.U + cos * radius, host.V + sin * radius);
				Rectangle2D body = tag.GetBodyAt(head).Expand(tagClearance / 2.0);

				if (!assemblyGeometry.BodyClearsSilhouette(body, silhouetteClear))
				{
					continue;
				}

				if (BodyHitsOccupied(body, occupiedBodies))
				{
					continue;
				}

				if (LeaderHitsOccupiedBodies(host, head, occupiedBodies, Tolerance))
				{
					continue;
				}

				if (LeadersConflictAny(host, head, placedLeaders, minLeaderSeparation))
				{
					continue;
				}

				double score = radius + Math.Abs(offsetDeg) * PaperToModelDistance(0.002, viewScale);
				if (best == null || score < best.Score)
				{
					best = new Candidate
					{
						HeadPosition = head,
						LeaderLength = radius,
						Score = score
					};
				}

				// First clear seat on this ray — keep leaders short.
				break;
			}

			if (best != null && Math.Abs(offsetDeg) <= 30.0)
			{
				return best;
			}
		}

		return best;
	}

	private static bool LeadersConflictAny(
		UV host,
		UV head,
		IReadOnlyCollection<LeaderRay> placedLeaders,
		double minLeaderSeparation)
	{
		if (placedLeaders == null)
		{
			return false;
		}

		foreach (LeaderRay other in placedLeaders)
		{
			if (LeadersConflict(host, head, other.Start, other.End, minLeaderSeparation))
			{
				return true;
			}
		}

		return false;
	}

	private static int NudgeOverlappingTagsNearHosts(
		List<TagItem> tags,
		ViewPlane plane,
		AssemblyGeometry2D assemblyGeometry,
		double minLeader,
		double maxLeader,
		double radialStep,
		double tagClearance,
		double silhouetteClear,
		double minLeaderSeparation,
		int viewScale)
	{
		if (tags == null || tags.Count < 2)
		{
			return 0;
		}

		int repaired = 0;
		for (int pass = 0; pass < 3; pass++)
		{
			bool any = false;
			for (int i = 0; i < tags.Count; i++)
			{
				TagItem tag = tags[i];
				UV head = plane.ToView(tag.Tag.TagHeadPosition);
				List<Rectangle2D> others = new List<Rectangle2D>();
				List<LeaderRay> othersLeaders = new List<LeaderRay>();
				for (int j = 0; j < tags.Count; j++)
				{
					if (j == i)
					{
						continue;
					}

					UV otherHead = plane.ToView(tags[j].Tag.TagHeadPosition);
					others.Add(tags[j].GetBodyAt(otherHead).Expand(tagClearance / 2.0));
					othersLeaders.Add(new LeaderRay(tags[j].HostAnchor, otherHead));
				}

				Rectangle2D body = tag.GetBodyAt(head).Expand(tagClearance / 2.0);
				bool bad = !assemblyGeometry.BodyClearsSilhouette(body, silhouetteClear)
					|| BodyHitsOccupied(body, others)
					|| LeaderHitsOccupiedBodies(tag.HostAnchor, head, others, Tolerance)
					|| LeadersConflictAny(tag.HostAnchor, head, othersLeaders, minLeaderSeparation);
				if (!bad)
				{
					continue;
				}

				Candidate fix = FindClearSeatNearHost(
					tag,
					assemblyGeometry,
					others,
					othersLeaders,
					minLeader,
					maxLeader,
					radialStep,
					tagClearance,
					silhouetteClear,
					minLeaderSeparation,
					viewScale);
				if (fix == null)
				{
					continue;
				}

				tag.Tag.TagHeadPosition = plane.ToModel(fix.HeadPosition, tag.Tag.TagHeadPosition);
				repaired++;
				any = true;
				Log("REPAIR-NUDGE id=" + tag.Tag.Id.Value + " host=" + tag.HostElementId);
			}

			if (!any)
			{
				break;
			}
		}

		return repaired;
	}

	/// <summary>
	/// Place the tag pill on the expanded assembly rectangle so the body clears the solid
	/// and other already-placed pills. Leaders may cross the assembly (unavoidable).
	/// </summary>
	private static Candidate FindPerimeterSeat(
		TagItem tag,
		Rectangle2D parkRing,
		UV hullCenter,
		AssemblyGeometry2D assemblyGeometry,
		IReadOnlyCollection<Rectangle2D> occupiedBodies,
		IReadOnlyCollection<LeaderRay> placedLeaders,
		double tagClearance,
		double minLeaderSeparation,
		int viewScale)
	{
		double slideStep = PaperToModelDistance(0.12, viewScale);
		double maxSlide = Math.Max(parkRing.Width, parkRing.Height) + slideStep;

		// Prefer the ray from hull center through the host (keeps leader short-ish).
		List<UV> seeds = new List<UV>();
		seeds.Add(ProjectHostToPerimeter(parkRing, hullCenter, tag.HostAnchor));

		// Also try the four side midpoints nearest the host, then evenly spaced perimeter.
		seeds.Add(new UV(parkRing.MinU, Clamp(tag.HostAnchor.V, parkRing.MinV, parkRing.MaxV)));
		seeds.Add(new UV(parkRing.MaxU, Clamp(tag.HostAnchor.V, parkRing.MinV, parkRing.MaxV)));
		seeds.Add(new UV(Clamp(tag.HostAnchor.U, parkRing.MinU, parkRing.MaxU), parkRing.MinV));
		seeds.Add(new UV(Clamp(tag.HostAnchor.U, parkRing.MinU, parkRing.MaxU), parkRing.MaxV));

		const int evenCount = 12;
		for (int i = 0; i < evenCount; i++)
		{
			double t = i / (double)evenCount;
			seeds.Add(PointOnRectanglePerimeter(parkRing, t));
		}

		Candidate best = null;
		foreach (UV seed in seeds)
		{
			for (double slide = 0.0; slide <= maxSlide + Tolerance; slide += slideStep)
			{
				UV[] candidates = slide <= Tolerance
					? new[] { seed }
					: new[]
					{
						SlideOnRectanglePerimeter(parkRing, seed, slide),
						SlideOnRectanglePerimeter(parkRing, seed, -slide)
					};

				foreach (UV head in candidates)
				{
					Rectangle2D body = tag.GetBodyAt(head).Expand(tagClearance / 2.0);
					if (assemblyGeometry.Intersects(body))
					{
						continue;
					}

					if (BodyHitsOccupied(body, occupiedBodies))
					{
						continue;
					}

					if (LeaderHitsOccupiedBodies(tag.HostAnchor, head, occupiedBodies, pad: Tolerance))
					{
						continue;
					}

					bool leaderConflict = false;
					foreach (LeaderRay other in placedLeaders)
					{
						if (LeadersConflict(
							tag.HostAnchor, head, other.Start, other.End, minLeaderSeparation * 0.5))
						{
							leaderConflict = true;
							break;
						}
					}

					if (leaderConflict)
					{
						continue;
					}

					double leaderLength = Distance(tag.HostAnchor, head);
					double score = leaderLength;
					if (best == null || score < best.Score)
					{
						best = new Candidate
						{
							HeadPosition = head,
							Score = score,
							LeaderLength = leaderLength
						};
					}

					// First valid seat at this seed is enough.
					if (best != null && slide <= Tolerance)
					{
						return best;
					}
				}

				if (best != null && slide > slideStep * 2.0)
				{
					return best;
				}
			}
		}

		return best;
	}

	private static int SeparateOverlappingPerimeterTags(
		List<TagItem> tags,
		ViewPlane plane,
		Rectangle2D parkRing,
		UV hullCenter,
		AssemblyGeometry2D assemblyGeometry,
		double tagClearance,
		double minLeaderSeparation,
		int viewScale)
	{
		if (tags == null || tags.Count < 2)
		{
			return 0;
		}

		int repaired = 0;
		for (int pass = 0; pass < 3; pass++)
		{
			bool any = false;
			for (int i = 0; i < tags.Count; i++)
			{
				TagItem tag = tags[i];
				UV head = plane.ToView(tag.Tag.TagHeadPosition);
				List<Rectangle2D> others = new List<Rectangle2D>();
				List<LeaderRay> othersLeaders = new List<LeaderRay>();
				for (int j = 0; j < tags.Count; j++)
				{
					if (j == i)
					{
						continue;
					}

					UV otherHead = plane.ToView(tags[j].Tag.TagHeadPosition);
					others.Add(tags[j].GetBodyAt(otherHead).Expand(tagClearance / 2.0));
					othersLeaders.Add(new LeaderRay(tags[j].HostAnchor, otherHead));
				}

				Rectangle2D body = tag.GetBodyAt(head).Expand(tagClearance / 2.0);
				bool bad = assemblyGeometry.Intersects(body) || BodyHitsOccupied(body, others);
				if (!bad)
				{
					continue;
				}

				Candidate fix = FindPerimeterSeat(
					tag,
					parkRing,
					hullCenter,
					assemblyGeometry,
					others,
					othersLeaders,
					tagClearance,
					minLeaderSeparation,
					viewScale);
				if (fix == null)
				{
					continue;
				}

				tag.Tag.TagHeadPosition = plane.ToModel(fix.HeadPosition, tag.Tag.TagHeadPosition);
				repaired++;
				any = true;
				Log("REPAIR-SEPARATE id=" + tag.Tag.Id.Value + " host=" + tag.HostElementId);
			}

			if (!any)
			{
				break;
			}
		}

		return repaired;
	}

	private static bool BodyHitsOccupied(
		Rectangle2D body,
		IReadOnlyCollection<Rectangle2D> occupiedBodies)
	{
		if (occupiedBodies == null)
		{
			return false;
		}

		foreach (Rectangle2D occupied in occupiedBodies)
		{
			if (occupied != null && body.Intersects(occupied))
			{
				return true;
			}
		}

		return false;
	}

	private static UV ProjectHostToPerimeter(Rectangle2D park, UV center, UV host)
	{
		double du = host.U - center.U;
		double dv = host.V - center.V;
		if (Math.Abs(du) < Tolerance && Math.Abs(dv) < Tolerance)
		{
			du = 1.0;
			dv = 0.0;
		}

		double len = Math.Sqrt(du * du + dv * dv);
		du /= len;
		dv /= len;

		// Ray from center through host → far enough to hit park boundary.
		double reach = Math.Max(park.Width, park.Height) * 2.0 + 1.0;
		return ClosestPointOnRectangleBoundary(
			park,
			new UV(center.U + du * reach, center.V + dv * reach));
	}

	private static UV ClosestPointOnRectangleBoundary(Rectangle2D rect, UV point)
	{
		double u = Clamp(point.U, rect.MinU, rect.MaxU);
		double v = Clamp(point.V, rect.MinV, rect.MaxV);

		double dLeft = Math.Abs(point.U - rect.MinU);
		double dRight = Math.Abs(point.U - rect.MaxU);
		double dBottom = Math.Abs(point.V - rect.MinV);
		double dTop = Math.Abs(point.V - rect.MaxV);
		double best = Math.Min(Math.Min(dLeft, dRight), Math.Min(dBottom, dTop));

		if (Math.Abs(best - dLeft) <= Tolerance)
		{
			return new UV(rect.MinU, v);
		}

		if (Math.Abs(best - dRight) <= Tolerance)
		{
			return new UV(rect.MaxU, v);
		}

		if (Math.Abs(best - dBottom) <= Tolerance)
		{
			return new UV(u, rect.MinV);
		}

		return new UV(u, rect.MaxV);
	}

	private static UV PointOnRectanglePerimeter(Rectangle2D rect, double t01)
	{
		double peri = 2.0 * (rect.Width + rect.Height);
		double d = ((t01 % 1.0) + 1.0) % 1.0 * peri;
		if (d <= rect.Width)
		{
			return new UV(rect.MinU + d, rect.MinV);
		}

		d -= rect.Width;
		if (d <= rect.Height)
		{
			return new UV(rect.MaxU, rect.MinV + d);
		}

		d -= rect.Height;
		if (d <= rect.Width)
		{
			return new UV(rect.MaxU - d, rect.MaxV);
		}

		d -= rect.Width;
		return new UV(rect.MinU, rect.MaxV - d);
	}

	private static UV SlideOnRectanglePerimeter(Rectangle2D rect, UV point, double distance)
	{
		double peri = 2.0 * (rect.Width + rect.Height);
		if (peri <= Tolerance)
		{
			return point;
		}

		double t = PerimeterParameter(rect, point) + distance / peri;
		return PointOnRectanglePerimeter(rect, t);
	}

	private static double PerimeterParameter(Rectangle2D rect, UV point)
	{
		double peri = 2.0 * (rect.Width + rect.Height);
		UV p = ClosestPointOnRectangleBoundary(rect, point);
		double d;
		if (Math.Abs(p.V - rect.MinV) <= Tolerance)
		{
			d = p.U - rect.MinU;
		}
		else if (Math.Abs(p.U - rect.MaxU) <= Tolerance)
		{
			d = rect.Width + (p.V - rect.MinV);
		}
		else if (Math.Abs(p.V - rect.MaxV) <= Tolerance)
		{
			d = rect.Width + rect.Height + (rect.MaxU - p.U);
		}
		else
		{
			d = rect.Width + rect.Height + rect.Width + (rect.MaxV - p.V);
		}

		return peri <= Tolerance ? 0.0 : d / peri;
	}

	private static double Clamp(double value, double min, double max)
	{
		if (value < min)
		{
			return min;
		}

		if (value > max)
		{
			return max;
		}

		return value;
	}

	/// <summary>
	/// Visible references: X at host snap, ONE outer placement circle, rectangle = tag body.
	/// Tag body must sit inside that outer circle.
	/// </summary>
	private static int DrawDebugHostRingGraphics(
		Document doc,
		View view,
		ViewPlane plane,
		IList<DebugRing> rings)
	{
		if (doc == null || view == null || rings == null || rings.Count == 0)
		{
			return 0;
		}

		int drawn = 0;
		XYZ right = view.RightDirection.Normalize();
		XYZ up = view.UpDirection.Normalize();

		foreach (DebugRing ring in rings)
		{
			try
			{
				XYZ hostModel = plane.ToModel(ring.Tag.HostAnchor, ring.Tag.Tag.TagHeadPosition);
				XYZ headModel = plane.ToModel(ring.FinalHead, ring.Tag.Tag.TagHeadPosition);

				Plane sketchPlane = Plane.CreateByOriginAndBasis(hostModel, right, up);
				SketchPlane sp = SketchPlane.Create(doc, sketchPlane);

				double cross = Math.Max(ring.MaxRadius * 0.12, 1.0 / 96.0);
				drawn += DrawModelLine(doc, sp, hostModel - right * cross, hostModel + right * cross);
				drawn += DrawModelLine(doc, sp, hostModel - up * cross, hostModel + up * cross);

				// Outer placement circle only (no inner circle).
				drawn += DrawModelCircle(doc, sp, sketchPlane, ring.MaxRadius);

				drawn += DrawModelLine(doc, sp, hostModel, headModel);

				Rectangle2D body = ring.Tag.GetBodyAt(ring.FinalHead);
				UV[] corners =
				{
					new UV(body.MinU, body.MinV),
					new UV(body.MaxU, body.MinV),
					new UV(body.MaxU, body.MaxV),
					new UV(body.MinU, body.MaxV)
				};
				for (int i = 0; i < 4; i++)
				{
					XYZ a = plane.ToModel(corners[i], ring.Tag.Tag.TagHeadPosition);
					XYZ b = plane.ToModel(corners[(i + 1) % 4], ring.Tag.Tag.TagHeadPosition);
					drawn += DrawModelLine(doc, sp, a, b);
				}

				Log("DEBUG host=" + ring.Tag.HostElementId
					+ " tag=" + ring.Tag.Tag.Id.Value
					+ " maxR_ft=" + ring.MaxRadius.ToString("0.####")
					+ " headInside=" + (Distance(ring.Tag.HostAnchor, ring.FinalHead) <= ring.MaxRadius + Tolerance)
					+ " unresolved=" + ring.Unresolved);
			}
			catch (Exception ex)
			{
				Log("DEBUG_DRAW_FAIL host=" + ring.Tag.HostElementId + " " + ex.Message);
			}
		}

		return drawn;
	}

	private static int DrawModelCircle(
		Document doc,
		SketchPlane sp,
		Plane plane,
		double radius)
	{
		if (radius <= Tolerance)
		{
			return 0;
		}

		int count = 0;
		try
		{
			Arc a1 = Arc.Create(plane, radius, 0.0, Math.PI);
			Arc a2 = Arc.Create(plane, radius, Math.PI, 2.0 * Math.PI);
			doc.Create.NewModelCurve(a1, sp);
			doc.Create.NewModelCurve(a2, sp);
			count += 2;
		}
		catch
		{
		}

		return count;
	}

	private static int DrawModelLine(Document doc, SketchPlane sp, XYZ a, XYZ b)
	{
		if (a == null || b == null || a.DistanceTo(b) <= Tolerance)
		{
			return 0;
		}

		try
		{
			doc.Create.NewModelCurve(Line.CreateBound(a, b), sp);
			return 1;
		}
		catch
		{
			return 0;
		}
	}

	private sealed class DebugRing
	{
		public DebugRing(
			TagItem tag,
			UV finalHead,
			double maxRadius,
			bool keptOriginal,
			bool unresolved = false)
		{
			Tag = tag;
			FinalHead = finalHead;
			MaxRadius = maxRadius;
			KeptOriginal = keptOriginal;
			Unresolved = unresolved;
		}

		public TagItem Tag { get; }

		public UV FinalHead { get; }

		public double MaxRadius { get; }

		public bool KeptOriginal { get; }

		public bool Unresolved { get; }
	}

	private static Candidate FindBestCandidateAroundHost(
		TagItem tag,
		AssemblyGeometry2D assemblyGeometry,
		IReadOnlyCollection<Rectangle2D> occupiedBodies,
		IReadOnlyCollection<LeaderRay> placedLeaders,
		double minimumDistance,
		double maximumDistance,
		double tagClearance,
		double radialStep,
		double angleStepDegrees,
		double minLeaderSeparation)
	{
		Candidate best = null;
		UV searchCenter = tag.HostAnchor;

		// Search by HEAD distance from host origin (1/2"–3/4" paper).
		double endRadius = Math.Max(radialStep, maximumDistance);
		double startRadius = Math.Min(endRadius, Math.Max(radialStep, minimumDistance));

		int angleCount = Math.Max(24, (int)Math.Ceiling(360.0 / angleStepDegrees));

		double preferredAngle = Math.Atan2(
			tag.OriginalHeadPosition.V - searchCenter.V,
			tag.OriginalHeadPosition.U - searchCenter.U);

		for (double radius = startRadius;
			radius <= endRadius + Tolerance;
			radius += radialStep)
		{
			for (int angleIndex = 0; angleIndex < angleCount; angleIndex++)
			{
				int alternatingIndex = angleIndex == 0
					? 0
					: (angleIndex % 2 == 1
						? (angleIndex + 1) / 2
						: -(angleIndex / 2));

				double angle = preferredAngle
					+ DegreesToRadians(alternatingIndex * angleStepDegrees);

				UV candidateHead = new UV(
					searchCenter.U + Math.Cos(angle) * radius,
					searchCenter.V + Math.Sin(angle) * radius);

				Rectangle2D body = tag.GetBodyAt(candidateHead)
					.Expand(tagClearance / 2.0);

				if (!IsValid(
					body,
					candidateHead,
					tag,
					assemblyGeometry,
					occupiedBodies,
					placedLeaders,
					minimumDistance,
					maximumDistance,
					minLeaderSeparation))
				{
					continue;
				}

				double leaderLength = Distance(searchCenter, candidateHead);
				// Prefer short leaders; slight bonus for more clearance from the solid silhouette.
				double clearance = assemblyGeometry.DistanceToFast(body);
				double score = leaderLength * 4.0 - Math.Min(clearance, maximumDistance) * 0.75;

				if (best == null || score < best.Score)
				{
					best = new Candidate
					{
						HeadPosition = candidateHead,
						Score = score,
						LeaderLength = leaderLength
					};
				}
			}

			// Take the first radius that has any valid seat — keeps leaders short.
			if (best != null)
			{
				break;
			}
		}

		return best;
	}

	/// <summary>
	/// Prefer the caller's max ring, then step outward (paper inches) until a valid seat
	/// or AbsoluteMaxLeaderInches. Crowded 3D Ortho elbows routinely need ~1–1.5".
	/// </summary>
	private static Candidate FindBestCandidateWithExpandingRing(
		TagItem tag,
		AssemblyGeometry2D assemblyGeometry,
		IReadOnlyCollection<Rectangle2D> occupiedBodies,
		IReadOnlyCollection<LeaderRay> placedLeaders,
		double preferredMinimum,
		double preferredMaximum,
		double tagClearance,
		double radialStep,
		double angleStepDegrees,
		double minLeaderSeparation,
		int viewScale)
	{
		double absMax = PaperToModelDistance(AbsoluteMaxLeaderInches, viewScale);
		// Few coarse rings only — dense expand hung Scale and Annotate at 48%.
		double[] expandInches = { 1.00, 1.25, AbsoluteMaxLeaderInches };
		double expandRadial = Math.Max(radialStep, PaperToModelDistance(0.10, viewScale));
		double expandAngle = Math.Max(10.0, angleStepDegrees);

		foreach (double maxInches in expandInches)
		{
			double maxDist = PaperToModelDistance(maxInches, viewScale);
			if (maxDist <= preferredMaximum + Tolerance)
			{
				continue;
			}

			maxDist = Math.Min(maxDist, absMax);
			Candidate candidate = FindBestCandidateAroundHost(
				tag,
				assemblyGeometry,
				occupiedBodies,
				placedLeaders,
				preferredMinimum * 0.85,
				maxDist,
				tagClearance * 0.5,
				expandRadial,
				expandAngle,
				minLeaderSeparation * 0.6);
			if (candidate != null)
			{
				return candidate;
			}
		}

		// Last resort: park outside the assembly hull along outward rays (may exceed preferred ring).
		return FindEscapeSeatOutsideHull(
			tag,
			assemblyGeometry,
			occupiedBodies,
			placedLeaders,
			preferredMinimum,
			absMax,
			tagClearance,
			minLeaderSeparation,
			viewScale);
	}

	/// <summary>
	/// Shoot rays from the host; place the tag head just beyond the silhouette + body half-size.
	/// Used only when the tight ring cannot clear overlaps / assembly.
	/// </summary>
	private static Candidate FindEscapeSeatOutsideHull(
		TagItem tag,
		AssemblyGeometry2D assemblyGeometry,
		IReadOnlyCollection<Rectangle2D> occupiedBodies,
		IReadOnlyCollection<LeaderRay> placedLeaders,
		double minimumDistance,
		double absoluteMaxDistance,
		double tagClearance,
		double minLeaderSeparation,
		int viewScale)
	{
		UV searchCenter = tag.HostAnchor;
		double bodyHalfDiag = 0.5 * Math.Sqrt(
			tag.BodyWidth * tag.BodyWidth + tag.BodyHeight * tag.BodyHeight);
		double pad = Math.Max(tagClearance, PaperToModelDistance(0.06, viewScale));
		double step = PaperToModelDistance(0.04, viewScale);

		double preferredAngle = Math.Atan2(
			tag.OriginalHeadPosition.V - searchCenter.V,
			tag.OriginalHeadPosition.U - searchCenter.U);

		Candidate best = null;
		const int angleCount = 16;
		step = Math.Max(step, PaperToModelDistance(0.10, viewScale));
		for (int angleIndex = 0; angleIndex < angleCount; angleIndex++)
		{
			int alternatingIndex = angleIndex == 0
				? 0
				: (angleIndex % 2 == 1
					? (angleIndex + 1) / 2
					: -(angleIndex / 2));
			double angle = preferredAngle + DegreesToRadians(alternatingIndex * (360.0 / angleCount));
			double cos = Math.Cos(angle);
			double sin = Math.Sin(angle);

			// Walk outward until body clears silhouette, then validate vs other tags/leaders.
			for (double radius = Math.Max(minimumDistance, bodyHalfDiag + pad);
				radius <= absoluteMaxDistance + Tolerance;
				radius += step)
			{
				UV candidateHead = new UV(
					searchCenter.U + cos * radius,
					searchCenter.V + sin * radius);
				Rectangle2D body = tag.GetBodyAt(candidateHead).Expand(tagClearance / 2.0);

				if (assemblyGeometry.Intersects(body)
					|| assemblyGeometry.DistanceToFast(body) < pad + Tolerance)
				{
					continue;
				}

				if (TagNeedsRelocation(
					body,
					candidateHead,
					tag,
					assemblyGeometry,
					occupiedBodies,
					placedLeaders,
					minLeaderSeparation * 0.5))
				{
					continue;
				}

				bool hitsOther = false;
				foreach (Rectangle2D occupied in occupiedBodies)
				{
					if (body.Intersects(occupied))
					{
						hitsOther = true;
						break;
					}
				}

				if (hitsOther)
				{
					continue;
				}

				double leaderLength = Distance(searchCenter, candidateHead);
				double clearance = assemblyGeometry.DistanceToFast(body);
				double score = leaderLength * 3.0 - Math.Min(clearance, absoluteMaxDistance) * 1.25;
				if (best == null || score < best.Score)
				{
					best = new Candidate
					{
						HeadPosition = candidateHead,
						Score = score,
						LeaderLength = leaderLength
					};
				}

				// First clear seat on this ray is enough — keep leaders short.
				break;
			}

			if (best != null)
			{
				break;
			}
		}

		return best;
	}

	/// <summary>
	/// True when the item-number body sits on the assembly or the leader crosses it/other leaders
	/// (or skims another leader / cuts through another tag body).
	/// </summary>
	private static bool TagNeedsRelocation(
		Rectangle2D body,
		UV head,
		TagItem tag,
		AssemblyGeometry2D assemblyGeometry,
		IReadOnlyCollection<Rectangle2D> occupiedBodies,
		IReadOnlyCollection<LeaderRay> placedLeaders,
		double minLeaderSeparation)
	{
		if (assemblyGeometry.Intersects(body))
		{
			return true;
		}

		if (assemblyGeometry.DistanceToFast(body) < Tolerance)
		{
			return true;
		}

		if (assemblyGeometry.LeaderCrossesGeometry(
			tag.HostAnchor,
			head,
			ignoreRadiusNearHost: 1.0 / 96.0))
		{
			return true;
		}

		if (LeaderHitsOccupiedBodies(tag.HostAnchor, head, occupiedBodies, pad: Tolerance))
		{
			return true;
		}

		foreach (LeaderRay other in placedLeaders)
		{
			if (LeadersConflict(tag.HostAnchor, head, other.Start, other.End, minLeaderSeparation))
			{
				return true;
			}
		}

		return false;
	}

	private static bool IsValid(
		Rectangle2D body,
		UV head,
		TagItem tag,
		AssemblyGeometry2D assemblyGeometry,
		IReadOnlyCollection<Rectangle2D> occupiedBodies,
		IReadOnlyCollection<LeaderRay> placedLeaders,
		double minimumDistance,
		double maximumDistance,
		double minLeaderSeparation)
	{
		if (TagNeedsRelocation(
			body, head, tag, assemblyGeometry, occupiedBodies, placedLeaders, minLeaderSeparation))
		{
			return false;
		}

		foreach (Rectangle2D occupied in occupiedBodies)
		{
			if (body.Intersects(occupied))
			{
				return false;
			}
		}

		// Keep the pill off the grey body (not just "barely outside" the shrink box).
		double minClear = Math.Max(minimumDistance * 0.20, tag.BodyHeight * 0.35);
		if (assemblyGeometry.DistanceToFast(body) < minClear - Tolerance)
		{
			return false;
		}

		// Ring is host-origin → tag HEAD (1/2"–3/4"), not body corners.
		double leaderLength = Distance(tag.HostAnchor, head);
		if (leaderLength < minimumDistance - Tolerance
			|| leaderLength > maximumDistance + Tolerance)
		{
			return false;
		}

		return true;
	}

	/// <summary>
	/// Second pass: any remaining proper/near leader crossings get one more relocate attempt
	/// (order-dependent first pass can leave rare crosses).
	/// </summary>
	private static int RepairRemainingLeaderCrossings(
		List<TagItem> tags,
		ViewPlane plane,
		AssemblyGeometry2D assemblyGeometry,
		double minimumDistance,
		double maximumDistance,
		double tagClearance,
		double radialStep,
		double angleStepDegrees,
		double minLeaderSeparation,
		int viewScale)
	{
		if (tags == null || tags.Count < 2)
		{
			return 0;
		}

		int repaired = 0;
		for (int pass = 0; pass < 2; pass++)
		{
			bool any = false;
			for (int i = 0; i < tags.Count; i++)
			{
				TagItem tag = tags[i];
				UV head = plane.ToView(tag.Tag.TagHeadPosition);
				List<Rectangle2D> othersBodies = new List<Rectangle2D>();
				List<LeaderRay> othersLeaders = new List<LeaderRay>();
				for (int j = 0; j < tags.Count; j++)
				{
					if (j == i)
					{
						continue;
					}

					TagItem other = tags[j];
					UV otherHead = plane.ToView(other.Tag.TagHeadPosition);
					othersBodies.Add(other.GetBodyAt(otherHead).Expand(tagClearance / 2.0));
					othersLeaders.Add(new LeaderRay(other.HostAnchor, otherHead));
				}

				Rectangle2D body = tag.GetBodyAt(head).Expand(tagClearance / 2.0);
				bool bodyOnAssembly = assemblyGeometry.Intersects(body)
					|| assemblyGeometry.DistanceToFast(body) < Tolerance;
				if (!bodyOnAssembly
					&& !TagNeedsRelocation(
						body, head, tag, assemblyGeometry, othersBodies, othersLeaders, minLeaderSeparation))
				{
					continue;
				}

				Candidate fix = FindBestCandidateAroundHost(
					tag,
					assemblyGeometry,
					othersBodies,
					othersLeaders,
					minimumDistance,
					maximumDistance,
					tagClearance,
					radialStep,
					angleStepDegrees,
					minLeaderSeparation);
				if (fix == null)
				{
					fix = FindBestCandidateWithExpandingRing(
						tag,
						assemblyGeometry,
						othersBodies,
						othersLeaders,
						minimumDistance,
						maximumDistance,
						tagClearance,
						radialStep,
						angleStepDegrees,
						minLeaderSeparation,
						viewScale);
				}

				if (fix == null)
				{
					continue;
				}

				tag.Tag.TagHeadPosition = plane.ToModel(fix.HeadPosition, tag.Tag.TagHeadPosition);
				repaired++;
				any = true;
				Log("REPAIR-CROSS id=" + tag.Tag.Id.Value + " host=" + tag.HostElementId);
			}

			if (!any)
			{
				break;
			}
		}

		return repaired;
	}

	private static bool LeaderHitsOccupiedBodies(
		UV host,
		UV head,
		IReadOnlyCollection<Rectangle2D> occupiedBodies,
		double pad)
	{
		if (occupiedBodies == null || occupiedBodies.Count == 0)
		{
			return false;
		}

		foreach (Rectangle2D occupied in occupiedBodies)
		{
			if (occupied == null)
			{
				continue;
			}

			Rectangle2D box = pad > Tolerance ? occupied.Expand(pad) : occupied;
			if (SegmentIntersectsRectangle(host, head, box))
			{
				return true;
			}
		}

		return false;
	}

	/// <summary>
	/// Leaders conflict if they properly cross, nearly cross, or come closer than minSeparation.
	/// </summary>
	private static bool LeadersConflict(UV a1, UV a2, UV b1, UV b2, double minSeparation)
	{
		if (SegmentsCrossProperly(a1, a2, b1, b2))
		{
			return true;
		}

		// Inclusive intersection (T-junctions / endpoint-on-segment).
		if (SegmentsIntersectInclusive(a1, a2, b1, b2))
		{
			return true;
		}

		double sep = Math.Max(Tolerance, minSeparation);
		return SegmentSegmentDistance(a1, a2, b1, b2) < sep;
	}

	private static bool SegmentsCrossProperly(UV a1, UV a2, UV b1, UV b2)
	{
		double d1 = Cross(a1, a2, b1);
		double d2 = Cross(a1, a2, b2);
		double d3 = Cross(b1, b2, a1);
		double d4 = Cross(b1, b2, a2);

		return ((d1 > Tolerance && d2 < -Tolerance) || (d1 < -Tolerance && d2 > Tolerance))
			&& ((d3 > Tolerance && d4 < -Tolerance) || (d3 < -Tolerance && d4 > Tolerance));
	}

	private static bool SegmentsIntersectInclusive(UV a1, UV a2, UV b1, UV b2)
	{
		double d1 = Cross(a1, a2, b1);
		double d2 = Cross(a1, a2, b2);
		double d3 = Cross(b1, b2, a1);
		double d4 = Cross(b1, b2, a2);

		bool abStraddle = (d1 >= -Tolerance && d2 <= Tolerance) || (d1 <= Tolerance && d2 >= -Tolerance);
		bool baStraddle = (d3 >= -Tolerance && d4 <= Tolerance) || (d3 <= Tolerance && d4 >= -Tolerance);
		if (abStraddle && baStraddle && (Math.Abs(d1) + Math.Abs(d2) + Math.Abs(d3) + Math.Abs(d4) > Tolerance))
		{
			// Reject pure shared-endpoint "touches" at host/head corners that aren't real crossings.
			if (PointsNear(a1, b1) || PointsNear(a1, b2) || PointsNear(a2, b1) || PointsNear(a2, b2))
			{
				return SegmentSegmentDistance(a1, a2, b1, b2) < Tolerance * 10.0
					&& !OnlyShareEndpoint(a1, a2, b1, b2);
			}

			return true;
		}

		return false;
	}

	private static bool OnlyShareEndpoint(UV a1, UV a2, UV b1, UV b2)
	{
		int shares = 0;
		if (PointsNear(a1, b1) || PointsNear(a1, b2)) shares++;
		if (PointsNear(a2, b1) || PointsNear(a2, b2)) shares++;
		return shares == 1 && SegmentSegmentDistance(a1, a2, b1, b2) < Tolerance * 20.0;
	}

	private static bool PointsNear(UV a, UV b)
	{
		return Distance(a, b) <= Tolerance * 20.0;
	}

	private static double SegmentSegmentDistance(UV a1, UV a2, UV b1, UV b2)
	{
		if (SegmentsCrossProperly(a1, a2, b1, b2) || SegmentsIntersectInclusive(a1, a2, b1, b2))
		{
			return 0.0;
		}

		return Math.Min(
			Math.Min(PointSegmentDistance(a1, b1, b2), PointSegmentDistance(a2, b1, b2)),
			Math.Min(PointSegmentDistance(b1, a1, a2), PointSegmentDistance(b2, a1, a2)));
	}

	private static double PointSegmentDistance(UV p, UV a, UV b)
	{
		double abu = b.U - a.U;
		double abv = b.V - a.V;
		double len2 = abu * abu + abv * abv;
		if (len2 < Tolerance)
		{
			return Distance(p, a);
		}

		double t = ((p.U - a.U) * abu + (p.V - a.V) * abv) / len2;
		t = Math.Max(0.0, Math.Min(1.0, t));
		return Distance(p, new UV(a.U + t * abu, a.V + t * abv));
	}

	private static bool SegmentIntersectsRectangle(UV a, UV b, Rectangle2D rect)
	{
		if (rect == null)
		{
			return false;
		}

		if (rect.Contains(a) || rect.Contains(b))
		{
			return true;
		}

		UV c1 = new UV(rect.MinU, rect.MinV);
		UV c2 = new UV(rect.MaxU, rect.MinV);
		UV c3 = new UV(rect.MaxU, rect.MaxV);
		UV c4 = new UV(rect.MinU, rect.MaxV);
		return SegmentsIntersectInclusive(a, b, c1, c2)
			|| SegmentsIntersectInclusive(a, b, c2, c3)
			|| SegmentsIntersectInclusive(a, b, c3, c4)
			|| SegmentsIntersectInclusive(a, b, c4, c1);
	}

	private static double Cross(UV start, UV end, UV point)
	{
		return (end.U - start.U) * (point.V - start.V)
			- (end.V - start.V) * (point.U - start.U);
	}

	private static TagItem CreateTagItem(
		Document doc,
		IndependentTag tag,
		View view,
		ViewPlane plane,
		HashSet<long> assemblyMemberIds,
		double defaultBodyW,
		double defaultBodyH)
	{
		Element host = GetTaggedElement(doc, tag);
		if (host == null || !assemblyMemberIds.Contains(host.Id.Value))
		{
			return null;
		}

		UV head = plane.ToView(tag.TagHeadPosition);

		// Body size only (pill around TagHeadPosition). Never use get_BoundingBox for
		// collision — that includes the leader and always hits the host (caused REVERT).
		double bodyW = defaultBodyW;
		double bodyH = defaultBodyH;
		try
		{
			string text = tag.TagText;
			if (!string.IsNullOrWhiteSpace(text) && text.Length > 6)
			{
				bodyW = Math.Max(bodyW, defaultBodyW * (0.7 + text.Length * 0.08));
			}
		}
		catch
		{
		}

		Rectangle2D hostBounds = TryGetProjectedPartBounds(host, view, plane);
		UV hostAnchor = hostBounds != null
			? hostBounds.Center
			: plane.ToView(TryGetElementModelCenter(host, view) ?? tag.TagHeadPosition);

		UV leaderAnchor = TryGetLeaderAnchor(tag, plane);
		if (leaderAnchor != null)
		{
			hostAnchor = leaderAnchor;
		}

		return new TagItem
		{
			Tag = tag,
			HostElementId = host.Id.Value,
			OriginalHeadPosition = head,
			HostBounds = hostBounds,
			HostAnchor = hostAnchor,
			BodyWidth = bodyW,
			BodyHeight = bodyH
		};
	}

	private static Element GetTaggedElement(Document doc, IndependentTag tag)
	{
		IList<Reference> references;
		try
		{
			references = tag.GetTaggedReferences();
		}
		catch
		{
			return null;
		}

		if (references == null)
		{
			return null;
		}

		foreach (Reference reference in references)
		{
			if (reference == null)
			{
				continue;
			}

			Element element = doc.GetElement(reference.ElementId);
			if (element != null)
			{
				return element;
			}
		}

		return null;
	}

	private static UV TryGetLeaderAnchor(IndependentTag tag, ViewPlane plane)
	{
		if (tag == null || !tag.HasLeader)
		{
			return null;
		}

		IList<Reference> references;
		try
		{
			references = tag.GetTaggedReferences();
		}
		catch
		{
			return null;
		}

		if (references == null)
		{
			return null;
		}

		foreach (Reference reference in references)
		{
			try
			{
				XYZ leaderEnd = tag.GetLeaderEnd(reference);
				if (leaderEnd != null)
				{
					return plane.ToView(leaderEnd);
				}
			}
			catch
			{
			}
		}

		return null;
	}

	private static XYZ TryGetElementModelCenter(Element element, View view)
	{
		BoundingBoxXYZ box = null;
		try
		{
			box = element.get_BoundingBox(view) ?? element.get_BoundingBox(null);
		}
		catch
		{
			return null;
		}

		return box == null ? null : (box.Min + box.Max) * 0.5;
	}

	private static Rectangle2D TryGetProjectedPartBounds(
		Element element,
		View view,
		ViewPlane plane)
	{
		BoundingBoxXYZ box = null;
		try
		{
			box = element.get_BoundingBox(view) ?? element.get_BoundingBox(null);
		}
		catch
		{
			return null;
		}

		if (box == null)
		{
			return null;
		}

		List<UV> corners = GetBoxCorners(box).Select(plane.ToView).ToList();
		return new Rectangle2D(
			corners.Min(p => p.U),
			corners.Max(p => p.U),
			corners.Min(p => p.V),
			corners.Max(p => p.V));
	}

	private static IEnumerable<XYZ> GetBoxCorners(BoundingBoxXYZ box)
	{
		XYZ min = box.Min;
		XYZ max = box.Max;

		yield return new XYZ(min.X, min.Y, min.Z);
		yield return new XYZ(max.X, min.Y, min.Z);
		yield return new XYZ(min.X, max.Y, min.Z);
		yield return new XYZ(max.X, max.Y, min.Z);

		yield return new XYZ(min.X, min.Y, max.Z);
		yield return new XYZ(max.X, min.Y, max.Z);
		yield return new XYZ(min.X, max.Y, max.Z);
		yield return new XYZ(max.X, max.Y, max.Z);
	}

	private static double PaperToModelDistance(double paperInches, int viewScale)
	{
		return paperInches / 12.0 * viewScale;
	}

	private static double DegreesToRadians(double degrees)
	{
		return degrees * Math.PI / 180.0;
	}

	private static double Distance(UV first, UV second)
	{
		double u = first.U - second.U;
		double v = first.V - second.V;
		return Math.Sqrt(u * u + v * v);
	}

	private static void Log(string message)
	{
		try
		{
			string directory = Path.GetDirectoryName(LogPath);
			if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
			{
				Directory.CreateDirectory(directory);
			}

			File.AppendAllText(
				LogPath,
				DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "\t" + message + Environment.NewLine,
				Encoding.UTF8);
		}
		catch
		{
		}
	}

	private sealed class TagItem
	{
		public IndependentTag Tag { get; set; }

		public long HostElementId { get; set; }

		public UV OriginalHeadPosition { get; set; }

		public Rectangle2D HostBounds { get; set; }

		public UV HostAnchor { get; set; }

		public double BodyWidth { get; set; }

		public double BodyHeight { get; set; }

		/// <summary>Item-number pill centered on the tag head (not leader-inclusive).</summary>
		public Rectangle2D GetBodyAt(UV headPosition)
		{
			return new Rectangle2D(
				headPosition.U - BodyWidth / 2.0,
				headPosition.U + BodyWidth / 2.0,
				headPosition.V - BodyHeight / 2.0,
				headPosition.V + BodyHeight / 2.0);
		}
	}

	private sealed class Candidate
	{
		public UV HeadPosition { get; set; }

		public double Score { get; set; }

		public double LeaderLength { get; set; }
	}

	private sealed class LeaderRay
	{
		public LeaderRay(UV start, UV end)
		{
			Start = start;
			End = end;
		}

		public UV Start { get; }

		public UV End { get; }
	}

	private sealed class AssemblyGeometry2D
	{
		private readonly List<Segment2D> _segments;
		private readonly List<UV> _samplePoints;
		private readonly List<Rectangle2D> _partBoxes;

		private AssemblyGeometry2D(
			List<Segment2D> segments,
			List<UV> samplePoints,
			List<Rectangle2D> partBoxes)
		{
			_segments = segments;
			_samplePoints = samplePoints;
			_partBoxes = partBoxes;
		}

		public int SegmentCount => _segments.Count;

		public int SampleCount => _samplePoints.Count;

		public int PartBoxCount => _partBoxes.Count;

		/// <summary>Axis-aligned silhouette envelope of the assembly in view UV.</summary>
		public Rectangle2D GetUnionBounds()
		{
			double minU = double.PositiveInfinity;
			double maxU = double.NegativeInfinity;
			double minV = double.PositiveInfinity;
			double maxV = double.NegativeInfinity;

			void include(UV p)
			{
				if (p == null)
				{
					return;
				}

				minU = Math.Min(minU, p.U);
				maxU = Math.Max(maxU, p.U);
				minV = Math.Min(minV, p.V);
				maxV = Math.Max(maxV, p.V);
			}

			foreach (Rectangle2D box in _partBoxes)
			{
				include(new UV(box.MinU, box.MinV));
				include(new UV(box.MaxU, box.MaxV));
			}

			foreach (Segment2D segment in _segments)
			{
				include(segment.Start);
				include(segment.End);
			}

			foreach (UV sample in _samplePoints)
			{
				include(sample);
			}

			if (double.IsPositiveInfinity(minU))
			{
				return new Rectangle2D(-0.5, 0.5, -0.5, 0.5);
			}

			return new Rectangle2D(minU, maxU, minV, maxV);
		}

		public static AssemblyGeometry2D Create(
			Document doc,
			View view,
			ViewPlane plane,
			IEnumerable<ElementId> assemblyElementIds)
		{
			List<Segment2D> segments = new List<Segment2D>();
			List<UV> solidSamples = new List<UV>();
			List<UV> faceSamples = new List<UV>();
			List<Rectangle2D> partBoxes = new List<Rectangle2D>();

			Options options = new Options
			{
				DetailLevel = ViewDetailLevel.Fine,
				IncludeNonVisibleObjects = false,
				ComputeReferences = false
			};

			double pad = PaperToModelDistance(0.03, view.Scale);

			foreach (ElementId id in assemblyElementIds)
			{
				Element element = doc.GetElement(id);
				if (element == null)
				{
					continue;
				}

				GeometryElement geometry;
				try
				{
					geometry = element.get_Geometry(options);
				}
				catch
				{
					geometry = null;
				}

				if (geometry != null)
				{
					ExtractGeometry(
						geometry,
						Transform.Identity,
						plane,
						segments,
						faceSamples);
				}

				// Centerline / connectors define the grey pipe body — keep all of them.
				AddConnectorSamples(element, plane, solidSamples);
				AddThickCenterlineSamples(element, plane, solidSamples);

				Rectangle2D partBox = TryCreatePartBox(element, view, plane, pad, shrink: 0.40);
				if (partBox != null)
				{
					partBoxes.Add(partBox);
				}
			}

			List<UV> hitSamples = new List<UV>(solidSamples);
			hitSamples.AddRange(DownsampleSamples(faceSamples, MaxFaceHitSamples));
			return new AssemblyGeometry2D(segments, hitSamples, partBoxes);
		}

		private static List<UV> DownsampleSamples(List<UV> samples, int maxCount)
		{
			if (samples == null || samples.Count == 0)
			{
				return new List<UV>();
			}

			if (samples.Count <= maxCount)
			{
				return samples;
			}

			List<UV> reduced = new List<UV>(maxCount);
			double stride = samples.Count / (double)maxCount;
			for (int i = 0; i < maxCount; i++)
			{
				int index = Math.Min(samples.Count - 1, (int)(i * stride));
				reduced.Add(samples[index]);
			}

			return reduced;
		}

		public bool Intersects(Rectangle2D rectangle)
		{
			return HitsSilhouette(rectangle);
		}

		/// <summary>
		/// True when the tag pill sits on / too near the visible pipe/fitting body.
		/// Uses edges + thick centerline/face samples — not fat projected AABBs.
		/// </summary>
		public bool HitsSilhouette(Rectangle2D rectangle)
		{
			return !BodyClearsSilhouette(rectangle, clearPad: 0.0);
		}

		/// <summary>
		/// Pill is clear of the solid when it does not contain silhouette samples/edges
		/// and stays at least <paramref name="clearPad"/> away from them.
		/// </summary>
		public bool BodyClearsSilhouette(Rectangle2D rectangle, double clearPad)
		{
			if (rectangle == null)
			{
				return true;
			}

			double pad = Math.Max(0.0, clearPad);

			foreach (Segment2D segment in _segments)
			{
				if (rectangle.IntersectsSegment(segment.Start, segment.End))
				{
					return false;
				}

				if (pad > Tolerance
					&& rectangle.DistanceToSegment(segment.Start, segment.End) < pad - Tolerance)
				{
					return false;
				}
			}

			foreach (UV point in _samplePoints)
			{
				if (rectangle.Contains(point))
				{
					return false;
				}

				if (pad > Tolerance
					&& rectangle.DistanceToPoint(point) < pad - Tolerance)
				{
					return false;
				}
			}

			return true;
		}

		/// <summary>Box+segment distance only — safe for hot candidate scoring.</summary>
		public double DistanceToFast(Rectangle2D rectangle)
		{
			if (IntersectsBoxesOrSegments(rectangle))
			{
				return 0.0;
			}

			double minimum = double.PositiveInfinity;
			foreach (Rectangle2D box in _partBoxes)
			{
				minimum = Math.Min(minimum, rectangle.DistanceToRectangle(box));
			}

			foreach (Segment2D segment in _segments)
			{
				minimum = Math.Min(
					minimum,
					rectangle.DistanceToSegment(segment.Start, segment.End));
			}

			return double.IsPositiveInfinity(minimum) ? 0.0 : minimum;
		}

		private bool IntersectsBoxesOrSegments(Rectangle2D rectangle)
		{
			foreach (Rectangle2D box in _partBoxes)
			{
				if (rectangle.Intersects(box))
				{
					return true;
				}
			}

			foreach (Segment2D segment in _segments)
			{
				if (rectangle.IntersectsSegment(segment.Start, segment.End))
				{
					return true;
				}
			}

			return false;
		}

		public double DistanceTo(Rectangle2D rectangle)
		{
			double fast = DistanceToFast(rectangle);
			if (fast <= Tolerance)
			{
				return 0.0;
			}

			double minimum = fast;
			foreach (UV point in _samplePoints)
			{
				minimum = Math.Min(minimum, rectangle.DistanceToPoint(point));
			}

			return minimum;
		}

		/// <summary>
		/// True when the leader from host snap to head cuts through assembly body
		/// beyond a small allowance at the host tip.
		/// </summary>
		public bool LeaderCrossesGeometry(
			UV hostAnchor,
			UV head,
			double ignoreRadiusNearHost)
		{
			// Leave a short tip allowance at the host; beyond that, any foreign part box is a cross.
			double tipIgnore = Math.Max(ignoreRadiusNearHost, 1.0 / 48.0);

			const int steps = 14;
			for (int i = 1; i <= steps; i++)
			{
				double t = i / (double)steps;
				UV point = new UV(
					hostAnchor.U + (head.U - hostAnchor.U) * t,
					hostAnchor.V + (head.V - hostAnchor.V) * t);

				double fromHost = Distance(point, hostAnchor);
				if (fromHost <= tipIgnore + Tolerance)
				{
					continue;
				}

				foreach (Rectangle2D box in _partBoxes)
				{
					if (!box.Contains(point))
					{
						continue;
					}

					// Still inside the host part near the snap — expected for short radial leaders.
					if (box.Contains(hostAnchor) && fromHost <= tipIgnore * 3.0 + Tolerance)
					{
						continue;
					}

					return true;
				}
			}

			foreach (Segment2D segment in _segments)
			{
				if (!SegmentsCrossProperly(hostAnchor, head, segment.Start, segment.End))
				{
					continue;
				}

				UV hit = ApproximateIntersection(hostAnchor, head, segment.Start, segment.End)
					?? segment.Start;
				if (Distance(hit, hostAnchor) > tipIgnore + Tolerance)
				{
					return true;
				}
			}

			return false;
		}

		private static Rectangle2D TryCreatePartBox(
			Element element,
			View view,
			ViewPlane plane,
			double pad,
			double shrink = 1.0)
		{
			BoundingBoxXYZ box = null;
			try
			{
				box = element.get_BoundingBox(view) ?? element.get_BoundingBox(null);
			}
			catch
			{
				return null;
			}

			if (box == null)
			{
				return null;
			}

			List<UV> corners = GetBoxCorners(box).Select(plane.ToView).ToList();
			double minU = corners.Min(p => p.U);
			double maxU = corners.Max(p => p.U);
			double minV = corners.Min(p => p.V);
			double maxV = corners.Max(p => p.V);
			double cx = (minU + maxU) * 0.5;
			double cy = (minV + maxV) * 0.5;
			double halfU = (maxU - minU) * 0.5 * Math.Max(0.25, Math.Min(1.0, shrink));
			double halfV = (maxV - minV) * 0.5 * Math.Max(0.25, Math.Min(1.0, shrink));
			return new Rectangle2D(
				cx - halfU - pad,
				cx + halfU + pad,
				cy - halfV - pad,
				cy + halfV + pad);
		}

		private static void AddPartBoundEdgeSamples(
			Rectangle2D box,
			ICollection<UV> samplePoints)
		{
			double minU = box.MinU;
			double maxU = box.MaxU;
			double minV = box.MinV;
			double maxV = box.MaxV;

			samplePoints.Add(new UV(minU, minV));
			samplePoints.Add(new UV(maxU, minV));
			samplePoints.Add(new UV(minU, maxV));
			samplePoints.Add(new UV(maxU, maxV));
			samplePoints.Add(new UV((minU + maxU) * 0.5, minV));
			samplePoints.Add(new UV((minU + maxU) * 0.5, maxV));
			samplePoints.Add(new UV(minU, (minV + maxV) * 0.5));
			samplePoints.Add(new UV(maxU, (minV + maxV) * 0.5));
			samplePoints.Add(new UV((minU + maxU) * 0.5, (minV + maxV) * 0.5));

			// Light interior fill so bodies centered on shaded faces are rejected.
			for (int i = 1; i <= 3; i++)
			{
				double tu = i / 4.0;
				for (int j = 1; j <= 3; j++)
				{
					double tv = j / 4.0;
					samplePoints.Add(new UV(
						minU + (maxU - minU) * tu,
						minV + (maxV - minV) * tv));
				}
			}
		}

		private static void AddThickCenterlineSamples(
			Element element,
			ViewPlane plane,
			ICollection<UV> samplePoints)
		{
			try
			{
				if (!(element is FabricationPart part) || part.ConnectorManager == null)
				{
					return;
				}

				List<XYZ> origins = new List<XYZ>();
				double radius = 0.0;
				foreach (Connector connector in part.ConnectorManager.Connectors)
				{
					if (connector?.Origin == null)
					{
						continue;
					}

					origins.Add(connector.Origin);
					try
					{
						if (connector.Radius > radius)
						{
							radius = connector.Radius;
						}
					}
					catch
					{
					}
				}

				if (origins.Count < 2)
				{
					return;
				}

				// Cover the shaded OD in ortho — under-sampling left pills sitting on the grey.
				double band = Math.Max(radius * 1.20, 1.0 / 64.0);
				double bandOuter = band * 1.35;
				for (int i = 0; i < origins.Count; i++)
				{
					for (int j = i + 1; j < origins.Count; j++)
					{
						XYZ a = origins[i];
						XYZ b = origins[j];
						if (a.DistanceTo(b) > 30.0)
						{
							continue;
						}

						for (int k = 0; k <= 20; k++)
						{
							double t = k / 20.0;
							XYZ p = a + (b - a) * t;
							UV uv = plane.ToView(p);
							samplePoints.Add(uv);
							foreach (double r in new[] { band * 0.55, band, bandOuter })
							{
								samplePoints.Add(new UV(uv.U + r, uv.V));
								samplePoints.Add(new UV(uv.U - r, uv.V));
								samplePoints.Add(new UV(uv.U, uv.V + r));
								samplePoints.Add(new UV(uv.U, uv.V - r));
								samplePoints.Add(new UV(uv.U + r * 0.7, uv.V + r * 0.7));
								samplePoints.Add(new UV(uv.U - r * 0.7, uv.V + r * 0.7));
								samplePoints.Add(new UV(uv.U + r * 0.7, uv.V - r * 0.7));
								samplePoints.Add(new UV(uv.U - r * 0.7, uv.V - r * 0.7));
							}
						}
					}
				}
			}
			catch
			{
			}
		}

		private static void AddConnectorSamples(
			Element element,
			ViewPlane plane,
			ICollection<UV> samplePoints)
		{
			try
			{
				if (!(element is FabricationPart part) || part.ConnectorManager == null)
				{
					return;
				}

				foreach (Connector connector in part.ConnectorManager.Connectors)
				{
					if (connector?.Origin != null)
					{
						samplePoints.Add(plane.ToView(connector.Origin));
					}
				}
			}
			catch
			{
			}
		}

		private static UV ApproximateIntersection(UV a1, UV a2, UV b1, UV b2)
		{
			double a = a2.U - a1.U;
			double b = b1.U - b2.U;
			double c = a2.V - a1.V;
			double d = b1.V - b2.V;
			double e = b1.U - a1.U;
			double f = b1.V - a1.V;
			double den = a * d - b * c;
			if (Math.Abs(den) < Tolerance)
			{
				return null;
			}

			double t = (e * d - b * f) / den;
			return new UV(a1.U + t * a, a1.V + t * c);
		}

		private static bool SegmentsCrossProperly(UV a1, UV a2, UV b1, UV b2)
		{
			double d1 = Cross2(a1, a2, b1);
			double d2 = Cross2(a1, a2, b2);
			double d3 = Cross2(b1, b2, a1);
			double d4 = Cross2(b1, b2, a2);

			return ((d1 > Tolerance && d2 < -Tolerance) || (d1 < -Tolerance && d2 > Tolerance))
				&& ((d3 > Tolerance && d4 < -Tolerance) || (d3 < -Tolerance && d4 > Tolerance));
		}

		private static double Cross2(UV start, UV end, UV point)
		{
			return (end.U - start.U) * (point.V - start.V)
				- (end.V - start.V) * (point.U - start.U);
		}

		private static void ExtractGeometry(
			GeometryElement geometry,
			Transform transform,
			ViewPlane plane,
			ICollection<Segment2D> segments,
			ICollection<UV> samplePoints)
		{
			foreach (GeometryObject geometryObject in geometry)
			{
				if (geometryObject is GeometryInstance instance)
				{
					Transform combined = transform.Multiply(instance.Transform);
					GeometryElement symbolGeometry = instance.GetSymbolGeometry();
					if (symbolGeometry != null)
					{
						ExtractGeometry(
							symbolGeometry,
							combined,
							plane,
							segments,
							samplePoints);
					}

					continue;
				}

				if (geometryObject is Solid solid
					&& solid.Faces.Size > 0
					&& solid.Volume > Tolerance)
				{
					foreach (Edge edge in solid.Edges)
					{
						IList<XYZ> points;
						try
						{
							points = edge.Tessellate();
						}
						catch
						{
							continue;
						}

						AddSegments(points, transform, plane, segments);
					}

					foreach (Face face in solid.Faces)
					{
						Mesh mesh;
						try
						{
							mesh = face.Triangulate();
						}
						catch
						{
							mesh = null;
						}

						if (mesh?.Vertices == null)
						{
							continue;
						}

						IList<XYZ> vertices = mesh.Vertices;
						int step = Math.Max(1, vertices.Count / 48);
						for (int i = 0; i < vertices.Count; i += step)
						{
							XYZ vertex = vertices[i];
							if (vertex != null)
							{
								samplePoints.Add(plane.ToView(transform.OfPoint(vertex)));
							}
						}
					}

					continue;
				}

				if (geometryObject is Curve curve)
				{
					IList<XYZ> points;
					try
					{
						points = curve.Tessellate();
					}
					catch
					{
						points = new[]
						{
							curve.GetEndPoint(0),
							curve.GetEndPoint(1)
						};
					}

					AddSegments(points, transform, plane, segments);
				}
			}
		}

		private static void AddSegments(
			IList<XYZ> points,
			Transform transform,
			ViewPlane plane,
			ICollection<Segment2D> segments)
		{
			if (points == null || points.Count < 2)
			{
				return;
			}

			UV previous = plane.ToView(transform.OfPoint(points[0]));

			for (int index = 1; index < points.Count; index++)
			{
				UV current = plane.ToView(transform.OfPoint(points[index]));
				if (Distance(previous, current) > Tolerance)
				{
					segments.Add(new Segment2D(previous, current));
				}

				previous = current;
			}
		}
	}

	private sealed class Segment2D
	{
		public Segment2D(UV start, UV end)
		{
			Start = start;
			End = end;
		}

		public UV Start { get; }

		public UV End { get; }
	}

	private sealed class Rectangle2D
	{
		public Rectangle2D(double minU, double maxU, double minV, double maxV)
		{
			MinU = Math.Min(minU, maxU);
			MaxU = Math.Max(minU, maxU);
			MinV = Math.Min(minV, maxV);
			MaxV = Math.Max(minV, maxV);
		}

		public double MinU { get; }

		public double MaxU { get; }

		public double MinV { get; }

		public double MaxV { get; }

		public double Width => MaxU - MinU;

		public double Height => MaxV - MinV;

		public UV Center => new UV((MinU + MaxU) / 2.0, (MinV + MaxV) / 2.0);

		public Rectangle2D Expand(double amount)
		{
			return new Rectangle2D(
				MinU - amount,
				MaxU + amount,
				MinV - amount,
				MaxV + amount);
		}

		public bool Contains(UV point)
		{
			return point.U >= MinU - Tolerance
				&& point.U <= MaxU + Tolerance
				&& point.V >= MinV - Tolerance
				&& point.V <= MaxV + Tolerance;
		}

		public bool Intersects(Rectangle2D other)
		{
			return MinU < other.MaxU - Tolerance
				&& MaxU > other.MinU + Tolerance
				&& MinV < other.MaxV - Tolerance
				&& MaxV > other.MinV + Tolerance;
		}

		public double DistanceToPoint(UV point)
		{
			double deltaU = Math.Max(Math.Max(MinU - point.U, 0.0), point.U - MaxU);
			double deltaV = Math.Max(Math.Max(MinV - point.V, 0.0), point.V - MaxV);
			return Math.Sqrt(deltaU * deltaU + deltaV * deltaV);
		}

		public double DistanceToRectangle(Rectangle2D other)
		{
			if (Intersects(other))
			{
				return 0.0;
			}

			double deltaU = 0.0;
			if (MaxU < other.MinU)
			{
				deltaU = other.MinU - MaxU;
			}
			else if (other.MaxU < MinU)
			{
				deltaU = MinU - other.MaxU;
			}

			double deltaV = 0.0;
			if (MaxV < other.MinV)
			{
				deltaV = other.MinV - MaxV;
			}
			else if (other.MaxV < MinV)
			{
				deltaV = MinV - other.MaxV;
			}

			return Math.Sqrt(deltaU * deltaU + deltaV * deltaV);
		}

		public bool IntersectsSegment(UV start, UV end)
		{
			if (Contains(start) || Contains(end))
			{
				return true;
			}

			UV bottomLeft = new UV(MinU, MinV);
			UV bottomRight = new UV(MaxU, MinV);
			UV topRight = new UV(MaxU, MaxV);
			UV topLeft = new UV(MinU, MaxV);

			return SegmentsIntersect(start, end, bottomLeft, bottomRight)
				|| SegmentsIntersect(start, end, bottomRight, topRight)
				|| SegmentsIntersect(start, end, topRight, topLeft)
				|| SegmentsIntersect(start, end, topLeft, bottomLeft);
		}

		public double DistanceToSegment(UV start, UV end)
		{
			if (IntersectsSegment(start, end))
			{
				return 0.0;
			}

			UV bottomLeft = new UV(MinU, MinV);
			UV bottomRight = new UV(MaxU, MinV);
			UV topRight = new UV(MaxU, MaxV);
			UV topLeft = new UV(MinU, MaxV);

			return Math.Min(
				Math.Min(
					SegmentDistance(start, end, bottomLeft, bottomRight),
					SegmentDistance(start, end, bottomRight, topRight)),
				Math.Min(
					SegmentDistance(start, end, topRight, topLeft),
					SegmentDistance(start, end, topLeft, bottomLeft)));
		}

		private static double SegmentDistance(UV a1, UV a2, UV b1, UV b2)
		{
			if (SegmentsIntersect(a1, a2, b1, b2))
			{
				return 0.0;
			}

			return Math.Min(
				Math.Min(
					PointToSegmentDistance(a1, b1, b2),
					PointToSegmentDistance(a2, b1, b2)),
				Math.Min(
					PointToSegmentDistance(b1, a1, a2),
					PointToSegmentDistance(b2, a1, a2)));
		}

		private static double PointToSegmentDistance(UV point, UV start, UV end)
		{
			double du = end.U - start.U;
			double dv = end.V - start.V;
			double lengthSquared = du * du + dv * dv;

			if (lengthSquared <= Tolerance)
			{
				return Distance(point, start);
			}

			double t =
				((point.U - start.U) * du + (point.V - start.V) * dv)
				/ lengthSquared;

			t = Math.Max(0.0, Math.Min(1.0, t));

			UV closest = new UV(start.U + t * du, start.V + t * dv);
			return Distance(point, closest);
		}

		private static bool SegmentsIntersect(UV a1, UV a2, UV b1, UV b2)
		{
			double d1 = Cross(a1, a2, b1);
			double d2 = Cross(a1, a2, b2);
			double d3 = Cross(b1, b2, a1);
			double d4 = Cross(b1, b2, a2);

			return ((d1 > Tolerance && d2 < -Tolerance)
					|| (d1 < -Tolerance && d2 > Tolerance))
				&& ((d3 > Tolerance && d4 < -Tolerance)
					|| (d3 < -Tolerance && d4 > Tolerance));
		}

		private static double Cross(UV start, UV end, UV point)
		{
			return (end.U - start.U) * (point.V - start.V)
				- (end.V - start.V) * (point.U - start.U);
		}
	}

	private sealed class ViewPlane
	{
		private readonly XYZ _origin;
		private readonly XYZ _right;
		private readonly XYZ _up;
		private readonly XYZ _viewDirection;

		public ViewPlane(View view)
		{
			_origin = view.Origin;
			_right = view.RightDirection.Normalize();
			_up = view.UpDirection.Normalize();
			_viewDirection = view.ViewDirection.Normalize();
		}

		public UV ToView(XYZ modelPoint)
		{
			XYZ offset = modelPoint - _origin;
			return new UV(
				offset.DotProduct(_right),
				offset.DotProduct(_up));
		}

		public XYZ ToModel(UV viewPoint, XYZ originalPoint)
		{
			double depth = (originalPoint - _origin).DotProduct(_viewDirection);

			return _origin
				+ _right * viewPoint.U
				+ _up * viewPoint.V
				+ _viewDirection * depth;
		}
	}
}
