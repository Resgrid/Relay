using System.Collections.Generic;

namespace Resgrid.Audio.Core.Model
{
	public class Config
	{
		public int InputDevice { get; set; }
		public int AudioLength { get; set; } = 60;
		public string ApiUrl { get; set; }
		public bool Multiple { get; set; }
		public double Tolerance { get; set; } = 100;
		public sbyte Threshold { get; set; } = -40;
		public bool EnableSilenceDetection { get; set; }
		public bool Debug { get; set; }
		public string DebugKey { get; set; }
		public ResgridConnectionSettings Resgrid { get; set; } = new ResgridConnectionSettings();
		public DispatchMappingSettings DispatchMapping { get; set; } = new DispatchMappingSettings();
		public List<Watcher> Watchers { get; set; } = new List<Watcher>();
	}

	public class ResgridConnectionSettings
	{
		public string BaseUrl { get; set; } = "https://api.resgrid.com";
		public string ApiVersion { get; set; } = "4";
		public string ClientId { get; set; }
		public string ClientSecret { get; set; }
		public string RefreshToken { get; set; }
		public string Scope { get; set; } = "openid profile email offline_access mobile";
		public string TokenCachePath { get; set; } = ".\\data\\resgrid-token.json";
	}

	public class DispatchMappingSettings
	{
		public string GroupDispatchPrefix { get; set; } = "G";
		public string DepartmentDispatchPrefix { get; set; } = "G";
	}
}
