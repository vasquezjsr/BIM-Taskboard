using System;
using System.Collections.Generic;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Fabrication;
using Autodesk.Revit.DB.Events;
using Autodesk.Revit.UI;
using SpoolingSavantV3Exports.Workers;

namespace SpoolingSavantV3Exports.Workers.SpoolingManager.Services;

internal static class AssemblyMemberChangeCoordinator
{
	internal readonly struct PendingAssemblySync
	{
		internal PendingAssemblySync(Document document, ElementId assemblyId)
		{
			Document = document;
			AssemblyId = assemblyId;
		}

		internal Document Document { get; }

		internal ElementId AssemblyId { get; }
	}

	private static readonly object SyncRoot = new object();
	private static readonly List<PendingAssemblySync> PendingSyncs = new List<PendingAssemblySync>();
	private static ExternalEvent _syncExternalEvent;
	private static bool _isProcessing;
	private static int _suppressionDepth;

	internal static void SetExternalEvent(ExternalEvent externalEvent)
	{
		_syncExternalEvent = externalEvent;
	}

	internal static void SetProcessing(bool isProcessing)
	{
		_isProcessing = isProcessing;
	}

	/// <summary>
	/// Suppresses the automatic member-sync while Spooling Savant 3.0 runs its own operations
	/// (creating/refreshing/renaming sheets, applying packages, etc.). Those operations already
	/// assign item numbers, weld numbers, packages, continuation values and tags inside their own
	/// transaction, so letting <see cref="OnDocumentChanged"/> queue a full re-sync afterward just
	/// re-tags every view again — that redundant pass is what freezes Revit for ~30s right after
	/// the completion dialog closes. The auto-sync is only meant to react to *manual* assembly
	/// membership edits made outside the tool.
	/// </summary>
	internal static IDisposable SuppressAutoSync()
	{
		return new SuppressionScope();
	}

	private static bool IsSuppressed()
	{
		lock (SyncRoot)
		{
			return _suppressionDepth > 0;
		}
	}

	private sealed class SuppressionScope : IDisposable
	{
		private bool _disposed;

		internal SuppressionScope()
		{
			lock (SyncRoot)
			{
				_suppressionDepth++;
			}
		}

		public void Dispose()
		{
			if (_disposed)
			{
				return;
			}
			_disposed = true;
			lock (SyncRoot)
			{
				if (_suppressionDepth > 0)
				{
					_suppressionDepth--;
				}
			}
		}
	}

	internal static List<PendingAssemblySync> TakePendingSyncs()
	{
		lock (SyncRoot)
		{
			if (PendingSyncs.Count == 0)
				return new List<PendingAssemblySync>();

			List<PendingAssemblySync> batch = new List<PendingAssemblySync>(PendingSyncs);
			PendingSyncs.Clear();
			return batch;
		}
	}

	internal static void OnDocumentChanged(object sender, DocumentChangedEventArgs e)
	{
		if (_isProcessing || e == null)
			return;

		if (IsSuppressed())
			return;

		if (IsOwnTransaction(e))
			return;

		Document doc = e.GetDocument();
		if (doc == null || doc.IsFamilyDocument)
			return;

		HashSet<ElementId> assemblyIds = new HashSet<ElementId>();
		ICollection<ElementId> addedElementIds = e.GetAddedElementIds();
		if (addedElementIds != null)
		{
			foreach (ElementId elementId in addedElementIds)
				TryQueueAssemblyForElement(doc, elementId, assemblyIds, isNew: true);
		}

		ICollection<ElementId> modifiedElementIds = e.GetModifiedElementIds();
		if (modifiedElementIds != null)
		{
			foreach (ElementId elementId in modifiedElementIds)
				TryQueueAssemblyForElement(doc, elementId, assemblyIds, isNew: false);
		}

		if (assemblyIds.Count == 0)
			return;

		QueueSync(doc, assemblyIds);
	}

	private static bool IsOwnTransaction(DocumentChangedEventArgs e)
	{
		ICollection<string> transactionNames = e.GetTransactionNames();
		if (transactionNames == null)
			return false;

		foreach (string transactionName in transactionNames)
		{
			if (!string.IsNullOrWhiteSpace(transactionName) &&
			    (transactionName.StartsWith("Spooling Savant", StringComparison.Ordinal) ||
			     transactionName.StartsWith("SS Manager", StringComparison.Ordinal)))
			{
				return true;
			}
		}

		return false;
	}

	private static void TryQueueAssemblyForElement(
		Document doc,
		ElementId elementId,
		ISet<ElementId> assemblyIds,
		bool isNew)
	{
		Element element = doc.GetElement(elementId);
		if (element == null)
			return;

		// Hangers are numbered/tagged separately — do not queue assembly sync for them.
		if (FabricationPartClassification.IsFabricationHanger(element))
			return;

		AssemblyInstance assembly = element as AssemblyInstance;
		if (assembly != null)
		{
			assemblyIds.Add(assembly.Id);
			return;
		}

		ElementId assemblyInstanceId = element.AssemblyInstanceId;
		if (assemblyInstanceId == null || assemblyInstanceId == ElementId.InvalidElementId)
			return;

		if (isNew || NeedsMemberSync(element))
			assemblyIds.Add(assemblyInstanceId);
	}

	private static bool NeedsMemberSync(Element element)
	{
		if (element == null || FabricationPartClassification.IsFabricationHanger(element))
			return false;

		if (HasEmptyPackage(element))
			return true;

		FabricationPart fabricationPart = element as FabricationPart;
		if (fabricationPart == null)
			return false;

		return string.IsNullOrWhiteSpace(CreateSpoolSheetsHandler.GetFabricationItemNumber(fabricationPart));
	}

	private static bool HasEmptyPackage(Element element)
	{
		Parameter parameter = element?.LookupParameter(SsSavantSharedParameterBootstrap.PackageParameterName);
		if (parameter == null)
			return false;

		string value = parameter.StorageType == StorageType.String
			? parameter.AsString()
			: parameter.AsValueString();

		return string.IsNullOrWhiteSpace(value);
	}

	private static void QueueSync(Document doc, ISet<ElementId> assemblyIds)
	{
		lock (SyncRoot)
		{
			foreach (ElementId assemblyId in assemblyIds)
				PendingSyncs.Add(new PendingAssemblySync(doc, assemblyId));
		}

		_syncExternalEvent?.Raise();
	}
}
