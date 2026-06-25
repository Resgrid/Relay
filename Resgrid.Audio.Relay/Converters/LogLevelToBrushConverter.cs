using System;
using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;
using Serilog.Events;

namespace Resgrid.Audio.Relay.Converters
{
	/// <summary>
	/// Maps a Serilog <see cref="LogEventLevel"/> to a foreground brush for the Logs list so
	/// warnings/errors stand out: Error/Fatal red, Warning amber, Information default, lower
	/// levels dimmed. Exposes a shared <see cref="Instance"/> for <c>{x:Static}</c> use.
	/// </summary>
	public sealed class LogLevelToBrushConverter : IValueConverter
	{
		public static readonly LogLevelToBrushConverter Instance = new LogLevelToBrushConverter();

		private static readonly Brush Dim = Make(0x90, 0x90, 0x90);
		private static readonly Brush Normal = Make(0xDD, 0xDD, 0xDD);
		private static readonly Brush Warn = StatusBrushes.Connecting;
		private static readonly Brush Error = StatusBrushes.Disconnected;

		public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
		{
			if (value is LogEventLevel level)
			{
				switch (level)
				{
					case LogEventLevel.Fatal:
					case LogEventLevel.Error:
						return Error;
					case LogEventLevel.Warning:
						return Warn;
					case LogEventLevel.Information:
						return Normal;
					default:
						return Dim;
				}
			}

			return Normal;
		}

		public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
		{
			throw new NotSupportedException();
		}

		private static Brush Make(byte r, byte g, byte b)
		{
			var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
			brush.Freeze();
			return brush;
		}
	}
}
