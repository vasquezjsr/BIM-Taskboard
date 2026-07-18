using System.Collections.Generic;

namespace SpoolingSavantV3Exports.Workers.SpoolingManager.Models;

public class RenameSheetsRequest
{
	public List<RenameSheetItem> Items { get; set; } = new List<RenameSheetItem>();

	public SpoolingManagerKind ProductKind { get; set; }
}
