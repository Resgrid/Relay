using System.Windows.Media;

namespace Resgrid.Audio.Relay.Converters
{
	/// <summary>
	/// Shared, frozen traffic-light brushes used by the status converters and controls so
	/// every health indicator across the app uses one consistent palette.
	/// </summary>
	public static class StatusBrushes
	{
		public static readonly Brush Connected = Freeze(Color.FromRgb(0x2E, 0xCC, 0x71));    // green
		public static readonly Brush Connecting = Freeze(Color.FromRgb(0xF1, 0xC4, 0x0F));   // amber
		public static readonly Brush Degraded = Freeze(Color.FromRgb(0xE6, 0x7E, 0x22));     // orange
		public static readonly Brush Disconnected = Freeze(Color.FromRgb(0xE7, 0x4C, 0x3C)); // red
		public static readonly Brush Faulted = Freeze(Color.FromRgb(0xE7, 0x4C, 0x3C));      // red
		public static readonly Brush NotApplicable = Freeze(Color.FromRgb(0x7F, 0x8C, 0x8D));// grey
		public static readonly Brush Running = Freeze(Color.FromRgb(0x2E, 0xCC, 0x71));      // green
		public static readonly Brush Inactive = Freeze(Color.FromRgb(0x95, 0xA5, 0xA6));     // grey

		private static Brush Freeze(Color color)
		{
			var brush = new SolidColorBrush(color);
			brush.Freeze();
			return brush;
		}
	}
}
