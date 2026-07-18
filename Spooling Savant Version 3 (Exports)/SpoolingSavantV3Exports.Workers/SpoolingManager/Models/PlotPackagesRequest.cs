using System.Collections.Generic;

namespace SpoolingSavantV3Exports.Workers.SpoolingManager.Models;

public class PlotPackagesRequest
{
	public List<PlotPackageBatch> Batches { get; set; } = new List<PlotPackageBatch>();

	public string OutputFolder { get; set; }

	public SpoolingManagerKind ProductKind { get; set; }

	public PlotPackagesReportOptions ReportOptions { get; set; }

	/// <summary>When set, writes boardroom-package.json for Fab/Shipping import after reports.</summary>
	public bool ExportToBoardroom { get; set; }

	public string BoardroomProjectId { get; set; }

	public string BoardroomProjectName { get; set; }

	public string BoardroomClientName { get; set; }

	public string BoardroomJobCode { get; set; }

	public string BoardroomTaskId { get; set; }

	public string BoardroomTaskNumber { get; set; }

	public string BoardroomTaskTitle { get; set; }
}
