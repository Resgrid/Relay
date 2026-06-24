using System.ComponentModel;

namespace Resgrid.Relay.Engine
{
	/// <summary>
	/// Live, observable health and traffic snapshot for a running relay mode.
	/// Implements <see cref="INotifyPropertyChanged"/> so the desktop UI can bind
	/// directly. Connection properties report <see cref="ConnectionState.NotApplicable"/>
	/// for dependencies the mode does not use.
	/// </summary>
	public interface IRelayStatus : INotifyPropertyChanged
	{
		ConnectionState ResgridApi { get; }
		ConnectionState LiveKit { get; }
		ConnectionState Redis { get; }
		ConnectionState Smtp { get; }
		ConnectionState Tts { get; }

		double InputDbfs { get; }
		bool SquelchOpen { get; }
		bool Transmitting { get; }
		bool Receiving { get; }

		long CallsCreated { get; }
		long MessagesProcessed { get; }
		long TransmissionsRecorded { get; }
	}
}
