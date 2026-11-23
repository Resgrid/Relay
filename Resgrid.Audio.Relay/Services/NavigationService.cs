using System;

namespace Resgrid.Audio.Relay.Services
{
	/// <summary>
	/// Lets view-models move the shell to another page (e.g. the Dashboard's setup checklist). The shell window registers
	/// the real navigation callback once its NavigationView exists.
	/// </summary>
	public sealed class NavigationService
	{
		private Action<Type> _navigate;

		public void Register(Action<Type> navigate) => _navigate = navigate;

		public void Navigate(Type pageType) => _navigate?.Invoke(pageType);
	}
}
