using Resgrid.Providers.ApiClient.V4;

namespace Resgrid.Audio.Relay.Console.Configuration
{
	public sealed class RelayHostOptions
	{
		public string Mode { get; set; } = "smtp";
		public string AudioConfigPath { get; set; } = "settings.json";
		public ResgridApiClientOptions Resgrid { get; set; } = new ResgridApiClientOptions();
		public RelayTelemetryOptions Telemetry { get; set; } = new RelayTelemetryOptions();
		public SmtpRelayOptions Smtp { get; set; } = new SmtpRelayOptions();
	}

	public sealed class RelayTelemetryOptions
	{
		public string Environment { get; set; } = "";
		public SentryTelemetryOptions Sentry { get; set; } = new SentryTelemetryOptions();
		public CountlyTelemetryOptions Countly { get; set; } = new CountlyTelemetryOptions();
	}

	public sealed class SentryTelemetryOptions
	{
		public string Dsn { get; set; } = "";
		public string Release { get; set; } = "";
		public bool SendDefaultPii { get; set; } = true;
	}

	public sealed class CountlyTelemetryOptions
	{
		public string Url { get; set; } = "";
		public string AppKey { get; set; } = "";
		public string DeviceId { get; set; } = "";
		public int RequestTimeoutSeconds { get; set; } = 5;
	}

	public sealed class SmtpRelayOptions
	{
		public string ServerName { get; set; } = "resgrid-relay";
		public int Port { get; set; } = 2525;
		public string DataDirectory { get; set; } = ".\\data";
		public int DuplicateWindowHours { get; set; } = 72;
		public int DefaultCallPriority { get; set; } = 1;
		public int MaxAttachmentBytes { get; set; } = 10485760;
		public bool SaveRawMessages { get; set; } = true;
		public string DepartmentDispatchPrefix { get; set; } = "G";
		public string[] DepartmentAddressDomains { get; set; } = new[] { "dispatch.resgrid.com" };
		public string[] GroupAddressDomains { get; set; } = new[] { "groups.resgrid.com" };
	}
}
