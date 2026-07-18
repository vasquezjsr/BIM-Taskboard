namespace SpoolingSavantV3Exports.Workers.SpoolingManager.Models;

public class PlotPackagesReportOptions
{
	public bool IncludeSpoolsCombined { get; set; } = true;

	public bool IncludeSpoolMap { get; set; } = true;

	public bool IncludeAssemblyList { get; set; } = true;

	public bool IncludeBillOfMaterials { get; set; } = true;

	public bool IncludeCutList { get; set; } = true;

	public bool IncludeWeldLog { get; set; } = true;

	public bool IncludeTigerStop { get; set; } = true;

	public bool IncludePcfFiles { get; set; } = true;

	/// <summary>Header Project line on Assembly List / BOM / Cut List PDFs.</summary>
	public string ProjectName { get; set; } = string.Empty;

	/// <summary>Header Created By line on Assembly List / BOM / Cut List PDFs.</summary>
	public string CreatedBy { get; set; } = string.Empty;

	/// <summary>Header Date line on Assembly List / BOM / Cut List PDFs (free text as entered).</summary>
	public string DateText { get; set; } = string.Empty;

	public bool AnySelected
	{
		get
		{
			return IncludeSpoolsCombined
				|| IncludeSpoolMap
				|| IncludeAssemblyList
				|| IncludeBillOfMaterials
				|| IncludeCutList
				|| IncludeWeldLog
				|| IncludeTigerStop
				|| IncludePcfFiles;
		}
	}
}
