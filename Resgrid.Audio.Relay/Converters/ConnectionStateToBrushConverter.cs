using System;
using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;
using Resgrid.Relay.Engine;

namespace Resgrid.Audio.Relay.Converters
{
	/// <summary>
	/// Maps a <see cref="ConnectionState"/> to a traffic-light <see cref="Brush"/>:
	/// Connected = green, Connecting = amber, Disconnected/Degraded = red,
	/// NotApplicable/Unknown = grey.
	/// </summary>
	public sealed class ConnectionStateToBrushConverter : IValueConverter
	{
		public Brush ConnectedBrush { get; set; } = StatusBrushes.Connected;
		public Brush ConnectingBrush { get; set; } = StatusBrushes.Connecting;
		public Brush DisconnectedBrush { get; set; } = StatusBrushes.Disconnected;
		public Brush DegradedBrush { get; set; } = StatusBrushes.Degraded;
		public Brush NotApplicableBrush { get; set; } = StatusBrushes.NotApplicable;

		public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
		{
			if (value is ConnectionState state)
			{
				switch (state)
				{
					case ConnectionState.Connected:
						return ConnectedBrush;
					case ConnectionState.Connecting:
						return ConnectingBrush;
					case ConnectionState.Degraded:
						return DegradedBrush;
					case ConnectionState.Disconnected:
						return DisconnectedBrush;
					default:
						return NotApplicableBrush;
				}
			}

			return NotApplicableBrush;
		}

		public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
		{
			throw new NotSupportedException();
		}
	}
}
