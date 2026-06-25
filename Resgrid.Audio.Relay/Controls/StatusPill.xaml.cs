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
		}

		public static readonly DependencyProperty StateProperty = DependencyProperty.Register(
			nameof(State),
			typeof(ConnectionState),
			typeof(StatusPill),
			new FrameworkPropertyMetadata(ConnectionState.NotApplicable));

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
