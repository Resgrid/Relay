using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace Resgrid.Audio.Relay.Converters
{
	/// <summary>
	/// Maps a <see cref="bool"/> to <see cref="Visibility"/> (true → Visible, false →
	/// Collapsed). Set <see cref="Invert"/> to flip the mapping. Exposes a shared
	/// <see cref="Instance"/> usable as <c>{x:Static ...}</c> from XAML.
	/// </summary>
	public sealed class BoolToVisibilityConverter : IValueConverter
	{
		public static readonly BoolToVisibilityConverter Instance = new BoolToVisibilityConverter();

		public bool Invert { get; set; }

		public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
		{
			var flag = value is bool b && b;
			if (Invert)
				flag = !flag;
			return flag ? Visibility.Visible : Visibility.Collapsed;
		}

		public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
		{
			var isVisible = value is Visibility v && v == Visibility.Visible;
			return Invert ? !isVisible : isVisible;
		}
	}
}
