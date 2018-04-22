using GalaSoft.MvvmLight.Threading;
using System.Windows;

namespace Resgrid.Audio.Relay
{
	public partial class App : Application
	{
		static App()
		{
			DispatcherHelper.Initialize();
		}
	}
}
