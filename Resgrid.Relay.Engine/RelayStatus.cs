using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Threading;

namespace Resgrid.Relay.Engine
{
	/// <summary>
	/// Mutable <see cref="IRelayStatus"/> that a running relay service updates as it
	/// connects and passes traffic. Connection/level/flag setters raise
	/// <see cref="INotifyPropertyChanged"/>; the counters are incremented atomically.
	/// A service sets only the connections it actually uses — the rest stay
	/// <see cref="ConnectionState.NotApplicable"/> so the UI can grey them out.
	/// </summary>
	public sealed class RelayStatus : IRelayStatus
	{
		public event PropertyChangedEventHandler PropertyChanged;

		private ConnectionState _resgridApi = ConnectionState.NotApplicable;
		public ConnectionState ResgridApi { get => _resgridApi; set => SetField(ref _resgridApi, value); }

		private ConnectionState _liveKit = ConnectionState.NotApplicable;
		public ConnectionState LiveKit { get => _liveKit; set => SetField(ref _liveKit, value); }

		private ConnectionState _redis = ConnectionState.NotApplicable;
		public ConnectionState Redis { get => _redis; set => SetField(ref _redis, value); }

		private ConnectionState _smtp = ConnectionState.NotApplicable;
		public ConnectionState Smtp { get => _smtp; set => SetField(ref _smtp, value); }

		private ConnectionState _tts = ConnectionState.NotApplicable;
		public ConnectionState Tts { get => _tts; set => SetField(ref _tts, value); }

		private double _inputDbfs;
		public double InputDbfs { get => _inputDbfs; set => SetField(ref _inputDbfs, value); }

		private bool _squelchOpen;
		public bool SquelchOpen { get => _squelchOpen; set => SetField(ref _squelchOpen, value); }

		private bool _transmitting;
		public bool Transmitting { get => _transmitting; set => SetField(ref _transmitting, value); }

		private bool _receiving;
		public bool Receiving { get => _receiving; set => SetField(ref _receiving, value); }

		private long _callsCreated;
		public long CallsCreated => Interlocked.Read(ref _callsCreated);

		private long _messagesProcessed;
		public long MessagesProcessed => Interlocked.Read(ref _messagesProcessed);

		private long _transmissionsRecorded;
		public long TransmissionsRecorded => Interlocked.Read(ref _transmissionsRecorded);

		public void IncrementCallsCreated()
		{
			Interlocked.Increment(ref _callsCreated);
			OnPropertyChanged(nameof(CallsCreated));
		}

		public void IncrementMessagesProcessed()
		{
			Interlocked.Increment(ref _messagesProcessed);
			OnPropertyChanged(nameof(MessagesProcessed));
		}

		public void IncrementTransmissionsRecorded()
		{
			Interlocked.Increment(ref _transmissionsRecorded);
			OnPropertyChanged(nameof(TransmissionsRecorded));
		}

		private void SetField<T>(ref T field, T value, [CallerMemberName] string propertyName = null)
		{
			if (EqualityComparer<T>.Default.Equals(field, value))
				return;
			field = value;
			OnPropertyChanged(propertyName);
		}

		private void OnPropertyChanged(string propertyName) =>
			PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
	}
}
