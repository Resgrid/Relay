using System.Windows.Controls;
using Resgrid.Audio.Relay.ViewModels;

namespace Resgrid.Audio.Relay.Views
{
	/// <summary>Placeholder Devices screen — filled in by the next UI pass.</summary>
	public partial class DevicesView : UserControl
	{
		public DevicesView()
		{
			InitializeComponent();
		}

		public DevicesView(DevicesViewModel viewModel) : this()
		{
			DataContext = viewModel;
		}
	}
}
