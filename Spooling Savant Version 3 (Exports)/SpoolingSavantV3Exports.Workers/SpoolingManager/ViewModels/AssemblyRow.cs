using System.ComponentModel;
using System.Runtime.CompilerServices;
using Autodesk.Revit.DB;

namespace SpoolingSavantV3Exports.Workers.SpoolingManager.ViewModels;

public class AssemblyRow : INotifyPropertyChanged
{
	private bool _isSelected;

	private bool _hasSpoolSheet;

	private string _spoolName;

	private string _sPackage = string.Empty;

	public ElementId AssemblyId { get; set; }

	public bool HasSpoolSheet
	{
		get
		{
			return _hasSpoolSheet;
		}
		set
		{
			if (_hasSpoolSheet != value)
			{
				_hasSpoolSheet = value;
				OnPropertyChanged("HasSpoolSheet");
			}
		}
	}

	public string SpoolName
	{
		get
		{
			return _spoolName;
		}
		set
		{
			if (!(_spoolName == value))
			{
				_spoolName = value;
				OnPropertyChanged("SpoolName");
			}
		}
	}

	public string SPackage
	{
		get
		{
			return _sPackage;
		}
		set
		{
			if (!(_sPackage == value))
			{
				_sPackage = value ?? string.Empty;
				OnPropertyChanged("SPackage");
			}
		}
	}

	public bool IsSelected
	{
		get
		{
			return _isSelected;
		}
		set
		{
			if (_isSelected != value)
			{
				_isSelected = value;
				OnPropertyChanged("IsSelected");
			}
		}
	}

	public event PropertyChangedEventHandler PropertyChanged;

	protected void OnPropertyChanged([CallerMemberName] string propertyName = null)
	{
		this.PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
	}
}
