using System;
using System.IO;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;
using System.Windows.Media;
using SpoolingSavantV3Exports.Workers.SpoolingManager.Models;
using SpoolingSavantV3Exports.Workers.UI;

namespace SpoolingSavantV3Exports.Workers.SpoolingManager.Views;

internal static class SpoolingManagerXamlLoader
{
	internal static Window LoadWindow(string resourceName)
	{
		return (Window)LoadElement(resourceName);
	}

	internal static UserControl LoadUserControl(string resourceName)
	{
		return (UserControl)LoadElement(resourceName);
	}

	internal static void ApplyWindow(Window target, Window source, SpoolingManagerKind productKind = SpoolingManagerKind.Standard)
	{
		target.Title = source.Title;
		target.Width = source.Width;
		target.MinWidth = source.MinWidth;
		target.MinHeight = source.MinHeight;
		target.SizeToContent = source.SizeToContent;
		if (source.SizeToContent == SizeToContent.Manual)
		{
			target.Height = source.Height;
		}
		else
		{
			target.Height = double.NaN;
		}
		target.WindowStartupLocation = source.WindowStartupLocation;
		target.ResizeMode = source.ResizeMode;
		target.Background = source.Background;
		target.Content = source.Content;
		SsSavantChrome.MergeInto(target);
		CopyWindowResources(target, source);
	}

	private static void CopyWindowResources(Window target, Window source)
	{
		if (target == null || source == null)
		{
			return;
		}
		foreach (ResourceDictionary dictionary in source.Resources.MergedDictionaries)
		{
			target.Resources.MergedDictionaries.Add(dictionary);
		}
		foreach (object key in source.Resources.Keys)
		{
			target.Resources[key] = source.Resources[key];
		}
	}

	internal static void ApplyUserControl(UserControl target, UserControl source, SpoolingManagerKind productKind = SpoolingManagerKind.Standard)
	{
		target.MinWidth = source.MinWidth;
		target.MinHeight = source.MinHeight;
		target.Width = source.Width;
		target.Height = source.Height;
		target.Background = source.Background;
		target.Content = source.Content;
		SsSavantChrome.MergeInto(target);
	}

	internal static void ApplyNamedStyle(FrameworkElement root, string elementName, string styleKey)
	{
		if (TryFind<FrameworkElement>(root, elementName) is FrameworkElement element)
		{
			ApplyNamedStyle(element, styleKey);
		}
	}

	internal static void ApplyNamedStyle(FrameworkElement element, string styleKey)
	{
		if (element == null || string.IsNullOrWhiteSpace(styleKey))
		{
			return;
		}
		if (element.TryFindResource(styleKey) is Style style)
		{
			element.Style = style;
		}
	}

	internal static T TryFind<T>(FrameworkElement root, string name) where T : FrameworkElement
	{
		return FindByName<T>(root, name);
	}

	internal static T Find<T>(FrameworkElement root, string name) where T : FrameworkElement
	{
		return FindByName<T>(root, name) ?? throw new InvalidOperationException("XAML element not found: " + name);
	}

	internal static Button FindButtonByContent(FrameworkElement root, string content)
	{
		return FindButton(root, (Button button) => string.Equals(button.Content as string, content, StringComparison.Ordinal), content);
	}

	internal static Button FindButtonByToolTip(FrameworkElement root, string toolTip)
	{
		return FindButton(root, (Button button) => string.Equals(button.ToolTip as string, toolTip, StringComparison.Ordinal), toolTip);
	}

	private static FrameworkElement LoadElement(string resourceName)
	{
		Assembly executingAssembly = Assembly.GetExecutingAssembly();
		using Stream stream = executingAssembly.GetManifestResourceStream(resourceName);
		if (stream == null)
		{
			throw new InvalidOperationException("Embedded XAML resource not found: " + resourceName);
		}
		using StreamReader streamReader = new StreamReader(stream);
		string input = streamReader.ReadToEnd();
		string assemblyName = executingAssembly.GetName().Name ?? "SpoolingSavantV3Exports.Workers";
		// Embedded XAML clr-namespace assembly= must match the loaded DLL simple name (hotload / rename safe).
		input = Regex.Replace(input, "assembly=SpoolingSavantV3Exports\\.Workers", "assembly=" + assemblyName);
		input = Regex.Replace(input, "\\s+x:Class=\"[^\"]+\"", string.Empty);
		input = Regex.Replace(input, "\\s+Click=\"[^\"]+\"", string.Empty);
		input = Regex.Replace(input, "\\s+TextChanged=\"[^\"]+\"", string.Empty);
		input = Regex.Replace(input, "\\s+PreviewMouseLeftButtonDown=\"[^\"]+\"", string.Empty);
		using MemoryStream stream2 = new MemoryStream(Encoding.UTF8.GetBytes(input));
		ParserContext parserContext = new ParserContext();
		parserContext.BaseUri = new Uri("pack://application:,,,/" + executingAssembly.GetName().Name + ";component/");
		string[] assemblyNames = ((!string.IsNullOrEmpty(executingAssembly.Location)) ? new string[1] { executingAssembly.Location } : new string[1] { executingAssembly.FullName });
		parserContext.XamlTypeMapper = new XamlTypeMapper(assemblyNames);
		return (FrameworkElement)XamlReader.Load(stream2, parserContext);
	}

	private static Button FindButton(FrameworkElement root, Func<Button, bool> predicate, string description)
	{
		Button obj = FindLogicalChild(root, predicate) ?? FindVisualChild(root, predicate);
		if (obj == null)
		{
			throw new InvalidOperationException("XAML button not found: " + description);
		}
		return obj;
	}

	private static T FindByName<T>(FrameworkElement root, string name) where T : FrameworkElement
	{
		if (root == null)
		{
			return null;
		}
		if (root.FindName(name) is T result)
		{
			return result;
		}
		return FindLogicalChild(root, (T candidate) => string.Equals(candidate.Name, name, StringComparison.Ordinal)) ?? FindVisualChild(root, (T candidate) => string.Equals(candidate.Name, name, StringComparison.Ordinal));
	}

	private static T FindLogicalChild<T>(DependencyObject root, Func<T, bool> predicate) where T : DependencyObject
	{
		if (root == null)
		{
			return null;
		}
		foreach (object child in LogicalTreeHelper.GetChildren(root))
		{
			if (child is DependencyObject dependencyObject)
			{
				if (dependencyObject is T val && predicate(val))
				{
					return val;
				}
				T val2 = FindLogicalChild(dependencyObject, predicate);
				if (val2 != null)
				{
					return val2;
				}
			}
		}
		return null;
	}

	private static T FindVisualChild<T>(DependencyObject root, Func<T, bool> predicate) where T : DependencyObject
	{
		if (root == null)
		{
			return null;
		}
		int childrenCount = VisualTreeHelper.GetChildrenCount(root);
		for (int i = 0; i < childrenCount; i++)
		{
			DependencyObject child = VisualTreeHelper.GetChild(root, i);
			if (child is T val && predicate(val))
			{
				return val;
			}
			T val2 = FindVisualChild(child, predicate);
			if (val2 != null)
			{
				return val2;
			}
		}
		return null;
	}
}
