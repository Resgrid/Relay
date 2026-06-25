using System;
using System.Globalization;
using System.Windows.Data;

namespace Resgrid.Audio.Relay.Converters
{
	/// <summary>
	/// Renders the squelch <see cref="bool"/> as a badge label: open → "OPEN", closed →
	/// "closed". Exposes a shared <see cref="Instance"/> for <c>{x:Static}</c> use from XAML.
	/// </summary>
	public sealed class SquelchTextConverter : IValueConverter
	{
		public static readonly SquelchTextConverter Instance = new SquelchTextConverter();

		public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
		{
			return value is bool b && b ? "SQ OPEN" : "SQ closed";
		}

		public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
		{
			throw new NotSupportedException();
		}
	}
}
