using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Windows;
using Resgrid.Relay.Engine;

namespace Resgrid.Audio.Relay.Services
{
	/// <summary>
	/// Builds the tray icons from the bundled PNG logo, with a status dot (green running, amber starting/stopping, red faulted)
	/// so the relay's health shows in the notification area. H.NotifyIcon's <c>IconSource</c> can't take a PNG (it needs ICO
	/// data and threw "Argument 'picture' must be a picture that can be used as a Icon"), so the icon is rendered here and
	/// assigned as a <see cref="Icon"/>. Icons are cached per state, so state changes don't leak GDI handles.
	/// </summary>
	public static class TrayIconFactory
	{
		private static readonly object Gate = new object();
		private static readonly Dictionary<string, Icon> Cache = new Dictionary<string, Icon>();

		public static Icon Create(RelayServiceState state, bool anyRunning)
		{
			var key = anyRunning ? state.ToString() : "idle";
			lock (Gate)
			{
				if (Cache.TryGetValue(key, out var cached))
					return cached;

				var icon = Render(anyRunning ? DotColor(state) : (Color?)null);
				Cache[key] = icon;
				return icon;
			}
		}

		private static Color DotColor(RelayServiceState state)
		{
			switch (state)
			{
				case RelayServiceState.Running: return Color.FromArgb(46, 160, 67);
				case RelayServiceState.Faulted: return Color.FromArgb(218, 54, 51);
				case RelayServiceState.Starting:
				case RelayServiceState.Stopping: return Color.FromArgb(210, 153, 34);
				default: return Color.FromArgb(139, 148, 158);
			}
		}

		private static Icon Render(Color? dot)
		{
			const int size = 32;
			using var bitmap = new Bitmap(size, size, PixelFormat.Format32bppArgb);
			using (var graphics = Graphics.FromImage(bitmap))
			{
				graphics.SmoothingMode = SmoothingMode.AntiAlias;
				graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
				graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
				graphics.Clear(Color.Transparent);

				var resource = Application.GetResourceStream(new Uri("pack://application:,,,/Resources/ResgridRelay.png", UriKind.Absolute));
				if (resource != null)
				{
					using var stream = resource.Stream;
					using var logo = new Bitmap(stream);
					graphics.DrawImage(logo, new Rectangle(0, 0, size, size));
				}

				if (dot.HasValue)
				{
					using var outline = new SolidBrush(Color.FromArgb(230, 20, 20, 20));
					using var fill = new SolidBrush(dot.Value);
					graphics.FillEllipse(outline, 17, 17, 15, 15);
					graphics.FillEllipse(fill, 19, 19, 11, 11);
				}
			}

			var handle = bitmap.GetHicon();
			try
			{
				using var borrowed = Icon.FromHandle(handle);
				return (Icon)borrowed.Clone();
			}
			finally
			{
				DestroyIcon(handle);
			}
		}

		[DllImport("user32.dll", SetLastError = true)]
		private static extern bool DestroyIcon(IntPtr handle);
	}
}
