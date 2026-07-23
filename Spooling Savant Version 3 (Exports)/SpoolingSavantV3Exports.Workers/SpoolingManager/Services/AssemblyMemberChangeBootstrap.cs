using Autodesk.Revit.UI;

namespace SpoolingSavantV3Exports.Workers.SpoolingManager.Services;

public static class AssemblyMemberChangeBootstrap
{
	private static bool _registered;
	private static AssemblyMemberChangeHandler _handler;
	private static ExternalEvent _syncExternalEvent;

	public static void Register(UIControlledApplication application)
	{
		// DocumentChanged and ExternalEvent live in SpoolingSavantV2.dll (SsSavantAssemblyMemberSync).
		EnsureHandler();
	}

	public static void Unregister(UIControlledApplication application)
	{
		_registered = false;
	}

	public static void EnsureHandler()
	{
		if (_syncExternalEvent != null)
			return;

		_handler = new AssemblyMemberChangeHandler();
		_syncExternalEvent = ExternalEvent.Create(_handler);
		AssemblyMemberChangeCoordinator.SetExternalEvent(_syncExternalEvent);
	}

	public static void RefreshHandler()
	{
		_handler = new AssemblyMemberChangeHandler();
		_syncExternalEvent = ExternalEvent.Create(_handler);
		AssemblyMemberChangeCoordinator.SetExternalEvent(_syncExternalEvent);
	}
}
