using System.ComponentModel;
using System.Runtime.CompilerServices;
using Autodesk.Revit.DB;

namespace SpoolingSavantV3Exports.Workers.SpoolingManager.ViewModels;

public class RenameSheetRow : INotifyPropertyChanged
{
	private string _newName;

	public ElementId AssemblyId { get; set; }

	public string CurrentName { get; set; }

	public string NewName
	{
		get
		{
			return _newName;
		}
		set
		{
			if (!(_newName == value))
			{
				_newName = value;
				OnPropertyChanged("NewName");
			}
		}
	}

	public event PropertyChangedEventHandler PropertyChanged;

	protected void OnPropertyChanged([CallerMemberName] string propertyName = null)
	{
		this.PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
	}
}
