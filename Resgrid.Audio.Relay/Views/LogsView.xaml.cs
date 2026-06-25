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
			Loaded += OnLoaded;
			Unloaded += OnUnloaded;
		}

		public LogsView(LogsViewModel viewModel) : this()
		{
			DataContext = viewModel;
			_viewModel = viewModel;
		}

		private void OnEntriesAppended(object sender, EventArgs e)
		{
			if (LogList.Items.Count == 0)
				return;

			var last = LogList.Items[LogList.Items.Count - 1];
			LogList.ScrollIntoView(last);
		}

		private void OnLoaded(object sender, RoutedEventArgs e)
		{
			// Re-attach the view-only auto-scroll handler whenever the page (re)enters the visual
			// tree — the page instance may be reused on back-navigation. Idempotent so repeated
			// Loaded events don't double-subscribe. The view-model is a long-lived singleton whose
			// log pump keeps running across navigation, so there's nothing to restart here.
			if (_viewModel != null)
			{
				_viewModel.EntriesAppended -= OnEntriesAppended;
				_viewModel.EntriesAppended += OnEntriesAppended;
			}
		}

		private void OnUnloaded(object sender, RoutedEventArgs e)
		{
			// Detach only the view-only auto-scroll handler. Do NOT dispose the view-model or null
			// the field: the page may be reused on back-navigation and the shared pump must keep
			// running. The singleton view-model is disposed by the DI container at app shutdown.
			if (_viewModel != null)
				_viewModel.EntriesAppended -= OnEntriesAppended;
		}
	}
}
