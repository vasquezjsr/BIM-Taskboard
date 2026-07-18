namespace SpoolingSavantV3Exports.Workers.SpoolingManager.Models;

public static class SpoolingManagerKindExtensions
{
	public static bool IsMmcStyle(this SpoolingManagerKind kind)
	{
		if (kind != SpoolingManagerKind.Mmc)
		{
			return kind == SpoolingManagerKind.MmcTesting;
		}
		return true;
	}

	public static bool IsMmcTesting(this SpoolingManagerKind kind)
	{
		return kind == SpoolingManagerKind.MmcTesting;
	}
}
