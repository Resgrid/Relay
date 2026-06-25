using System;
using System.Windows;
using System.Windows.Controls;
using Resgrid.Audio.Relay.ViewModels;

namespace Resgrid.Audio.Relay.Views
{
	/// <summary>
	/// Live Logs screen. Bound to <see cref="LogsViewModel"/>; this code-behind only handles
	/// the auto-scroll-to-tail behaviour (a pure view concern) by listening for the view-model's
	/// <see cref="LogsViewModel.EntriesAppended"/> event and scrolling the last item into view.
	/// </summary>
	public partial class LogsView : UserControl
	{
		private LogsViewModel _viewModel;

		public LogsView()
		{
			InitializeComponent();
			Unloaded += OnUnloaded;
		}

		public LogsView(LogsViewModel viewModel) : this()
		{
			DataContext = viewModel;
			_viewModel = viewModel;
			_viewModel.EntriesAppended += OnEntriesAppended;
		}

		private void OnEntriesAppended(object sender, EventArgs e)
		{
			if (LogList.Items.Count == 0)
				return;

			var last = LogList.Items[LogList.Items.Count - 1];
			LogList.ScrollIntoView(last);
		}

		private void OnUnloaded(object sender, RoutedEventArgs e)
		{
			// The pages are transient and re-created on navigation, so detach + stop the pump
			// when this instance leaves the visual tree.
			if (_viewModel != null)
			{
				_viewModel.EntriesAppended -= OnEntriesAppended;
				_viewModel.Dispose();
				_viewModel = null;
			}
		}
	}
}
