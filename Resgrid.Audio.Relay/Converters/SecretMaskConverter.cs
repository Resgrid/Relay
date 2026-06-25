using System;
using System.Globalization;
using System.Windows.Data;

namespace Resgrid.Audio.Relay.Converters
{
	/// <summary>
	/// One-way converter that renders a secret string as a fixed run of bullet characters for
	/// the masked (not-revealed) display of a secret field. Empty stays empty so the operator
	/// can tell an unset secret from a stored one. Exposes a shared <see cref="Instance"/>.
	/// </summary>
	public sealed class SecretMaskConverter : IValueConverter
	{
		public static readonly SecretMaskConverter Instance = new SecretMaskConverter();

		public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
		{
			var s = value as string;
			if (string.IsNullOrEmpty(s))
				return "";
			// Fixed length regardless of the real secret length, so the mask never leaks size.
			return new string('•', 8);
		}

		public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
		{
			throw new NotSupportedException();
		}
	}
}
