using System.Windows.Controls;
using Resgrid.Audio.Relay.ViewModels;

namespace Resgrid.Audio.Relay.Views
{
	/// <summary>Placeholder About screen — filled in by the next UI pass.</summary>
	public partial class AboutView : UserControl
	{
		public AboutView()
		{
			InitializeComponent();
		}

		public AboutView(AboutViewModel viewModel) : this()
		{
			DataContext = viewModel;
		}
	}
}
