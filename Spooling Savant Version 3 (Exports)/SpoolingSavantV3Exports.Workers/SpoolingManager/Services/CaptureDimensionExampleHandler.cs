using System;
using System.Windows;
using Autodesk.Revit.UI;
using SpoolingSavantV3Exports.Workers;
using SpoolingSavantV3Exports.Workers.SpoolingManager.Infrastructure;
using SpoolingSavantV3Exports.Workers.SpoolingManager.Models;

namespace SpoolingSavantV3Exports.Workers.SpoolingManager.Services;

public sealed class CaptureDimensionExampleHandler : IExternalEventHandler
{
	public CaptureDimensionExampleRequest PendingRequest { get; set; }

	public CaptureDimensionExampleResult LastResult { get; private set; }

	public string GetName()
	{
		return "Capture Dimension Example";
	}

	public void Execute(UIApplication app)
	{
		CaptureDimensionExampleRequest request = PendingRequest;
		PendingRequest = null;
		LastResult = null;
		if (request == null)
		{
			return;
		}
		if (app?.Application != null)
		{
			InstallLayout.ApplyRevitVersionNumber(app.Application.VersionNumber);
		}
		UIDocument uidoc = app?.ActiveUIDocument;
		if (uidoc?.Document == null)
		{
			Views.SsSavantMessageBox.Show("No active Revit document.", CreateSpoolSheetsHandler.GetToolWindowTitle(request.ProductKind), MessageBoxButton.OK, MessageBoxImage.Exclamation);
			return;
		}
		string title = CreateSpoolSheetsHandler.GetToolWindowTitle(request.ProductKind);
		try
		{
			LastResult = DimensionExampleCaptureService.Capture(uidoc.Document, uidoc.ActiveView, request.AssemblyId);
		}
		catch (Exception ex)
		{
			LastResult = new CaptureDimensionExampleResult
			{
				Success = false,
				Message = ex.Message
			};
		}
		if (LastResult == null)
		{
			return;
		}
		if (LastResult.Success)
		{
			Views.SsSavantMessageBox.Show(
				LastResult.Message + "\n\nSaved to:\n" + LastResult.OutputPath,
				title,
				MessageBoxButton.OK,
				MessageBoxImage.Information);
			return;
		}
		Views.SsSavantMessageBox.Show(LastResult.Message, title, MessageBoxButton.OK, MessageBoxImage.Exclamation);
	}
}
