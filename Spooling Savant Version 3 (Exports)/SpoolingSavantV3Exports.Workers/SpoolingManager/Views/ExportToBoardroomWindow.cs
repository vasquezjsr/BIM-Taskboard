using System;
using System.Collections.Generic;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using SpoolingSavantV3Exports.Workers.SpoolingManager.Services;
using SpoolingSavantV3Exports.Workers.UI;

namespace SpoolingSavantV3Exports.Workers.SpoolingManager.Views;

/// <summary>Pick Boardroom project, Spooling task, and output folder for package export.</summary>
internal sealed class ExportToBoardroomWindow : Window
{
	private const double DialogPadding = 12.0;

	private readonly BoardroomApiClient _client;
	private readonly ComboBox _cmbProject;
	private readonly ComboBox _cmbTask;
	private readonly TextBox _txtOutputFolder;
	private readonly TextBlock _taskHint;

	public BoardroomProjectOption SelectedProject { get; private set; }

	public BoardroomTaskOption SelectedTask { get; private set; }

	public string OutputFolder { get; private set; }

	public ExportToBoardroomWindow(
		BoardroomApiClient client,
		IReadOnlyList<BoardroomProjectOption> projects,
		string defaultOutputFolder)
	{
		_client = client ?? throw new ArgumentNullException(nameof(client));
		IReadOnlyList<BoardroomProjectOption> projectList = projects ?? Array.Empty<BoardroomProjectOption>();

		Title = "Export to Boardroom";
		Width = 560.0;
		MinWidth = 500.0;
		SizeToContent = SizeToContent.Height;
		ResizeMode = ResizeMode.NoResize;
		WindowStartupLocation = WindowStartupLocation.CenterOwner;
		SsSavantChrome.MergeInto(this);

		var title = new TextBlock
		{
			Text = "Export packages to BIM Boardroom",
			Style = TryFindResource("SsSavantDialogTitleText") as Style
		};
		var hint = new TextBlock
		{
			Text = "Choose a live Boardroom project and Spooling-board task. Reports are written to the export folder with a boardroom-package.json that attaches to that task. Fab receives the package only after the task is marked Ready for Fab in Boardroom.",
			Style = TryFindResource("SsSavantDialogHintText") as Style,
			TextWrapping = TextWrapping.Wrap,
			Margin = new Thickness(0, 0, 0, 12)
		};

		var projectLabel = new TextBlock
		{
			Text = "Boardroom project",
			FontWeight = FontWeights.SemiBold,
			Margin = new Thickness(0, 0, 0, 4)
		};

		_cmbProject = new ComboBox
		{
			Height = 28,
			Margin = new Thickness(0, 0, 0, 12),
			DisplayMemberPath = nameof(BoardroomProjectOption.DisplayLabel),
			ItemsSource = projectList
		};
		_cmbProject.SelectionChanged += (_, __) => ReloadTasks();

		var taskLabel = new TextBlock
		{
			Text = "Spooling board task",
			FontWeight = FontWeights.SemiBold,
			Margin = new Thickness(0, 0, 0, 4)
		};

		_cmbTask = new ComboBox
		{
			Height = 28,
			Margin = new Thickness(0, 0, 0, 4),
			DisplayMemberPath = nameof(BoardroomTaskOption.DisplayLabel)
		};

		_taskHint = new TextBlock
		{
			Text = "Select a project to load Spooling tasks.",
			Style = TryFindResource("SsSavantDialogHintText") as Style,
			TextWrapping = TextWrapping.Wrap,
			Margin = new Thickness(0, 0, 0, 12)
		};

		var folderLabel = new TextBlock
		{
			Text = "Export folder",
			FontWeight = FontWeights.SemiBold,
			Margin = new Thickness(0, 0, 0, 4)
		};

		_txtOutputFolder = new TextBox
		{
			Height = 28,
			VerticalContentAlignment = VerticalAlignment.Center,
			Text = defaultOutputFolder ?? string.Empty,
			Margin = new Thickness(0, 0, 8, 0)
		};

		var browseButton = new Button
		{
			Content = "Browse…",
			Height = 28,
			MinWidth = 88,
			Padding = new Thickness(10, 0, 10, 0)
		};
		browseButton.Click += (_, __) => BrowseFolder();

		var folderRow = new Grid { Margin = new Thickness(0, 0, 0, 8) };
		folderRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
		folderRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
		Grid.SetColumn(_txtOutputFolder, 0);
		Grid.SetColumn(browseButton, 1);
		folderRow.Children.Add(_txtOutputFolder);
		folderRow.Children.Add(browseButton);

		var folderHint = new TextBlock
		{
			Text = "Existing files with the same names are overwritten.",
			Style = TryFindResource("SsSavantDialogHintText") as Style,
			Margin = new Thickness(0, 0, 0, 4)
		};

		var exportButton = new Button
		{
			Content = "Export",
			Width = 100.0,
			IsDefault = true,
			Margin = new Thickness(0, 0, 8, 0)
		};
		var cancelButton = new Button
		{
			Content = "Cancel",
			Width = 100.0,
			IsCancel = true
		};

		exportButton.Click += (_, __) =>
		{
			if (_cmbProject.SelectedItem is not BoardroomProjectOption project)
			{
				MessageBox.Show(this, "Select a Boardroom project.\n\nIf the list is empty, start BIM Boardroom and confirm Settings → Boardroom API URL.", Title, MessageBoxButton.OK, MessageBoxImage.Asterisk);
				return;
			}

			if (_cmbTask.SelectedItem is not BoardroomTaskOption task)
			{
				MessageBox.Show(this, "Select a Spooling board task for this export.\n\nCreate or open a Spooling task in Boardroom for this project first.", Title, MessageBoxButton.OK, MessageBoxImage.Asterisk);
				return;
			}

			string folder = (_txtOutputFolder.Text ?? string.Empty).Trim();
			if (folder.Length == 0)
			{
				MessageBox.Show(this, "Choose an export folder.", Title, MessageBoxButton.OK, MessageBoxImage.Asterisk);
				return;
			}

			try
			{
				Directory.CreateDirectory(folder);
				folder = Path.GetFullPath(folder);
			}
			catch (Exception ex)
			{
				MessageBox.Show(this, "Could not create the export folder.\n\n" + ex.Message, Title, MessageBoxButton.OK, MessageBoxImage.Exclamation);
				return;
			}

			if (task.HasSsv3Export)
			{
				string taskLabel = string.IsNullOrWhiteSpace(task.Title) ? "this Spooling task" : "\"" + task.Title.Trim() + "\"";
				MessageBoxResult overwrite = MessageBox.Show(
					this,
					"Boardroom already has an SSv3 export on " + taskLabel + ".\n\n"
					+ "Exporting again will overwrite the assemblies and report attachments on that task.\n\n"
					+ "Are you sure you want to overwrite?",
					Title,
					MessageBoxButton.YesNo,
					MessageBoxImage.Warning,
					MessageBoxResult.No);
				if (overwrite != MessageBoxResult.Yes)
				{
					return;
				}
			}

			SelectedProject = project;
			SelectedTask = task;
			OutputFolder = folder;
			DialogResult = true;
			Close();
		};

		cancelButton.Click += (_, __) =>
		{
			DialogResult = false;
			Close();
		};

		var footer = new StackPanel
		{
			Orientation = Orientation.Horizontal,
			HorizontalAlignment = HorizontalAlignment.Right,
			Margin = new Thickness(0, 16, 0, 0)
		};
		footer.Children.Add(exportButton);
		footer.Children.Add(cancelButton);

		var root = new StackPanel();
		root.Children.Add(title);
		root.Children.Add(hint);
		root.Children.Add(projectLabel);
		root.Children.Add(_cmbProject);
		root.Children.Add(taskLabel);
		root.Children.Add(_cmbTask);
		root.Children.Add(_taskHint);
		root.Children.Add(folderLabel);
		root.Children.Add(folderRow);
		root.Children.Add(folderHint);
		root.Children.Add(footer);

		var chrome = new Border { Child = root };
		SsSavantDialogChrome.ApplyThemedBorder(chrome, new Thickness(DialogPadding));
		Content = chrome;

		if (projectList.Count > 0)
		{
			_cmbProject.SelectedIndex = 0;
		}
	}

