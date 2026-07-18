using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Linq;

namespace SpoolingSavantV3Exports.Workers.SpoolingManager.ViewModels;

public sealed class PackageAssemblyGroup : INotifyPropertyChanged
{
	private bool _bulkChildUpdate;

	private bool _pushFromChildren;

	private bool? _headerCheckState;

	private string _displayTitle = string.Empty;

	private readonly string _committedCanonical;

	private bool _suppressNextLostFocusRenameCommit;

	private bool _packageTreeExpanded = true;

	public Action<string> RenameHandler { get; set; }

	public ObservableCollection<AssemblyRow> Assemblies { get; }

	public string DisplayTitle
	{
		get
		{
			return _displayTitle;
		}
		set
		{
			string text = value ?? string.Empty;
			if (!(_displayTitle == text))
			{
				_displayTitle = text;
				this.PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("DisplayTitle"));
			}
		}
	}

	public string AssemblyCountSuffix => $" ({Assemblies.Count})";

	public bool PackageTreeExpanded
	{
		get
		{
			return _packageTreeExpanded;
		}
		set
		{
			if (_packageTreeExpanded != value)
			{
				_packageTreeExpanded = value;
				this.PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("PackageTreeExpanded"));
			}
		}
	}

	public bool? HeaderCheckState
	{
		get
		{
			return _headerCheckState;
		}
		set
		{
			if (_pushFromChildren)
			{
				_headerCheckState = value;
			}
			else if (ComputeHeaderFromChildren() == value)
			{
				_headerCheckState = value;
			}
			else
			{
				ApplyPackageSelection(value);
			}
		}
	}

	public event PropertyChangedEventHandler PropertyChanged;

	public PackageAssemblyGroup(string canonicalPackageKey, IList<AssemblyRow> assemblies)
	{
		_committedCanonical = (string.IsNullOrWhiteSpace(canonicalPackageKey) ? string.Empty : canonicalPackageKey.Trim());
		Assemblies = new ObservableCollection<AssemblyRow>((assemblies ?? Enumerable.Empty<AssemblyRow>()).OrderBy((AssemblyRow x) => x.SpoolName ?? string.Empty, StringComparer.OrdinalIgnoreCase));
		Assemblies.CollectionChanged += OnAssembliesCollectionChanged;
		_displayTitle = (string.IsNullOrEmpty(_committedCanonical) ? "(No package)" : _committedCanonical);
		RefreshHeaderFromChildren();
	}

	public void BeginRenameCommitFromEnterKey()
	{
		_suppressNextLostFocusRenameCommit = true;
	}

	internal bool ConsumeRenameLostFocusSuppression()
	{
		if (!_suppressNextLostFocusRenameCommit)
		{
			return false;
		}
		_suppressNextLostFocusRenameCommit = false;
		return true;
	}

	public void CommitRenameFromEditor(string text)
	{
		string text2 = (text ?? string.Empty).Trim();
		if (text2.Length == 0 || string.Equals(text2, "(No package)", StringComparison.OrdinalIgnoreCase))
		{
			RevertDisplayTitleToCommitted();
		}
		else if (!string.Equals(text2, _committedCanonical, StringComparison.Ordinal))
		{
			RenameHandler?.Invoke(text2);
		}
	}

	public void RevertDisplayTitleToCommitted()
	{
		DisplayTitle = (string.IsNullOrEmpty(_committedCanonical) ? "(No package)" : _committedCanonical);
	}

	public void RefreshHeaderFromChildren()
	{
		if (_bulkChildUpdate)
		{
			return;
		}
		bool? flag = ComputeHeaderFromChildren();
		if (_headerCheckState == flag)
		{
			return;
		}
		_pushFromChildren = true;
		try
		{
			_headerCheckState = flag;
			this.PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("HeaderCheckState"));
		}
		finally
		{
			_pushFromChildren = false;
		}
	}

	private void OnAssembliesCollectionChanged(object sender, NotifyCollectionChangedEventArgs e)
	{
		this.PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("AssemblyCountSuffix"));
	}

	private bool? ComputeHeaderFromChildren()
	{
		if (Assemblies == null || Assemblies.Count == 0)
		{
			return false;
		}
		bool flag = Assemblies.Any((AssemblyRow x) => x.IsSelected);
		if (Assemblies.All((AssemblyRow x) => x.IsSelected))
		{
			return true;
		}
		if (!flag)
		{
			return false;
		}
		return null;
	}

	private void ApplyPackageSelection(bool? value)
	{
		bool isSelected = value != false && (value == true || ComputeHeaderFromChildren() != true);
		_bulkChildUpdate = true;
		try
		{
			foreach (AssemblyRow assembly in Assemblies)
			{
				assembly.IsSelected = isSelected;
			}
		}
		finally
		{
			_bulkChildUpdate = false;
		}
		RefreshHeaderFromChildren();
	}
}
