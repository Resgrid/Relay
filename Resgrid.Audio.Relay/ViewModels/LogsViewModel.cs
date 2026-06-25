using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Resgrid.Relay.Engine.Logging;
using Serilog.Events;

namespace Resgrid.Audio.Relay.ViewModels
{
	/// <summary>
	/// Live log view fed by the engine's <see cref="UiLogBus"/>. A background pump drains the
	/// bus channel and batches records to the UI thread at ~10 Hz, ring-buffering the visible
	/// list to a fixed cap (dropping the oldest). A minimum-level filter, auto-scroll toggle
	/// and Copy/Clear commands round out the screen. The pump is started in the constructor and
	/// stopped on <see cref="Dispose"/>.
	/// </summary>
	public partial class LogsViewModel : ObservableObject, IDisposable
	{
		private const int MaxEntries = 5000;
		private static readonly TimeSpan FlushInterval = TimeSpan.FromMilliseconds(100); // ~10 Hz

		private readonly UiLogBus _bus;
		private readonly Dispatcher _dispatcher;
		private readonly CancellationTokenSource _cts = new CancellationTokenSource();
		private readonly object _pendingGate = new object();
		private readonly List<LogRecord> _pending = new List<LogRecord>();
		private bool _disposed;

		public LogsViewModel(UiLogBus bus)
		{
			_bus = bus;
			_dispatcher = Application.Current?.Dispatcher ?? Dispatcher.CurrentDispatcher;
			_ = PumpAsync(_cts.Token);
		}

		[ObservableProperty]
		private string _title = "Logs";

		/// <summary>Most-recent-last log entries currently shown (filtered, ring-buffered).</summary>
		public ObservableCollection<LogRecord> Entries { get; } = new ObservableCollection<LogRecord>();

		/// <summary>Available minimum-level filter options.</summary>
		public IReadOnlyList<LogEventLevel> Levels { get; } = new[]
		{
			LogEventLevel.Verbose,
			LogEventLevel.Debug,
			LogEventLevel.Information,
			LogEventLevel.Warning,
			LogEventLevel.Error,
			LogEventLevel.Fatal
		};

		/// <summary>Records below this level are not shown.</summary>
		[ObservableProperty]
		private LogEventLevel _minimumLevel = LogEventLevel.Information;

		/// <summary>When true, newly appended entries scroll into view (the View handles the scroll).</summary>
		[ObservableProperty]
		private bool _autoScroll = true;

		/// <summary>Raised after a batch is appended so the View can auto-scroll to the tail.</summary>
		public event EventHandler EntriesAppended;

		private void Enqueue(LogRecord record)
		{
			lock (_pendingGate)
			{
				_pending.Add(record);
			}
		}

		private async Task PumpAsync(CancellationToken token)
		{
			try
			{
				var reader = _bus.Reader;
				while (await reader.WaitToReadAsync(token).ConfigureAwait(false))
				{
					while (reader.TryRead(out var record))
						Enqueue(record);

					// Coalesce a burst before marshalling to the UI thread (~10 Hz).
					await Task.Delay(FlushInterval, token).ConfigureAwait(false);
					Flush();
				}
			}
			catch (OperationCanceledException)
			{
				// Normal on Dispose.
			}
		}

		private void Flush()
		{
			List<LogRecord> batch;
			lock (_pendingGate)
			{
				if (_pending.Count == 0)
					return;
				batch = new List<LogRecord>(_pending);
				_pending.Clear();
			}

			_ = _dispatcher.BeginInvoke(new Action(() => AppendBatch(batch)));
		}

		private void AppendBatch(List<LogRecord> batch)
		{
			var appendedVisible = false;
			foreach (var record in batch)
			{
				if (record.Level < MinimumLevel)
					continue;

				Entries.Add(record);
				appendedVisible = true;
			}

			while (Entries.Count > MaxEntries)
				Entries.RemoveAt(0);

			if (appendedVisible && AutoScroll)
				EntriesAppended?.Invoke(this, EventArgs.Empty);
		}

		[RelayCommand]
		private void Clear()
		{
			Entries.Clear();
		}

		[RelayCommand]
		private void Copy()
		{
			var sb = new StringBuilder();
			foreach (var e in Entries)
			{
				sb.Append(e.Timestamp.ToString("yyyy-MM-dd HH:mm:ss.fff"))
					.Append(" [").Append(e.Level).Append("] ")
					.AppendLine(e.Message);
				if (!string.IsNullOrEmpty(e.Exception))
					sb.AppendLine(e.Exception);
			}

			try
			{
				Clipboard.SetText(sb.ToString());
			}
			catch
			{
				// Clipboard may be locked by another process — non-fatal.
			}
		}

		public void Dispose()
		{
			if (_disposed)
				return;
			_disposed = true;

			try { _cts.Cancel(); } catch { /* already disposed */ }
			_cts.Dispose();
		}
	}
}
