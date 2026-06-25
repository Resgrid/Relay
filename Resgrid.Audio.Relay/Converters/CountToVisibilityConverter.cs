using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace Resgrid.Audio.Relay.Converters
{
	/// <summary>
	/// Maps an integer count to <see cref="Visibility"/>. By default a count of zero is
	/// <see cref="Visibility.Visible"/> (so an "empty list" hint can be shown) and any positive
	/// count is <see cref="Visibility.Collapsed"/>. Set <see cref="Invert"/> to flip it (show
	/// content only when the count is non-zero). Exposes shared <see cref="ZeroVisible"/> and
	/// <see cref="NonZeroVisible"/> instances for <c>{x:Static}</c> use from XAML.
	/// </summary>
	public sealed class CountToVisibilityConverter : IValueConverter
	{
		/// <summary>Visible when the count is zero (empty-state hint).</summary>
		public static readonly CountToVisibilityConverter ZeroVisible = new CountToVisibilityConverter { Invert = false };

		/// <summary>Visible when the count is non-zero.</summary>
		public static readonly CountToVisibilityConverter NonZeroVisible = new CountToVisibilityConverter { Invert = true };

		public bool Invert { get; set; }

		public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
		{
			var count = value is int i ? i : 0;
			var isZero = count == 0;
			if (Invert)
				isZero = !isZero;
			return isZero ? Visibility.Visible : Visibility.Collapsed;
		}

		public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
		{
			throw new NotSupportedException();
		}
	}
}
