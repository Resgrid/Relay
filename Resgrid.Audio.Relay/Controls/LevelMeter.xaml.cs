using System;
using System.Windows;
using System.Windows.Controls;

namespace Resgrid.Audio.Relay.Controls
{
	/// <summary>
	/// A simple input-level meter that maps a dBFS value (typically -80..0) onto a 0..100
	/// <see cref="ProgressBar"/> using <c>(db + 80) / 80</c>, and shows the numeric value.
	/// </summary>
	public partial class LevelMeter : UserControl
	{
		public LevelMeter()
		{
			InitializeComponent();
			UpdateVisual(Dbfs);
		}

		public static readonly DependencyProperty DbfsProperty = DependencyProperty.Register(
			nameof(Dbfs),
			typeof(double),
			typeof(LevelMeter),
			new FrameworkPropertyMetadata(-80.0, OnDbfsChanged));

		/// <summary>Current input level in dBFS. Clamped to the -80..0 display range.</summary>
		public double Dbfs
		{
			get => (double)GetValue(DbfsProperty);
			set => SetValue(DbfsProperty, value);
		}

		private static void OnDbfsChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
		{
			((LevelMeter)d).UpdateVisual((double)e.NewValue);
		}

		private void UpdateVisual(double db)
		{
			// Map dBFS to a 0..100 percentage: -80 dBFS -> 0, 0 dBFS -> 100.
			var percent = Math.Clamp((db + 80.0) / 80.0 * 100.0, 0.0, 100.0);
			Bar.Value = percent;
			Label.Text = $"{db,5:0.0} dBFS";
		}
	}
}
