using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace Resgrid.Audio.Relay.Converters
{
	/// <summary>
	/// Maps a <see cref="bool"/> to <see cref="Visibility"/> inverted (true → Collapsed, false →
	/// Visible). Used to swap the masked vs revealed secret editors on the Configuration screen.
	/// Exposes a shared <see cref="Instance"/> for <c>{x:Static}</c> use from XAML.
	/// </summary>
	public sealed class BoolToVisibilityInvertedConverter : IValueConverter
	{
		public static readonly BoolToVisibilityInvertedConverter Instance = new BoolToVisibilityInvertedConverter();

		public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
		{
			var flag = value is bool b && b;
			return flag ? Visibility.Collapsed : Visibility.Visible;
		}

		public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
		{
			return value is Visibility v && v == Visibility.Collapsed;
		}
	}
}
