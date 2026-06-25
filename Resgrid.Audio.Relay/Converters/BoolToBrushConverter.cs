using System;
using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;

namespace Resgrid.Audio.Relay.Converters
{
	/// <summary>
	/// Maps a <see cref="bool"/> to one of two brushes (e.g. transmitting / squelch-open
	/// indicators). True → <see cref="TrueBrush"/>, false → <see cref="FalseBrush"/>.
	/// </summary>
	public sealed class BoolToBrushConverter : IValueConverter
	{
		public Brush TrueBrush { get; set; } = StatusBrushes.Running;
		public Brush FalseBrush { get; set; } = StatusBrushes.Inactive;

		public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
		{
			return value is bool b && b ? TrueBrush : FalseBrush;
		}

		public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
		{
			throw new NotSupportedException();
		}
	}
}
