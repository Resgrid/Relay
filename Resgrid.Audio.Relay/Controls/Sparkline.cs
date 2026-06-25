using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;

namespace Resgrid.Audio.Relay.Controls
{
	/// <summary>
	/// A tiny <see cref="Polyline"/>-backed sparkline that plots a rolling series of values
	/// (e.g. input dBFS over time) normalized to the control's bounds. Minimal for now — the
	/// later UI pass styles and animates it.
	/// </summary>
	public sealed class Sparkline : Control
	{
		private readonly Polyline _polyline;

		static Sparkline()
		{
			DefaultStyleKeyProperty.OverrideMetadata(
				typeof(Sparkline),
				new FrameworkPropertyMetadata(typeof(Sparkline)));
		}

		public Sparkline()
		{
			_polyline = new Polyline
			{
				Stroke = Brushes.LimeGreen,
				StrokeThickness = 1.5,
				StrokeLineJoin = PenLineJoin.Round
			};

			var canvas = new Canvas();
			canvas.Children.Add(_polyline);
			AddVisualChild(canvas);
			_canvas = canvas;

			SizeChanged += (_, __) => Redraw();
		}

		private readonly Canvas _canvas;

		protected override int VisualChildrenCount => 1;

		protected override Visual GetVisualChild(int index) => _canvas;

		protected override Size MeasureOverride(Size constraint)
		{
			_canvas.Measure(constraint);
			return new Size(
				double.IsInfinity(constraint.Width) ? 100 : constraint.Width,
				double.IsInfinity(constraint.Height) ? 24 : constraint.Height);
		}

		protected override Size ArrangeOverride(Size arrangeBounds)
		{
			_canvas.Arrange(new Rect(arrangeBounds));
			Redraw();
			return arrangeBounds;
		}

		public static readonly DependencyProperty ValuesProperty = DependencyProperty.Register(
			nameof(Values),
			typeof(IEnumerable<double>),
			typeof(Sparkline),
			new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender, OnValuesChanged));

		/// <summary>The series to plot. Min/max are auto-scaled to the control height.</summary>
		public IEnumerable<double> Values
		{
			get => (IEnumerable<double>)GetValue(ValuesProperty);
			set => SetValue(ValuesProperty, value);
		}

		public static readonly DependencyProperty StrokeProperty = DependencyProperty.Register(
			nameof(Stroke),
			typeof(Brush),
			typeof(Sparkline),
			new FrameworkPropertyMetadata(Brushes.LimeGreen, OnStrokeChanged));

		public Brush Stroke
		{
			get => (Brush)GetValue(StrokeProperty);
			set => SetValue(StrokeProperty, value);
		}

		private static void OnValuesChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
		{
			((Sparkline)d).Redraw();
		}

		private static void OnStrokeChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
		{
			((Sparkline)d)._polyline.Stroke = e.NewValue as Brush ?? Brushes.LimeGreen;
		}

		private void Redraw()
		{
			var width = ActualWidth;
			var height = ActualHeight;
			_polyline.Points.Clear();

			var data = Values?.ToList();
			if (data == null || data.Count < 2 || width <= 0 || height <= 0)
				return;

			var min = data.Min();
			var max = data.Max();
			var range = max - min;
			if (range <= 0)
				range = 1;

			var stepX = width / (data.Count - 1);
			for (var i = 0; i < data.Count; i++)
			{
				var x = i * stepX;
				var y = height - ((data[i] - min) / range * height);
				_polyline.Points.Add(new Point(x, y));
			}
		}
	}
}
