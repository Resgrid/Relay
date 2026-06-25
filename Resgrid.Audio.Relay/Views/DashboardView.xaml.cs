using System.Windows;
using System.Windows.Controls;
using Resgrid.Audio.Relay.ViewModels;

namespace Resgrid.Audio.Relay.Views
{
	/// <summary>
	/// Dashboard screen. Bound to <see cref="DashboardViewModel"/>; disposes that view-model
	/// (detaching it from the controller's collection/state events) when the page leaves the
	/// visual tree, since navigation re-creates transient pages.
	/// </summary>
	public partial class DashboardView : UserControl
	{
		private DashboardViewModel _viewModel;

		public DashboardView()
		{
			InitializeComponent();
			Unloaded += OnUnloaded;
		}

		public DashboardView(DashboardViewModel viewModel) : this()
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
