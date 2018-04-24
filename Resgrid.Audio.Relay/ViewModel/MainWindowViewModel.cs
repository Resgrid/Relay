using GalaSoft.MvvmLight;
using GalaSoft.MvvmLight.Command;
using GalaSoft.MvvmLight.Messaging;
using LiveCharts;
using LiveCharts.Wpf;
using Resgrid.Audio.Core;
using System;
using System.IO;
using System.Linq;
using System.Windows.Input;

namespace Resgrid.Audio.Relay.ViewModel
{
	public class MainWindowViewModel : ViewModelBase
	{
		private readonly IAudioRecorder _audioRecorder;

		private readonly RelayCommand toggleCommand;
		private readonly RelayCommand settingsCommand;

		private float lastPeak;
		public const string ViewName = "Resgrid Streamer";

		public SeriesCollection SeriesCollection { get; set; }
		public SeriesCollection SeriesCollection2 { get; set; }
		public string[] Labels { get; set; }
		public string[] Labels2 { get; set; }

		/// <summary>
		/// The <see cref="WelcomeTitle" /> property's name.
		/// </summary>
		public const string WelcomeTitlePropertyName = "WelcomeTitle";

		private string _toggleButtonText = string.Empty;

		/// <summary>
		/// Gets the WelcomeTitle property.
		/// Changes to that property's value raise the PropertyChanged event. 
		/// </summary>
		public string ToggleButtonText
		{
			get
			{
				return _toggleButtonText;
			}
			set
			{
				Set(ref _toggleButtonText, value);
			}
		}

		/// <summary>
		/// Initializes a new instance of the MainViewModel class.
		/// </summary>
		public MainWindowViewModel(IAudioRecorder audioRecorder)
		{
			_audioRecorder = audioRecorder;
			//_dataService.GetData(
			//		(item, error) =>
			//		{
			//			if (error != null)
			//			{
			//				// Report error here
			//				return;
			//			}

			//			WelcomeTitle = item.Title;
			//		});

			SeriesCollection = new SeriesCollection
						{
								new LineSeries
								{
										Title = "Frequency",
										LineSmoothness = 1,
										StrokeThickness = 1,
										DataLabels = false,
										PointGeometrySize = 0,
										Fill = System.Windows.Media.Brushes.Transparent,
										Values = new ChartValues<double> { 0 }
								}
						};

			SeriesCollection2 = new SeriesCollection
						{
								new LineSeries
								{
										Title = "Frequency",
										LineSmoothness = 1,
										StrokeThickness = 1,
										DataLabels = false,
										PointGeometrySize = 0,
										Fill = System.Windows.Media.Brushes.Transparent,
										Values = new ChartValues<double> { 0 }
								}
						};

			toggleCommand = new RelayCommand(ToggleRecording,
					() => this._audioRecorder.RecordingState == RecordingState.Stopped ||
								this._audioRecorder.RecordingState == RecordingState.Monitoring ||
								this._audioRecorder.RecordingState == RecordingState.Recording);

			this._audioRecorder.SampleAggregator.MaximumCalculated += OnRecorderMaximumCalculated;
			this._audioRecorder.SampleAggregator.WaveformCalculated += SampleAggregator_WaveformCalculated;

			Messenger.Default.Register<ShuttingDownMessage>(this, OnShuttingDown);

			this.ToggleButtonText = "Start Monitoring";
		}

		private void SampleAggregator_WaveformCalculated(object sender, WaveformEventArgs e)
		{
			SeriesCollection[0].Values.Clear();
			SeriesCollection[0].Values.AddRange(e.PulseCodeModulation.Cast<Object>());

			SeriesCollection2[0].Values.Clear();
			SeriesCollection2[0].Values.AddRange(e.FastFourierTransform.Cast<Object>());

			RaisePropertyChanged("SeriesCollection");
			RaisePropertyChanged("SeriesCollection2");
		}

		void OnRecorderMaximumCalculated(object sender, MaxSampleEventArgs e)
		{
			lastPeak = Math.Max(e.MaxSample, Math.Abs(e.MinSample));

			RaisePropertyChanged("CurrentInputLevel");
			RaisePropertyChanged("RecordedTime");
		}

		public ICommand ToggleCommand { get { return toggleCommand; } }

		private void OnShuttingDown(ShuttingDownMessage message)
		{
			if (message.CurrentViewName == ViewName)
			{
				this._audioRecorder.Stop();
			}
		}

		public string RecordedTime
		{
			get
			{
				var current = this._audioRecorder.RecordedTime;
				return String.Format("{0:D2}:{1:D2}.{2:D3}",
						current.Minutes, current.Seconds, current.Milliseconds);
			}
		}

		private void BeginMonitoring(int recordingDevice)
		{
			this._audioRecorder.BeginMonitoring(recordingDevice);
			RaisePropertyChanged("MicrophoneLevel");
		}

		private void ToggleRecording()
		{
			if (this._audioRecorder.RecordingState == RecordingState.Stopped)
			{
				this.ToggleButtonText = "Stop Monitoring";
				BeginMonitoring(0);
				this._audioRecorder.BeginRecording(Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".wav"));
			}
			else
			{
				this.ToggleButtonText = "Start Monitoring";
				this._audioRecorder.Stop();
			}

			//RaisePropertyChanged("MicrophoneLevel");
			//RaisePropertyChanged("ShowWaveForm");
		}

		public SampleAggregator SampleAggregator
		{
			get
			{
				return this._audioRecorder.SampleAggregator;
			}
		}

		public override void Cleanup()
		{
		    // Clean up if needed

		    base.Cleanup();
		}
	}
}
