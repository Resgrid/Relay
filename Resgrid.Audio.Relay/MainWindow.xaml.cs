using NAudio.Wave;
using Resgrid.Audio.Core;
using Serilog;
using Serilog.Core;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;

namespace Resgrid.Audio.Relay
{
	public partial class MainWindow : Window
	{
		private readonly Logger _logger;
		private readonly WatcherAudioStorage _audioStorage;
		private readonly AudioEvaluator _audioEvaluator;
		private readonly AudioRecorder _audioRecorder;

		public MainWindow()
		{
			InitializeComponent();

			_logger = new LoggerConfiguration()
				.MinimumLevel.Information()
				.CreateLogger();

			_audioStorage = new WatcherAudioStorage(_logger);
			_audioEvaluator = new AudioEvaluator(_logger);
			_audioRecorder = new AudioRecorder(_audioEvaluator, _audioStorage);
			_audioRecorder.SetSampleAggregator(new SampleAggregator());
			_audioRecorder.SampleAggregator.MaximumCalculated += SampleAggregator_MaximumCalculated;
			_audioRecorder.SampleAggregator.WaveformCalculated += SampleAggregator_WaveformCalculated;

			LoadDevices();
			Closed += OnClosed;
		}

		private void LoadDevices()
		{
			var devices = new List<DeviceOption>();
			for (var deviceNumber = 0; deviceNumber < WaveIn.DeviceCount; deviceNumber++)
			{
				var capabilities = WaveIn.GetCapabilities(deviceNumber);
				devices.Add(new DeviceOption
				{
					Id = deviceNumber,
					Name = $"{deviceNumber}: {capabilities.ProductName} ({capabilities.Channels} channels)"
				});
			}

			DeviceComboBox.ItemsSource = devices;
			DeviceComboBox.SelectedIndex = devices.Count > 0 ? 0 : -1;
			StatusTextBlock.Text = devices.Count > 0 ? "Ready" : "No input devices found";
		}

		private void ToggleMonitoring_Click(object sender, RoutedEventArgs e)
		{
			if (_audioRecorder.RecordingState == RecordingState.Stopped)
			{
				var selectedDevice = DeviceComboBox.SelectedItem as DeviceOption;
				if (selectedDevice == null)
				{
					StatusTextBlock.Text = "Select an input device before starting.";
					return;
				}

				_audioRecorder.BeginMonitoring(selectedDevice.Id);
				ToggleButton.Content = "Stop Monitoring";
				StatusTextBlock.Text = $"Monitoring {selectedDevice.Name}";
				AppendEvent($"Started monitoring device {selectedDevice.Name}");
			}
			else
			{
				_audioRecorder.Stop();
				ToggleButton.Content = "Start Monitoring";
				StatusTextBlock.Text = "Stopped";
				AppendEvent("Stopped monitoring");
			}
		}

		private void SampleAggregator_MaximumCalculated(object sender, MaxSampleEventArgs e)
		{
			Dispatcher.Invoke(() =>
			{
				MinTextBlock.Text = e.MinSample.ToString("F4");
				MaxTextBlock.Text = e.MaxSample.ToString("F4");
				DbTextBlock.Text = e.Db.ToString("F2");
			});
		}

		private void SampleAggregator_WaveformCalculated(object sender, WaveformEventArgs e)
		{
			Dispatcher.Invoke(() =>
			{
				FftTextBlock.Text = e.FastFourierTransform.Length.ToString();
			});
		}

		private void OnClosed(object sender, EventArgs e)
		{
			_audioRecorder.Stop();
		}

		private void AppendEvent(string text)
		{
			EventsTextBox.AppendText($"{DateTime.Now:G}: {text}{Environment.NewLine}");
			EventsTextBox.ScrollToEnd();
		}

		private sealed class DeviceOption
		{
			public int Id { get; set; }
			public string Name { get; set; }
		}
	}
}
