using System.Windows;
using System.Windows.Controls;
using Resgrid.Audio.Relay.ViewModels;

namespace Resgrid.Audio.Relay.Views
{
	/// <summary>
	/// Operations screen. Bound to <see cref="OperationsViewModel"/>; disposes it (detaching
	/// the controller event handlers) when the transient page leaves the visual tree.
	/// </summary>
	public partial class OperationsView : UserControl
	{
		private OperationsViewModel _viewModel;

		public OperationsView()
		{
			InitializeComponent();
			Unloaded += OnUnloaded;
		}

		public OperationsView(OperationsViewModel viewModel) : this()
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
