using System;
using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;
using Resgrid.Relay.Engine;

namespace Resgrid.Audio.Relay.Converters
{
	/// <summary>
	/// Maps a <see cref="RelayServiceState"/> to a brush: Running = green, Starting/Stopping
	/// = amber, Faulted = red, Stopped = grey.
	/// </summary>
	public sealed class RelayServiceStateToBrushConverter : IValueConverter
	{
		public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
		{
			if (value is RelayServiceState state)
			{
				switch (state)
				{
					case RelayServiceState.Running:
						return StatusBrushes.Running;
					case RelayServiceState.Starting:
					case RelayServiceState.Stopping:
						return StatusBrushes.Connecting;
					case RelayServiceState.Faulted:
						return StatusBrushes.Faulted;
					default:
						return StatusBrushes.Inactive;
				}
			}

			return StatusBrushes.Inactive;
		}

		public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
		{
			throw new NotSupportedException();
		}
	}
}
