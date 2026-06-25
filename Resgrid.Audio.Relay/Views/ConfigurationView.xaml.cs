using System.Windows;
using System.Windows.Controls;
using Resgrid.Audio.Relay.ViewModels;

namespace Resgrid.Audio.Relay.Views
{
	/// <summary>
	/// Configuration screen. Bound to <see cref="ConfigurationViewModel"/>; disposes it (which
	/// stops any in-progress radio tune) when the transient page leaves the visual tree.
	/// </summary>
	public partial class ConfigurationView : UserControl
	{
		private ConfigurationViewModel _viewModel;

		public ConfigurationView()
		{
			InitializeComponent();
			Unloaded += OnUnloaded;
		}

		public ConfigurationView(ConfigurationViewModel viewModel) : this()
		{
			DataContext = viewModel;
			_viewModel = viewModel;
		}

		private void OnUnloaded(object sender, RoutedEventArgs e)
		{
			if (_viewModel != null)
			{
				_viewModel.Dispose();
				_viewModel = null;
			}
		}
	}
}
