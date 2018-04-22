using MahApps.Metro.Controls;
using Resgrid.Audio.Relay.ViewModel;

namespace Resgrid.Audio.Relay
{
	public partial class MainWindow : MetroWindow
	{
		public MainWindow()
		{
			InitializeComponent();
			Closing += (s, e) => ViewModelLocator.Cleanup();
		}
	}
}