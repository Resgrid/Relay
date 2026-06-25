using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Resgrid.Relay.Engine;

namespace Resgrid.Audio.Relay.ViewModels
{
	/// <summary>
	/// A single mode card on the Operations screen. Holds no state of its own beyond what it
	/// re-reads from the owning <see cref="OperationsViewModel"/> on <see cref="Refresh"/>:
	/// the derived lifecycle state, the running flag (drives the Start/Stop button), and a
	/// pre-flight validation summary.
	/// </summary>
	public partial class ModeCardViewModel : ObservableObject
	{
		private readonly OperationsViewModel _owner;

		public ModeCardViewModel(string mode, OperationsViewModel owner)
		{
			Mode = mode;
			_owner = owner;
			DisplayName = FriendlyName(mode);
			Description = DescriptionFor(mode);
			Refresh();
		}

		/// <summary>The mode key passed to the controller (e.g. "radio").</summary>
		public string Mode { get; }

		public string DisplayName { get; }

		public string Description { get; }

		[ObservableProperty]
		private RelayServiceState _state = RelayServiceState.Stopped;

		[ObservableProperty]
		private bool _isRunning;

		[ObservableProperty]
		private string _stateText = "Stopped";

		/// <summary>Pre-flight problems for this mode (empty when ready to start).</summary>
		public ObservableCollectionLite<string> ValidationErrors { get; } = new ObservableCollectionLite<string>();

		[ObservableProperty]
		private bool _hasValidationErrors;

		[ObservableProperty]
		private string _validationSummary = "Ready";

		/// <summary>Re-reads derived state from the owner. Called when the running set changes.</summary>
		public void Refresh()
		{
			IsRunning = _owner.IsRunning(Mode);
			State = _owner.StateFor(Mode);
			StateText = IsRunning ? State.ToString() : "Stopped";

			var errors = _owner.Validate(Mode);
			ValidationErrors.Reset(errors);
			HasValidationErrors = errors.Count > 0;
			ValidationSummary = errors.Count == 0
				? "Pre-flight OK"
				: $"{errors.Count} issue(s) — start disabled";

			StartCommand.NotifyCanExecuteChanged();
			StopCommand.NotifyCanExecuteChanged();
		}

		// Mirror OperationsViewModel.StartMode: disallow Start when already running or when
		// pre-flight validation has failed. Refresh() notifies StartCommand when these change.
		private bool CanStart() => !IsRunning && !HasValidationErrors;
		private bool CanStop() => IsRunning;

		[RelayCommand(CanExecute = nameof(CanStart))]
		private void Start()
		{
			_owner.StartMode(Mode);
		}

		[RelayCommand(CanExecute = nameof(CanStop))]
		private async Task Stop()
		{
			await _owner.StopModeAsync(Mode).ConfigureAwait(true);
		}

		private static string FriendlyName(string mode)
		{
			switch (mode)
			{
				case "smtp": return "SMTP Dispatch";
				case "audio": return "Audio Tone-Detect";
				case "radio": return "Radio Bridge";
				case "record": return "Recorder";
				case "dispatch": return "Dispatch Tone-Out";
				default: return mode;
			}
		}

		private static string DescriptionFor(string mode)
		{
			switch (mode)
			{
				case "smtp": return "SMTP dispatch relay (cross-platform).";
				case "audio": return "Windows tone-detect dispatch importer.";
				case "radio": return "Bidirectional radio ↔ Resgrid PTT bridge (Windows).";
				case "record": return "Records PTT channel transmissions to disk/S3 + metadata.";
				case "dispatch": return "Tones out new calls (tones + TTS) to a PTT channel.";
				default: return "";
			}
		}
	}

	/// <summary>
	/// A minimal observable list helper used for the per-card validation list. It mirrors the
	/// small subset of <see cref="System.Collections.ObjectModel.ObservableCollection{T}"/> the
	/// view needs while supporting an atomic <see cref="Reset"/> from a fresh sequence.
	/// </summary>
	public sealed class ObservableCollectionLite<T> : System.Collections.ObjectModel.ObservableCollection<T>
	{
		public void Reset(IEnumerable<T> items)
		{
			Clear();
			foreach (var item in items)
				Add(item);
		}
	}
}
