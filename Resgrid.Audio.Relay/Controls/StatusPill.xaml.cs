using System.Windows;
using System.Windows.Controls;
using Resgrid.Relay.Engine;

namespace Resgrid.Audio.Relay.Controls
{
	/// <summary>
	/// A small colored pill that reflects a <see cref="ConnectionState"/> with an optional
	/// text label, used for the per-dependency health chips on the Dashboard / status bar.
	/// </summary>
	public partial class StatusPill : UserControl
	{
		public StatusPill()
		{
			InitializeComponent();
			Visibility = Visibility.Collapsed;
		}

		public static readonly DependencyProperty StateProperty = DependencyProperty.Register(
			nameof(State),
			typeof(ConnectionState),
			typeof(StatusPill),
			new FrameworkPropertyMetadata(ConnectionState.NotApplicable, OnStateChanged));

		// A connection the mode doesn't use (e.g. Redis for the radio bridge) isn't shown at all, instead of a grey
		// "NotApplicable" pill.
		private static void OnStateChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
		{
			if (d is StatusPill pill)
				pill.Visibility = (ConnectionState)e.NewValue == ConnectionState.NotApplicable ? Visibility.Collapsed : Visibility.Visible;
		}

		public ConnectionState State
		{
			get => (ConnectionState)GetValue(StateProperty);
			set => SetValue(StateProperty, value);
		}

		public static readonly DependencyProperty LabelProperty = DependencyProperty.Register(
			nameof(Label),
			typeof(string),
			typeof(StatusPill),
			new FrameworkPropertyMetadata(string.Empty));

		public string Label
		{
			get => (string)GetValue(LabelProperty);
			set => SetValue(LabelProperty, value);
		}
	}
}