	private void ReloadTasks()
	{
		_cmbTask.ItemsSource = null;
		_cmbTask.SelectedItem = null;

		if (_cmbProject.SelectedItem is not BoardroomProjectOption project || string.IsNullOrWhiteSpace(project.ProjectId))
		{
			_taskHint.Text = "Select a project to load Spooling tasks.";
			return;
		}

		try
		{
			IReadOnlyList<BoardroomTaskOption> tasks = _client.GetSpoolingTasks(project.ProjectId);
			_cmbTask.ItemsSource = tasks;
			if (tasks.Count == 0)
			{
				_taskHint.Text = "No Spooling-board tasks in this project. Create one in BIM Boardroom first.";
			}
			else
			{
				_cmbTask.SelectedIndex = 0;
				int withExport = 0;
				foreach (BoardroomTaskOption t in tasks)
				{
					if (t.HasSsv3Export)
					{
						withExport++;
					}
				}
				_taskHint.Text = withExport > 0
					? tasks.Count + " Spooling task(s) available · " + withExport + " already have an export (overwrite will be confirmed)."
					: tasks.Count + " Spooling task(s) available.";
			}
		}
		catch (Exception ex)
		{
			_taskHint.Text = "Could not load Spooling tasks: " + ex.Message;
		}
	}

	private void BrowseFolder()
	{
		using var dialog = new System.Windows.Forms.FolderBrowserDialog
		{
			Description = "Select Boardroom export folder (reports + boardroom-package.json)."
		};
		string current = (_txtOutputFolder.Text ?? string.Empty).Trim();
		if (Directory.Exists(current))
		{
			dialog.SelectedPath = current;
		}

		if (dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK && !string.IsNullOrWhiteSpace(dialog.SelectedPath))
		{
			_txtOutputFolder.Text = dialog.SelectedPath.Trim();
		}
	}
}
