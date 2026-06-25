using System;
using System.Globalization;
using System.Windows.Data;

namespace Resgrid.Audio.Relay.Converters
{
	/// <summary>
	/// Renders the tune-toggle button caption from the <c>IsTuning</c> flag: tuning → "Stop
	/// tuning", idle → "Start tuning". Exposes a shared <see cref="Instance"/> for XAML.
	/// </summary>
	public sealed class TuneButtonTextConverter : IValueConverter
	{
		public static readonly TuneButtonTextConverter Instance = new TuneButtonTextConverter();

		public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
		{
			return value is bool b && b ? "Stop tuning" : "Start tuning";
		}

		public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
		{
			throw new NotSupportedException();
		}
	}
}
